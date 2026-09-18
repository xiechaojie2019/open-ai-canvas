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
/// 项目 CRUD 路由的端到端契约测试：列表/分页/创建校验/局部更新/删除解绑。
/// 对应 Go: <c>handler/project.go</c> 与 <c>app/project.go</c> 的项目基础部分。
/// </summary>
public sealed class ProjectEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ProjectEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-proj6-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task 未登录访问项目列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 创建项目_默认值与校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/projects", new
        {
            name = "短剧一",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement project = (await ReadDataAsync(created)).GetProperty("project");
        Assert.Equal("短剧一", project.GetProperty("name").GetString());
        Assert.Equal("short-drama", project.GetProperty("type").GetString());
        Assert.Equal("9:16", project.GetProperty("aspectRatio").GetString());
        Assert.Equal("blank", project.GetProperty("sourceType").GetString());
        Assert.Equal("active", project.GetProperty("status").GetString());

        // 空名称 → 400。
        HttpResponseMessage invalid = await admin.PostAsJsonAsync("/api/projects", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("项目名称不能为空", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 项目列表_摘要与分页()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await admin.PostAsJsonAsync("/api/projects", new { name = "项目A" });
        await admin.PostAsJsonAsync("/api/projects", new { name = "项目B" });

        HttpResponseMessage list = await admin.GetAsync("/api/projects");
        JsonElement projects = (await ReadDataAsync(list)).GetProperty("projects");
        Assert.Equal(2, projects.GetArrayLength());
        // 摘要计数字段出现。
        Assert.True(projects[0].TryGetProperty("canvasCount", out _));
        Assert.True(projects[0].TryGetProperty("completedUnitCount", out _));

        HttpResponseMessage page = await admin.GetAsync("/api/projects?page=1&pageSize=1");
        JsonElement pageData = await ReadDataAsync(page);
        Assert.Equal(2, pageData.GetProperty("total").GetInt64());
        Assert.Equal(1, pageData.GetProperty("pageSize").GetInt64());
        Assert.True(pageData.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task 更新项目_局部提交语义()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/projects", new
        {
            name = "原名称",
            description = "原描述",
        });
        string id = (await ReadDataAsync(created)).GetProperty("project").GetProperty("id").GetString()!;

        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/projects/{id}", new
        {
            name = "新名称",
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement project = (await ReadDataAsync(updated)).GetProperty("project");
        Assert.Equal("新名称", project.GetProperty("name").GetString());
        // 未提交的 description 保留。
        Assert.Equal("原描述", project.GetProperty("description").GetString());
    }

    [Fact]
    public async Task 删除项目后_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/projects", new { name = "待删" });
        string id = (await ReadDataAsync(created)).GetProperty("project").GetProperty("id").GetString()!;

        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(id, (await ReadDataAsync(deleted)).GetProperty("id").GetString());

        HttpResponseMessage again = await admin.DeleteAsync($"/api/projects/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task 画风配置校验_与预设不一致()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage invalid = await admin.PostAsJsonAsync("/api/projects", new
        {
            name = "画风项目",
            stylePresetId = "preset-a",
            styleProfileJson = """{"schemaVersion":1,"presetId":"preset-b","title":"t","prompt":"p","revision":1,"assets":[]}""",
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "项目画风预设与结构化快照不一致",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }
}
