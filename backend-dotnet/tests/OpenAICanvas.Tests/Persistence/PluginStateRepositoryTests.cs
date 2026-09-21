#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 插件状态仓储验证：平台状态 upsert 与用户安装状态 upsert 在真实 SQLite 库上执行。
/// </summary>
public sealed class PluginStateRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public PluginStateRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-plugin-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task 平台状态_upsert_更新与插入都生效()
    {
        await MigrateAsync();
        DateTime now = DateTime.UtcNow;

        Assert.Null(await _repository.PluginPlatformStateAsync("runninghub-workflow-provider"));

        await _repository.SavePluginPlatformStateAsync(new PluginPlatformState
        {
            PluginID = "runninghub-workflow-provider",
            Available = true,
            UpdatedBy = "USR_ADMIN",
            CreatedAt = now,
            UpdatedAt = now,
        });
        PluginPlatformState inserted = await _repository.PluginPlatformStateAsync("runninghub-workflow-provider")
            ?? throw new InvalidOperationException("平台状态行应已插入");
        Assert.True(inserted.Available);

        await _repository.SavePluginPlatformStateAsync(new PluginPlatformState
        {
            PluginID = "runninghub-workflow-provider",
            Available = false,
            UpdatedBy = "USR_ADMIN",
            CreatedAt = now,
            UpdatedAt = now,
        });
        PluginPlatformState updated = await _repository.PluginPlatformStateAsync("runninghub-workflow-provider")
            ?? throw new InvalidOperationException("平台状态行应已更新");
        Assert.False(updated.Available);

        IReadOnlyList<PluginPlatformState> all = await _repository.PluginPlatformStatesAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task 用户安装状态_upsert_保留单行并补主键()
    {
        await MigrateAsync();

        Assert.Null(await _repository.UserPluginStateAsync("USR_1", "runninghub-workflow-provider"));

        await _repository.SaveUserPluginStateAsync(new UserPluginState
        {
            UserID = "USR_1",
            PluginID = "runninghub-workflow-provider",
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        UserPluginState inserted = await _repository.UserPluginStateAsync("USR_1", "runninghub-workflow-provider")
            ?? throw new InvalidOperationException("用户状态行应已插入");
        Assert.False(string.IsNullOrWhiteSpace(inserted.ID));
        Assert.True(inserted.Enabled);

        await _repository.SaveUserPluginStateAsync(new UserPluginState
        {
            ID = "ignored-on-update",
            UserID = "USR_1",
            PluginID = "runninghub-workflow-provider",
            Enabled = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        UserPluginState updated = await _repository.UserPluginStateAsync("USR_1", "runninghub-workflow-provider")
            ?? throw new InvalidOperationException("用户状态行应已更新");
        Assert.False(updated.Enabled);

        IReadOnlyList<UserPluginState> all = await _repository.UserPluginStatesAsync("USR_1");
        Assert.Single(all);
    }
}
