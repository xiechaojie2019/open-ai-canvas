#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 画布工具库（Go v30/v31）：内置种子、列表/详情、收藏、自定义 CRUD 与 mention 令牌解析。
/// 对应 Go: <c>internal/tools</c> 与 <c>app/tools_bridge.go</c>。
/// </summary>
public sealed class ToolsServiceTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly ToolsService _tools;

    public ToolsServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-tools-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _tools = new ToolsService(_repository);
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

    private async Task MigrateAndSeedAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
        await _tools.EnsureBuiltinToolsAsync();
    }

    [Fact]
    public async Task 内置种子幂等落库且列表不含提示词()
    {
        await MigrateAndSeedAsync();
        // 幂等：重复执行不重复写入。
        await _tools.EnsureBuiltinToolsAsync();

        ToolListDto list = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Page = 1, PageSize = 100 });
        Assert.Equal(87, list.TotalCount);
        Assert.All(list.Tools, item => Assert.Equal("builtin", item.Source));
        Assert.All(list.Tools, item => Assert.True(item.Enabled));

        // 列表摘要不含 prompt；详情才含。
        ToolItemDto detail = await _tools.DetailAsync("user-a", list.Tools[0].ID);
        Assert.NotEmpty(detail.Prompt);

        // 类型过滤。
        ToolListDto styles = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Type = "style" });
        Assert.Equal(45, styles.TotalCount);
        await Assert.ThrowsAsync<AppError>(
            () => _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Type = "nope" }));
    }

    [Fact]
    public async Task 收藏添加取消与收藏范围()
    {
        await MigrateAndSeedAsync();
        ToolListDto list = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Page = 1, PageSize = 5 });
        long toolID = list.Tools[0].ID;

        ToolItemDto favorited = await _tools.SetFavoriteAsync("user-a", toolID, favorite: true);
        Assert.True(favorited.Favorited);
        ToolListDto favorites = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "favorites" });
        Assert.Equal(1, favorites.TotalCount);
        Assert.True(favorites.Tools[0].Favorited);

        await _tools.SetFavoriteAsync("user-a", toolID, favorite: false);
        Assert.False((await _tools.DetailAsync("user-a", toolID)).Favorited);
        Assert.Equal(0, (await _tools.ListAsync("user-a", new ToolListRequest { Scope = "favorites" })).TotalCount);
    }

    [Fact]
    public async Task 自定义工具创建删除与内置保护()
    {
        await MigrateAndSeedAsync();
        ToolItemDto created = await _tools.CreateAsync("user-a", new ToolMutationRequest
        {
            Type = "effect",
            Label = "我的特效",
            Desc = "自定义特效",
            Tag = "自定义",
            Prompt = "为视频添加电影感调色",
            Visibility = "private",
        });
        Assert.Equal("user", created.Source);
        // 中文标签无 ASCII 字符，回落 custom_tool（Go sanitizeLabelEn 同语义）。
        Assert.Equal("custom_tool", created.LabelEn);

        // custom 范围只看自己的 user 工具。
        ToolListDto custom = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "custom" });
        Assert.Equal(1, custom.TotalCount);

        // 内置工具不能被删除（owner 不匹配 + source=builtin）。
        ToolListDto list = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Page = 1, PageSize = 1 });
        await Assert.ThrowsAsync<AppError>(() => _tools.DeleteAsync("user-a", list.Tools[0].ID));

        await _tools.DeleteAsync("user-a", created.ID);
        Assert.Equal(0, (await _tools.ListAsync("user-a", new ToolListRequest { Scope = "custom" })).TotalCount);
    }

    [Fact]
    public async Task 创建校验_类型_提示词_嵌套标签()
    {
        await MigrateAndSeedAsync();
        await Assert.ThrowsAsync<AppError>(() => _tools.CreateAsync("user-a", new ToolMutationRequest
        {
            Type = "nope", Label = "t", Prompt = "p",
        }));
        await Assert.ThrowsAsync<AppError>(() => _tools.CreateAsync("user-a", new ToolMutationRequest
        {
            Type = "style", Label = "t", Prompt = "带 @[tool:style:1:a:b] 嵌套",
        }));
        await Assert.ThrowsAsync<AppError>(() => _tools.CreateAsync("user-a", new ToolMutationRequest
        {
            Type = "style", Label = "", Prompt = "p",
        }));
        await Assert.ThrowsAsync<AppError>(() => _tools.CreateAsync("user-a", new ToolMutationRequest
        {
            Type = "style", Label = "t", Prompt = "p", Visibility = "internal",
        }));
    }

    [Fact]
    public async Task Mention令牌解析_类型与生成模式匹配()
    {
        await MigrateAndSeedAsync();
        ToolListDto styles = await _tools.ListAsync("user-a", new ToolListRequest { Scope = "public", Type = "style" });
        ToolItemDto style = await _tools.DetailAsync("user-a", styles.Tools[0].ID);

        string token = $"@[tool:style:{style.ID}:任展示:任图标]";
        string resolved = await _tools.ResolveToolMentionTokensAsync("user-a", "image", $"用 {token} 处理");
        Assert.Equal($"用 {style.Prompt} 处理", resolved);

        // 无令牌直通。
        Assert.Equal("普通提示词", await _tools.ResolveToolMentionTokensAsync("user-a", "image", "普通提示词"));

        // 类型不匹配。
        await Assert.ThrowsAsync<AppError>(() => _tools.ResolveToolMentionTokensAsync(
            "user-a", "video", $"用 {token} 处理"));
        // 类型与令牌段不符。
        string wrongType = $"@[tool:motion:{style.ID}:任展示:任图标]";
        await Assert.ThrowsAsync<AppError>(() => _tools.ResolveToolMentionTokensAsync(
            "user-a", "video", $"用 {wrongType} 处理"));
        // 未知工具。
        string ghost = $"@[tool:style:999999:任展示:任图标]";
        await Assert.ThrowsAsync<AppError>(() => _tools.ResolveToolMentionTokensAsync(
            "user-a", "image", $"用 {ghost} 处理"));
        // 畸形令牌（未闭合的 @[tool: 残留）。
        await Assert.ThrowsAsync<AppError>(() => _tools.ResolveToolMentionTokensAsync(
            "user-a", "image", $"用 {token} 和残缺 @[tool:style 处理"));
    }
}
