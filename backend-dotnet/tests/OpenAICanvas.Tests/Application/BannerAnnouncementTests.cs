#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 常驻滚动通知（Go v20–v22）：标题分段校验/合并、类型与状态白名单、生效窗口与管理端 CRUD。
/// 对应 Go: <c>app/announcement.go</c> banner 部分与 <c>repository/announcement.go</c>。
/// </summary>
public sealed class BannerAnnouncementTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly BannerAnnouncementService _service;
    private readonly User _admin = new()
    {
        ID = "user-admin",
        Username = "admin",
        Role = "admin",
        Status = "active",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    public BannerAnnouncementTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-banner-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _service = new BannerAnnouncementService(_repository);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private async Task MigrateAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
    }

    private static CreateBannerAnnouncementRequest ValidRequest() => new()
    {
        Title = "版本更新公告",
        NoticeType = "update",
        Link = "/changelog",
        Status = "active",
    };

    [Fact]
    public async Task 创建_生效列表_更新_删除全链路()
    {
        await MigrateAsync();
        BannerAnnouncement created = await _service.CreateAsync(_admin, ValidRequest());
        Assert.Equal("update", created.NoticeType);
        Assert.Equal("/changelog", created.Link);
        Assert.Equal("user-admin", created.CreatedBy);

        List<BannerAnnouncement> active = await _service.ActiveAsync();
        Assert.Single(active);
        Assert.Equal(created.ID, active[0].ID);

        // 停用后不再出现在公开列表。
        await _service.UpdateAsync(_admin, created.ID, new CreateBannerAnnouncementRequest
        {
            Title = "停用",
            Status = "disabled",
        });
        Assert.Empty(await _service.ActiveAsync());

        await _service.DeleteAsync(_admin, created.ID);
        BannerAnnouncementPage page = await _service.AdminPageAsync(_admin, "", "", 1, 20);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task 时间窗口过滤生效列表()
    {
        await MigrateAsync();
        await _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "未来才生效",
            Status = "active",
            StartsAt = DateTime.UtcNow.AddDays(1),
        });
        await _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "已过期",
            Status = "active",
            EndsAt = DateTime.UtcNow.AddDays(-1),
        });
        await _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "当前生效",
            Status = "active",
        });
        List<BannerAnnouncement> active = await _service.ActiveAsync();
        Assert.Single(active);
        Assert.Equal("当前生效", active[0].Title);
    }

    [Fact]
    public async Task 标题分段校验合并与编码回读()
    {
        await MigrateAsync();
        BannerAnnouncement created = await _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            TitleRuns =
            [
                new BannerTitleRun { Text = "限时", Color = "#FF00AA" },
                new BannerTitleRun { Text = "活动", Color = "#FF00AA" },
                new BannerTitleRun { Text = "开始", FontSize = 18, FontWeight = 600, FontFamily = "serif" },
            ],
            Status = "active",
        });
        // 相同样式相邻分段被合并；标题由全部分段拼接。
        Assert.NotNull(created.TitleRuns);
        Assert.Equal(2, created.TitleRuns.Count);
        Assert.Equal("限时活动", created.TitleRuns[0].Text);
        Assert.Equal("限时活动开始", created.Title);

        // 回读：title_runs 列解码为分段。
        BannerAnnouncement? reloaded = await _repository.BannerAnnouncementAsync(created.ID);
        Assert.NotNull(reloaded?.TitleRuns);
        Assert.Equal(2, reloaded.TitleRuns.Count);

        // 非法值。
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            TitleRuns = [new BannerTitleRun { Text = "x", FontSize = 9 }],
            Status = "active",
        }));
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            TitleRuns = [new BannerTitleRun { Text = "x", FontWeight = 300 }],
            Status = "active",
        }));
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            TitleRuns = [new BannerTitleRun { Text = "x", Color = "red" }],
            Status = "active",
        }));
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "t",
            NoticeType = "promo",
            Status = "active",
        }));
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "t",
            Link = "javascript:alert(1)",
            Status = "active",
        }));
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(_admin, new CreateBannerAnnouncementRequest
        {
            Title = "t",
            Status = "paused",
        }));
    }

    [Fact]
    public async Task 非管理员与管理端校验()
    {
        await MigrateAsync();
        User normal = new() { ID = "u1", Username = "n", Role = "user", Status = "active" };
        await Assert.ThrowsAsync<AppError>(() => _service.CreateAsync(normal, ValidRequest()));
        await Assert.ThrowsAsync<AppError>(() => _service.AdminPageAsync(normal, "", "", 1, 20));

        await _service.CreateAsync(_admin, ValidRequest());
        // 关键词过滤。
        BannerAnnouncementPage hit = await _service.AdminPageAsync(_admin, "更新", "", 1, 20);
        Assert.Equal(1, hit.Total);
        BannerAnnouncementPage miss = await _service.AdminPageAsync(_admin, "不存在", "", 1, 20);
        Assert.Equal(0, miss.Total);
        await Assert.ThrowsAsync<AppError>(() => _service.DeleteAsync(_admin, "missing"));
    }
}
