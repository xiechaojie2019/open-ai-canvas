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
/// 管理端存储管理路由的契约测试。
/// 对应 Go: <c>handler/admin_storage.go</c> 与 <c>app/admin_storage.go</c> /
/// <c>app/admin_storage_delete.go</c>。
/// </summary>
public sealed class AdminStorageEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AdminStorageEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-admin-storage-{Guid.NewGuid():N}");
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

    /// <summary>首个注册用户即管理员。</summary>
    private async Task<HttpClient> SignInAdminAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "rootadmin",
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

    /// <summary>走分片上传落一个资源，返回资源 id 与 objectKey。</summary>
    private async Task<(string Id, string ObjectKey)> CreateResourceAsync(
        HttpClient client, byte[] payload, string fileName = "asset.png", string kind = "image")
    {
        HttpResponseMessage start = await client.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName,
            kind,
            size = payload.Length,
            width = 2,
            height = 2,
            durationMs = 0,
        });
        start.EnsureSuccessStatusCode();
        string uploadId = (await ReadDataAsync(start)).GetProperty("uploadId").GetString()!;

        HttpResponseMessage chunk = await client.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        chunk.EnsureSuccessStatusCode();

        HttpResponseMessage completed = await client.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        JsonElement resource = (await ReadDataAsync(completed)).GetProperty("resource");
        return (
            resource.GetProperty("id").GetString()!,
            resource.GetProperty("objectKey").GetString()!);
    }

    private string PhysicalPathOf(string objectKey) =>
        Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task 资源分页_返回视图字段与总数()
    {
        HttpClient admin = await SignInAdminAsync();
        byte[] payload = new byte[300];
        new Random(51).NextBytes(payload);
        (string id, _) = await CreateResourceAsync(admin, payload);

        HttpResponseMessage response = await admin.GetAsync("/api/admin/resources?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("page").GetInt64());
        Assert.Equal(20, data.GetProperty("pageSize").GetInt64());
        Assert.True(data.GetProperty("total").GetInt64() >= 1);

        JsonElement items = data.GetProperty("items");
        JsonElement? item = null;
        foreach (JsonElement candidate in items.EnumerateArray())
        {
            if (candidate.GetProperty("id").GetString() == id)
            {
                item = candidate;
                break;
            }
        }
        Assert.True(item.HasValue, "分页结果中应包含刚上传的资源");

        JsonElement view = item!.Value;
        Assert.Equal("image", view.GetProperty("kind").GetString());
        Assert.Equal("ready", view.GetProperty("status").GetString());
        Assert.Equal("local", view.GetProperty("provider").GetString());
        Assert.Equal(300, view.GetProperty("size").GetInt64());
        // physicalBytes 仅在 ready 时等于 size。
        Assert.Equal(300, view.GetProperty("physicalBytes").GetInt64());
        Assert.Equal($"/api/admin/resources/{id}/file", view.GetProperty("fileUrl").GetString());
        // 资源归属非空；userName 回落到 username（首个注册用户即管理员）。
        Assert.False(string.IsNullOrEmpty(view.GetProperty("userId").GetString()));
        Assert.Equal("rootadmin", view.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task 资源分页_筛选参数校验()
    {
        HttpClient admin = await SignInAdminAsync();

        HttpResponseMessage badKind = await admin.GetAsync("/api/admin/resources?kind=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        Assert.Contains("资源类型筛选无效", await badKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badStatus = await admin.GetAsync("/api/admin/resources?status=weird");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Contains("资源状态筛选无效", await badStatus.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badProvider = await admin.GetAsync("/api/admin/resources?provider=ftp");
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);
        Assert.Contains("资源存储位置筛选无效", await badProvider.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 合法筛选取值不应报错。
        HttpResponseMessage ok = await admin.GetAsync("/api/admin/resources?kind=image&status=ready&provider=local");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task 资源分页_关键字匹配ID与objectKey()
    {
        HttpClient admin = await SignInAdminAsync();
        byte[] payload = new byte[64];
        (string id, string objectKey) = await CreateResourceAsync(admin, payload, fileName: "unique-name.bin", kind: "file");

        // 按 objectKey 片段搜索应命中该资源。
        string fragment = objectKey.Split('/').Last();
        HttpResponseMessage response = await admin.GetAsync($"/api/admin/resources?keyword={fragment}");
        response.EnsureSuccessStatusCode();
        JsonElement data = await ReadDataAsync(response);
        bool found = false;
        foreach (JsonElement item in data.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "按 objectKey 关键字应能检索到该资源");

        // 按 id 搜索同样命中。
        HttpResponseMessage byId = await admin.GetAsync($"/api/admin/resources?keyword={id}");
        byId.EnsureSuccessStatusCode();
        JsonElement byIdData = await ReadDataAsync(byId);
        Assert.True(byIdData.GetProperty("total").GetInt64() >= 1);
    }

    [Fact]
    public async Task 批量删除_无引用资源删除成功并清理物理文件()
    {
        HttpClient admin = await SignInAdminAsync();
        byte[] payload = new byte[256];
        new Random(52).NextBytes(payload);
        (string id, string objectKey) = await CreateResourceAsync(admin, payload);
        string physical = PhysicalPathOf(objectKey);
        Assert.True(File.Exists(physical), "删除前物理文件应存在");

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await ReadDataAsync(response);
        Assert.Contains(id, data.GetProperty("deleted").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(data.GetProperty("blocked").EnumerateArray());
        // 本地 provider 已同步 drain，物理文件应被删除。
        Assert.False(File.Exists(physical), "删除后物理文件应被清理");

        // 再次查分页应不再包含该资源。
        HttpResponseMessage page = await admin.GetAsync("/api/admin/resources");
        page.EnsureSuccessStatusCode();
        JsonElement pageData = await ReadDataAsync(page);
        foreach (JsonElement item in pageData.GetProperty("items").EnumerateArray())
        {
            Assert.NotEqual(id, item.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task 批量删除_不存在的资源进入blocked()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { "no-such-resource" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await ReadDataAsync(response);
        Assert.Empty(data.GetProperty("deleted").EnumerateArray());
        JsonElement blocked = data.GetProperty("blocked").EnumerateArray().First();
        Assert.Equal("no-such-resource", blocked.GetProperty("id").GetString());
        Assert.Equal("资源不存在", blocked.GetProperty("reason").GetString());
        Assert.Empty(blocked.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public async Task 批量删除_空列表与超限与非法ID被拒()
    {
        HttpClient admin = await SignInAdminAsync();

        HttpResponseMessage empty = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("请选择要删除的资源", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 101 个 ID 超限。
        string[] tooMany = [.. Enumerable.Range(0, 101).Select(index => "id-" + index)];
        HttpResponseMessage overLimit = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = tooMany });
        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        Assert.Contains("单次最多删除 100 个资源", await overLimit.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 超长 ID 非法。
        HttpResponseMessage badId = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { new string('x', 81) } });
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Contains("资源 ID 无效", await badId.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量删除_重复ID去重后成功()
    {
        HttpClient admin = await SignInAdminAsync();
        byte[] payload = new byte[128];
        (string id, _) = await CreateResourceAsync(admin, payload);

        HttpResponseMessage response = await admin.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { id, id, id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await ReadDataAsync(response);
        // 去重后只删除一次。
        Assert.Single(data.GetProperty("deleted").EnumerateArray());
        Assert.Equal(id, data.GetProperty("deleted").EnumerateArray().First().GetString());
    }

    [Fact]
    public async Task 文件下发_整文件与Range与下载头()
    {
        HttpClient admin = await SignInAdminAsync();
        byte[] payload = new byte[400];
        new Random(53).NextBytes(payload);
        (string id, _) = await CreateResourceAsync(admin, payload);

        // 整文件
        HttpResponseMessage full = await admin.GetAsync($"/api/admin/resources/{id}/file");
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(payload, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", full.Headers.GetValues("Accept-Ranges").First());
        Assert.Equal("nosniff", full.Headers.GetValues("X-Content-Type-Options").First());
        Assert.Contains("no-cache", full.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);
        // 未传 download 时不带 attachment。
        Assert.False(full.Content.Headers.Contains("Content-Disposition"));

        // Range
        using HttpRequestMessage ranged = new(HttpMethod.Get, $"/api/admin/resources/{id}/file");
        ranged.Headers.TryAddWithoutValidation("Range", "bytes=10-109");
        HttpResponseMessage partial = await admin.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("bytes 10-109/400", partial.Content.Headers.GetValues("Content-Range").First());
        Assert.Equal(payload.AsSpan(10, 100).ToArray(), await partial.Content.ReadAsByteArrayAsync());

        // download=1
        HttpResponseMessage download = await admin.GetAsync($"/api/admin/resources/{id}/file?download=1");
        Assert.Equal("attachment", download.Content.Headers.GetValues("Content-Disposition").First());

        // 越界 Range → 416
        using HttpRequestMessage bad = new(HttpMethod.Get, $"/api/admin/resources/{id}/file");
        bad.Headers.TryAddWithoutValidation("Range", "bytes=9999-");
        HttpResponseMessage unsatisfiable = await admin.SendAsync(bad);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal("bytes */400", unsatisfiable.Content.Headers.GetValues("Content-Range").First());
    }

    [Fact]
    public async Task 文件下发_资源不存在返回404()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/admin/resources/nope/file");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("资源不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 管理端存储_未登录返回401()
    {
        HttpResponseMessage page = await _client.GetAsync("/api/admin/resources");
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);

        HttpResponseMessage delete = await _client.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { "x" } });
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task 管理端存储_非管理员返回403()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage opened = await admin.PatchAsJsonAsync(
            "/api/admin/settings/registration", new { enabled = true });
        opened.EnsureSuccessStatusCode();

        // 非首个用户需邮箱验证码，直接落库后走正式登录。
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.User
            {
                ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
                Username = "plainuser",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                Role = "user",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "plainuser",
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        using HttpClient user = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        user.DefaultRequestHeaders.Add(
            "Cookie", login.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

        HttpResponseMessage page = await user.GetAsync("/api/admin/resources");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("需要管理员权限", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage delete = await user.PostAsJsonAsync(
            "/api/admin/resources/delete", new { resourceIds = new[] { "x" } });
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }
}
