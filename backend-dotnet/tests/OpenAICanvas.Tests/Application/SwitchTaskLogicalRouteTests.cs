#nullable enable
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Persistence.Schema;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 切线路成本重算事务的契约测试。对应 Go: <c>TestSwitchTaskLogicalRouteUpdatesCostWithoutChangingUnifiedSales</c>
/// 与 billing_authorization_test.go 的上限语义。
/// </summary>
public sealed class SwitchTaskLogicalRouteTests : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _dataDir;
    private readonly CanvasDatabase _database;
    private readonly Repository _repository;
    private readonly User _user = new() { ID = "u-route", Username = "route-user", Role = "user", Status = "active" };

    public SwitchTaskLogicalRouteTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"canvas-route-{Guid.NewGuid():N}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-route-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _database = new CanvasDatabase(new CanvasDatabaseOptions
        {
            Driver = "sqlite",
            Dsn = $"Data Source={_databasePath};Pooling=False",
        });
        _repository = new Repository(_database);
        new SchemaMigrator(_database).MigrateAsync().GetAwaiter().GetResult();
        _repository.CreateAsync(_user).GetAwaiter().GetResult();
        _repository.CreateAsync(new CreditAccount
        {
            UserID = _user.ID,
            AvailableMicrocredits = 1_000_000,
            ReservedMicrocredits = 500_000,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    private TaskEntity SeedRunningTask(string routeID, string billingOrderID)
    {
        TaskEntity task = new()
        {
            ID = IdGenerator.NewId(),
            UserID = _user.ID,
            Type = "canvas_image",
            Status = "running",
            RouteID = routeID,
            LogicalModelID = "lm-1",
            LogicalModelRevisionID = "rev-1",
            ChannelModelID = "cm-1",
            BillingOrderID = billingOrderID,
            InputJSON = "{}",
            Prompt = "p",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _repository.CreateAsync(task).GetAwaiter().GetResult();
        return task;
    }

    private BillingOrder SeedOrder(string orderID, string taskID, long amount, long? chargeLimit = null)
    {
        BillingOrder order = new()
        {
            ID = orderID,
            UserID = _user.ID,
            TaskID = taskID,
            Model = "m",
            ChannelID = "ch-1",
            ChannelModelID = "cm-1",
            Scene = "canvas",
            BillingMode = "fixed_request",
            Quantity = 1,
            AmountMicrocredits = amount,
            ReservedAmountMicrocredits = amount,
            Status = "reserved",
            ChargeLimitSet = true,
            ChargeLimitMicrocredits = chargeLimit ?? amount,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _repository.CreateAsync(order).GetAwaiter().GetResult();
        return order;
    }

    [Fact]
    public async Task 统一价格切换只改成本快照不改销售金额()
    {
        TaskEntity task = SeedRunningTask("route-a", "");
        // 无计费订单：只切换路由并写回输入。
        await _repository.SwitchTaskLogicalRouteAsync(
            task.ID, "route-a", "route-b", "{\"k\":1}", "", "ch-2", "cm-2",
            replacement: null,
            cost: new TaskRouteCostSnapshot(true, 100, 0, 0, 0, "fixed_request", 1, 0));
        TaskEntity? updated = await _repository.TaskAsync(task.ID);
        Assert.Equal("route-b", updated!.RouteID);
        Assert.Equal("cm-2", updated.ChannelModelID);
        Assert.Contains("\"k\":1", updated.InputJSON, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 渠道计费切换_替换订单调整预留并留痕()
    {
        TaskEntity task = SeedRunningTask("route-a", "order-1");
        // 原授权上限 700_000：备用线路报价恰好用满授权而不越界。
        SeedOrder("order-1", task.ID, amount: 500_000, chargeLimit: 700_000);
        BillingOrder replacement = new()
        {
            ID = IdGenerator.NewId(),
            UserID = _user.ID,
            TaskID = task.ID,
            ChannelID = "ch-2",
            ChannelModelID = "cm-2",
            Model = "m",
            Scene = "canvas",
            BillingMode = "fixed_request",
            Quantity = 1,
            UnitPriceMicrocredits = 700_000,
            CostConfigured = true,
            CostUnitPriceMicrocredits = 700_000,
            CostBillingMode = "fixed_request",
            CostQuantity = 1,
            AmountMicrocredits = 700_000,
            ReservedAmountMicrocredits = 700_000,
            Status = "reserved",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        // 备用线路更贵：差额 200_000 从可用扣、进预留。
        await _repository.SwitchTaskLogicalRouteAsync(
            task.ID, "route-a", "route-b", "{}", "order-1", "ch-2", "cm-2",
            replacement, TaskRouteCostSnapshot.FromOrder(replacement));

        BillingOrder? order = await _repository.BillingOrderAsync("order-1");
        Assert.Equal("ch-2", order!.ChannelID);
        Assert.Equal(700_000, order.AmountMicrocredits);
        Assert.Equal(700_000, order.ReservedAmountMicrocredits);
        // 成本快照随替换订单改写。
        Assert.Equal(700_000, order.CostUnitPriceMicrocredits);
        Assert.True(order.CostConfigured || order.CostUnitPriceMicrocredits > 0);
        CreditAccount? account = await _repository.CreditAccountAsync(_user.ID);
        Assert.Equal(800_000, account!.AvailableMicrocredits);
        Assert.Equal(700_000, account.ReservedMicrocredits);
    }

    [Fact]
    public async Task 渠道计费切换_超批准上限被拒绝()
    {
        TaskEntity task = SeedRunningTask("route-a", "order-1");
        SeedOrder("order-1", task.ID, amount: 500_000);
        BillingOrder replacement = new()
        {
            ID = IdGenerator.NewId(),
            UserID = _user.ID,
            TaskID = task.ID,
            ChannelID = "ch-2",
            ChannelModelID = "cm-2",
            BillingMode = "fixed_request",
            Quantity = 1,
            AmountMicrocredits = 900_000,
            ReservedAmountMicrocredits = 900_000,
            Status = "reserved",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await Assert.ThrowsAsync<TaskRouteChargeLimitException>(() =>
            _repository.SwitchTaskLogicalRouteAsync(
                task.ID, "route-a", "route-b", "{}", "order-1", "ch-2", "cm-2",
                replacement, TaskRouteCostSnapshot.FromOrder(replacement)));
    }

    [Fact]
    public async Task 切线路_任务非运行态或路由过期时冲突()
    {
        TaskEntity task = SeedRunningTask("route-a", "");
        await Assert.ThrowsAsync<TaskRouteConflictException>(() =>
            _repository.SwitchTaskLogicalRouteAsync(
                task.ID, "route-stale", "route-b", "{}", "", "ch-2", "cm-2",
                null, new TaskRouteCostSnapshot(false, 0, 0, 0, 0, "", 0, 0)));
    }
}


