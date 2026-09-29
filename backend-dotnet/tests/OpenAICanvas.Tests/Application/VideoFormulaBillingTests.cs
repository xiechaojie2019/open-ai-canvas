#nullable enable
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 视频 Token 公式快照结算（Go v25）：公式预估、下单落 video_formula_tokens、
/// 结算 usage_source 回退与快照计费。
/// 对应 Go: <c>video_token_billing.go</c> 与 <c>repository/finance.go</c> 的 tokenSettlementUsage。
/// </summary>
public sealed class VideoFormulaBillingTests : IDisposable
{
    private readonly string _databasePath;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;

    public VideoFormulaBillingTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-video-billing-{Guid.NewGuid():N}.db");
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

    private static Dictionary<string, JsonElement> VideoConfig(string seconds = "5", string quality = "720p") =>
        new(StringComparer.Ordinal)
        {
            ["videoSeconds"] = JsonSerializer.SerializeToElement(seconds),
            ["vquality"] = JsonSerializer.SerializeToElement(quality),
            ["size"] = JsonSerializer.SerializeToElement("16:9"),
        };

    [Fact]
    public void 公式快照与预留余量()
    {
        BillingCalc.TokenBillingEstimate estimate = BillingCalc.EstimateTaskBillingTokens(
            BillingCalc.QuoteInput("video", "doubao-seedance", VideoConfig(), new Dictionary<string, long> { ["video"] = 1 }),
            "video");
        Assert.NotNull(estimate.Video);
        // 预留 = 公式 × 1.1（向上取整），公式量是结算依据。
        long formula = estimate.Video.FormulaTokens;
        Assert.Equal((formula * 110 + 99) / 100, estimate.OutputTokens);
        Assert.Equal((formula * 110 + 99) / 100, estimate.Video.ReservedTokens);
        Assert.True(formula > 0);
        Assert.Equal(24, estimate.Video.FramesPerSecond);
        Assert.Equal(10, estimate.Video.ReservationMarginPercent);
        // 报价路径参考视频缺时长 → 按 15 秒上限预留。
        Assert.True(estimate.Video.ReferenceDurationEstimated);

        // 无时长 → 无估算。
        BillingCalc.TokenBillingEstimate empty = BillingCalc.EstimateTaskBillingTokens(
            BillingCalc.QuoteInput("video", "doubao-seedance", new Dictionary<string, JsonElement>(StringComparer.Ordinal), null),
            "video");
        Assert.True(empty.OutputTokens <= 0);
        Assert.Null(empty.Video);
    }

    [Fact]
    public async Task 结算_无usage回退公式快照并记录来源()
    {
        await MigrateAsync();
        await SeedOrderAndAccountAsync(videoFormulaTokens: 1_000_000);
        await _repository.SettleBillingOrderAsync("order-1", "");

        BillingOrder order = (await _repository.BillingOrderAsync("order-1"))!;
        Assert.Equal(BillingStatus.BillingStatusSettled, order.Status);
        // 快照计费：output = formula × 单价 × 倍率。
        long expectedAmount = (1_000_000L * order.OutputTokenPriceMicrocredits * order.MultiplierBasisPoints + 9_999_999_999) / 10_000_000_000;
        Assert.Equal(expectedAmount, order.ActualAmountMicrocredits);
        Assert.Equal("video_formula", order.UsageSource);
        Assert.False(order.UsageAvailable);
        Assert.Equal(1_000_000, order.OutputTokens);
        Assert.Equal(0, order.InputTokens);

        // 流水注明快照结算。
        List<CreditLedgerEntry> ledger = await LedgerAsync("order-1");
        Assert.Contains(ledger, e => (e.Note ?? "").Contains("公式快照", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 结算_有usage按供应商并清零输入()
    {
        await MigrateAsync();
        await SeedOrderAndAccountAsync(videoFormulaTokens: 1_000_000);
        // 上游返回成功 usage（视频只有 output 有意义）。
        await _repository.CreateAsync(new ApiCallLog
        {
            ID = IdGenerator.NewId(),
            UserID = "user-a",
            BillingOrderID = "order-1",
            Source = "backend-task",
            Capability = "video",
            Operation = "canvas_video",
            RequestKind = "submit",
            Method = "POST",
            Path = "test",
            Status = ApiCallStatus.ApiCallStatusSucceeded,
            Billable = true,
            InputTokens = 123,
            OutputTokens = 800_000,
            CachedTokens = 5,
            UsageAvailable = true,
            CreatedAt = DateTime.UtcNow,
        });

        await _repository.SettleBillingOrderAsync("order-1", "prov-1");

        BillingOrder order = (await _repository.BillingOrderAsync("order-1"))!;
        Assert.Equal("provider", order.UsageSource);
        Assert.True(order.UsageAvailable);
        Assert.Equal(800_000, order.OutputTokens);
        // 视频只按输出计价：输入/缓存清零。
        Assert.Equal(0, order.InputTokens);
        Assert.Equal(0, order.CachedTokens);
        long expectedAmount = (800_000L * order.OutputTokenPriceMicrocredits * order.MultiplierBasisPoints + 9_999_999_999) / 10_000_000_000;
        Assert.Equal(expectedAmount, order.ActualAmountMicrocredits);
    }

    [Fact]
    public async Task 结算_无usage且无快照时报不可用()
    {
        await MigrateAsync();
        await SeedOrderAndAccountAsync(videoFormulaTokens: 0);
        await Assert.ThrowsAnyAsync<Exception>(() => _repository.SettleBillingOrderAsync("order-1", ""));
    }

    [Fact]
    public async Task 非视频缺usage仍拒绝结算()
    {
        await MigrateAsync();
        await SeedOrderAndAccountAsync(videoFormulaTokens: 1_000_000, capability: "text");
        await Assert.ThrowsAnyAsync<Exception>(() => _repository.SettleBillingOrderAsync("order-1", ""));
    }

    private async Task SeedOrderAndAccountAsync(long videoFormulaTokens, string capability = "video")
    {
        await _repository.CreateAsync(new User
        {
            ID = "user-a",
            Username = "alice",
            Role = "user",
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _repository.CreateAsync(new CreditAccount
        {
            UserID = "user-a",
            AvailableMicrocredits = 100_000_000,
            ReservedMicrocredits = 10_000_000,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _repository.CreateAsync(new BillingOrder
        {
            ID = "order-1",
            UserID = "user-a",
            TaskID = "task-1",
            ChannelID = "chan-1",
            ChannelModelID = "cm-1",
            Model = "doubao-seedance",
            Capability = capability,
            Scene = "canvas_video",
            BillingMode = "token",
            PriceVersion = 1,
            MultiplierBasisPoints = 10_000,
            Quantity = 1_100_000,
            AmountMicrocredits = 10_000_000,
            ReservedAmountMicrocredits = 10_000_000,
            InputTokenPriceMicrocredits = 2_000,
            OutputTokenPriceMicrocredits = 20_000,
            CachedTokenPriceMicrocredits = 200,
            VideoFormulaTokens = videoFormulaTokens,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = BillingStatus.BillingStatusRunning,
        });
    }

    private async Task<List<CreditLedgerEntry>> LedgerAsync(string orderID)
    {
        await using SqliteConnection connection = (SqliteConnection)_database.CreateConnection();
        await connection.OpenAsync();
        return (await connection.QueryAsync<CreditLedgerEntry>(
            "SELECT * FROM credit_ledger_entries WHERE billing_order_id = @orderID",
            new { orderID })).AsList();
    }
}
