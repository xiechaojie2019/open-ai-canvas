using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;

namespace OpenAICanvas.Tools.Commands;

/// <summary>一次性命令共用的数据库装配和安全前置检查。</summary>
internal sealed class ToolDatabase : IAsyncDisposable
{
    private ToolDatabase(CanvasDatabase database, Repository repository, CanvasService service, string dataDir)
    {
        Database = database;
        Repository = repository;
        Service = service;
        DataDir = dataDir;
    }

    public CanvasDatabase Database { get; }
    public Repository Repository { get; }
    public CanvasService Service { get; }
    public string DataDir { get; }

    public static ToolDatabase Open(string? driver = null, string? dsn = null, string? dataDir = null)
    {
        string resolvedDriver = ToolEnvironment.Read(driver, "CANVAS_DATABASE_DRIVER", "sqlite");
        string resolvedDataDir = ToolEnvironment.Read(dataDir, "CANVAS_BACKEND_DATA_DIR", "data");
        string? resolvedDsn = dsn ?? Environment.GetEnvironmentVariable("DATABASE_URL");
        CanvasDatabase database = new(new CanvasDatabaseOptions
        {
            Driver = resolvedDriver,
            Dsn = resolvedDsn,
            DataDir = resolvedDataDir,
        });
        Repository repository = new(database);
        CanvasService service = new(repository, dataDir: resolvedDataDir);
        return new ToolDatabase(database, repository, service, resolvedDataDir);
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

internal static class ToolEnvironment
{
    public static string Read(string? explicitValue, string key, string fallback)
    {
        string value = explicitValue ?? Environment.GetEnvironmentVariable(key) ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(value => string.Equals(value, flag, StringComparison.Ordinal));

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static async Task EnsureMigratedAsync(CanvasDatabase database, CancellationToken cancellationToken = default)
    {
        await new SchemaMigrator(database).MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void RequirePostgres(CanvasDatabase database, string command)
    {
        if (!database.IsPostgres)
        {
            throw new InvalidOperationException($"{command} 只允许连接 PostgreSQL");
        }
    }

    public static User FindMigrationAdmin(IReadOnlyList<User> users)
    {
        User? admin = users
            .Where(user => user.Role == UserRole.UserRoleAdmin)
            .OrderBy(user => user.CreatedAt)
            .FirstOrDefault();
        return admin ?? throw new InvalidOperationException("需要现有管理员作为迁移审计主体");
    }

    public static void PrintJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
