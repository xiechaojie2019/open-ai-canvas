#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 平台设置管理路由（registration/email/linuxdo/credits）的端到端契约测试。
/// 对应 Go: <c>handler/finance.go</c> / <c>auth.go</c> 的设置部分与
/// <c>auth/registration.go</c>、<c>auth/email.go</c>、<c>app/credit_policy.go</c>。
/// </summary>
public sealed class AdminSettingsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AdminSettingsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-settings-{Guid.NewGuid():N}");
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
    public async Task 未登录访问设置返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/settings/registration");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 注册开关_读写与持久化()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/registration");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        // 首用户注册后默认关闭（非首用户场景）。
        JsonElement setting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(setting.GetProperty("enabled").GetBoolean());

        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            "/api/admin/settings/registration", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedSetting = (await ReadDataAsync(updated)).GetProperty("setting");
        Assert.True(updatedSetting.GetProperty("enabled").GetBoolean());
        Assert.False(string.IsNullOrEmpty(updatedSetting.GetProperty("updatedBy").GetString()));

        // 回读持久化。
        HttpResponseMessage reread = await admin.GetAsync("/api/admin/settings/registration");
        Assert.True((await ReadDataAsync(reread)).GetProperty("setting").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task 邮件设置_默认值与启用校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/email");
        JsonElement setting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(setting.GetProperty("enabled").GetBoolean());
        Assert.False(setting.GetProperty("hasPassword").GetBoolean());
        // 默认注册域名白名单。
        Assert.True(setting.GetProperty("registrationAllowedDomains").GetArrayLength() > 0);

        // 启用但缺 SMTP 主机 → 400。
        HttpResponseMessage invalid = await admin.PatchAsJsonAsync(
            "/api/admin/settings/email", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "启用邮件前请完整填写 SMTP 主机、端口和发件邮箱",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 完整配置 → 成功，hasPassword 为真。
        HttpResponseMessage saved = await admin.PatchAsJsonAsync("/api/admin/settings/email", new
        {
            enabled = true,
            host = "smtp.example.com",
            port = 587,
            username = "user",
            password = "secret",
            encryption = "starttls",
            fromEmail = "noreply@example.com",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement savedSetting = (await ReadDataAsync(saved)).GetProperty("setting");
        Assert.True(savedSetting.GetProperty("enabled").GetBoolean());
        Assert.True(savedSetting.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("smtp.example.com", savedSetting.GetProperty("host").GetString());
    }

    [Fact]
    public async Task 邮件设置_域名归一化与用户名缺密码()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 用户名有密码无 → 400。
        HttpResponseMessage missingPassword = await admin.PatchAsJsonAsync("/api/admin/settings/email", new
        {
            enabled = true,
            host = "smtp.example.com",
            port = 465,
            username = "user",
            fromEmail = "noreply@example.com",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);
        Assert.Contains(
            "SMTP 用户名已填写，请同时填写密码",
            await missingPassword.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 域名归一化：去 @ 前缀、去尾点、小写、去重。
        HttpResponseMessage saved = await admin.PatchAsJsonAsync("/api/admin/settings/email", new
        {
            enabled = false,
            registrationAllowedDomains = new[] { "@QQ.com", "gmail.com.", "GMAIL.COM", "  " },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement domains = (await ReadDataAsync(saved)).GetProperty("setting")
            .GetProperty("registrationAllowedDomains");
        Assert.Equal(2, domains.GetArrayLength());
        Assert.Equal("qq.com", domains[0].GetString());
        Assert.Equal("gmail.com", domains[1].GetString());
    }

    [Fact]
    public async Task 积分策略_读写与校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/credits");
        JsonElement policy = (await ReadDataAsync(initial)).GetProperty("policy");
        Assert.Equal(10_000, policy.GetProperty("defaultMultiplierBasisPoints").GetInt64());

        // 越界倍率 → 400。
        HttpResponseMessage invalid = await admin.PatchAsJsonAsync("/api/admin/settings/credits", new
        {
            signupBonusMicrocredits = 100_000_000,
            checkinBonusMicrocredits = 10_000_000,
            defaultMultiplierBasisPoints = 2_000_000,
            modelMultiplierBasisPoints = new Dictionary<string, long>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(
            "默认模型倍率必须在 0.0001-100 之间",
            await invalid.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 合法更新。
        HttpResponseMessage saved = await admin.PatchAsJsonAsync("/api/admin/settings/credits", new
        {
            signupBonusMicrocredits = 50_000_000,
            checkinBonusMicrocredits = 5_000_000,
            defaultMultiplierBasisPoints = 12_000,
            modelMultiplierBasisPoints = new Dictionary<string, long> { ["seedance-video"] = 20_000 },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement savedPolicy = (await ReadDataAsync(saved)).GetProperty("policy");
        Assert.Equal(50_000_000, savedPolicy.GetProperty("signupBonusMicrocredits").GetInt64());
        Assert.Equal(20_000, savedPolicy.GetProperty("modelMultiplierBasisPoints")
            .GetProperty("seedance-video").GetInt64());
    }

    [Fact]
    public async Task LinuxDO设置_读取与局部更新保留密钥()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/linuxdo");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement setting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(setting.GetProperty("enabled").GetBoolean());
        Assert.False(setting.GetProperty("hasClientSecret").GetBoolean());

        // 启用但配置不完整 → 400（Go 语义）。
        HttpResponseMessage incomplete = await admin.PatchAsJsonAsync("/api/admin/settings/linuxdo", new
        {
            enabled = true,
            clientId = "client-1",
            clientSecret = "secret-1",
        });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Contains(
            "启用 Linux.do 登录前请完整填写 Client、端点和回调配置",
            await incomplete.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        HttpResponseMessage saved = await admin.PatchAsJsonAsync("/api/admin/settings/linuxdo", new
        {
            enabled = true,
            clientId = "client-1",
            clientSecret = "secret-1",
            authorizationUrl = "https://connect.linux.do/oauth2/authorize",
            tokenUrl = "https://connect.linux.do/oauth2/token",
            userInfoUrl = "https://connect.linux.do/api/user",
            redirectUrl = "https://example.com/api/auth/linuxdo/callback",
            clientAuthMethod = "client_secret_post",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement savedSetting = (await ReadDataAsync(saved)).GetProperty("setting");
        Assert.True(savedSetting.GetProperty("hasClientSecret").GetBoolean());

        // 不带 clientSecret 的更新保留旧密钥。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync("/api/admin/settings/linuxdo", new
        {
            enabled = false,
            clientId = "client-2",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedSetting = (await ReadDataAsync(updated)).GetProperty("setting");
        Assert.False(updatedSetting.GetProperty("enabled").GetBoolean());
        Assert.Equal("client-2", updatedSetting.GetProperty("clientId").GetString());
        Assert.True(updatedSetting.GetProperty("hasClientSecret").GetBoolean());
    }
}