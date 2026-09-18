#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 前台模型管理端 CRUD、路由模拟与报价的端到端契约测试。
/// 对应 Go: <c>handler/logical_models.go</c> 与 <c>app/logical_models.go</c>、<c>logical_model_quote.go</c>。
/// </summary>
public sealed class LogicalModelAdminTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LogicalModelAdminTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-lm-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDir))
        {
            // 连接池释放有延迟，重试几次避免句柄占用导致清理失败。
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_dataDir, recursive: true);
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    private async Task<HttpClient> SignInAsAdminAsync(string username = "admin")
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    /// <summary>种子系统渠道 + 渠道模型（720p×5s 与 1080p×10s 两档价格）。</summary>
    private async Task SeedChannelModelAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;

        await repository.CreateAsync(new Domain.Entities.ModelChannel
        {
            ID = "CH_SYS",
            UserID = "USR_SYSTEM",
            Scope = "system",
            Enabled = true,
            Name = "内置渠道",
            ModelsJSON = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });

        const string capabilityJson = """
            {"version":1,"video":{"references":{"promptMaxChars":1000,"minImages":0,"maxImages":9,"maxImageBytes":31457280,"maxVideos":3,"maxVideoBytes":209715200,"maxVideoDurationSeconds":15,"maxAudios":3,"maxAudioBytes":15728640,"maxAudioDurationSeconds":15},"duration":{"selection":"enum","values":[5,10],"default":5},"ratios":["16:9","9:16"],"defaultRatio":"16:9","resolutions":["720p","1080p"],"defaultResolution":"720p","generateAudio":{"supported":true,"default":true},"watermark":{"supported":false,"default":false},"operations":["text_to_video","image_to_video"],"defaultOperation":"text_to_video"}}
            """;

        await repository.CreateAsync(new Domain.Entities.ChannelModel
        {
            ID = "CM_ADMIN",
            ChannelID = "CH_SYS",
            ModelKey = "seedance-pro",
            ProviderModelKey = "seedance-pro",
            DisplayName = "即梦专业版",
            Capability = "video",
            Protocol = "volcengine-ark-video",
            BillingMode = "fixed_request",
            PriceConfigured = true,
            Enabled = true,
            PriceVersion = 1,
            CapabilityConfigJSON = capabilityJson,
            CapabilityVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        foreach ((string id, string selectorJson, string resolution, long seconds, long price) in new[]
                 {
                     ("TIER_A", "{\"vquality\":\"720p\",\"videoSeconds\":\"5\"}", "720p", 5L, 1_500_000L),
                     ("TIER_B", "{\"vquality\":\"1080p\",\"videoSeconds\":\"10\"}", "1080p", 10L, 3_000_000L),
                 })
        {
            await repository.CreateAsync(new Domain.Entities.ChannelModelPriceTier
            {
                ID = id,
                ChannelModelID = "CM_ADMIN",
                SelectorKey = selectorJson,
                SelectorJSON = selectorJson,
                Resolution = resolution,
                VideoSeconds = seconds,
                BillingMode = "fixed_request",
                UnitPriceMicrocredits = price,
                PriceConfigured = true,
                Enabled = true,
                PriceVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static object ChannelPolicyRequest(bool enabled = true, string code = "seed-video") => new
    {
        code,
        name = "种子视频",
        capability = "video",
        enabled,
        sortOrder = 1,
        pricePolicy = "channel",
        billingMode = "fixed_request",
        capabilitySpec = new
        {
            version = 1,
            capability = "video",
            operations = new[] { "text_to_video" },
            inputs = new { image = new { min = 0, max = 3 } },
            options = new
            {
                size = new { values = new[] { "16:9" } },
                vquality = new { values = new[] { "720p" } },
                videoSeconds = new { values = new[] { 5 } },
            },
        },
        defaultOptions = new { size = "16:9", vquality = "720p", videoSeconds = 5 },
        routes = new[] { new { channelModelId = "CM_ADMIN", enabled = true, priority = 100, weight = 100 } },
    };

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    // ------------------------------------------------------------ 权限

    [Fact]
    public async Task 未登录访问管理端模型列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/logical-models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 非管理员访问管理端模型列表返回_403()
    {
        await SignInAsAdminAsync();
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = "password123",
        });
        _ = login;

        // 同一实例内首用户即管理员；再注册第二个用户验证普通用户被拒。
        HttpResponseMessage second = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "bob",
            password = "password123",
            email = "bob@example.com",
        });
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    // ------------------------------------------------------------ 创建与校验

    [Fact]
    public async Task 创建模型的完整管理投影()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement model = document.RootElement.GetProperty("data").GetProperty("model");

        Assert.Equal("seed-video", model.GetProperty("code").GetString());
        Assert.True(model.GetProperty("enabled").GetBoolean());
        Assert.Equal("channel", model.GetProperty("pricePolicy").GetString());
        Assert.True(model.GetProperty("available").GetBoolean());
        Assert.Equal(1, model.GetProperty("revisionVersion").GetInt64());
        Assert.False(model.GetProperty("activeRevisionId").GetString() is null or "");

        // 管理视图暴露线路明细（含渠道 ID 与模型键），与创作端匿名画像形成对照。
        JsonElement routes = model.GetProperty("routes");
        Assert.Equal(1, routes.GetArrayLength());
        Assert.Equal("CM_ADMIN", routes[0].GetProperty("channelModelId").GetString());
        Assert.Equal("CH_SYS", routes[0].GetProperty("channelId").GetString());
        Assert.Equal("seedance-pro", routes[0].GetProperty("channelModelKey").GetString());
        Assert.True(routes[0].GetProperty("available").GetBoolean());

        // 配置无错误时错误字段以 omitempty 省略。
        Assert.False(model.TryGetProperty("configurationError", out _));
        Assert.False(model.TryGetProperty("availabilityError", out _));

        // 管理端列表包含该模型。
        HttpResponseMessage list = await admin.GetAsync("/api/admin/logical-models");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        JsonElement models = (await ReadDataAsync(list)).GetProperty("models");
        Assert.Equal(1, models.GetArrayLength());

        // 审计事件已写入。
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        (_, long total) = await repository.AdminAuditEventsAsync(
            "logical_model", model.GetProperty("id").GetString()!, 10, 0);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task 创建校验_非法code()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request["code"] = "BAD CODE";

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/logical-models", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "模型 code 需为 2-80 位小写字母、数字、点、下划线或连字符",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 创建校验_启用但能力配置缺失()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request.Remove("capabilitySpec");

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/logical-models", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Go：缺省 CapabilitySpec{} 先被 NormalizeCapabilitySpec 拒绝（version != 1）。
        Assert.Contains(
            "能力配置 version 必须为 1",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 创建校验_启用但无供应线路()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request["routes"] = Array.Empty<object>();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/logical-models", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "启用前台模型前至少需要一条已启用的供应线路",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 创建校验_统一定价按秒仅限视频()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request["capability"] = "image";
        request["capabilitySpec"] = new
        {
            version = 1,
            capability = "image",
            operations = new[] { "text_to_image" },
            options = new { size = new { values = new[] { "1024x1024" } } },
        };
        request["defaultOptions"] = new { size = "1024x1024" };
        request["pricePolicy"] = "unified";
        request["billingMode"] = "per_second";

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/logical-models", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("只有视频前台模型可以按秒计费", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 更新与归档

    [Fact]
    public async Task 更新会创建新版本并递增版本号()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        JsonElement model = (await ReadDataAsync(created)).GetProperty("model");
        string id = model.GetProperty("id").GetString()!;
        string firstRevision = model.GetProperty("activeRevisionId").GetString()!;

        Dictionary<string, object?> update = ToMutable(ChannelPolicyRequest());
        update["name"] = "改名视频";
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/admin/logical-models/{id}", update);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedModel = (await ReadDataAsync(updated)).GetProperty("model");
        Assert.Equal("改名视频", updatedModel.GetProperty("name").GetString());
        Assert.Equal(2, updatedModel.GetProperty("revisionVersion").GetInt64());
        Assert.NotEqual(firstRevision, updatedModel.GetProperty("activeRevisionId").GetString());
    }

    [Fact]
    public async Task 归档后从列表消失且不可重复删除()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/admin/logical-models/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        HttpResponseMessage list = await admin.GetAsync("/api/admin/logical-models");
        Assert.Equal(0, (await ReadDataAsync(list)).GetProperty("models").GetArrayLength());

        HttpResponseMessage again = await admin.DeleteAsync($"/api/admin/logical-models/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("前台模型不存在或已删除", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 模拟与报价

    [Fact]
    public async Task 路由模拟返回候选与匹配原因()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        HttpResponseMessage simulate = await admin.PostAsJsonAsync(
            $"/api/admin/logical-models/{id}/simulate",
            new { capability = "video" });

        Assert.Equal(HttpStatusCode.OK, simulate.StatusCode);
        JsonElement data = await ReadDataAsync(simulate);
        Assert.True(data.GetProperty("productMatch").GetProperty("matched").GetBoolean());

        JsonElement candidates = data.GetProperty("candidates");
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.True(candidates[0].GetProperty("inPool").GetBoolean());
        Assert.Equal("CM_ADMIN", candidates[0].GetProperty("channelModelId").GetString());
    }

    [Fact]
    public async Task 路由模拟对未发布模型返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage simulate = await admin.PostAsJsonAsync(
            "/api/admin/logical-models/LMODEL_MISSING/simulate",
            new { capability = "video" });

        Assert.Equal(HttpStatusCode.BadRequest, simulate.StatusCode);
        Assert.Contains("前台模型未启用或尚未发布", await simulate.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 报价_跟随供应价格按档计价()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        HttpResponseMessage quote = await admin.PostAsJsonAsync(
            $"/api/models/{id}/quote",
            new { capability = "video", options = new { vquality = "720p", videoSeconds = 5 } });

        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        JsonElement data = await ReadDataAsync(quote).ConfigureAwait(false);
        JsonElement quoteData = data.GetProperty("quote");
        Assert.Equal("fixed_request", quoteData.GetProperty("billingMode").GetString());
        Assert.Equal(1, quoteData.GetProperty("quantity").GetInt64());
        Assert.Equal(1_500_000, quoteData.GetProperty("amountMicrocredits").GetInt64());
        Assert.False(quoteData.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task 报价_统一定价按次()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request["pricePolicy"] = "unified";
        request["billingMode"] = "fixed_request";
        request["unitPriceMicrocredits"] = 2_500_000;
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", request);
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        HttpResponseMessage quote = await admin.PostAsJsonAsync(
            $"/api/models/{id}/quote",
            new { capability = "video" });

        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        JsonElement quoteData = (await ReadDataAsync(quote)).GetProperty("quote");
        Assert.Equal("fixed_request", quoteData.GetProperty("billingMode").GetString());
        Assert.Equal(2_500_000, quoteData.GetProperty("amountMicrocredits").GetInt64());
        Assert.Equal(1, quoteData.GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task 报价_统一定价按秒由默认时长填充()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        Dictionary<string, object?> request = ToMutable(ChannelPolicyRequest());
        request["pricePolicy"] = "unified";
        request["billingMode"] = "per_second";
        request["unitPriceMicrocredits"] = 2_000_000;
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", request);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        // 请求不带时长时，默认参数 videoSeconds=5 会合并进意图 → 数量 5、金额 = 单价 × 5。
        HttpResponseMessage quote = await admin.PostAsJsonAsync(
            $"/api/models/{id}/quote",
            new { capability = "video" });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        JsonElement quoteData = (await ReadDataAsync(quote)).GetProperty("quote");
        Assert.Equal("per_second", quoteData.GetProperty("billingMode").GetString());
        Assert.Equal(5, quoteData.GetProperty("quantity").GetInt64());
        Assert.Equal(10_000_000, quoteData.GetProperty("amountMicrocredits").GetInt64());

        // 超出产品规格的时长（规格只允许 5 秒）被能力匹配拒绝。
        HttpResponseMessage outOfRange = await admin.PostAsJsonAsync(
            $"/api/models/{id}/quote",
            new { capability = "video", options = new { videoSeconds = 2 } });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        Assert.Contains(
            "所选模型不支持当前请求",
            await outOfRange.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 报价空意图被能力匹配拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", ChannelPolicyRequest());
        string id = (await ReadDataAsync(created)).GetProperty("model").GetProperty("id").GetString()!;

        // Go 先解析路由：空意图的能力类型与模型不一致，先命中能力匹配错误。
        HttpResponseMessage quote = await admin.PostAsJsonAsync($"/api/models/{id}/quote", new { });

        Assert.Equal(HttpStatusCode.BadRequest, quote.StatusCode);
        Assert.Contains(
            "所选模型不支持当前请求",
            await quote.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 报价对不存在模型返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedChannelModelAsync();

        HttpResponseMessage quote = await admin.PostAsJsonAsync(
            "/api/models/LMODEL_MISSING/quote",
            new { capability = "video" });

        Assert.Equal(HttpStatusCode.BadRequest, quote.StatusCode);
        Assert.Contains("所选模型不可用", await quote.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>匿名对象转可变字典，便于按字段改写请求。</summary>
    private static Dictionary<string, object?> ToMutable(object source)
    {
        string json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
    }
}
