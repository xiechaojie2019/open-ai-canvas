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
/// RunningHub 工作流插件门控：默认禁用、平台行放行与未知 interfaceType 文案。
/// 对应 Go: <c>workflow_plugins_test.go</c> 的行为子集。
/// </summary>
public sealed class WorkflowPluginGateTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly WorkflowPluginGate _gate;

    public WorkflowPluginGateTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-gate-{Guid.NewGuid():N}.db");
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath}",
        });
        _repository = new Repository(_database);
        _gate = new WorkflowPluginGate(_repository);
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

    private async Task EnablePlatformAsync()
    {
        await _repository.SavePluginPlatformStateAsync(new PluginPlatformState
        {
            PluginID = WorkflowPluginGate.RunningHub,
            Available = true,
            UpdatedBy = "USR_ADMIN",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task 默认禁用_创建与执行侧都拒绝()
    {
        await MigrateAsync();
        AppError userError = await Assert.ThrowsAsync<AppError>(
            () => _gate.RequireForUserAsync("USR_1", "runninghub-workflow-image"));
        Assert.Equal("RunningHub 工作流插件未启用", userError.Message);

        AppError interfaceError = await Assert.ThrowsAsync<AppError>(
            () => _gate.RequireForInterfaceAsync("runninghub-workflow-image"));
        Assert.Equal("RunningHub 工作流插件未启用", interfaceError.Message);

        (IReadOnlyDictionary<string, string> statuses, IReadOnlyDictionary<string, WorkflowPluginStateView> states) =
            await _gate.StatusesForUserAsync("USR_1");
        Assert.Equal("disabled", statuses[WorkflowPluginGate.RunningHub]);
        Assert.False(states[WorkflowPluginGate.RunningHub].PlatformAvailable);
        Assert.False(states[WorkflowPluginGate.RunningHub].EffectiveEnabled);
        Assert.Equal("管理员已停用该插件", states[WorkflowPluginGate.RunningHub].BlockedReason);
    }

    [Fact]
    public async Task 平台行启用后_按用户维度放行()
    {
        await MigrateAsync();
        await EnablePlatformAsync();

        await _gate.RequireForUserAsync("USR_1", "runninghub-workflow-image");
        await _gate.RequireForInterfaceAsync("runninghub-workflow-video");

        (IReadOnlyDictionary<string, string> statuses, IReadOnlyDictionary<string, WorkflowPluginStateView> states) =
            await _gate.StatusesForUserAsync("USR_1");
        Assert.Equal("enabled", statuses[WorkflowPluginGate.RunningHub]);
        WorkflowPluginStateView state = states[WorkflowPluginGate.RunningHub];
        Assert.True(state.PlatformAvailable);
        Assert.True(state.EffectiveEnabled);
        Assert.True(state.UserEnabled);
        Assert.False(state.UserConfigured);
        Assert.Null(state.BlockedReason);
    }

    [Fact]
    public async Task 用户显式停用后_平台可用但个人禁用()
    {
        await MigrateAsync();
        await EnablePlatformAsync();
        await _repository.SaveUserPluginStateAsync(new UserPluginState
        {
            UserID = "USR_1",
            PluginID = WorkflowPluginGate.RunningHub,
            Enabled = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        AppError error = await Assert.ThrowsAsync<AppError>(
            () => _gate.RequireForUserAsync("USR_1", "runninghub-workflow-audio"));
        Assert.Equal("RunningHub 工作流插件未启用", error.Message);
    }

    [Fact]
    public async Task 未知工作流插件_403文案()
    {
        await MigrateAsync();
        AppError error = await Assert.ThrowsAsync<AppError>(
            () => _gate.RequireForUserAsync("USR_1", "runninghub-workflow-unknown"));
        Assert.Equal("未知工作流插件", error.Message);
    }
}
