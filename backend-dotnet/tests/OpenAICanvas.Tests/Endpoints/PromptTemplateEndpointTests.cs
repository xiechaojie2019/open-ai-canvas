#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 提示词模板管理路由的端到端契约测试。
/// </summary>
/// <remarks>
/// 模板是版本化的：启用中的版本不能改内容、不能停用、不能删除。
/// 这些约束是「历史生成结果可追溯」的前提，测试逐条覆盖。
/// </remarks>
public sealed class PromptTemplateEndpointTests : IDisposable
{
    /// <summary>与实现一致的操作数（内置定义）。</summary>
    private const int BuiltinOperationCount = 9;

    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PromptTemplateEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-prompts-{Guid.NewGuid():N}");
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
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 权限与种子

    [Fact]
    public async Task 非管理员访问返回_403()
    {
        using HttpClient _ = await SignInAsAdminAsync();
        using HttpClient bob = await SignInAsync("bob");

        HttpResponseMessage response = await bob.GetAsync("/api/admin/prompt-templates");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 启动时种入全部内置模板()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        JsonElement data = await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates"));

        // 每个内置操作都应该有一个默认版本。
        Assert.Equal(BuiltinOperationCount, data.GetProperty("templates").GetArrayLength());
        Assert.Equal(BuiltinOperationCount, data.GetProperty("definitions").GetArrayLength());

        JsonElement template = data.GetProperty("templates")[0];
        Assert.Equal(1, template.GetProperty("version").GetInt64());
        Assert.True(template.GetProperty("enabled").GetBoolean());
        Assert.StartsWith("默认", template.GetProperty("name").GetString(), StringComparison.Ordinal);

        // definitions 里绝不能出现 defaultContent（Go 的 json:"-"）。
        string body = await (await admin.GetAsync("/api/admin/prompt-templates")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("defaultContent", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 种子幂等重复启动不重复种入()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 手动再跑一次种子（等价于二次启动）。
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Application.CanvasService>();
            await service.PromptTemplates.EnsureDefaultPromptTemplatesAsync();
        }

        JsonElement data = await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates"));
        Assert.Equal(BuiltinOperationCount, data.GetProperty("templates").GetArrayLength());
    }

    // ------------------------------------------------------------ 创建

    [Fact]
    public async Task 创建新版本版本号递增()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "v2 实验版",
            content = "{{项目名称}} 的分镜规划（第二版）",
            enabled = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement template = (await ReadDataAsync(response)).GetProperty("template");
        Assert.Equal(2, template.GetProperty("version").GetInt64());
        Assert.Equal("v2 实验版", template.GetProperty("name").GetString());
        Assert.False(template.GetProperty("enabled").GetBoolean());
        Assert.Equal("json", template.GetProperty("outputType").GetString());
    }

    [Fact]
    public async Task 启用新版本会自动停用旧版本()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "v2 启用版",
            content = "{{项目名称}} 的新分镜规划",
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement templates = (await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates")))
            .GetProperty("templates");

        // 同一操作下只能有一个启用版本。
        List<JsonElement> planTemplates = templates.EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "storyboard_plan")
            .ToList();

