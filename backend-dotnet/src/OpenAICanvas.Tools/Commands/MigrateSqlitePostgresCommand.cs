using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using OpenAICanvas.Persistence;

namespace OpenAICanvas.Tools.Commands;

/// <summary>对应 Go <c>cmd/migrate-sqlite-postgres</c>。</summary>
internal static class MigrateSqlitePostgresCommand
{
    private const int BatchSize = 100;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        string sourcePath = Environment.GetEnvironmentVariable("SQLITE_SOURCE_PATH")?.Trim() ?? string.Empty;
        string targetDsn = Environment.GetEnvironmentVariable("DATABASE_URL")?.Trim() ?? string.Empty;
        if (sourcePath.Length == 0 || targetDsn.Length == 0)
        {
            throw new InvalidOperationException("必须配置 SQLITE_SOURCE_PATH 和 DATABASE_URL");
        }
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"读取 SQLite 源文件失败：{sourcePath}");
        }

        string fullSourcePath = Path.GetFullPath(sourcePath);
        await using ToolDatabase targetTools = ToolDatabase.Open("postgres", targetDsn);
        ToolEnvironment.RequirePostgres(targetTools.Database, "migrate-sqlite-postgres");
        await using ToolDatabase sourceTools = ToolDatabase.Open(
            "sqlite",
            $"Data Source={fullSourcePath};Mode=ReadOnly;Cache=Shared;Foreign Keys=True;Default Timeout=5",
            Path.GetDirectoryName(fullSourcePath) ?? ".");

        await VerifySQLiteAsync(sourceTools.Database, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TablePlan> tables = TablePlans();
        await VerifySourceTablesAsync(sourceTools.Database, tables, cancellationToken).ConfigureAwait(false);

        long targetTableCount = await PublicTableCountAsync(targetTools.Database, cancellationToken).ConfigureAwait(false);
        long expectedTableCount = tables.Count + 1;
        bool copyRows = targetTableCount == 0;
        if (!copyRows && targetTableCount != expectedTableCount)
        {
            throw new InvalidOperationException(
                $"PostgreSQL public schema 已有 {targetTableCount} 张表，期望 {expectedTableCount} 张，拒绝覆盖或补写");
        }

        if (copyRows)
        {
            await ToolEnvironment.EnsureMigratedAsync(targetTools.Database, cancellationToken).ConfigureAwait(false);
        }

        int totalRows = 0;
        foreach (TablePlan table in tables)
        {
            int rows = await CopyOrVerifyTableAsync(
                sourceTools.Database,
                targetTools.Database,
                table,
                copyRows,
                cancellationToken).ConfigureAwait(false);
            totalRows += rows;
            Console.WriteLine($"已迁移并核对 {table.Map.Table}：{rows} 行");
        }

        Console.WriteLine(copyRows
            ? $"全量迁移核对完成：{tables.Count} 张表，{totalRows} 行"
            : $"目标库已有完整迁移结果，未重复写入：{tables.Count} 张表，{totalRows} 行");
        return 0;
    }

    private static IReadOnlyList<TablePlan> TablePlans() => EntityMetadata.KnownTypes
        .Select(type => (Type: type, Map: EntityMetadata.For(type)))
        .Where(item => item.Map.Table != "schema_migrations" && item.Map.KeyProperty is not null)
        .GroupBy(item => item.Map.Table, StringComparer.Ordinal)
        .Select(group =>
        {
            (Type entityType, EntityMap map) = group.First();
            return new TablePlan(entityType, map);
        })
        .OrderBy(item => item.Map.Table, StringComparer.Ordinal)
        .ToArray();

    private static async Task VerifySQLiteAsync(CanvasDatabase database, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"SQLite 完整性检查失败：quick_check 返回 {result}");
        }
    }

    private static async Task VerifySourceTablesAsync(
        CanvasDatabase database,
        IReadOnlyList<TablePlan> tables,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (TablePlan table in tables)
        {
            long exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
                new { name = table.Map.Table },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (exists == 0)
            {
                throw new InvalidOperationException($"SQLite 源库缺少表 {table.Map.Table}");
            }
        }
    }

    private static async Task<long> PublicTableCountAsync(CanvasDatabase database, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public'",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<int> CopyOrVerifyTableAsync(
        CanvasDatabase sourceDatabase,
        CanvasDatabase targetDatabase,
        TablePlan table,
        bool copyRows,
        CancellationToken cancellationToken)
    {
        (string sourceFingerprint, int sourceRows) = copyRows
            ? await targetDatabase.InTransactionAsync(
                async (targetConnection, transaction) => await CopyTableAsync(
                    sourceDatabase, targetConnection, transaction, table, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false)
            : await FingerprintTableAsync(sourceDatabase, table, cancellationToken).ConfigureAwait(false);

        (string targetFingerprint, int targetRows) = await FingerprintTableAsync(
            targetDatabase, table, cancellationToken).ConfigureAwait(false);
        if (sourceRows != targetRows || !string.Equals(sourceFingerprint, targetFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"表 {table.Map.Table} 源数据与目标数据逐字段核对不一致：source={sourceRows}/{sourceFingerprint} target={targetRows}/{targetFingerprint}");
        }
        return sourceRows;
    }

    private static async Task<(string Fingerprint, int Rows)> CopyTableAsync(
        CanvasDatabase sourceDatabase,
        DbConnection targetConnection,
        DbTransaction targetTransaction,
        TablePlan table,
        CancellationToken cancellationToken)
    {
        await using DbConnection sourceConnection = await sourceDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand sourceCommand = sourceConnection.CreateCommand();
        sourceCommand.CommandText = SelectSql(table.Map);
        await using DbDataReader reader = await sourceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        string insertSql = InsertSql(table.Map);
        List<DynamicParameters> batch = new(BatchSize);
        RowFingerprint fingerprint = new(table);
        int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            object?[] values = ReadValues(reader);
            fingerprint.Append(values);
            DynamicParameters parameters = new();
            for (int index = 0; index < values.Length; index++)
            {
                parameters.Add("p" + index, ConvertForTarget(values[index], table.PropertyTypes[index]) ?? DBNull.Value);
            }
            batch.Add(parameters);
            rows++;
            if (batch.Count >= BatchSize)
            {
                await targetConnection.ExecuteAsync(new CommandDefinition(
                    insertSql, batch, targetTransaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await targetConnection.ExecuteAsync(new CommandDefinition(
                insertSql, batch, targetTransaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        return (fingerprint.Complete(), rows);
    }

    private static async Task<(string Fingerprint, int Rows)> FingerprintTableAsync(
        CanvasDatabase database,
        TablePlan table,
        CancellationToken cancellationToken)
    {
        await using DbConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = SelectSql(table.Map);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        RowFingerprint fingerprint = new(table);
        int rows = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fingerprint.Append(ReadValues(reader));
            rows++;
        }
        return (fingerprint.Complete(), rows);
    }

    private static string SelectSql(EntityMap map)
    {
        string columns = string.Join(", ", map.Columns.Select(column => Quote(column.Column)));
        string key = Quote(map.ColumnOf(map.KeyProperty!)!);
        return $"SELECT {columns} FROM {Quote(map.Table)} ORDER BY {key}";
    }

    private static string InsertSql(EntityMap map)
    {
        string columns = string.Join(", ", map.Columns.Select(column => Quote(column.Column)));
        string values = string.Join(", ", map.Columns.Select((_, index) => "@p" + index));
        return $"INSERT INTO {Quote(map.Table)} ({columns}) VALUES ({values})";
    }

    private static object?[] ReadValues(DbDataReader reader)
    {
        object?[] values = new object?[reader.FieldCount];
        for (int index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }
        return values;
    }

    private static object? ConvertForTarget(object? value, Type propertyType)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }
        Type targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (targetType == typeof(bool))
        {
            if (value is bool boolean)
            {
                return boolean;
            }
            if (value is string text && bool.TryParse(text, out bool parsed))
            {
                return parsed;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }
        if (targetType == typeof(DateTime) && value is string dateText)
        {
            return DateTime.Parse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        return value;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private sealed class TablePlan
    {
        public TablePlan(Type entityType, EntityMap map)
        {
            EntityType = entityType;
            Map = map;
            PropertyTypes = map.Columns
                .Select(column => entityType.GetProperty(column.Property)?.PropertyType ?? typeof(object))
                .ToArray();
        }

        public Type EntityType { get; }
        public EntityMap Map { get; }
        public IReadOnlyList<Type> PropertyTypes { get; }
    }

    private sealed class RowFingerprint
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly TablePlan _table;

        public RowFingerprint(TablePlan table) => _table = table;

        public void Append(IReadOnlyList<object?> values)
        {
            for (int index = 0; index < values.Count; index++)
            {
                string normalized = Normalize(values[index], _table.PropertyTypes[index]);
                byte[] bytes = Encoding.UTF8.GetBytes(normalized);
                _hash.AppendData(BitConverter.GetBytes(bytes.Length));
                _hash.AppendData(bytes);
            }
            _hash.AppendData([0xff]);
        }

        public string Complete() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        private static string Normalize(object? value, Type propertyType)
        {
            if (value is null || value is DBNull)
            {
                return "<null>";
            }
            Type targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (targetType == typeof(bool))
            {
                bool boolean = value is bool flag
                    ? flag
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                return boolean ? "true" : "false";
            }
            if (value is byte[] bytes)
            {
                return "bytes:" + Convert.ToBase64String(bytes);
            }
            if (targetType == typeof(DateTime))
            {
                DateTime date = value is DateTime dateTime
                    ? dateTime
                    : DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                date = date.ToUniversalTime();
                date = new DateTime(date.Ticks - (date.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
                return "time:" + date.ToString("O", CultureInfo.InvariantCulture);
            }
            if (value is IFormattable formattable)
            {
                return value.GetType().FullName + ":" + formattable.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.GetType().FullName + ":" + value;
        }
    }
}
