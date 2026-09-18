#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Application;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 系统渠道管理 CRUD（列表/创建/复制/更新/删除）、渠道模型列表与排序的端到端契约测试。
/// 对应 Go: <c>handler/auth.go</c> 渠道部分、<c>app/admin.go</c> 与 <c>app/channel_models.go</c>。
/// </summary>
/// <remarks>
/// 渠道 Base URL 使用 <c>http://localhost</c> 并通过
/// <c>CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS</c> 显式放行，避免测试依赖外网 DNS。
/// </remarks>
public sealed class ChannelAdminTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ChannelAdminTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ch-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
        });
        // SSRF 放行走进程环境变量（对应 Go 的 os.Getenv），UseSetting 不影响它。
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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

    private static object CreateChannelRequest(string? name = "内置渠道") => new
    {
        name = name ?? "",
        baseUrl = "http://localhost:9999/v1",
        apiKey = "sk-test-123",
        concurrencyLimit = 4,
        models = new[] { "gpt-image-2", "models/seedance-lite" },
        enabled = true,
    };

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 启动种子_为旧系统渠道补齐模型占位并尊重退役清单()
    {
        Repository repository = _factory.Services.GetRequiredService<Repository>();
        CanvasService service = _factory.Services.GetRequiredService<CanvasService>();
        ModelChannel channel = new()
        {
            ID = "CHANNEL_LEGACY",
            UserID = "admin",
            Scope = "system",
            Enabled = false,
            Name = "旧渠道",
            APIFormat = "openai",
            ModelsJSON = "[\" models/alpha \",\"beta\",\"\"]",
            RetiredModelsJSON = "[\"models/beta\"]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.CreateAsync(channel);

        await service.EnsureSystemChannelModelsAsync();
        IReadOnlyList<ChannelModel> models = await repository.ChannelModelsAsync(channel.ID, enabledOnly: false);

        ChannelModel model = Assert.Single(models);
        Assert.Equal("alpha", model.ModelKey);
        Assert.Equal("alpha", model.DisplayName);
        Assert.False(model.Enabled);
        Assert.False(model.PriceConfigured);
        Assert.Equal("fixed_request", model.BillingMode);
        Assert.Equal(1, model.PriceVersion);

        // 已有记录（包括停用项）不应在下一次启动时重复创建或重置状态。
        await service.EnsureSystemChannelModelsAsync();
        Assert.Single(await repository.ChannelModelsAsync(channel.ID, enabledOnly: false));
    }

    [Fact]
    public async Task 未登录访问渠道列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/channels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 创建渠道_校验与初始模型同步()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement channel = (await ReadDataAsync(created)).GetProperty("channel");

        Assert.Equal("内置渠道", channel.GetProperty("name").GetString());
        Assert.Equal("http://localhost:9999/v1", channel.GetProperty("baseUrl").GetString());
        // Go 契约：apiKey 对任何视角都不回显（空串），仅 hasApiKey 标记存在。
        Assert.Equal("", channel.GetProperty("apiKey").GetString());
        Assert.True(channel.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(4, channel.GetProperty("concurrencyLimit").GetInt64());
        Assert.True(channel.GetProperty("enabled").GetBoolean());
        Assert.True(channel.GetProperty("hasApiKey").GetBoolean());

        // models 列表按请求顺序去空。
        JsonElement models = channel.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        // 兜底列表来自 ModelsJSON 原文，Go 不改写 models/ 前缀。
        Assert.Equal("gpt-image-2", models[0].GetString());
        Assert.Equal("models/seedance-lite", models[1].GetString());

        // 列表可见且总数为 1。
        HttpResponseMessage list = await admin.GetAsync("/api/admin/channels");
        JsonElement page = await ReadDataAsync(list);
        Assert.Equal(1, page.GetProperty("total").GetInt64());
        // 默认分页大小 20（Go parsePaginationQuery fallback）。
        Assert.Equal(20, page.GetProperty("pageSize").GetInt64());
        Assert.Equal(1, page.GetProperty("channels").GetArrayLength());

        // 缺少名称 → 400。
        HttpResponseMessage invalid = await admin.PostAsJsonAsync(
            "/api/admin/channels", new { baseUrl = "http://localhost:9999/v1" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("请填写渠道名称", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 创建渠道_并发数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage invalid = await admin.PostAsJsonAsync("/api/admin/channels", new
        {
            name = "渠道",
            baseUrl = "http://localhost:9999/v1",
            concurrencyLimit = 1000,
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("最大并发数必须是 1-999 的整数", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage missing = await admin.PostAsJsonAsync("/api/admin/channels", new
        {
            name = "渠道",
            baseUrl = "http://localhost:9999/v1",
            useGlobalConcurrency = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("请填写渠道最大并发数", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新渠道_presentation_短路径不改其他字段()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            $"/api/admin/channels/{id}",
            new { publicAlias = "对外的名字", sortOrder = 7 });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement channel = (await ReadDataAsync(updated)).GetProperty("channel");
        Assert.Equal("对外的名字", channel.GetProperty("publicAlias").GetString());
        Assert.Equal(7, channel.GetProperty("sortOrder").GetInt64());
        // 只改展示字段：名称、地址与凭证保持不变（hasApiKey 证明凭证仍在）。
        Assert.Equal("内置渠道", channel.GetProperty("name").GetString());
        Assert.True(channel.GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task 更新渠道_完整路径与秘钥保留()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

        // 不带 apiKey 的更新请求保留旧凭证；Base URL 未变不做出站校验。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/admin/channels/{id}", new
        {
            name = "改名渠道",
            baseUrl = "http://localhost:9999/v1",
            models = new[] { "gpt-image-2" },
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement channel = (await ReadDataAsync(updated)).GetProperty("channel");
        Assert.Equal("改名渠道", channel.GetProperty("name").GetString());
        // 不带 apiKey 的更新保留旧凭证。
        Assert.True(channel.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal(1, channel.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task 复制渠道_副本含模型且名称带后缀()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

        HttpResponseMessage duplicated = await admin.PostAsJsonAsync($"/api/admin/channels/{id}/duplicate", new { });

        Assert.True(duplicated.IsSuccessStatusCode, await duplicated.Content.ReadAsStringAsync());
        JsonElement channel = (await ReadDataAsync(duplicated)).GetProperty("channel");
        Assert.NotEqual(id, channel.GetProperty("id").GetString());
        Assert.Equal("内置渠道 - 副本", channel.GetProperty("name").GetString());
        Assert.True(channel.GetProperty("hasApiKey").GetBoolean());

        // 复制后的渠道模型占位已同步。
        string newId = channel.GetProperty("id").GetString()!;
        HttpResponseMessage models = await admin.GetAsync($"/api/admin/channels/{newId}/models");
        JsonElement modelList = (await ReadDataAsync(models)).GetProperty("models");
        Assert.Equal(2, modelList.GetArrayLength());
    }

    [Fact]
    public async Task 删除渠道_列表消失且重复删除报错()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/admin/channels/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        HttpResponseMessage list = await admin.GetAsync("/api/admin/channels");
        Assert.Equal(0, (await ReadDataAsync(list)).GetProperty("total").GetInt64());

        HttpResponseMessage again = await admin.DeleteAsync($"/api/admin/channels/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("系统渠道不存在或已删除", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 渠道模型列表_未配置模型时兜底同步()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

        HttpResponseMessage models = await admin.GetAsync($"/api/admin/channels/{id}/models");

        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        JsonElement modelList = (await ReadDataAsync(models)).GetProperty("models");
        Assert.Equal(2, modelList.GetArrayLength());
        Assert.Equal("gpt-image-2", modelList[0].GetProperty("modelKey").GetString());
        Assert.Equal("seedance-lite", modelList[1].GetProperty("modelKey").GetString());
        // 初始占位未启用、未定价。
        Assert.False(modelList[0].GetProperty("enabled").GetBoolean());
        Assert.False(modelList[0].GetProperty("priceConfigured").GetBoolean());
    }

    [Fact]
    public async Task 渠道模型排序_校验并生效()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", CreateChannelRequest());
        string id = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;
        HttpResponseMessage models = await admin.GetAsync($"/api/admin/channels/{id}/models");
        string modelId = (await ReadDataAsync(models)).GetProperty("models")[0].GetProperty("id").GetString()!;

        HttpResponseMessage invalid = await admin.PatchAsJsonAsync(
            $"/api/admin/channels/{id}/models/{modelId}/sort",
            new { sortOrder = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "排序值必须是 0-999999 的整数，数值越小越靠前",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        HttpResponseMessage sorted = await admin.PatchAsJsonAsync(
            $"/api/admin/channels/{id}/models/{modelId}/sort",
            new { sortOrder = 5 });
        Assert.Equal(HttpStatusCode.OK, sorted.StatusCode);
        Assert.True((await ReadDataAsync(sorted)).GetProperty("updated").GetBoolean());

        // 排序后渠道 ModelsJSON 刷新，渠道列表保持一条。
        HttpResponseMessage channelList = await admin.GetAsync("/api/admin/channels");
        JsonElement channels = (await ReadDataAsync(channelList)).GetProperty("channels");
        Assert.Equal(1, channels.GetArrayLength());
    }
}
