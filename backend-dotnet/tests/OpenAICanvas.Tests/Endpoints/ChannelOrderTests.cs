#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 渠道/渠道模型排序路由（乐观并发 + 409 冲突）与启动种子的端到端契约测试。
/// 对应 Go: <c>handler/channel_order.go</c> 与 <c>app.EnsureSystemChannelModels</c>。
/// </summary>
public sealed class ChannelOrderTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ChannelOrderTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ch-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
            builder.UseSetting("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
        });
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");

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

    private async Task<string> CreateChannelAsync(string name)
    {
        HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
        {
            name,
            baseUrl = "http://localhost:9999/v1",
            enabled = true,
        });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("channel").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录访问排序返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/channels/order");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 渠道排序_读取交换与快照冲突()
    {
        string first = await CreateChannelAsync("渠道一");
        string second = await CreateChannelAsync("渠道二");
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage list = await admin.GetAsync("/api/admin/channels/order");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        JsonElement items = (await ReadDataAsync(list)).GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("渠道一", items[0].GetProperty("name").GetString());
        string[] originalOrder = items.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        // 交换顺序保存。
        HttpResponseMessage saved = await admin.PutAsJsonAsync(
            "/api/admin/channels/order",
            new { ids = new[] { originalOrder[1], originalOrder[0] }, expectedIds = originalOrder });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await ReadDataAsync(saved)).GetProperty("saved").GetBoolean());

        // 保存后用旧快照再存 → 409。
        HttpResponseMessage stale = await admin.PutAsJsonAsync(
            "/api/admin/channels/order",
            new { ids = originalOrder, expectedIds = originalOrder });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains(
            "列表已发生变化，请重新打开排序后再保存",
            await stale.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 重新读取顺序已交换。
        HttpResponseMessage reread = await admin.GetAsync("/api/admin/channels/order");
        JsonElement reordered = (await ReadDataAsync(reread)).GetProperty("items");
        Assert.Equal(originalOrder[1], reordered[0].GetProperty("id").GetString());
        Assert.Equal(originalOrder[0], reordered[1].GetProperty("id").GetString());
        _ = first;
        _ = second;
    }

    [Fact]
    public async Task 渠道排序_携带未知ID或长度不符回409()
    {
        string first = await CreateChannelAsync("渠道甲");
        await CreateChannelAsync("渠道乙");
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage list = await admin.GetAsync("/api/admin/channels/order");
        string[] originalOrder = (await ReadDataAsync(list)).GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        // 长度不符。
        HttpResponseMessage wrongLength = await admin.PutAsJsonAsync(
            "/api/admin/channels/order",
            new { ids = new[] { first }, expectedIds = originalOrder });
        Assert.Equal(HttpStatusCode.Conflict, wrongLength.StatusCode);

        // 未知 ID。
        HttpResponseMessage unknownId = await admin.PutAsJsonAsync(
            "/api/admin/channels/order",
            new { ids = new[] { first, "CHANNEL_NOPE" }, expectedIds = originalOrder });
        Assert.Equal(HttpStatusCode.Conflict, unknownId.StatusCode);

        // ids/expectedIds 缺失 → 400。
        HttpResponseMessage missing = await admin.PutAsJsonAsync(
            "/api/admin/channels/order", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains(
            "请重新加载完整排序列表",
            await missing.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 渠道模型排序_保存后刷新ModelsJSON()
    {
        string channelId = await CreateChannelAsync("模型排序渠道");
        HttpClient admin = await SignInAsAdminAsync();

        // 保存两个渠道模型（带合法视频能力配置与价格档）。
        const string capabilityJson = """
            {"version":1,"video":{"references":{"promptMaxChars":1000,"minImages":0,"maxImages":9,"maxImageBytes":31457280,"maxVideos":3,"maxVideoBytes":209715200,"maxVideoDurationSeconds":15,"maxAudios":3,"maxAudioBytes":15728640,"maxAudioDurationSeconds":15},"duration":{"selection":"enum","values":[5,10],"default":5},"ratios":["16:9"],"defaultRatio":"16:9","resolutions":["720p"],"defaultResolution":"720p","generateAudio":{"supported":true,"default":true},"watermark":{"supported":false,"default":false},"operations":["text_to_video"],"defaultOperation":"text_to_video"}}
            """;
        foreach (string modelKey in new[] { "model-a", "model-b" })
        {
            HttpResponseMessage modelSaved = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models",
                new
                {
                    modelKey,
                    capability = "video",
                    protocol = "volcengine-ark-video",
                    capabilityConfig = JsonSerializer.Deserialize<JsonElement>(capabilityJson),
                    priceTiers = new object[]
                    {
                        new
                        {
                            selector = new { vquality = "720p", videoSeconds = "5" },
                            billingMode = "fixed_request",
                            unitPriceMicrocredits = 1_000_000,
                            priceConfigured = true,
                            enabled = true,
                        },
                    },
                });
            Assert.Equal(HttpStatusCode.OK, modelSaved.StatusCode);
        }

        HttpResponseMessage list = await admin.GetAsync($"/api/admin/channels/{channelId}/models/order");
        JsonElement items = (await ReadDataAsync(list)).GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        // 排序条目名称取 displayName（此处等于 modelKey）。
        Assert.Equal("model-a", items[0].GetProperty("name").GetString());
        string[] originalOrder = items.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        // 交换模型顺序。
        HttpResponseMessage saved = await admin.PutAsJsonAsync(
            $"/api/admin/channels/{channelId}/models/order",
            new { ids = new[] { originalOrder[1], originalOrder[0] }, expectedIds = originalOrder });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // 渠道 ModelsJSON 顺序随之刷新。
        HttpResponseMessage channelList = await admin.GetAsync("/api/admin/channels");
        JsonElement channels = (await ReadDataAsync(channelList)).GetProperty("channels");
        JsonElement models = channels[0].GetProperty("models");
        Assert.Equal("model-b", models[0].GetString());
        Assert.Equal("model-a", models[1].GetString());
    }
}
