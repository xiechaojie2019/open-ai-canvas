#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 项目素材文件夹路由的端到端契约测试。
/// 对应 Go: <c>handler/project.go</c> asset-folders 部分与 <c>app/project_asset_folder.go</c>。
/// </summary>
public sealed class ProjectAssetFolderEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ProjectAssetFolderEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-paf-{Guid.NewGuid():N}");
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

    private async Task<string> CreateProjectAsync(HttpClient admin)
    {
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/projects", new { name = "素材项目" });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("project").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 文件夹CRUD_默认样式与主题()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        // 空列表。
        HttpResponseMessage empty = await admin.GetAsync($"/api/projects/{projectId}/asset-folders");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(empty)).GetProperty("folders").GetArrayLength());

        // 创建：默认 style=glass、theme=aurora。
        HttpResponseMessage created = await admin.PostAsJsonAsync($"/api/projects/{projectId}/asset-folders", new
        {
            name = "角色",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement folder = (await ReadDataAsync(created)).GetProperty("folder");
        string folderId = folder.GetProperty("id").GetString()!;
        Assert.Equal("glass", folder.GetProperty("style").GetString());
        Assert.Equal("aurora", folder.GetProperty("theme").GetString());
        Assert.Equal(0, folder.GetProperty("position").GetInt64());

        // 同级重名 → 400。
        HttpResponseMessage duplicate = await admin.PostAsJsonAsync($"/api/projects/{projectId}/asset-folders", new
        {
            name = "角色",
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("同级目录下已存在同名文件夹", await duplicate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 更新（局部）。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders/{folderId}",
            new { name = "角色立绘", style = "cinema" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedFolder = (await ReadDataAsync(updated)).GetProperty("folder");
        Assert.Equal("角色立绘", updatedFolder.GetProperty("name").GetString());
        Assert.Equal("cinema", updatedFolder.GetProperty("style").GetString());
        // 未提交的 theme 保留。
        Assert.Equal("aurora", updatedFolder.GetProperty("theme").GetString());

        // 删除。
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/projects/{projectId}/asset-folders/{folderId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(await admin.GetAsync($"/api/projects/{projectId}/asset-folders")))
            .GetProperty("folders").GetArrayLength());
    }

    [Fact]
    public async Task 文件夹校验_名称样式主题()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        HttpResponseMessage emptyName = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.Contains("文件夹名称不能为空", await emptyName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badStyle = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "x", style = "neon" });
        Assert.Equal(HttpStatusCode.BadRequest, badStyle.StatusCode);
        Assert.Contains("不支持的文件夹样式", await badStyle.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badTheme = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "x", theme = "sunset" });
        Assert.Equal(HttpStatusCode.BadRequest, badTheme.StatusCode);
        Assert.Contains("不支持的文件夹主题", await badTheme.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文件夹层级_自引用与深度限制()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        // 建父子两级。
        HttpResponseMessage parent = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "父" });
        string parentId = (await ReadDataAsync(parent)).GetProperty("folder").GetProperty("id").GetString()!;
        HttpResponseMessage child = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "子", parentId });
        string childId = (await ReadDataAsync(child)).GetProperty("folder").GetProperty("id").GetString()!;

        // 移动到自身 → 400。
        HttpResponseMessage self = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders/{parentId}", new { parentId });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Contains(
            "文件夹不能移动到自身或其子目录",
            await self.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 移动到自己的子目录 → 400（同样命中自引用检查链）。
        HttpResponseMessage intoChild = await admin.PatchAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders/{parentId}", new { parentId = childId });
        Assert.Equal(HttpStatusCode.BadRequest, intoChild.StatusCode);

        // 父文件夹不存在 → 400。
        HttpResponseMessage missingParent = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "孤儿", parentId = "FOLDER_NOPE" });
        Assert.Equal(HttpStatusCode.BadRequest, missingParent.StatusCode);
        Assert.Contains(
            "父文件夹不存在或不属于当前项目",
            await missingParent.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 删除非空文件夹被拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string projectId = await CreateProjectAsync(admin);

        HttpResponseMessage parent = await admin.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-folders", new { name = "父" });
        string parentId = (await ReadDataAsync(parent)).GetProperty("folder").GetProperty("id").GetString()!;
        await admin.PostAsJsonAsync($"/api/projects/{projectId}/asset-folders", new { name = "子", parentId });

        HttpResponseMessage deleted = await admin.DeleteAsync(
            $"/api/projects/{projectId}/asset-folders/{parentId}");
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        Assert.Contains(
            "文件夹非空，请先移动其中的素材和子文件夹",
            await deleted.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 项目不存在时返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/projects/PROJ_NOPE/asset-folders");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}