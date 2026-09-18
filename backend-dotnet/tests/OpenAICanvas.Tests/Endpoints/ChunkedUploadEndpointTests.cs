#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 本地媒体分片上传路由的端到端契约测试。
/// 对应 Go: <c>handler/resource_upload_session.go</c> 与
/// <c>handler/chunk_upload_routes_test.go</c>，以及 <c>app/resource.go</c> 的
/// <c>UploadResourceFile</c> / <c>storeResource</c> 写路径。
/// </summary>
public sealed class ChunkedUploadEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ChunkedUploadEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-chunk-{Guid.NewGuid():N}");
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
            username = "uploader",
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

    /// <summary>开始一个分片会话并返回 uploadId。</summary>
    private static async Task<(string UploadId, int ChunkCount, long ChunkSize)> StartSessionAsync(
        HttpClient client, long size, string fileName = "sample.png", string? idempotencyKey = null)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName,
            kind = "image",
            size,
            width = 4,
            height = 4,
            durationMs = 0,
            idempotencyKey,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        return (
            data.GetProperty("uploadId").GetString()!,
            data.GetProperty("chunkCount").GetInt32(),
            data.GetProperty("chunkSize").GetInt64());
    }

    [Fact]
    public async Task 分片上传_单片合并后资源就绪并落盘()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[1024];
        new Random(7).NextBytes(payload);

        (string uploadId, int chunkCount, long chunkSize) = await StartSessionAsync(user, payload.Length);
        Assert.Equal(1, chunkCount);
        Assert.Equal(8L << 20, chunkSize);

        HttpResponseMessage chunk = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0",
            new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(chunk)).GetProperty("index").GetInt32());

        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        Assert.Equal("ready", resource.GetProperty("status").GetString());
        Assert.Equal("image", resource.GetProperty("kind").GetString());
        Assert.Equal("local", resource.GetProperty("provider").GetString());
        Assert.Equal(payload.Length, resource.GetProperty("size").GetInt64());
        Assert.Equal("image/png", resource.GetProperty("mimeType").GetString());

        // 物理对象按 objectKey 落在 dataDir/resources 下。
        string objectKey = resource.GetProperty("objectKey").GetString()!;
        string path = Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"期望物理文件存在：{path}");
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task 分片上传_多片合并且顺序正确()
    {
        HttpClient user = await SignInAsync();
        // 跨过一个分片边界（8MB + 13 字节），验证末片余量逻辑。
        long size = (8L << 20) + 13;
        byte[] first = new byte[(int)(8L << 20)];
        byte[] last = new byte[13];
        new Random(11).NextBytes(first);
        new Random(12).NextBytes(last);

        (string uploadId, int chunkCount, _) = await StartSessionAsync(user, size, fileName: "big.bin", idempotencyKey: "multi-1");
        Assert.Equal(2, chunkCount);

        Assert.Equal(HttpStatusCode.OK,
            (await user.PutAsync($"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await user.PutAsync($"/api/resources/uploads/{uploadId}/chunks/1", new ByteArrayContent(last))).StatusCode);

        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        Assert.Equal(size, resource.GetProperty("size").GetInt64());

        string objectKey = resource.GetProperty("objectKey").GetString()!;
        string path = Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar));
        byte[] merged = await File.ReadAllBytesAsync(path);
        Assert.Equal(size, merged.Length);
        Assert.Equal(first, merged.AsSpan(0, first.Length).ToArray());
        Assert.Equal(last, merged.AsSpan(first.Length).ToArray());
    }

    [Fact]
    public async Task 分片上传_幂等键复用返回同一资源()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[512];
        new Random(21).NextBytes(payload);

        (string firstUploadId, _, _) = await StartSessionAsync(user, payload.Length, idempotencyKey: "idem-key-1");
        await user.PutAsync($"/api/resources/uploads/{firstUploadId}/chunks/0", new ByteArrayContent(payload));
        HttpResponseMessage first = await user.PostAsync(
            $"/api/resources/uploads/{firstUploadId}/complete", content: null);
        first.EnsureSuccessStatusCode();
        string firstId = (await ReadDataAsync(first)).GetProperty("resource").GetProperty("id").GetString()!;

        // 同一幂等键再次走完整流程：应复用既有 ready 资源，不新建。
        (string secondUploadId, _, _) = await StartSessionAsync(user, payload.Length, idempotencyKey: "idem-key-1");
        await user.PutAsync($"/api/resources/uploads/{secondUploadId}/chunks/0", new ByteArrayContent(payload));
        HttpResponseMessage second = await user.PostAsync(
            $"/api/resources/uploads/{secondUploadId}/complete", content: null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstId, (await ReadDataAsync(second)).GetProperty("resource").GetProperty("id").GetString());
    }

    [Fact]
    public async Task 分片上传_开始会话的字段校验()
    {
        HttpClient user = await SignInAsync();

        HttpResponseMessage emptyName = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "",
            size = 10L,
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.Contains("文件名不能为空", await emptyName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage zeroSize = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "a.png",
            size = 0L,
        });
        Assert.Equal(HttpStatusCode.BadRequest, zeroSize.StatusCode);
        Assert.Contains("文件大小必须大于 0", await zeroSize.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 超过账号存储总量上限（默认 20GB）→ 400。
        HttpResponseMessage tooLarge = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "huge.bin",
            size = 21L << 30,
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("文件超过账号存储总量上限", await tooLarge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分片上传_分片序号与长度校验()
    {
        HttpClient user = await SignInAsync();
        byte[] payload = new byte[100];
        (string uploadId, _, _) = await StartSessionAsync(user, payload.Length);

        // 越界序号 → 400。
        HttpResponseMessage badIndex = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/5", new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.BadRequest, badIndex.StatusCode);
        Assert.Contains("非法的分片序号", await badIndex.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 非数字序号（路由匹配后解析失败）→ 400。
        HttpResponseMessage nonNumeric = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/abc", new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.BadRequest, nonNumeric.StatusCode);

        // 短于期望长度 → 400「上传不完整」。
        HttpResponseMessage shortBody = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(new byte[10]));
        Assert.Equal(HttpStatusCode.BadRequest, shortBody.StatusCode);
        Assert.Contains("上传不完整", await shortBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 长于期望长度 → 400「超过大小限制」。
        HttpResponseMessage longBody = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(new byte[101]));
        Assert.Equal(HttpStatusCode.BadRequest, longBody.StatusCode);
        Assert.Contains("超过大小限制", await longBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分片上传_会话不存在或分片不全()
    {
        HttpClient user = await SignInAsync();

        // 未开始会话先传片 → 404。
        HttpResponseMessage noSession = await user.PutAsync(
            "/api/resources/uploads/nope/chunks/0", new ByteArrayContent(new byte[4]));
        Assert.Equal(HttpStatusCode.NotFound, noSession.StatusCode);
        Assert.Contains("上传会话不存在或已过期", await noSession.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 分片未传完就合并 → 400「上传文件不完整」。
        byte[] part = new byte[128];
        (string uploadId, _, _) = await StartSessionAsync(user, part.Length * 2);
        await user.PutAsync($"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(part));
        HttpResponseMessage incomplete = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains("上传文件不完整", await incomplete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分片上传_未登录被拒绝()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "a.png",
            size = 10L,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 分片上传_跨用户不能操作他人会话()
    {
        HttpClient owner = await SignInAsync();
        byte[] payload = new byte[64];
        (string uploadId, _, _) = await StartSessionAsync(owner, payload.Length);

        // 非首个用户注册必须走邮箱验证码，测试里直接落库第二个用户后走正式登录。
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.User
            {
                ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
                Username = "intruder",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                Role = "user",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        // 第二个用户登录后尝试操作该会话 → 404（与 Go 一致：会话归属校验当作不存在）。
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "intruder",
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        using HttpClient intruder = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        intruder.DefaultRequestHeaders.Add(
            "Cookie", login.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

        HttpResponseMessage chunk = await intruder.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.NotFound, chunk.StatusCode);

        HttpResponseMessage complete = await intruder.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
    }
}
