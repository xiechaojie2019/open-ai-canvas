#nullable enable
using System.Data.Common;
using Dapper;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>数据库 Schema 状态。对应 Go: <c>database.SchemaStatus</c>。</summary>
public sealed class SchemaStatusDto
{
    public long Current { get; set; }
    public long Expected { get; set; }
    public bool Ready { get; set; }
}

/// <summary>数据库运行时统计。对应 Go: <c>repository.DatabaseRuntimeStats</c>。</summary>
public sealed class DatabaseRuntimeStatsDto
{
    public string Driver { get; set; } = "";
    public bool Connected { get; set; }
    public long LatencyMs { get; set; }
    public long DatabaseBytes { get; set; }
    public SchemaStatusDto Schema { get; set; } = new();
    public long MaxOpenConnections { get; set; }
    public long OpenConnections { get; set; }
    public long InUse { get; set; }
    public long Idle { get; set; }
}

/// <summary>数据库运行时统计。对应 Go: <c>DatabaseRuntimeStats</c>。</summary>
public sealed partial class Repository
{
    /// <summary>
    /// 数据库连通性/延迟/容量/Schema 状态。对应 Go: <c>DatabaseRuntimeStats</c>。
    /// 连接池计数为 ADO.NET 简化（Go 的 sql.DBStats 字段无法逐项对应，见 PENDING #66 后续）。
    /// </summary>
    public async Task<DatabaseRuntimeStatsDto> DatabaseRuntimeStatsAsync(
        CancellationToken cancellationToken = default)
    {
        DatabaseRuntimeStatsDto result = new()
        {
            Driver = Dialect.IsPostgres ? "postgres" : "sqlite",
            Schema = new SchemaStatusDto { Expected = SchemaMigrationCatalog.CurrentSchemaVersion },
        };
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DateTime started = DateTime.UtcNow;
        try
        {
            await ScalarAsync<long>(connection, "SELECT 1", new { }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            result.Connected = true;
            result.LatencyMs = Math.Max(1, (long)(DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception)
        {
            return result;
        }

        if (!Dialect.IsPostgres)
        {
            long pageCount = await ScalarAsync<long>(
                connection, "PRAGMA page_count", new { }, cancellationToken: cancellationToken).ConfigureAwait(false);
            long pageSize = await ScalarAsync<long>(
                connection, "PRAGMA page_size", new { }, cancellationToken: cancellationToken).ConfigureAwait(false);
            result.DatabaseBytes = pageCount * pageSize;
        }
        else
        {
            try
            {
                result.DatabaseBytes = await ScalarAsync<long>(
                    connection,
                    "SELECT pg_database_size(current_database())",
                    new { },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 权限不足时跳过容量统计（Go 同样容错）。
            }
        }

        long current = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
            new { },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        result.Schema.Current = current;
        result.Schema.Ready = current == result.Schema.Expected;
        return result;
    }
}
