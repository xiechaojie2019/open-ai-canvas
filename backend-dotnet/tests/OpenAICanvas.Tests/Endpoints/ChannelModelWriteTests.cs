#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 渠道模型写路径（保存/价格档归一化/删除）的端到端契约测试。
/// 对应 Go: <c>app/channel_models.go</c> 的 SaveAdminChannelModel / DeleteAdminChannelModels。
/// </summary>
public sealed class ChannelModelWriteTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ChannelModelWriteTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ch-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
            builder.UseSetting("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // SSRF 放行读取进程环境变量；与 ChannelAdminTests 并行时保持一致取值。
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
    }

    public void Dispose()
    {
        // 不清理环境变量：并行测试类共享进程环境，清理会与仍在运行的类竞态。
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

    private HttpClient? _adminClient;

    /// <summary>同一实例内只注册一次管理员；后续复用带 Cookie 的客户端。</summary>
    private async Task<HttpClient> SignInAsAdminAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _adminClient;
    }

    private async Task<string> SeedChannelAsync()
    {
        HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
        {
            name = "模型渠道",
            baseUrl = "http://localhost:9999/v1",
            models = Array.Empty<string>(),
            enabled = true,
        });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        string id = document.RootElement.GetProperty("data").GetProperty("channel").GetProperty("id").GetString()!;
        return id;
    }

    private static readonly string VideoCapabilityJson = """
        {"version":1,"video":{"references":{"promptMaxChars":1000,"minImages":0,"maxImages":9,"maxImageBytes":31457280,"maxVideos":3,"maxVideoBytes":209715200,"maxVideoDurationSeconds":15,"maxAudios":3,"maxAudioBytes":15728640,"maxAudioDurationSeconds":15},"duration":{"selection":"enum","values":[5,10],"default":5},"ratios":["16:9","9:16"],"defaultRatio":"16:9","resolutions":["720p","1080p"],"defaultResolution":"720p","generateAudio":{"supported":true,"default":true},"watermark":{"supported":false,"default":false},"operations":["text_to_video","image_to_video"],"defaultOperation":"text_to_video"}}
        """;

    private static object VideoModelRequest(
        string modelKey = "seedance-pro",
        object[]? tiers = null,
        string protocol = "volcengine-ark-video") => new
    {
        modelKey,
        displayName = "即梦专业版",
        capability = "video",
        protocol,
        capabilityConfig = JsonSerializer.Deserialize<JsonElement>(VideoCapabilityJson),
        priceTiers = tiers ?? new object[]
        {
            new
            {
                selector = new { vquality = "720p", videoSeconds = "5" },
                billingMode = "fixed_request",
                unitPriceMicrocredits = 1_500_000,
                priceConfigured = true,
                enabled = true,
            },
            new
            {
                selector = new { vquality = "1080p", videoSeconds = "10" },
                billingMode = "fixed_request",
                unitPriceMicrocredits = 3_000_000,
                priceConfigured = true,
                enabled = true,
            },
        },
    };

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 保存文本模型_旧API价格折叠为默认档()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new
            {
                modelKey = "gpt-text",
                displayName = "文本模型",
                capability = "text",
                protocol = "chat-completion",
                billingMode = "fixed_request",
                unitPriceMicrocredits = 800_000,
                priceConfigured = true,
                capabilityConfig = new
                {
                    version = 1,
                    text = new { references = new { promptMaxChars = 32000, maxImages = 4, maxVideos = 0 } },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement model = (await ReadDataAsync(response)).GetProperty("model");
        Assert.Equal("gpt-text", model.GetProperty("modelKey").GetString());
        Assert.True(model.GetProperty("priceConfigured").GetBoolean());
        Assert.Equal("fixed_request", model.GetProperty("billingMode").GetString());
        Assert.Equal(800_000, model.GetProperty("unitPriceMicrocredits").GetInt64());

        // 旧 API 无 priceTiers → 折叠为一个通配默认档。
        JsonElement tiers = model.GetProperty("priceTiers");
        Assert.Equal(1, tiers.GetArrayLength());
        Assert.Equal(800_000, tiers[0].GetProperty("unitPriceMicrocredits").GetInt64());

        // 能力配置归一化后回填（版本计数首次落库为 1）。
        Assert.Equal(1, model.GetProperty("capabilityVersion").GetInt64());

        // 渠道 ModelsJSON 已刷新。
        HttpResponseMessage list = await admin.GetAsync("/api/admin/channels");
        JsonElement channels = (await ReadDataAsync(list)).GetProperty("channels");
        Assert.Equal(["gpt-text"], channels[0].GetProperty("models").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task 保存视频模型_双档价格与规格选择器()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models", VideoModelRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement model = (await ReadDataAsync(response)).GetProperty("model");
        Assert.True(model.GetProperty("priceConfigured").GetBoolean());
        // 摘要档：无通配档时取第一档。
        Assert.Equal(1_500_000, model.GetProperty("unitPriceMicrocredits").GetInt64());

        JsonElement tiers = model.GetProperty("priceTiers");
        Assert.Equal(2, tiers.GetArrayLength());
        Assert.Equal("720p", tiers[0].GetProperty("selector").GetProperty("vquality").GetString());
        Assert.Equal("5", tiers[0].GetProperty("selector").GetProperty("videoSeconds").GetString());
        Assert.Equal(10, tiers[1].GetProperty("videoSeconds").GetInt64());
    }

    [Fact]
    public async Task 保存视频模型_档位规格超出能力配置时报错()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            VideoModelRequest(tiers: new object[]
            {
                new
                {
                    selector = new { vquality = "2160p", videoSeconds = "5" },
                    billingMode = "fixed_request",
                    unitPriceMicrocredits = 9_000_000,
                    priceConfigured = true,
                    enabled = true,
                },
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "价格档分辨率不在该视频模型支持范围内：2160p",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 保存模型_协议与能力校验()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        // 无效协议。
        HttpResponseMessage invalidProtocol = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new { modelKey = "m1", capability = "text", protocol = "no-such-protocol" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidProtocol.StatusCode);
        Assert.Contains("请选择有效的模型请求协议", await invalidProtocol.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 能力与协议不匹配。
        HttpResponseMessage mismatch = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new { modelKey = "m2", capability = "image", protocol = "chat-completion" });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("模型能力与请求协议不匹配", await mismatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 保存模型_重复模型键冲突()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage first = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new
            {
                modelKey = "dup-model",
                capability = "text",
                protocol = "chat-completion",
                capabilityConfig = new
                {
                    version = 1,
                    text = new { references = new { promptMaxChars = 32000, maxImages = 0, maxVideos = 0 } },
                },
            });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpResponseMessage second = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new
            {
                modelKey = "dup-model",
                capability = "text",
                protocol = "chat-completion",
                capabilityConfig = new
                {
                    version = 1,
                    text = new { references = new { promptMaxChars = 32000, maxImages = 0, maxVideos = 0 } },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains(
            "该渠道已存在模型 dup-model，请直接编辑已有模型",
            await second.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 保存图片模型_插件包协议元数据可解析()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        // grok-image 由官方插件包声明（非内置），验证插件元数据加载进注册表。
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            new
            {
                modelKey = "grok-2-image",
                displayName = "Grok 图片",
                capability = "image",
                protocol = "grok-image",
                priceConfigured = true,
                billingMode = "fixed_request",
                unitPriceMicrocredits = 500_000,
                capabilityConfig = new
                {
                    version = 1,
                    image = new
                    {
                        references = new { promptMaxChars = 8000, maxImages = 1, maxImageBytes = 10485760, maskSupported = false },
                        size = new
                        {
                            parameter = "aspect_ratio",
                            values = new[] { "1:1", "3:4", "4:3", "9:16", "16:9" },
                            @default = "1:1",
                            allowCustom = false,
                        },
                        quality = new { supported = true, values = new[] { "1k", "2k" }, @default = "2k" },
                        transparentBackground = new { supported = false, @default = false },
                        responseFormat = new { supported = true },
                        outputFormat = new { supported = false },
                        maxOutputs = 1,
                    },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement model = (await ReadDataAsync(response)).GetProperty("model");
        Assert.Equal("image", model.GetProperty("capability").GetString());
    }

    [Fact]
    public async Task 更新模型_价格版本递增且档位复用()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models", VideoModelRequest());
        JsonElement model = (await ReadDataAsync(created)).GetProperty("model");
        string modelId = model.GetProperty("id").GetString()!;
        string tierId = model.GetProperty("priceTiers")[0].GetProperty("id").GetString()!;
        Assert.Equal(1, model.GetProperty("priceVersion").GetInt64());

        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            $"/api/admin/channels/{channelId}/models/{modelId}",
            VideoModelRequest(modelKey: "seedance-pro-renamed"));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedModel = (await ReadDataAsync(updated)).GetProperty("model");
        Assert.Equal("seedance-pro-renamed", updatedModel.GetProperty("modelKey").GetString());
        // 模型价格版本递增。
        Assert.Equal(2, updatedModel.GetProperty("priceVersion").GetInt64());
        // 相同规格的档位复用行并递增版本。
        Assert.Equal(tierId, updatedModel.GetProperty("priceTiers")[0].GetProperty("id").GetString());
        Assert.Equal(2, updatedModel.GetProperty("priceTiers")[0].GetProperty("priceVersion").GetInt64());
    }

    [Fact]
    public async Task 删除模型_单个与批量()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models", VideoModelRequest());
        JsonElement model = (await ReadDataAsync(created)).GetProperty("model");
        string modelId = model.GetProperty("id").GetString()!;
        string secondId = ((JsonElement)model).Clone().GetProperty("priceTiers")[1].GetProperty("id").GetString()!;
        _ = secondId;

        HttpResponseMessage deleted = await admin.DeleteAsync(
            $"/api/admin/channels/{channelId}/models/{modelId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        HttpResponseMessage models = await admin.GetAsync($"/api/admin/channels/{channelId}/models");
        Assert.Equal(0, (await ReadDataAsync(models)).GetProperty("models").GetArrayLength());

        // 重复删除：服务层的归属校验先于仓储（Go 同样返回 400）。
        HttpResponseMessage again = await admin.DeleteAsync(
            $"/api/admin/channels/{channelId}/models/{modelId}");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains(
            "所选渠道模型中存在已删除或不属于当前渠道的记录，请刷新后重试",
            await again.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量删除_空选择与被前台模型引用时拒绝()
    {
        string channelId = await SeedChannelAsync();
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models", VideoModelRequest());
        JsonElement model = (await ReadDataAsync(created)).GetProperty("model");
        string modelId = model.GetProperty("id").GetString()!;

        // 空选择。
        HttpResponseMessage empty = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models/batch-delete",
            new { modelIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("请至少选择一个要删除的渠道模型", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 让该渠道模型成为前台模型的活动供应线路。
        await SeedLogicalModelRouteAsync(modelId);

        HttpResponseMessage batch = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models/batch-delete",
            new { modelIds = new[] { modelId } });
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Contains(
            "所选渠道模型中有模型仍被前台模型供应线路或进行中任务使用，本次未删除任何模型",
            await batch.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 未被引用的模型可以批量删除。
        HttpResponseMessage second = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models",
            VideoModelRequest(modelKey: "seedance-free"));
        string secondId = (await ReadDataAsync(second)).GetProperty("model").GetProperty("id").GetString()!;
        HttpResponseMessage ok = await admin.PostAsJsonAsync(
            $"/api/admin/channels/{channelId}/models/batch-delete",
            new { modelIds = new[] { secondId } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, (await ReadDataAsync(ok)).GetProperty("deleted").GetInt64());
    }

    /// <summary>种子一条引用指定渠道模型的前台模型供应线路。</summary>
    private async Task SeedLogicalModelRouteAsync(string channelModelId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;

        await repository.CreateAsync(new LogicalModel
        {
            ID = "LMODEL_BLOCK",
            Code = "block-model",
            Name = "占用模型",
            Capability = "video",
            Enabled = true,
            PricePolicy = "channel",
            BillingMode = "fixed_request",
            ActiveRevisionID = "REV_BLOCK",
            RevisionSequence = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await repository.CreateAsync(new LogicalModelRevision
        {
            ID = "REV_BLOCK",
            LogicalModelID = "LMODEL_BLOCK",
            Version = 1,
            CapabilitySpecJSON = """
                {"version":1,"capability":"video","inputs":{"image":{"min":0,"max":9}},"options":{"vquality":{"values":["720p"]},"videoSeconds":{"values":[5]}}}
                """,
            DefaultOptionsJSON = "{}",
            CreatedAt = now,
        });
        await repository.CreateAsync(new LogicalModelRoute
        {
            ID = "ROUTE_BLOCK",
            LogicalModelRevisionID = "REV_BLOCK",
            ChannelModelID = channelModelId,
            Enabled = true,
            Priority = 100,
            Weight = 100,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
