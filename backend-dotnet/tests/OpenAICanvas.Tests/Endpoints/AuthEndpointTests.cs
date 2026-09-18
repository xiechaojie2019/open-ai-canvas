using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 认证路由的端到端契约测试：真实 HTTP 请求打到底，校验状态码、响应体与 Set-Cookie。
/// </summary>
/// <remarks>
/// 这是纵向切片的验收：仓储 → 服务 → 路由 → 信封 → Cookie 全链路。
/// 响应体断言都按 Go 版的真实输出写（gin.H 的键按字典序）。
/// </remarks>
public sealed class AuthEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AuthEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
            builder.UseSetting("CANVAS_CORS_ORIGINS", "http://localhost:3000");
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
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

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 首个用户时公开设置开放注册()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();

        // Go 的 PublicAuthSettings 是 struct（按声明顺序输出），且这 5 个字段都没有 omitempty：
        // 首用户分支只赋值前 3 个，EmailEnabled / EmailCodeRequired 保持零值 false 但依然输出。
        Assert.Equal(
            "{\"code\":0,\"data\":{\"firstUser\":true,\"registrationEnabled\":true,\"linuxdoEnabled\":false,\"emailEnabled\":false,\"emailCodeRequired\":false},\"msg\":\"ok\"}",
            body);
    }

    [Fact]
    public async Task 首个用户注册直接成为管理员并下发会话_Cookie()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "founder",
            password = "password123",
            displayName = "创始人",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement data = await ReadDataAsync(response);
        JsonElement user = data.GetProperty("user");
        Assert.Equal("founder", user.GetProperty("username").GetString());
        Assert.Equal("admin", user.GetProperty("role").GetString());
        Assert.Equal("active", user.GetProperty("status").GetString());
        Assert.Equal("创始人", user.GetProperty("displayName").GetString());

        // 口令哈希绝不能出现在响应里（Go 的 json:"-"）。
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.Ordinal);

        // 空邮箱带 omitempty，必须被省略。
        Assert.False(user.TryGetProperty("email", out _));

        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("open_ai_canvas_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("Path=/", setCookie, StringComparison.Ordinal);
        Assert.Contains("Max-Age=2592000", setCookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", setCookie, StringComparison.Ordinal);
        // 明文 HTTP 且没有 X-Forwarded-Proto，不应带 Secure。
        Assert.DoesNotContain("Secure", setCookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 有用户后公开设置变为关闭注册()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "founder",
            password = "password123",
        });

        HttpResponseMessage response = await _client.GetAsync("/api/auth/settings");
        string body = await response.Content.ReadAsStringAsync();

        // 首个用户之后走设置读取路径；未配置注册开关时回落到环境变量（默认 false）。
        Assert.Contains("\"firstUser\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"registrationEnabled\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"emailCodeRequired\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 登录成功并下发会话_Cookie()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "alice",
            password = "password123",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal("alice", data.GetProperty("user").GetProperty("username").GetString());
        Assert.Single(response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task 登录失败返回_401_与_unauthorized_原因()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "alice",
            password = "wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            "{\"code\":401,\"data\":null,\"msg\":\"用户名、邮箱或密码不正确\",\"reason\":\"unauthorized\"}",
            body);
    }

    [Fact]
    public async Task 不存在的账号与错误口令返回同一文案()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        HttpResponseMessage missing = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = "password123",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("用户名、邮箱或密码不正确", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 被禁用账号返回_403()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        // 通过仓储把账号置为禁用，模拟管理员操作。
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            var user = await repository.UserByUsernameAsync("alice");
            Assert.NotNull(user);
            user!.Status = OpenAICanvas.Domain.Entities.UserStatus.UserStatusDisabled;
            await repository.SaveUserAsync(user);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "alice",
            password = "password123",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("该账号已被禁用", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 注册校验_用户名与口令规则()
    {
        HttpResponseMessage shortName = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "ab",
            password = "password123",
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortName.StatusCode);
        Assert.Contains("用户名需为 3-32 位", await shortName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage shortPassword = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "validname",
            password = "short",
        });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Contains("密码至少 8 位", await shortPassword.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非首个用户注册必须提供邮箱()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        // 第二个用户走注册开关校验，先放开注册。
        await SetRegistrationEnabledAsync(true);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "bob",
            password = "password123",
        });

        // Go 的顺序：先查注册开关，再要求邮箱，最后才校验用户名唯一性。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("请输入邮箱", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未开放注册时非首个用户被拒()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        // 未设置注册开关时回落到 CANVAS_REGISTRATION_ENABLED，默认关闭。
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "bob",
            email = "bob@example.com",
            password = "password123",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("管理员未开放新用户注册", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 登出清除_Cookie_且对无效_Cookie_也返回成功()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":0,\"data\":{\"ok\":true},\"msg\":\"ok\"}", await response.Content.ReadAsStringAsync());

        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        // Go 对 MaxAge<0 输出 Max-Age=0。
        Assert.Equal("open_ai_canvas_session=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax", setCookie);
    }

    [Fact]
    public async Task 验证码路由在未启用邮件时返回_403()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });
        await SetRegistrationEnabledAsync(true);

        // 默认域名白名单（gmail/163/126/qq/outlook/hotmail/icloud/yahoo/foxmail）之外先被拒。
        HttpResponseMessage outsideWhitelist = await _client.PostAsJsonAsync("/api/auth/email-code", new
        {
            email = "new@example.com",
        });
        Assert.Equal(HttpStatusCode.BadRequest, outsideWhitelist.StatusCode);
        Assert.Contains(
            "该邮箱域名不在管理员设置的白名单内",
            await outsideWhitelist.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 白名单内域名走到邮件启用判断 → 403。
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/email-code", new
        {
            email = "new@qq.com",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("平台尚未启用注册邮件", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 登录限流触发_429_并带_Retry_After()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });

        // 账号维度上限是 10 次/10 分钟，第 11 次必须被限流。
        HttpResponseMessage? limited = null;
        for (int attempt = 0; attempt < 11; attempt++)
        {
            limited = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                username = "alice",
                password = "wrong-password",
            });
        }

        Assert.NotNull(limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited!.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        string body = await limited.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":42901", body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"rate_limited\"", body, StringComparison.Ordinal);
        Assert.Contains("请求次数已达上限", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非法_JSON_请求体返回_400()
    {
        HttpResponseMessage response = await _client.PostAsync(
            "/api/auth/login",
            new StringContent("{not-json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>直接写注册开关设置，绕过尚未实现的管理员设置路由。</summary>
    private async Task SetRegistrationEnabledAsync(bool enabled)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();

        await repository.SaveSystemSettingAsync(new OpenAICanvas.Domain.Entities.SystemSetting
        {
            Key = "registration",
            ValueJSON = $"{{\"enabled\":{(enabled ? "true" : "false")}}}",
            UpdatedBy = "test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    // ------------------------------------------------------------ 会话恢复

    [Fact]
    public async Task 未登录时_会话恢复返回_user_null()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(JsonValueKind.Null, data.GetProperty("user").ValueKind);
        // 未登录时其他字段不出现（与 Go 的 gin.H{"user": nil} 一致）。
        Assert.False(data.TryGetProperty("logicalModels", out _));
    }

    [Fact]
    public async Task 登录后会话恢复返回完整用户信息()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "alice",
            password = "password123",
        });
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "alice",
            password = "password123",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        using HttpClient alice = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        // 会话 Cookie 以请求头形式回带（工厂客户端与 CookieContainer 不共享）。
        alice.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);

        HttpResponseMessage response = await alice.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        JsonElement user = data.GetProperty("user");
        Assert.Equal("alice", user.GetProperty("username").GetString());
        // 该用例里 alice 是首个注册用户，Go 约定首用户成为管理员。
        Assert.Equal("admin", user.GetProperty("role").GetString());

        // logicalModels 当前为空数组（等逻辑模型专项实现后替换）。
        Assert.Equal(JsonValueKind.Array, data.GetProperty("logicalModels").ValueKind);

        // runtimeLimits 必须包含核心字段。
        JsonElement limits = data.GetProperty("runtimeLimits");
        Assert.True(limits.GetProperty("activeTaskLimit").GetInt64() > 0);
        Assert.True(limits.GetProperty("resourceUploadMB").GetInt64() > 0);
        Assert.True(limits.GetProperty("recycleBinRetentionDays").GetInt64() > 0);

        // drawingEngine 必须包含默认引擎。
        JsonElement engine = data.GetProperty("drawingEngine");
        Assert.False(string.IsNullOrEmpty(engine.GetProperty("defaultEngine").GetString()));

        // features 必须包含前台模型目录开关。
        JsonElement features = data.GetProperty("features");
        Assert.False(features.GetProperty("frontendModelsEnabled").GetBoolean());
    }

}
