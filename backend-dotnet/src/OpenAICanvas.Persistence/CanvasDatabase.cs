using System.Data.Common;

namespace OpenAICanvas.Persistence;

/// <summary>支持的数据库类型。对应 Go <c>database.Open</c> 的 driver 分支。</summary>
public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

/// <summary>
/// 数据库连接配置。对应 Go: <c>database.Config</c>。
/// </summary>
public sealed class CanvasDatabaseOptions
{
    /// <summary>对应 <c>CANVAS_DATABASE_DRIVER</c>，默认 <c>sqlite</c>。</summary>
    public string Driver { get; init; } = "sqlite";

    /// <summary>对应 <c>DATABASE_URL</c>。</summary>
    public string? Dsn { get; init; }

    /// <summary>对应 <c>CANVAS_BACKEND_DATA_DIR</c>，默认 <c>data</c>。</summary>
    public string DataDir { get; init; } = "data";

    /// <summary>解析后的驱动类型。非法驱动直接抛错，与 Go 的 <c>不支持的数据库驱动</c> 一致。</summary>
    public DatabaseProvider ResolveProvider()
    {
        string driver = Driver.Trim().ToLowerInvariant();
        if (driver.Length == 0)
        {
            driver = "sqlite";
        }

        return driver switch
        {
            "sqlite" => DatabaseProvider.Sqlite,
            "postgres" or "postgresql" => DatabaseProvider.Postgres,
            _ => throw new InvalidOperationException($"不支持的数据库驱动：{Driver}"),
        };
    }

    /// <summary>计算实际连接串，默认值与 Go 完全一致。</summary>
    public string ResolveConnectionString()
    {
        DatabaseProvider provider = ResolveProvider();
        string dsn = (Dsn ?? string.Empty).Trim();

        if (provider == DatabaseProvider.Sqlite)
        {
            if (dsn.Length > 0)
            {
                return dsn;
            }

            Directory.CreateDirectory(DataDir);
            // 与 Go 相同的 pragma：忙等待、WAL、外键、同步级别。
            string path = Path.Combine(DataDir, "open_ai_canvas.db");
            return $"Data Source={path};Cache=Shared;Foreign Keys=True;Default Timeout=5";
        }

        if (dsn.Length == 0)
        {
            throw new InvalidOperationException("PostgreSQL 模式必须配置 DATABASE_URL");
        }

        return dsn;
    }

    /// <summary>连接池参数。对应 Go: <c>database.ConfigurePool</c>。</summary>
    public (int MaxOpen, int MaxIdle) PoolSize() =>
        ResolveProvider() == DatabaseProvider.Postgres ? (30, 10) : (8, 4);
}

/// <summary>
/// 连接工厂。每次取用新连接（由驱动自身的连接池管理），用完即释放。
/// </summary>
/// <remarks>
/// Dapper 不持有连接状态，所有仓储方法都通过 <see cref="CreateConnection"/> 取连接，
/// 与 Go 侧每次 <c>r.db</c> 操作由 GORM 内部借还连接的语义等价。
/// </remarks>
public sealed class CanvasDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly DatabaseProvider _provider;

    public CanvasDatabase(CanvasDatabaseOptions options)
    {
        _provider = options.ResolveProvider();
        _connectionString = options.ResolveConnectionString();
        (MaxOpenConnections, MaxIdleConnections) = options.PoolSize();
    }

    public DatabaseProvider Provider => _provider;

    public bool IsPostgres => _provider == DatabaseProvider.Postgres;

    public string ConnectionString => _connectionString;

    public int MaxOpenConnections { get; }

    public int MaxIdleConnections { get; }

    public SqlDialect Dialect => _provider == DatabaseProvider.Postgres
        ? SqlDialect.Postgres
        : SqlDialect.Sqlite;

    /// <summary>创建一个已打开的连接。调用方负责释放。</summary>
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        DbConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>创建连接但不打开，供 Dapper 自行管理。</summary>
    public DbConnection CreateConnection()
    {
        if (_provider == DatabaseProvider.Postgres)
        {
            return new Npgsql.NpgsqlConnection(_connectionString);
        }

        return new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
    }

    /// <summary>在事务中执行一段逻辑，对应 Go 的 <c>db.Transaction(func(tx) error)</c>。</summary>
    public async Task<T> InTransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T result = await action(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public Task InTransactionAsync(
        Func<DbConnection, DbTransaction, Task> action,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync<object?>(async (connection, transaction) =>
        {
            await action(connection, transaction).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
