#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Application;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Persistence;

/// <summary>
/// 账单巡检查询验证：只读统计长期未闭合订单的真实 SQLite 路径。
/// </summary>
public class BillingReviewRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public BillingReviewRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-billing-review-{Guid.NewGuid():N}.db");
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

    private async Task<Repository> MigratedAsync()
    {
        SchemaMigrator migrator = new(_database);
        await migrator.MigrateAsync();
        return _repository;
    }

    private static async Task SeedOrderAsync(
        Repository repository, string id, string status, DateTime updatedAt, DateTime createdAt)
    {
        await repository.CreateAsync(new BillingOrder
        {
            ID = id,
            UserID = "USR_REVIEW",
            // (user_id, idempotency_key) 有唯一索引，同一用户下不能重复。
            IdempotencyKey = id,
            Model = "test-model",
            Scene = "test",
            Capability = "text",
            BillingMode = "unit",
            AmountMicrocredits = 1_000_000,
            ReservedAmountMicrocredits = 1_000_000,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        });
    }

    [Fact]
    public async Task 巡检_只统计超龄未闭合订单()
    {
        Repository repository = await MigratedAsync();
        await repository.CreateAsync(new User
        {
            ID = "USR_REVIEW",
            Username = "review",
            Email = "review@example.com",
            DisplayName = "review",
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            PasswordHash = "$2a$10$abcdefghijklmnopqrstuv",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        DateTime now = DateTime.UtcNow;
        DateTime stale = now - TimeSpan.FromHours(2);
        // 超龄未闭合三类各一单，外加已闭合、新近未闭合、超龄已结算各一单不应计入。
        await SeedOrderAsync(repository, "ORD_STALE_RES", BillingStatus.BillingStatusReserved, stale, stale);
        await SeedOrderAsync(repository, "ORD_STALE_RUN", BillingStatus.BillingStatusRunning, stale, stale);
        await SeedOrderAsync(repository, "ORD_STALE_UNC", BillingStatus.BillingStatusUncertain, stale, stale);
        await SeedOrderAsync(repository, "ORD_FRESH_RUN", BillingStatus.BillingStatusRunning, now, now);
        await SeedOrderAsync(repository, "ORD_STALE_SETTLED", BillingStatus.BillingStatusSettled, stale, stale);

        Repository.BillingReviewStats stats = await repository.StaleBillingReviewStatsAsync(
            now, TaskBillingReviewService.StaleAfter);

        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Reserved);
        Assert.Equal(1, stats.Running);
        Assert.Equal(1, stats.Uncertain);
        Assert.NotNull(stats.Oldest);
    }

    [Fact]
    public async Task 巡检_无未闭合订单时返回零值()
    {
        Repository repository = await MigratedAsync();

        Repository.BillingReviewStats stats = await repository.StaleBillingReviewStatsAsync(
            DateTime.UtcNow, TaskBillingReviewService.StaleAfter);

        Assert.Equal(0, stats.Total);
        Assert.Null(stats.Oldest);
    }
}
