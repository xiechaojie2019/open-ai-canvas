#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 资源文件下发路由的契约测试。
/// 对应 Go: <c>handler/user_data.go</c> 的 <c>GET /resources/:id/file</c> 与
/// <c>GET /public/resources/:id/file</c>，以及 <c>app/resource.go</c> 的
/// <c>OpenResourceRange</c> / <c>normalizeSingleByteRange</c> /
/// <c>resourceResponseETag</c> / <c>ifNoneMatch</c>。
/// </summary>
public sealed class ResourceDeliveryEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ResourceDeliveryEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-delivery-{Guid.NewGuid():N}");
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
        _userClient?.Dispose();
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
            username = "downloader",
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

    /// <summary>走完整分片上传流程落一个资源，返回 (id, 字节内容)。</summary>
    private async Task<(string Id, string ObjectKey, byte[] Payload)> CreateResourceAsync(
        HttpClient user, byte[] payload, string fileName = "sample.png", string kind = "image")
    {
        HttpResponseMessage start = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName,
            kind,
            size = payload.Length,
            width = 4,
            height = 4,
            durationMs = 0,
        });
        start.EnsureSuccessStatusCode();
        string uploadId = (await ReadDataAsync(start)).GetProperty("uploadId").GetString()!;

        HttpResponseMessage chunk = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        chunk.EnsureSuccessStatusCode();

        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        return (
            resource.GetProperty("id").GetString()!,
            resource.GetProperty("objectKey").GetString()!,
            payload);
    }

    [Fact]
    public async Task 文件下发_整文件返回200与关键响应头()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[2048];
        new Random(31).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload);

        HttpResponseMessage response = await user.GetAsync($"/api/resources/{id}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", response.Headers.GetValues("Accept-Ranges").First());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
        Assert.Equal("image/png", response.Content.Headers.GetValues("Content-Type").First());
        // 图片走 30 天强缓存；资源 ID 内容不可变。
        Assert.Contains("max-age=2592000",
            response.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("ETag"));
    }

    [Fact]
    public async Task 文件下发_IfNoneMatch命中返回304()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[512];
        new Random(32).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload);

        HttpResponseMessage first = await user.GetAsync($"/api/resources/{id}/file");
        first.EnsureSuccessStatusCode();
        string etag = first.Headers.GetValues("ETag").First();

        // 同一 ETag 再次条件请求 → 304 且无响应体。
        using HttpRequestMessage conditional = new(HttpMethod.Get, $"/api/resources/{id}/file");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage second = await user.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 文件下发_Range返回206与ContentRange()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[1000];
        new Random(33).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload, fileName: "clip.bin", kind: "file");

        using HttpRequestMessage ranged = new(HttpMethod.Get, $"/api/resources/{id}/file");
        ranged.Headers.TryAddWithoutValidation("Range", "bytes=100-199");
        HttpResponseMessage response = await user.SendAsync(ranged);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-199/1000", response.Content.Headers.GetValues("Content-Range").First());
        Assert.Equal(100, response.Content.Headers.ContentLength);
        Assert.Equal(payload.AsSpan(100, 100).ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 文件下发_开放式Range与后缀Range()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[500];
        new Random(34).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload);

        // bytes=400- （到文件末尾）
        using HttpRequestMessage openEnded = new(HttpMethod.Get, $"/api/resources/{id}/file");
        openEnded.Headers.TryAddWithoutValidation("Range", "bytes=400-");
        HttpResponseMessage tail = await user.SendAsync(openEnded);
        Assert.Equal(HttpStatusCode.PartialContent, tail.StatusCode);
        Assert.Equal("bytes 400-499/500", tail.Content.Headers.GetValues("Content-Range").First());
        Assert.Equal(payload.AsSpan(400).ToArray(), await tail.Content.ReadAsByteArrayAsync());

        // bytes=-100 （最后 100 字节）
        using HttpRequestMessage suffix = new(HttpMethod.Get, $"/api/resources/{id}/file");
        suffix.Headers.TryAddWithoutValidation("Range", "bytes=-100");
        HttpResponseMessage last = await user.SendAsync(suffix);
        Assert.Equal(HttpStatusCode.PartialContent, last.StatusCode);
        Assert.Equal("bytes 400-499/500", last.Content.Headers.GetValues("Content-Range").First());
        Assert.Equal(payload.AsSpan(400).ToArray(), await last.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 文件下发_不可满足Range返回416()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[100];
        new Random(35).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload);

        // 起点越界 → 416 且带 Content-Range: bytes */size
        using HttpRequestMessage unsatisfiable = new(HttpMethod.Get, $"/api/resources/{id}/file");
        unsatisfiable.Headers.TryAddWithoutValidation("Range", "bytes=999-");
        HttpResponseMessage response = await user.SendAsync(unsatisfiable);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */100", response.Content.Headers.GetValues("Content-Range").First());
    }

    [Fact]
    public async Task 文件下发_kind为file时带附件与沙箱头()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[64];
        new Random(36).NextBytes(payload);
        (string id, _, _) = await CreateResourceAsync(user, payload, fileName: "doc.txt", kind: "file");

        HttpResponseMessage response = await user.GetAsync($"/api/resources/{id}/file");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.GetValues("Content-Disposition").First());
        Assert.Equal("sandbox", response.Headers.GetValues("Content-Security-Policy").First());
        // 非图片不享受强缓存。
        Assert.Contains("no-cache", response.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文件下发_未登录返回401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/resources/whatever/file");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 文件下发_资源不存在返回404()
    {
        HttpClient user = await SignInAsync();
        HttpResponseMessage response = await user.GetAsync("/api/resources/not-exist/file");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 匿名下发_签名缺失或错误一律403()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, _, _) = await CreateResourceAsync(user, payload);

        // 无签名
        HttpResponseMessage none = await _client.GetAsync($"/api/public/resources/{id}/file");
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Contains("匿名下载链接无效", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 签名错误
        long expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        HttpResponseMessage wrong = await _client.GetAsync(
            $"/api/public/resources/{id}/file?expires={expires}&signature=deadbeef");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("匿名下载链接无效", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 匿名下发_过期签名返回403带过期文案()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[128];
        (string id, _, _) = await CreateResourceAsync(user, payload);

        long expired = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/public/resources/{id}/file?expires={expired}&signature=whatever");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("匿名下载链接已过期", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 匿名下发_未知资源与非法expires均403()
    {
        HttpResponseMessage unknown = await _client.GetAsync(
            "/api/public/resources/not-exist/file?expires=99999999999&signature=x");
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);

        HttpResponseMessage badExpires = await _client.GetAsync(
            "/api/public/resources/any/file?expires=abc&signature=x");
        Assert.Equal(HttpStatusCode.Forbidden, badExpires.StatusCode);
    }

    [Fact]
    public void Range归一化_只接受合法单区间()
    {
        // 合法
        Assert.Equal("bytes=0-99",
            OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=0-99"));
        Assert.Equal("bytes=100-",
            OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=100-"));
        Assert.Equal("bytes=-50",
            OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=-50"));
        Assert.Equal("bytes=0-99",
            OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange(" bytes=0-99 "));

        // 非法：非 bytes 单位、多区间、空区间、超长、非数字
        Assert.Equal(string.Empty, OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("items=0-99"));
        Assert.Equal(string.Empty, OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=0-9,20-29"));
        Assert.Equal(string.Empty, OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=-"));
        Assert.Equal(string.Empty, OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange("bytes=a-b"));
        Assert.Equal(string.Empty, OpenAICanvas.Application.ResourceDomainService.NormalizeSingleByteRange(
            "bytes=" + new string('9', 130) + "-"));
    }

    [Fact]
    public void IfNoneMatch_支持通配与弱比较()
    {
        Assert.True(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch("\"abc\"", "\"abc\""));
        Assert.True(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch("*", "\"abc\""));
        Assert.True(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch("W/\"abc\"", "\"abc\""));
        Assert.True(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch("\"x\", \"abc\"", "\"abc\""));
        Assert.False(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch("\"zzz\"", "\"abc\""));
        Assert.False(OpenAICanvas.Application.ResourceDomainService.IfNoneMatch(null, "\"abc\""));
    }
}
