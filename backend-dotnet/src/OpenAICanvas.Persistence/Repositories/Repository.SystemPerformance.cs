#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>数据库 Schema 状态。对应 Go: <c>database.SchemaStatus</c>。</summary>
public sealed class SchemaStatusDto
{
    public long Current { get; set; }
    public long Expected { get; set; }
    public bool Ready { get; set; }
}

/// <summary>数据库连接池统计。对应 Go: <c>repository.DatabasePoolStats</c>。</summary>
public sealed class DatabasePoolStatsDto
{
    public long MaxOpenConnections { get; set; }
    public long OpenConnections { get; set; }
    public long InUse { get; set; }
    public long Idle { get; set; }
    public long WaitCount { get; set; }
    public long WaitDurationMs { get; set; }
    public long MaxIdleClosed { get; set; }
    public long MaxLifetimeClosed { get; set; }
}

/// <summary>PostgreSQL 运行时统计。对应 Go: <c>repository.PostgresRuntimeStats</c>。</summary>
public sealed class PostgresRuntimeStatsDto
{
    [GoOmitEmpty]
    public string ServerVersion { get; set; } = "";

    [GoOmitEmpty]
    public long DatabaseBytes { get; set; }

    [GoOmitEmpty]
    public long Connections { get; set; }

    [GoOmitEmpty]
    public long MaxConnections { get; set; }

    [GoOmitEmpty]
    public long Transactions { get; set; }

    [GoOmitEmpty]
    public long Rollbacks { get; set; }

    [GoOmitEmpty]
    public double CacheHitRate { get; set; }

    [GoOmitEmpty]
    public long TempFiles { get; set; }

    [GoOmitEmpty]
    public long TempBytes { get; set; }

    [GoOmitEmpty]
    public long Deadlocks { get; set; }
}

/// <summary>数据库运行时统计。对应 Go: <c>repository.DatabaseRuntimeStats</c>。</summary>
public sealed class DatabaseRuntimeStatsDto
{
    public string Driver { get; set; } = "";
    public bool Connected { get; set; }
    public long LatencyMs { get; set; }

    [GoOmitEmpty]
    public long DatabaseBytes { get; set; }
    public SchemaStatusDto Schema { get; set; } = new();
    public DatabasePoolStatsDto Pool { get; set; } = new();

    [GoOmitEmpty]
    public PostgresRuntimeStatsDto? Postgres { get; set; }
}

/// <summary>数据库运行时统计。对应 Go: <c>DatabaseRuntimeStats</c>。</summary>
public sealed partial class Repository
{
    /// <summary>
    /// 数据库连通性/延迟/容量/Schema 状态。对应 Go: <c>DatabaseRuntimeStats</c>。
    /// ADO.NET 不公开与 Go sql.DBStats 一一对应的连接池计数，因此池统计保留零值，
    /// 但始终返回完整对象以维持管理端 API 契约。
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
            try
            {
                long pageCount = await ScalarAsync<long>(
                    connection, "PRAGMA page_count", new { }, cancellationToken: cancellationToken).ConfigureAwait(false);
                long pageSize = await ScalarAsync<long>(
                    connection, "PRAGMA page_size", new { }, cancellationToken: cancellationToken).ConfigureAwait(false);
                result.DatabaseBytes = pageCount * pageSize;
            }
            catch (Exception)
            {
                // 容量统计失败不影响连通性与管理页可用性。
            }
        }
        else
        {
            result.Postgres = await PostgresRuntimeStatsAsync(connection, cancellationToken).ConfigureAwait(false);
            result.DatabaseBytes = result.Postgres.DatabaseBytes;
        }

        try
        {
            long current = await ScalarAsync<long>(
                connection,
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
                new { },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            result.Schema.Current = current;
            result.Schema.Ready = current == result.Schema.Expected;
        }
        catch (Exception)
        {
            // 与 Go 的 ReadSchemaStatus 一致：结构统计异常不阻断整页运行时指标。
        }
        return result;
    }

    private async Task<PostgresRuntimeStatsDto> PostgresRuntimeStatsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        PostgresRuntimeStatsDto result = new();
        try
        {
            result.ServerVersion = await ScalarAsync<string>(
                connection, "SHOW server_version", new { }, cancellationToken: cancellationToken).ConfigureAwait(false) ?? "";

            string maxConnections = await ScalarAsync<string>(
                connection, "SHOW max_connections", new { }, cancellationToken: cancellationToken).ConfigureAwait(false) ?? "";
            _ = long.TryParse(maxConnections, out long parsedMaxConnections);
            result.MaxConnections = parsedMaxConnections;

            const string query = """
                SELECT
                    pg_database_size(current_database()) AS "DatabaseBytes",
                    numbackends AS "Connections",
                    xact_commit AS "Transactions",
                    xact_rollback AS "Rollbacks",
                    blks_read AS "BlocksRead",
                    blks_hit AS "BlocksHit",
                    temp_files AS "TempFiles",
                    temp_bytes AS "TempBytes",
                    deadlocks AS "Deadlocks"
                FROM pg_stat_database
                WHERE datname = current_database()
                """;
            PostgresRuntimeQueryRow? row = await QuerySingleOrDefaultAsync<PostgresRuntimeQueryRow>(
                connection, query, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                result.DatabaseBytes = row.DatabaseBytes;
                result.Connections = row.Connections;
                result.Transactions = row.Transactions;
                result.Rollbacks = row.Rollbacks;
                result.TempFiles = row.TempFiles;
                result.TempBytes = row.TempBytes;
                result.Deadlocks = row.Deadlocks;
                long totalBlocks = row.BlocksRead + row.BlocksHit;
                if (totalBlocks > 0)
                {
                    result.CacheHitRate = row.BlocksHit * 100d / totalBlocks;
                }
            }
        }
        catch (Exception)
        {
            // 权限或统计视图不可用时仍返回可用的 PostgreSQL 标识，避免整页失败。
        }
        return result;
    }

    private sealed class PostgresRuntimeQueryRow
    {
        public long DatabaseBytes { get; init; }
        public long Connections { get; init; }
        public long Transactions { get; init; }
        public long Rollbacks { get; init; }
        public long BlocksRead { get; init; }
        public long BlocksHit { get; init; }
        public long TempFiles { get; init; }
        public long TempBytes { get; init; }
        public long Deadlocks { get; init; }
    }
}
