using System.Data.Common;
using Dapper;

namespace OpenAICanvas.Persistence.Schema;

/// <summary>
/// 数据库结构迁移与版本校验。
/// </summary>
/// <remarks>
/// 对应 Go: <c>database.MigrateSchema</c> / <c>ReadSchemaStatus</c> /
/// <c>RequireSchemaVersion</c>。语义必须完全一致，否则 .NET 版无法接管
/// Go 版已迁移过的数据库。
/// </remarks>
public sealed class SchemaMigrator
{
    private const string SchemaMigrationsTable = "schema_migrations";

    private readonly CanvasDatabase _database;
    private readonly SchemaScripts _scripts;

    public SchemaMigrator(CanvasDatabase database)
    {
        _database = database;
        _scripts = SchemaScripts.Load();
    }

    /// <summary>
    /// 应用缺失的迁移并在事务内校验版本。对应 Go: <c>MigrateSchema</c>。
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await _database.InTransactionAsync(async (connection, transaction) =>
        {
            // PostgreSQL 用事务级咨询锁串行化并发迁移。
            string? lockSql = _database.Dialect.AcquireMigrationLock(
                SchemaMigrationCatalog.PostgresMigrationLockId);
            if (lockSql is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    lockSql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await EnsureMigrationsTableAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            SchemaMigrationRecord? version6 = await ReadRecordAsync(
                connection, transaction, 6, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<SchemaMigration> plan = SchemaMigrationCatalog.PlanFor(version6);

            foreach (SchemaMigration item in plan)
            {
                SchemaMigrationRecord? applied = await ReadRecordAsync(
                    connection, transaction, item.Version, cancellationToken).ConfigureAwait(false);

                if (applied is not null)
                {
                    ValidateRecord(applied, item);
                    continue;
                }

                SchemaMigrationContext context = new(
                    connection, transaction, _database.Dialect, _scripts);
                await item.Apply(context).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO \"schema_migrations\" (version, name, checksum, applied_at) VALUES (@version, @name, @checksum, @appliedAt)",
                    new
                    {
                        version = item.Version,
                        name = item.Name,
                        checksum = item.Checksum,
                        appliedAt = DateTime.UtcNow,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await RequireSchemaVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只校验版本兼容性，不做任何结构变更。对应 Go: <c>RequireSchemaVersion</c>。
    /// 用于 <c>CANVAS_AUTO_MIGRATE=false</c> 的受管部署。
    /// </summary>
    public async Task RequireSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await RequireSchemaVersionAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取结构版本状态。对应 Go: <c>ReadSchemaStatus</c>。</summary>
    public async Task<SchemaStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SchemaStatus> ReadStatusAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        SchemaStatus status = new()
        {
            Current = 0,
            Expected = SchemaMigrationCatalog.CurrentSchemaVersion,
            Ready = false,
        };

        if (!await TableExistsAsync(connection, transaction, SchemaMigrationsTable, cancellationToken).ConfigureAwait(false))
        {
            return status;
        }

        long current = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT COALESCE(MAX(version), 0) FROM \"schema_migrations\"",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;

        status = new SchemaStatus
        {
            Current = current,
            Expected = SchemaMigrationCatalog.CurrentSchemaVersion,
            Ready = false,
        };

        if (current != status.Expected)
        {
            return status;
        }

        await ValidateAllRecordsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        return new SchemaStatus
        {
            Current = current,
            Expected = status.Expected,
            Ready = true,
        };
    }

    private async Task ValidateAllRecordsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        SchemaMigrationRecord? version6 = await ReadRecordAsync(
            connection, transaction, 6, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SchemaMigration> plan = SchemaMigrationCatalog.PlanFor(version6);

        foreach (SchemaMigration item in plan)
        {
            SchemaMigrationRecord? applied = await ReadRecordAsync(
                connection, transaction, item.Version, cancellationToken).ConfigureAwait(false);

            if (applied is null)
            {
                throw new InvalidOperationException(
                    $"数据库缺少迁移记录 {item.Version}（{item.Name}）");
            }

            ValidateRecord(applied, item);
        }
    }

    private async Task RequireSchemaVersionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        SchemaStatus status = await ReadStatusAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (status.Current < status.Expected)
        {
            throw new InvalidOperationException(
                $"数据库结构版本过旧：当前 {status.Current}，程序要求 {status.Expected}，请先执行 migrate-schema up");
        }

        if (status.Current > status.Expected)
        {
            throw new InvalidOperationException(
                $"数据库结构版本 {status.Current} 高于程序支持的 {status.Expected}，拒绝使用旧程序连接新数据库");
        }
    }

    /// <summary>逐条比对名称与校验和。对应 Go: <c>validateMigrationRecord</c>。</summary>
    private static void ValidateRecord(SchemaMigrationRecord applied, SchemaMigration expected)
    {
        if (!string.Equals(applied.Name, expected.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"数据库迁移 {expected.Version} 名称不一致：记录为 {applied.Name}，程序期望 {expected.Name}");
        }

        if (!string.Equals(applied.Checksum, expected.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"数据库迁移 {expected.Version} 校验和不一致：记录为 {applied.Checksum}，程序期望 {expected.Checksum}");
        }
    }

    private async Task<SchemaMigrationRecord?> ReadRecordAsync(
        DbConnection connection,
        DbTransaction? transaction,
        long version,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, SchemaMigrationsTable, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await connection.QueryFirstOrDefaultAsync<SchemaMigrationRecord>(new CommandDefinition(
            "SELECT version, name, checksum, applied_at AS appliedAt FROM \"schema_migrations\" WHERE version = @version",
            new { version },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>创建迁移记录表。对应 Go 的 <c>AutoMigrate(&amp;schemaMigration{})</c>。</summary>
    private async Task EnsureMigrationsTableAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        string sql = _database.Dialect.IsPostgres
            ? """
              CREATE TABLE IF NOT EXISTS "schema_migrations" (
                  "version" bigserial NOT NULL,
                  "name" varchar(160) NOT NULL,
                  "checksum" varchar(96) NOT NULL,
                  "applied_at" timestamptz NOT NULL,
                  PRIMARY KEY ("version")
              )
              """
            : """
              CREATE TABLE IF NOT EXISTS "schema_migrations" (
                  "version" integer PRIMARY KEY AUTOINCREMENT,
                  "name" text NOT NULL,
                  "checksum" text NOT NULL,
                  "applied_at" datetime NOT NULL
              )
              """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        string sql = _database.Dialect.IsPostgres
            ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @table"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";

        long count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, new { table }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }
}
