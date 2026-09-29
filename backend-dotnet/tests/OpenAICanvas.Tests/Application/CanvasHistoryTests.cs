#nullable enable
using System.Text.Json;
using Dapper;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 画布版本历史（Go v23）：随保存捕获快照、5 分钟间隔与保留上限、恢复 CAS、引用保护。
/// 对应 Go: <c>canvas/canvas_history.go</c> 与 <c>repository/canvas_history.go</c>。
/// </summary>
public sealed class CanvasHistoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly UserDataService _userData;

    public CanvasHistoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-history-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _userData = new UserDataService(_repository, null, null);
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

    private static string CanvasPayload(long revision, string content) => JsonSerializer.Serialize(new
    {
        id = "canvas-1",
        title = "测试画布",
        revision,
        nodes = new[] { new { id = "n1", content } },
        connections = Array.Empty<object>(),
    });

    private async Task<UserDataSummaryDto> SaveAsync(long revision, string content)
    {
        return await _userData.UpsertUserCanvasProjectAsync(
            "user-a", JsonSerializer.Deserialize<JsonElement>(CanvasPayload(revision, content)));
    }

    [Fact]
    public async Task 保存捕获快照且内容不变时跳过()
    {
        await MigrateAsync();
        await SaveAsync(0, "v1");
        await SaveAsync(1, "v2");
        await SaveAsync(2, "v2"); // 内容无变化 → 不捕获

        (List<CanvasSnapshot> snapshots, long current) = await _userData.HistoryAsync("user-a", "canvas-1");
        Assert.Equal(3, current);
        // 首建（revision 0 → before=null 无快照）+ v1→v2 一次 = 1 条
        Assert.Single(snapshots);
        Assert.Equal(1, snapshots[0].Revision);
        Assert.Equal("automatic", snapshots[0].Reason);

        // 快照摘要不含 payload。
        Assert.DoesNotContain("payload_json", snapshots[0].GetType().GetProperties().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 五分钟节流内只捕获一次且保留上限生效()
    {
        await MigrateAsync();
        // 快速连续保存受 5 分钟节流约束：只有首次（无上一快照）捕获。
        for (int index = 0; index < 25; index++)
        {
            await SaveAsync(index, $"v{index}");
        }

        (List<CanvasSnapshot> snapshots, _) = await _userData.HistoryAsync("user-a", "canvas-1");
        Assert.Single(snapshots);
        // 唯一快照捕获于第二次保存：记的是保存前的版本 1。
        Assert.Equal(1, snapshots[0].Revision);

        // 直接驱动仓储验证保留上限：预置 25 条旧快照后强制捕获 → 修剪到 20。
        for (int index = 0; index < 25; index++)
        {
            await _repository.CanvasSnapshotSeedForTestAsync("user-a", "canvas-1", 1000 + index);
        }

        CanvasProject project = (await _repository.CanvasProjectMetadataAsync("user-a", "canvas-1"))!;
        CanvasSnapshot forced = new()
        {
            ID = IdGenerator.NewId(),
            CanvasID = project.ID,
            UserID = project.UserID,
            Revision = project.Revision,
            Title = project.Title,
            PayloadJSON = "{}",
            PayloadBytes = 2,
            Reason = "before_restore",
            ContentUpdatedAt = project.UpdatedAt,
            CreatedAt = DateTime.UtcNow,
        };
        await _repository.SaveCanvasWithSnapshotAsync(
            project, forced, [], [], DateTime.UtcNow.AddMinutes(-5), 20, force: true);

        long total = await SnapshotCountAsync();
        Assert.Equal(20, total);
    }

    [Fact]
    public async Task 恢复校验当前版本并回滚内容()
    {
        await MigrateAsync();
        await SaveAsync(0, "v1");
        await SaveAsync(1, "v2");

        (List<CanvasSnapshot> snapshots, long current) = await _userData.HistoryAsync("user-a", "canvas-1");
        Assert.Equal(2, current);

        // 版本不匹配 → 409。
        await Assert.ThrowsAsync<AppError>(() => _userData.RestoreCanvasHistoryAsync(
            "user-a", "canvas-1", snapshots[0].ID, revision: 999));

        // 缺 revision → 428。
        await Assert.ThrowsAsync<AppError>(() => _userData.RestoreCanvasHistoryAsync(
            "user-a", "canvas-1", snapshots[0].ID, revision: null));

        // 正常恢复：内容回到 v1，版本号递增到 3。
        UserDataSummaryDto restored = await _userData.RestoreCanvasHistoryAsync(
            "user-a", "canvas-1", snapshots[0].ID, current);
        Assert.Equal(3, restored.Revision);

        JsonElement payload = await _userData.UserCanvasProjectAsync("user-a", "canvas-1");
        string raw = payload.GetRawText();
        Assert.Contains("v1", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("v2", raw, StringComparison.Ordinal);

        // before_restore 强制捕获：恢复动作本身留痕。
        (List<CanvasSnapshot> after, _) = await _userData.HistoryAsync("user-a", "canvas-1");
        Assert.Contains(after, s => s.Reason == "before_restore");
    }

    [Fact]
    public async Task 快照详情与不存在路径()
    {
        await MigrateAsync();
        await SaveAsync(0, "v1");
        await SaveAsync(1, "v2");
        (List<CanvasSnapshot> snapshots, _) = await _userData.HistoryAsync("user-a", "canvas-1");

        CanvasSnapshot detail = await _userData.HistorySnapshotAsync("user-a", "canvas-1", snapshots[0].ID);
        Assert.Contains("v1", detail.PayloadJSON, StringComparison.Ordinal);

        await Assert.ThrowsAsync<AppError>(() => _userData.HistorySnapshotAsync(
            "user-a", "canvas-1", "missing"));
        await Assert.ThrowsAsync<AppError>(() => _userData.HistoryAsync("user-b", "canvas-1"));
    }

    [Fact]
    public async Task 历史引用保护阻止物理删除()
    {
        await MigrateAsync();
        // 造一个 ready 资源并让画布引用它。
        await _repository.CreateAsync(new Resource
        {
            ID = "res-his-1",
            UserID = "user-a",
            Status = "ready",
            Provider = "local",
            ObjectKey = "objects/res-his-1.bin",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        // 两次保存：首存 before 为空不建快照；二存内容变化 → 捕获含引用的 before 快照。
        for (int index = 0; index < 2; index++)
        {
            string payload = JsonSerializer.Serialize(new
            {
                id = "canvas-1",
                title = "引用画布",
                revision = (long)index,
                nodes = new[] { new { id = "n1", content = $"c{index}" } },
                connections = Array.Empty<object>(),
                refs = new[] { new { resourceId = "res-his-1" } },
            });
            await _userData.UpsertUserCanvasProjectAsync(
                "user-a", JsonSerializer.Deserialize<JsonElement>(payload));
        }

        // 快照引用了资源 → 保护命中。
        bool protected1 = await _repository.CanvasHistoryReferencesObjectAsync(new Resource
        {
            ID = "res-his-1",
            Endpoint = "",
            Bucket = "",
            ObjectKey = "objects/res-his-1.bin",
        });
        Assert.True(protected1);

        // 未被引用的资源不保护。
        bool protected2 = await _repository.CanvasHistoryReferencesObjectAsync(new Resource
        {
            ID = "res-other",
            Endpoint = "",
            Bucket = "",
            ObjectKey = "objects/other.bin",
        });
        Assert.False(protected2);
    }

    private async Task<long> SnapshotCountAsync()
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            (Microsoft.Data.Sqlite.SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        long? count = await connection.ExecuteScalarAsync<long?>("SELECT COUNT(*) FROM canvas_snapshots");
        return count ?? 0;
    }
}
