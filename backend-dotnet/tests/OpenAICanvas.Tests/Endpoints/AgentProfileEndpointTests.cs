#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 云 Agent 偏好档案路由契约测试：空视图、创建、修订冲突、二次更新与请求体校验。
/// 对应 Go: <c>handler/agent.go</c> profile 部分与 <c>app/cloud_agent_profile.go</c>。
/// </summary>
public sealed class AgentProfileEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AgentProfileEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-agent-profile-{Guid.NewGuid():N}");
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

    private static StringContent RawBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task 未登录读取偏好返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/agent/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 空作用域返回空视图()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/agent/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement view = await ReadDataAsync(response);
        Assert.Equal(0, view.GetProperty("layers").GetArrayLength());
        Assert.NotEqual(string.Empty, view.GetProperty("revision").GetString());
        // 空 layers 的 hash 是空字符串的 SHA-256。
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            view.GetProperty("hash").GetString());
    }

    [Fact]
    public async Task 创建用户偏好后视图包含该层()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PatchAsync("/api/agent/profile", JsonContent.Create(new
        {
            scope = "user",
            content = "  偏好：避免旁白  ",
            revision = 0,
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement view = await ReadDataAsync(created);
        Assert.Equal(1, view.GetProperty("layers").GetArrayLength());
        JsonElement layer = view.GetProperty("layers")[0];
        Assert.Equal("user", layer.GetProperty("scope").GetString());
        Assert.Equal("偏好：避免旁白", layer.GetProperty("content").GetString());
        Assert.Equal(1, layer.GetProperty("revision").GetInt64());

        HttpResponseMessage fetched = await admin.GetAsync("/api/agent/profile");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        JsonElement fetchedView = await ReadDataAsync(fetched);
        Assert.Equal(
            view.GetProperty("revision").GetString(),
            fetchedView.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task 修订过期返回_409()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await admin.PatchAsync("/api/agent/profile", JsonContent.Create(new
        {
            scope = "user",
            content = "第一版",
            revision = 0,
        }));

        HttpResponseMessage conflict = await admin.PatchAsync("/api/agent/profile", JsonContent.Create(new
        {
            scope = "user",
            content = "第二版",
            revision = 0,
        }));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("Agent 偏好已变化", await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 传入当前修订可再次更新()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage first = await admin.PatchAsync("/api/agent/profile", JsonContent.Create(new
        {
            scope = "user",
            content = "第一版",
            revision = 0,
        }));
        JsonElement view = await ReadDataAsync(first);

        HttpResponseMessage second = await admin.PatchAsync("/api/agent/profile", JsonContent.Create(new
        {
            scope = "user",
            content = "第二版",
            revision = 1,
        }));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        JsonElement updated = await ReadDataAsync(second);
        Assert.Equal(2, updated.GetProperty("layers")[0].GetProperty("revision").GetInt64());
        Assert.NotEqual(
            view.GetProperty("revision").GetString(),
            updated.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task 未知字段被拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PatchAsync(
            "/api/agent/profile",
            RawBody("""{"scope":"user","content":"x","revision":0,"extra":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 控制字符内容被拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PatchAsync(
            "/api/agent/profile",
            RawBody("""{"scope":"user","content":"bad\u0001char","revision":0}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("控制字符", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行引擎能力端点返回契约清单()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/agent/capabilities");
        response.EnsureSuccessStatusCode();
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(2, data.GetProperty("version").GetInt32());
        Assert.Equal("canvas-capabilities/v4", data.GetProperty("capabilitySetVersion").GetString());
        Assert.True(data.GetProperty("skills").GetBoolean());
        Assert.Equal("fixed_request", data.GetProperty("billing").GetString());
        Assert.True(data.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array);
        Assert.True(data.TryGetProperty("nodeTypes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array);
    }
}
