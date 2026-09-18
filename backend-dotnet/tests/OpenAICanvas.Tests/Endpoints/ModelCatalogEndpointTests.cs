#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 创作端模型目录路由的端到端契约测试。
/// 对应 Go: <c>handler/model_catalog.go</c> + <c>app/model_catalog.go</c>。
/// </summary>
public sealed class ModelCatalogEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ModelCatalogEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-mcat-{Guid.NewGuid():N}");
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

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 模型目录_两集合恒为数组()
    {
        _ = await SignInAsAdminAsync();

        // 默认（前台模型开关关闭）：source=system，channels=[]；models 恒为数组。
        HttpResponseMessage catalog = await _client.GetAsync("/api/model-catalog");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        JsonElement data = await ReadDataAsync(catalog);
        Assert.Equal("system", data.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("models").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("channels").ValueKind);
        Assert.Equal(0, data.GetProperty("channels").GetArrayLength());

        // available：按意图过滤（空目录同样恒为数组）。
        HttpResponseMessage available = await _client.PostAsJsonAsync("/api/model-catalog/available", new
        {
            capability = "image",
            inputs = new { },
            options = new { },
        });
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        JsonElement availableData = await ReadDataAsync(available);
        Assert.Equal("system", availableData.GetProperty("source").GetString());
        Assert.Equal(0, availableData.GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/model-catalog")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/model-catalog/available", new { })).StatusCode);
    }
}
