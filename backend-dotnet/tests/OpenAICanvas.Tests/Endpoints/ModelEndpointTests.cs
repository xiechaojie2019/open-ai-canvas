#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 前台模型目录路由（<c>/models</c>、<c>/models/available</c>）、
/// 会话恢复（<c>/auth/session</c>）与系统渠道（<c>/channels/system</c>）
/// 在真实 SQLite 库上的端到端契约测试。
/// </summary>
public sealed class ModelEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ModelEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-models-{Guid.NewGuid():N}");
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

    private async Task<HttpClient> SignInAsAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    /// <summary>
    /// 种子一条完整的供应关系：系统渠道 → 渠道模型（视频 + 能力 JSON + 两个价格档）→
    /// 前台模型 → revision → 供应线路。
    /// </summary>
    private async Task SeedVideoLogicalModelAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        DateTime now = DateTime.UtcNow;

        await repository.CreateAsync(new ModelChannel
        {
            ID = "CH_SYS",
            UserID = "USR_SYSTEM",
            Scope = "system",
            Enabled = true,
            Name = "内置渠道",
            SortOrder = 0,
            APIFormat = "openai",
            ConcurrencyLimit = 4,
            ModelsJSON = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });

        const string capabilityJson = """
            {"version":1,"video":{"references":{"promptMaxChars":1000,"minImages":0,"maxImages":9,"maxImageBytes":31457280,"maxVideos":3,"maxVideoBytes":209715200,"maxVideoDurationSeconds":15,"maxAudios":3,"maxAudioBytes":15728640,"maxAudioDurationSeconds":15},"duration":{"selection":"enum","values":[5,10],"default":5},"ratios":["16:9","9:16"],"defaultRatio":"16:9","resolutions":["720p","1080p"],"defaultResolution":"720p","generateAudio":{"supported":true,"default":true},"watermark":{"supported":false,"default":false},"operations":["text_to_video","image_to_video"],"defaultOperation":"text_to_video"}}
            """;

        await repository.CreateAsync(new ChannelModel
        {
            ID = "CM_SEED",
            ChannelID = "CH_SYS",
            ModelKey = "seedance-lite",
            ProviderModelKey = "seedance-lite",
            DisplayName = "即梦极速版",
            SortOrder = 0,
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
                     ("TIER_A", "{\"operation\":\"text_to_video\",\"vquality\":\"720p\",\"videoSeconds\":\"5\"}", "720p", 5L, 1_500_000L),
                     ("TIER_B", "{\"vquality\":\"1080p\",\"videoSeconds\":\"10\"}", "1080p", 10L, 3_000_000L),
                 })
        {
            await repository.CreateAsync(new ChannelModelPriceTier
            {
                ID = id,
                ChannelModelID = "CM_SEED",
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

        await repository.CreateAsync(new LogicalModel
        {
            ID = "LMODEL_SEED",
            Code = "seedance-video",
            Name = "即梦视频",
            Capability = "video",
            Enabled = true,
            SortOrder = 1,
            ActiveRevisionID = "REV_SEED",
            RevisionSequence = 1,
            PricePolicy = "channel",
            BillingMode = "fixed_request",
            LegacyModelIDsJSON = "[\"legacy-video\"]",
            CreatedAt = now,
            UpdatedAt = now,
        });

        const string productSpec = """
            {"version":1,"capability":"video","operations":["text_to_video","image_to_video"],"inputs":{"image":{"min":0,"max":9},"video":{"min":0,"max":3},"audio":{"min":0,"max":3}},"options":{"size":{"values":["16:9","9:16"]},"vquality":{"values":["720p","1080p"]},"videoSeconds":{"values":[5,10]}}}
            """;

        await repository.CreateAsync(new LogicalModelRevision
        {
            ID = "REV_SEED",
            LogicalModelID = "LMODEL_SEED",
            Version = 1,
            CapabilitySpecJSON = productSpec,
            DefaultOptionsJSON = "{\"size\":\"16:9\",\"vquality\":\"720p\",\"videoSeconds\":5}",
            CreatedBy = "USR_ADMIN",
            CreatedAt = now,
        });

        await repository.CreateAsync(new LogicalModelRoute
        {
            ID = "ROUTE_SEED",
            LogicalModelRevisionID = "REV_SEED",
            ChannelModelID = "CM_SEED",
            Enabled = true,
            Priority = 100,
            Weight = 100,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // ------------------------------------------------------------ /models

    [Fact]
    public async Task 未登录访问模型目录返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"reason\":\"unauthorized\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空库时模型目录为空数组()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Go 的 gin.H{"models": models}：make(...) 初始为空切片 → []。
        Assert.Equal("[]", document.RootElement.GetProperty("data").GetProperty("models").GetRawText());
    }

    [Fact]
    public async Task 模型目录投影完整契约()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedVideoLogicalModelAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement models = document.RootElement.GetProperty("data").GetProperty("models");
        Assert.Equal(1, models.GetArrayLength());

        JsonElement model = models[0];
        Assert.Equal("LMODEL_SEED", model.GetProperty("id").GetString());
        Assert.Equal("seedance-video", model.GetProperty("code").GetString());
        Assert.Equal("即梦视频", model.GetProperty("name").GetString());
        Assert.Equal("video", model.GetProperty("capability").GetString());
        Assert.Equal("channel", model.GetProperty("pricePolicy").GetString());
        Assert.True(model.GetProperty("available").GetBoolean());

        // 价格展示：channel 策略 + 多档 → provider / 按渠道规格计费。
        Assert.Equal("provider", model.GetProperty("pricingMode").GetString());
        Assert.Equal("按渠道规格计费", model.GetProperty("priceLabel").GetString());
        // Go 的 displayPrice 是 *int64 + omitempty：nil 时整个字段省略，不是输出 null。
        Assert.False(model.TryGetProperty("displayPrice", out _));

        // 遗留模型 ID 去重解码。
        Assert.Equal(["legacy-video"], model.GetProperty("legacyModelIds").EnumerateArray().Select(v => v.GetString()));

        // 默认参数经能力范围校验后输出（map 键按字典序）。
        JsonElement defaults = model.GetProperty("defaultOptions");
        Assert.Equal("16:9", defaults.GetProperty("size").GetString());
        Assert.Equal("720p", defaults.GetProperty("vquality").GetString());
        Assert.Equal(5, defaults.GetProperty("videoSeconds").GetInt32());

        // 价格档：仅启用且已定价的档，选择器暴露为安全投影。
        JsonElement tiers = model.GetProperty("priceTiers");
        Assert.Equal(2, tiers.GetArrayLength());
        JsonElement tierA = tiers[0];
        Assert.Equal("720p", tierA.GetProperty("resolution").GetString());
        Assert.Equal(5, tierA.GetProperty("videoSeconds").GetInt32());
        Assert.Equal("fixed_request", tierA.GetProperty("billingMode").GetString());
        Assert.Equal(1_500_000, tierA.GetProperty("unitPriceMicrocredits").GetInt64());
        Assert.Equal("720p", tierA.GetProperty("selector").GetProperty("vquality").GetString());
        // 不暴露内部路由与渠道信息。
        Assert.False(tierA.TryGetProperty("channelModelId", out _));

        // 能力画像：启用线路去重后的匿名规格。
        JsonElement profiles = model.GetProperty("capabilityProfiles");
        Assert.Equal(1, profiles.GetArrayLength());
        Assert.Equal("video", profiles[0].GetProperty("capability").GetString());

        // 供应线路能力收窄：vquality 只剩已定价档。
        JsonElement vquality = model.GetProperty("capabilitySpec").GetProperty("options").GetProperty("vquality");
        Assert.Equal(
            ["720p", "1080p"],
            vquality.GetProperty("values").EnumerateArray().Select(v => v.GetString()));

        // 响应体里的 JSON 键序与 Go 一致：models data 内 model 字段按 Go 结构体顺序。
        // （信封自身也含 "code"，必须切到 models 数据段内比较。）
        string modelsSegment = body[body.IndexOf("\"models\":[", StringComparison.Ordinal)..];
        int idIndex = modelsSegment.IndexOf("\"id\":", StringComparison.Ordinal);
        int codeIndex = modelsSegment.IndexOf("\"code\":", StringComparison.Ordinal);
        int availableIndex = modelsSegment.IndexOf("\"available\":", StringComparison.Ordinal);
        Assert.True(idIndex >= 0 && codeIndex > idIndex && availableIndex > codeIndex);
    }

    [Fact]
    public async Task 意图匹配过滤不可用模型()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedVideoLogicalModelAsync();

        HttpResponseMessage videoMatch = await admin.PostAsJsonAsync("/api/models/available", new
        {
            capability = "video",
            inputs = new { image = 1 },
            // 默认时长 5s 只有 720p 档；选 1080p 必须同时给 10s，与价格档组合一致。
            options = new { vquality = "1080p", videoSeconds = 10 },
        });
        Assert.Equal(HttpStatusCode.OK, videoMatch.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(await videoMatch.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, document.RootElement.GetProperty("data").GetProperty("models").GetArrayLength());
        }

        HttpResponseMessage imageMiss = await admin.PostAsJsonAsync("/api/models/available", new
        {
            capability = "image",
        });
        Assert.Equal(HttpStatusCode.OK, imageMiss.StatusCode);
        using (JsonDocument document = JsonDocument.Parse(await imageMiss.Content.ReadAsStringAsync()))
        {
            Assert.Equal(0, document.RootElement.GetProperty("data").GetProperty("models").GetArrayLength());
        }
    }

    [Fact]
    public async Task 非法意图请求体返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        using StringContent content = new("{invalid", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response = await admin.PostAsync("/api/models/available", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("模型能力请求格式错误", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ /auth/session

    [Fact]
    public async Task 未登录会话只返回user空值()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"code":0,"data":{"user":null},"msg":"ok"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 已登录会话按字典序输出并携带模型目录()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedVideoLogicalModelAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();

        // gin.H 是 map：键按字典序输出。
        int drawingEngine = body.IndexOf("\"drawingEngine\":", StringComparison.Ordinal);
        int features = body.IndexOf("\"features\":", StringComparison.Ordinal);
        int logicalModels = body.IndexOf("\"logicalModels\":", StringComparison.Ordinal);
        int runtimeLimits = body.IndexOf("\"runtimeLimits\":", StringComparison.Ordinal);
        int user = body.IndexOf("\"user\":", StringComparison.Ordinal);
        Assert.True(drawingEngine >= 0);
        Assert.True(drawingEngine < features && features < logicalModels &&
                    logicalModels < runtimeLimits && runtimeLimits < user);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement data = document.RootElement.GetProperty("data");
        Assert.Equal("admin", data.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal(1, data.GetProperty("logicalModels").GetArrayLength());
        Assert.True(data.GetProperty("logicalModels")[0].GetProperty("available").GetBoolean());
    }

    // ------------------------------------------------------------ /channels/system

    [Fact]
    public async Task 系统渠道携带归一化能力配置()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await SeedVideoLogicalModelAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/channels/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement channel = document.RootElement.GetProperty("data").GetProperty("channels")[0];
        JsonElement modelCost = channel.GetProperty("modelCosts")[0];

        Assert.Equal("seedance-lite", modelCost.GetProperty("model").GetString());
        JsonElement capabilityConfig = modelCost.GetProperty("capabilityConfig");
        Assert.Equal(1, capabilityConfig.GetProperty("version").GetInt32());
        // 归一化保持 video 子配置，默认分辨率原样保留。
        Assert.Equal("720p", capabilityConfig.GetProperty("video").GetProperty("defaultResolution").GetString());
        // 文本/图片子配置不属于视频模型，归一化后应为 null（omitempty 省略）。
        Assert.False(capabilityConfig.TryGetProperty("text", out _));
        Assert.False(capabilityConfig.TryGetProperty("image", out _));
    }

    [Fact]
    public async Task 系统渠道能力配置损坏时省略字段()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;

        await repository.CreateAsync(new ModelChannel
        {
            ID = "CH_BROKEN",
            UserID = "USR_SYSTEM",
            Scope = "system",
            Enabled = true,
            Name = "坏配置渠道",
            ModelsJSON = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await repository.CreateAsync(new ChannelModel
        {
            ID = "CM_BROKEN",
            ChannelID = "CH_BROKEN",
            ModelKey = "broken-model",
            DisplayName = "坏配置模型",
            Capability = "video",
            Protocol = "openai",
            BillingMode = "fixed_request",
            PriceConfigured = true,
            Enabled = true,
            CapabilityConfigJSON = "{not-json",
            CreatedAt = now,
            UpdatedAt = now,
        });

        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/channels/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement channels = document.RootElement.GetProperty("data").GetProperty("channels");
        JsonElement broken = channels.EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == "CH_BROKEN");
        // 解码失败 → capabilityConfig 为 null，omitempty 省略字段。
        Assert.False(broken.GetProperty("modelCosts")[0].TryGetProperty("capabilityConfig", out _));
    }
}
