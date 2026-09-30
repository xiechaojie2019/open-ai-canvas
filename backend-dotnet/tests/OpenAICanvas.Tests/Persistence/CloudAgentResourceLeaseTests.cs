#nullable enable
using Dapper;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 云 Agent 资源租约（Go v29）：钉住/替换/转移/释放/过期清理。
/// 对应 Go: <c>repository/cloud_agent_resource_lease.go</c>。
/// </summary>
public sealed class CloudAgentResourceLeaseTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public CloudAgentResourceLeaseTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-lease-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
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

    private static DateTime Expires() => DateTime.UtcNow.AddMinutes(30);

    [Fact]
    public async Task 钉住_去重排序与冲突更新()
    {
        await MigrateAsync();
        DateTime expires = Expires();
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-1", ["res-b", "", "res-a", "res-b"], expires);

        // 重复 upsert 更新 run 与到期时间，不产生重复行。
        DateTime refreshed = Expires().AddMinutes(5);
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-2", "approval-1", ["res-a"], refreshed);

        long count = await CountAsync("user-a", "approval-1");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task 替换以所有者为准重建资源集()
    {
        await MigrateAsync();
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-1", ["res-1", "res-2"], Expires());
        // 另一所有者的租约不受影响。
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-2", ["res-3"], Expires());

        await _repository.ReplaceCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-1", ["res-9"], Expires());

        long owner1 = await CountAsync("user-a", "approval-1");
        long owner2 = await CountAsync("user-a", "approval-2");
        Assert.Equal(1, owner1);
        Assert.Equal(1, owner2);
    }

    [Fact]
    public async Task 转移_空所有者与同所有者直通()
    {
        await MigrateAsync();
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-1", ["res-1", "res-2"], Expires());

        DateTime expires = Expires();
        await _repository.TransferCloudAgentResourceLeasesAsync(
            "user-a", "approval-1", "task:task-1", "run-1", expires);
        Assert.Equal(0, await CountAsync("user-a", "approval-1"));
        Assert.Equal(2, await CountAsync("user-a", "task:task-1"));

        // 转移到已被占用的目标所有者：先清空目标再合入（Go 同语义）。
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "task:task-2", ["res-9"], Expires());
        await _repository.TransferCloudAgentResourceLeasesAsync(
            "user-a", "task:task-1", "task:task-2", "run-1", expires);
        Assert.Equal(0, await CountAsync("user-a", "task:task-1"));
        Assert.Equal(2, await CountAsync("user-a", "task:task-2"));

        // 空所有者 / 相同所有者直通。
        await _repository.TransferCloudAgentResourceLeasesAsync("user-a", "", "task:x", "run-1", expires);
        await _repository.TransferCloudAgentResourceLeasesAsync("user-a", "task:task-2", "task:task-2", "run-1", expires);
        Assert.Equal(2, await CountAsync("user-a", "task:task-2"));
    }

    [Fact]
    public async Task 按所有者_按运行与过期释放()
    {
        await MigrateAsync();
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "approval-1", ["res-1"], Expires());
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "task:t1", ["res-2"], DateTime.UtcNow.AddMinutes(-5));

        // 过期清理只删过期的。
        await _repository.ReleaseExpiredCloudAgentResourceLeasesAsync(DateTime.UtcNow);
        Assert.Equal(1, await CountAsync("user-a", "approval-1"));
        Assert.Equal(0, await CountAsync("user-a", "task:t1"));

        // 按运行释放。
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-1", "task:t2", ["res-3"], Expires());
        await _repository.ReleaseCloudAgentResourceLeasesByRunAsync("user-a", "run-1");
        Assert.Equal(0, await CountAsync("user-a", "approval-1"));
        Assert.Equal(0, await CountAsync("user-a", "task:t2"));

        // 按所有者释放。
        await _repository.UpsertCloudAgentResourceLeasesAsync(
            "user-a", "run-2", "approval-9", ["res-4"], Expires());
        await _repository.ReleaseCloudAgentResourceLeasesAsync("user-a", "approval-9");
        Assert.Equal(0, await CountAsync("user-a", "approval-9"));
    }

    private async Task<long> CountAsync(string userID, string ownerID)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            (Microsoft.Data.Sqlite.SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        long? count = await connection.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM \"cloudAgentResourceLeases\" WHERE \"userId\" = @userID AND \"ownerId\" = @ownerID",
            new { userID, ownerID });
        return count ?? 0;
    }
}
