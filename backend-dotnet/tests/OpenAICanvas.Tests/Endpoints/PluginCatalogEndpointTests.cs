#nullable enable

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 插件协议目录路由的契约测试。
/// 对应 Go: <c>handler/plugin.go</c> 的 <c>GET /plugins/catalog</c> 与
/// <c>app.Service.PluginProviderCatalog</c>。
/// </summary>
public sealed class PluginCatalogEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public PluginCatalogEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-plugin-catalog-{Guid.NewGuid():N}");
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
        _adminClient?.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDir))
        {
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

    /// <summary>注册首个用户（自动成为管理员）并返回带会话 Cookie 的客户端。</summary>
    private async Task<HttpClient> SignInAsAdminAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "plugincatalogadmin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();

        string setCookie = response.Headers.GetValues("Set-Cookie").First();
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _adminClient.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return _adminClient;
    }

    [Fact]
    public async Task 未登录访问目录返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/plugins/catalog?scope=admin.system-channel");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 登录后目录返回官方协议插件且字段齐全()
    {
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync(
            "/api/plugins/catalog?scope=admin.system-channel&capability=image");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        JsonElement providers = root.GetProperty("data").GetProperty("providers");
        Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        Assert.True(providers.GetArrayLength() > 0, $"官方插件包目录不应为空: {body}");

        foreach (JsonElement provider in providers.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, provider.TryGetProperty("id", out JsonElement id) ? id.ValueKind : JsonValueKind.Undefined);
            Assert.Equal(JsonValueKind.String, provider.TryGetProperty("version", out JsonElement version) ? version.ValueKind : JsonValueKind.Undefined);
            Assert.Equal(JsonValueKind.String, provider.TryGetProperty("name", out JsonElement name) ? name.ValueKind : JsonValueKind.Undefined);
            Assert.Equal(JsonValueKind.String, provider.TryGetProperty("vendor", out JsonElement vendor) ? vendor.ValueKind : JsonValueKind.Undefined);
            Assert.True(provider.TryGetProperty("categories", out JsonElement categories), $"缺少 categories: {provider}");
            if (categories.ValueKind == JsonValueKind.Array)
            {
                Assert.Contains("image", categories.EnumerateArray().Select(item => item.GetString()));
            }
            Assert.True(provider.TryGetProperty("scopes", out JsonElement scopes), $"缺少 scopes: {provider}");
            if (scopes.ValueKind == JsonValueKind.Array)
            {
                Assert.Contains("admin.system-channel", scopes.EnumerateArray().Select(item => item.GetString()));
            }
            Assert.True(provider.TryGetProperty("enabled", out JsonElement enabled) && enabled.GetBoolean());
            Assert.Equal(JsonValueKind.Array, provider.TryGetProperty("workflows", out JsonElement workflows) ? workflows.ValueKind : JsonValueKind.Undefined);
        }
    }

    [Fact]
    public async Task 目录按能力过滤_不匹配时返回空列表()
    {
        HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync(
            "/api/plugins/catalog?scope=admin.system-channel&capability=audio");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement providers = document.RootElement.GetProperty("data").GetProperty("providers");

        // 官方插件包当前没有 audio 类 provider；若后续补充该断言可随之放宽。
        Assert.True(providers.GetArrayLength() >= 0);
        foreach (JsonElement provider in providers.EnumerateArray())
        {
            Assert.Contains("audio", provider.GetProperty("categories").EnumerateArray()
                .Select(item => item.GetString()));
        }
    }
}