        Assert.Equal(2, planTemplates.Count);
        Assert.Single(planTemplates.Where(item => item.GetProperty("enabled").GetBoolean()));
        Assert.Equal(2, planTemplates.Single(item => item.GetProperty("enabled").GetBoolean())
            .GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task 创建参数校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 未知操作类型
        HttpResponseMessage unknownOperation = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "not-an-operation",
            name = "x",
            content = "y",
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknownOperation.StatusCode);
        Assert.Contains("不支持的提示词模板类型", await unknownOperation.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 名称为空
        HttpResponseMessage blankName = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "   ",
            content = "y",
        });
        Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);
        Assert.Contains("请填写版本名称", await blankName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 内容为空
        HttpResponseMessage blankContent = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "x",
            content = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, blankContent.StatusCode);
        Assert.Contains("请填写提示词模板", await blankContent.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未知占位符被拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // storyboard_plan 只声明了 {{项目名称}} / {{项目画风}} / {{用户要求}}。
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "坏模板",
            content = "{{项目名称}} 和 {{不存在的变量}} 以及 {{另一个未知}}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("模板包含不支持的变量", body, StringComparison.Ordinal);
        // 未知变量按字典序排列并用「、」连接。
        Assert.Contains("{{不存在的变量}}、{{另一个未知}}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 内容超长被拒绝()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 30001 个 ASCII 字符：能通过 64KB body 上限，但超过 30000 rune 的内容上限。
        string tooLong = new string('a', 30_001);
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "超长",
            content = tooLong,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("最多 30000 个字符", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 启用版本的约束

    [Fact]
    public async Task 启用中的版本不能改内容或名称()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string enabledId = await EnabledTemplateIdAsync(admin, "storyboard_plan");

        HttpResponseMessage contentChange = await admin.PatchAsJsonAsync(
            $"/api/admin/prompt-templates/{enabledId}",
            new { operation = "storyboard_plan", name = "默认分镜规划模板", content = "改了内容" });
        Assert.Equal(HttpStatusCode.BadRequest, contentChange.StatusCode);
        Assert.Contains("启用中的版本不可直接修改", await contentChange.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage nameChange = await admin.PatchAsJsonAsync(
            $"/api/admin/prompt-templates/{enabledId}",
            new { operation = "storyboard_plan", name = "改了名字", content = "{{项目名称}}" });
        Assert.Equal(HttpStatusCode.BadRequest, nameChange.StatusCode);
    }

    [Fact]
    public async Task 启用中的版本不能停用()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string enabledId = await EnabledTemplateIdAsync(admin, "storyboard_plan");

        HttpResponseMessage response = await admin.PatchAsJsonAsync(
            $"/api/admin/prompt-templates/{enabledId}",
            new
            {
                operation = "storyboard_plan",
                name = "默认分镜规划模板",
                content = await TemplateContentAsync(admin, enabledId),
                enabled = false,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不能直接停用", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 启用中的版本不能删除()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        string enabledId = await EnabledTemplateIdAsync(admin, "storyboard_plan");

        HttpResponseMessage response = await admin.DeleteAsync($"/api/admin/prompt-templates/{enabledId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("启用中的版本不能删除", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 非启用版本

    [Fact]
    public async Task 非启用版本可以修改与删除()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 先建一个未启用版本。
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/prompt-templates", new
        {
            operation = "storyboard_plan",
            name = "草稿版",
            content = "{{项目名称}} 草稿",
            enabled = false,
        });
        string draftId = (await ReadDataAsync(created)).GetProperty("template").GetProperty("id").GetString()!;

        // 修改
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/admin/prompt-templates/{draftId}", new
        {
            operation = "storyboard_plan",
            name = "草稿版改",
            content = "{{项目名称}} 改过的草稿",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedTemplate = (await ReadDataAsync(updated)).GetProperty("template");
        Assert.Equal("草稿版改", updatedTemplate.GetProperty("name").GetString());

        // 删除
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/admin/prompt-templates/{draftId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        JsonElement deletedData = await ReadDataAsync(deleted);
        Assert.True(deletedData.GetProperty("ok").GetBoolean());

        // 删除后不在列表里
        JsonElement templates = (await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates")))
            .GetProperty("templates");
        Assert.DoesNotContain(draftId,
            templates.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task 不存在的模板返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.DeleteAsync("/api/admin/prompt-templates/not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------ 辅助

    private async Task<HttpClient> SignInAsAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        return Authenticated(response);
    }

    private async Task<HttpClient> SignInAsync(string username)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        await repository.CreateAsync(new User
        {
            ID = IdGenerator.NewId(),
            Username = username,
            Email = $"{username}@example.com",
            DisplayName = username,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        return Authenticated(login);
    }

    private HttpClient Authenticated(HttpResponseMessage response)
    {
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<string> EnabledTemplateIdAsync(HttpClient admin, string operation)
    {
        JsonElement templates = (await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates")))
            .GetProperty("templates");

        return templates.EnumerateArray()
            .Single(item => item.GetProperty("operation").GetString() == operation
                && item.GetProperty("enabled").GetBoolean())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> TemplateContentAsync(HttpClient admin, string id)
    {
        JsonElement templates = (await ReadDataAsync(await admin.GetAsync("/api/admin/prompt-templates")))
            .GetProperty("templates");

        return templates.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id)
            .GetProperty("content").GetString()!;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
