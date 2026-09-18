using OpenAICanvas.Domain.Build;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>外部依赖探针。Phase 1 落地持久化后由 Program.cs 注入真实实现。</summary>
public sealed class SystemStatusProbes
{
    public Func<CancellationToken, Task<bool>> DatabaseCheck { get; init; } =
        static _ => Task.FromResult(false);

    public Func<bool> RuntimeCheck { get; init; } = static () => false;

    public Func<long> ActiveWorkerTasks { get; init; } = static () => 0L;

    public Func<CancellationToken, Task<SchemaStatusDto>> ReadSchema { get; init; } =
        static _ => Task.FromResult(new SchemaStatusDto
        {
            Current = 0,
            Expected = SystemStatus.CurrentSchemaVersion,
            Ready = false,
        });
}

/// <summary>
/// 系统状态快照与就绪判定。对应 Go: <c>cmd/server/system_status.go</c> 的 <c>systemStatus</c>。
/// </summary>
public sealed class SystemStatus
{
    /// <summary>对应 Go: <c>database.CurrentSchemaVersion</c>。</summary>
    public const long CurrentSchemaVersion = 15;

    private readonly SystemStatusProbes _probes;
    private volatile bool _started;
    private volatile bool _draining;

    public SystemStatus(SystemStatusProbes probes) => _probes = probes;

    public void MarkStarted() => _started = true;

    public void BeginDrain() => _draining = true;

    public async Task<SystemStatusSnapshotDto> SnapshotAsync(CancellationToken cancellationToken)
    {
        bool database = await _probes.DatabaseCheck(cancellationToken).ConfigureAwait(false);
        SchemaStatusDto schema = await _probes.ReadSchema(cancellationToken).ConfigureAwait(false);
        bool runtime = _probes.RuntimeCheck();

        bool ready = _started && !_draining && database && runtime && schema.Ready;

        // 就绪判定与 status 文案的分支顺序与 Go 完全一致。
        string status;
        if (_draining)
        {
            status = "draining";
        }
        else if (ready)
        {
            status = "ok";
        }
        else if (!_started)
        {
            status = "starting";
        }
        else
        {
            status = "unhealthy";
        }

        return new SystemStatusSnapshotDto
        {
            Status = status,
            Ready = ready,
            Started = _started,
            Draining = _draining,
            ActiveWorkerTasks = _probes.ActiveWorkerTasks(),
            Build = BuildInfoProvider.Current(),
            Schema = schema,
            Checks = new SystemStatusChecksDto
            {
                Database = database,
                Runtime = runtime,
                Schema = schema.Ready,
            },
        };
    }
}
