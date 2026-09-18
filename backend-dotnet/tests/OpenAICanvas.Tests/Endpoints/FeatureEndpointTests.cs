using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 功能开放配置路由的端到端契约测试。
/// </summary>
/// <remarks>
/// 重点验证 PATCH 的<b>局部更新语义</b>：请求里只出现的字段才被覆盖，
/// 未出现的字段保持当前值。这是 Go 的 <c>ShouldBindJSON</c> 到已有对象上的行为，
/// 与"反序列化到新对象"完全不同。
/// </remarks>
public sealed class FeatureEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public FeatureEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-feature-{Guid.NewGuid():N}");
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

    /// <summary>注册首个用户（自动成为管理员）并返回带会话 Cookie 的客户端。</summary>
    private async Task<HttpClient> SignInAsAdminAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();

        string setCookie = response.Headers.GetValues("Set-Cookie").First();
        string cookieValue = setCookie.Split(';')[0];

        HttpClient authed = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        authed.DefaultRequestHeaders.Add("Cookie", cookieValue);
        return authed;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 欢迎开关无需登录且禁用缓存()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/public/welcome");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("{\"code\":0,\"data\":{\"welcomeEnabled\":true},\"msg\":\"ok\"}",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 未登录访问功能开关返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"reason\":\"unauthorized\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 默认功能配置为全开放但前台模型目录关闭()
    {
        using HttpClient authed = await SignInAsAdminAsync();
        HttpResponseMessage response = await authed.GetAsync("/api/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement features = (await ReadDataAsync(response)).GetProperty("features");

        Assert.True(features.GetProperty("welcomeEnabled").GetBoolean());
        Assert.True(features.GetProperty("shortDramaEnabled").GetBoolean());
        Assert.True(features.GetProperty("taskCenterEnabled").GetBoolean());
        Assert.True(features.GetProperty("creditsEnabled").GetBoolean());
        Assert.True(features.GetProperty("customChannelsEnabled").GetBoolean());
        // 前台模型目录默认关闭，需运维明确配置。
        Assert.False(features.GetProperty("frontendModelsEnabled").GetBoolean());
        Assert.True(features.GetProperty("pluginCenterEnabled").GetBoolean());
        Assert.True(features.GetProperty("systemPluginsVisibleToUsers").GetBoolean());
        Assert.True(features.GetProperty("timelineTranscriptionEnabled").GetBoolean());

        // 没有设置记录时 configured=false，且 updatedBy 被 omitempty 省略。
        Assert.False(features.GetProperty("configured").GetBoolean());
        Assert.False(features.TryGetProperty("updatedBy", out _));
    }

    [Fact]
    public async Task 非管理员不能读写功能开关()
    {
        await SignInAsAdminAsync();

        // 直接建一个普通用户：非首个用户走 API 注册需要邮箱验证码，与本测试无关。
        await CreatePlainUserAsync("bob");
        HttpClient bob = await SignInAsPlainUserAsync("bob");

        HttpResponseMessage read = await bob.GetAsync("/api/admin/settings/features");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Contains("需要管理员权限", await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage write = await bob.PatchAsync(
            "/api/admin/settings/features",
            new StringContent("{\"shortDramaEnabled\":false}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        // 普通用户仍可读取自己的功能开关。
        HttpResponseMessage own = await bob.GetAsync("/api/features");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task 局部更新只覆盖请求里出现的字段()
    {
        using HttpClient authed = await SignInAsAdminAsync();

        // 只关掉短剧；其余字段不出现在请求里，必须保持原值。
        HttpResponseMessage response = await authed.PatchAsync(
            "/api/admin/settings/features",
            new StringContent("{\"shortDramaEnabled\":false}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement features = (await ReadDataAsync(response)).GetProperty("features");
        Assert.False(features.GetProperty("shortDramaEnabled").GetBoolean());
        Assert.True(features.GetProperty("welcomeEnabled").GetBoolean());
        Assert.True(features.GetProperty("taskCenterEnabled").GetBoolean());
        Assert.True(features.GetProperty("creditsEnabled").GetBoolean());
        Assert.True(features.GetProperty("configured").GetBoolean());
        // updatedBy 存的是用户 ID（Go 的 actor.ID），不是用户名。
        Assert.False(string.IsNullOrWhiteSpace(features.GetProperty("updatedBy").GetString()));

        // 再关掉任务中心，短剧必须仍然是 false（这正是"局部更新"的关键断言）。
        HttpResponseMessage second = await authed.PatchAsync(
            "/api/admin/settings/features",
            new StringContent("{\"taskCenterEnabled\":false}", Encoding.UTF8, "application/json"));

        JsonElement afterSecond = (await ReadDataAsync(second)).GetProperty("features");
        Assert.False(afterSecond.GetProperty("taskCenterEnabled").GetBoolean());
        Assert.False(afterSecond.GetProperty("shortDramaEnabled").GetBoolean());
    }

    [Fact]
    public async Task 打开前台模型目录后持久化生效()
    {
        using HttpClient authed = await SignInAsAdminAsync();

        await authed.PatchAsync(
            "/api/admin/settings/features",
            new StringContent("{\"frontendModelsEnabled\":true}", Encoding.UTF8, "application/json"));

        HttpResponseMessage response = await authed.GetAsync("/api/features");
        JsonElement features = (await ReadDataAsync(response)).GetProperty("features");
        Assert.True(features.GetProperty("frontendModelsEnabled").GetBoolean());
    }

    [Fact]
    public async Task 非法_JSON_返回_400()
    {
        using HttpClient authed = await SignInAsAdminAsync();

        HttpResponseMessage response = await authed.PatchAsync(
            "/api/admin/settings/features",
            new StringContent("{not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 老配置缺少新字段时不会被意外关闭()
    {
        using HttpClient authed = await SignInAsAdminAsync();

        // 模拟历史配置：只写了 3 个字段。读取时必须以默认值为基底补齐，
        // 否则升级后新增的开关会全部变成 false。
        await WriteRawFeatureSettingAsync("{\"welcomeEnabled\":false,\"shortDramaEnabled\":false,\"taskCenterEnabled\":false}");

        HttpResponseMessage response = await authed.GetAsync("/api/features");
        JsonElement features = (await ReadDataAsync(response)).GetProperty("features");

        Assert.False(features.GetProperty("welcomeEnabled").GetBoolean());
        Assert.False(features.GetProperty("shortDramaEnabled").GetBoolean());
        Assert.False(features.GetProperty("taskCenterEnabled").GetBoolean());
        // 未出现在老配置里的字段回落到默认值 true。
        Assert.True(features.GetProperty("creditsEnabled").GetBoolean());
        Assert.True(features.GetProperty("customChannelsEnabled").GetBoolean());
        Assert.True(features.GetProperty("pluginCenterEnabled").GetBoolean());
    }

    private async Task WriteRawFeatureSettingAsync(string valueJson)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();

        await repository.SaveSystemSettingAsync(new OpenAICanvas.Domain.Entities.SystemSetting
        {
            Key = "feature_availability",
            ValueJSON = valueJson,
            UpdatedBy = "ops",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>直接插入一个普通用户，绕过邮箱验证码路径。</summary>
    private async Task CreatePlainUserAsync(string username)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();

        await repository.CreateAsync(new OpenAICanvas.Domain.Entities.User
        {
            ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
            Username = username,
            DisplayName = username,
            Email = username + "@example.com",
            Role = OpenAICanvas.Domain.Entities.UserRole.UserRoleUser,
            Status = OpenAICanvas.Domain.Entities.UserStatus.UserStatusActive,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task<HttpClient> SignInAsPlainUserAsync(string username)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "password123",
        });
        response.EnsureSuccessStatusCode();

        string setCookie = response.Headers.GetValues("Set-Cookie").First();
        HttpClient authed = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        authed.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return authed;
    }
}
