#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 资源 CRUD/整传/导入/存储用量/OSS 直链路由的端到端契约测试。
/// 对应 Go: <c>handler/user_data.go</c> 的 resources 部分。
/// </summary>
public sealed class ResourceCrudEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ResourceCrudEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-res-{Guid.NewGuid():N}");
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

    private async Task<HttpClient> SignInAsync()
    {
        if (_userClient is not null)
        {
            return _userClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "resourcer",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _userClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _userClient;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 资源列表详情整传与存储用量()
    {
        HttpClient user = await SignInAsync();

        // 空列表 + 初始用量。
        HttpResponseMessage empty = await user.GetAsync("/api/resources");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(empty)).GetProperty("resources").GetArrayLength());
        HttpResponseMessage usage = await user.GetAsync("/api/resources/storage-usage");
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        JsonElement usageData = (await ReadDataAsync(usage)).GetProperty("usage");
        Assert.Equal(0, usageData.GetProperty("usedBytes").GetInt64());
        Assert.True(usageData.TryGetProperty("totalBytes", out _));

        // 整传（multipart）。
        byte[] payload = new byte[1024];
        new Random(5).NextBytes(payload);
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(payload), "file", "photo.png");
        form.Add(new StringContent("image"), "kind");
        form.Add(new StringContent("12"), "width");
        form.Add(new StringContent("34"), "height");
        HttpResponseMessage uploaded = await user.PostAsync("/api/resources", form);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        JsonElement resource = (await ReadDataAsync(uploaded)).GetProperty("resource");
        string resourceId = resource.GetProperty("id").GetString()!;
        Assert.Equal("ready", resource.GetProperty("status").GetString());
        Assert.Equal("image/png", resource.GetProperty("mimeType").GetString());
        Assert.Equal(payload.Length, resource.GetProperty("size").GetInt64());

        // 列表包含新资源；详情可读；不存在的资源 404。
        HttpResponseMessage listed = await user.GetAsync("/api/resources");
        Assert.Equal(1, (await ReadDataAsync(listed)).GetProperty("resources").GetArrayLength());
        HttpResponseMessage detail = await user.GetAsync($"/api/resources/{resourceId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(
            resourceId,
            (await ReadDataAsync(detail)).GetProperty("resource").GetProperty("id").GetString());
        HttpResponseMessage missing = await user.GetAsync("/api/resources/no-such-resource");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // 用量按上传字节数增加。
        HttpResponseMessage usageAfter = await user.GetAsync("/api/resources/storage-usage");
        Assert.Equal(
            payload.Length,
            (await ReadDataAsync(usageAfter)).GetProperty("usage").GetProperty("usedBytes").GetInt64());

        // OSS 直链：本地 provider 也可给出复制地址，并带禁缓存响应头。
        HttpResponseMessage oss = await user.GetAsync($"/api/resources/{resourceId}/oss-url");
        Assert.Equal(HttpStatusCode.OK, oss.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(
            (await ReadDataAsync(oss)).GetProperty("url").GetString()));
        // ASP.NET 会按自身顺序合成 Cache-Control（"no-store, private"），与 Go 的
        // "private, no-store" 语义一致（见待确认 #52 后的响应头说明）。
        string cacheControl = oss.Headers.GetValues("Cache-Control").FirstOrDefault() ?? "";
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 资源URL导入_校验与幂等键()
    {
        HttpClient user = await SignInAsync();

        // 非法 URL（SSRF 校验拒绝非 http 协议）→ 400。
        HttpResponseMessage badScheme = await user.PostAsJsonAsync("/api/resources/import", new
        {
            url = "ftp://example.com/a.png",
            kind = "image",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badScheme.StatusCode);

        // 私网地址 → 400（默认拒绝本机/私网上游）。
        HttpResponseMessage privateHost = await user.PostAsJsonAsync("/api/resources/import", new
        {
            url = "http://127.0.0.1:1/a.png",
            kind = "image",
        });
        Assert.Equal(HttpStatusCode.BadRequest, privateHost.StatusCode);

        // 缺 url → 绑定仍成功但下载失败 → 400。
        HttpResponseMessage missingUrl = await user.PostAsJsonAsync("/api/resources/import", new { kind = "image" });
        Assert.Equal(HttpStatusCode.BadRequest, missingUrl.StatusCode);
    }

    [Fact]
    public async Task 未登录访问资源路由返回_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/resources")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/resources/storage-usage")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/resources/import", new { url = "http://x/y.png" })).StatusCode);
    }
}
