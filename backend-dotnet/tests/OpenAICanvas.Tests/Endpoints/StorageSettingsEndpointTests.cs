#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>对象存储平台和个人设置的 HTTP 合同测试。</summary>
public sealed class StorageSettingsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public StorageSettingsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-oss-{Guid.NewGuid():N}");
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
        _adminClient?.Dispose();
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
        if (_adminClient is not null) return _adminClient;
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "oss-admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _adminClient;
    }

    private async Task<HttpClient> SignInAsUserAsync()
    {
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.User
            {
                ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
                Username = "ossuser",
                DisplayName = "ossuser",
                Email = "ossuser@example.com",
                Role = "user",
                Status = "active",
                PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "ossuser",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient user = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        user.DefaultRequestHeaders.Add("Cookie", cookie);
        return user;
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("msg").GetString() ?? "";
    }

    private static object LocalStorageRequest(string? secret = null) => new
    {
        enabled = false,
        provider = "aliyun",
        publicBaseUrl = "https://canvas.example.test/files/",
        pathPrefix = " project-assets/ ",
        accessKeySecret = secret ?? "",
    };

    [Fact]
    public async Task 管理端读取默认值并拒绝未认证与普通用户()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/admin/settings/oss")).StatusCode);

        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/oss");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement setting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(setting.GetProperty("enabled").GetBoolean());
        Assert.Equal("aliyun", setting.GetProperty("provider").GetString());
        Assert.Equal("open-ai-canvas", setting.GetProperty("pathPrefix").GetString());
        Assert.False(setting.TryGetProperty("accessKeySecret", out _));
        Assert.False(setting.TryGetProperty("sessionToken", out _));

        using HttpClient user = await SignInAsUserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/settings/oss")).StatusCode);
    }

    [Fact]
    public async Task 平台保存本地存储需要访问地址且密钥加密脱敏()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage missingUrl = await admin.PatchAsJsonAsync("/api/admin/settings/oss", new
        {
            enabled = false,
            provider = "aliyun",
            publicBaseUrl = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingUrl.StatusCode);
        Assert.Equal("服务器本地存储需要填写服务器访问地址", await ReadMessageAsync(missingUrl));

        HttpResponseMessage saved = await admin.PatchAsJsonAsync("/api/admin/settings/oss", LocalStorageRequest("secret-value"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonElement savedSetting = (await ReadDataAsync(saved)).GetProperty("setting");
        Assert.True(savedSetting.GetProperty("hasAccessKeySecret").GetBoolean());
        Assert.Equal("https://canvas.example.test/files", savedSetting.GetProperty("publicBaseUrl").GetString());
        Assert.Equal("project-assets", savedSetting.GetProperty("pathPrefix").GetString());
        Assert.False(savedSetting.TryGetProperty("accessKeySecret", out _));

        Repository repository = _factory.Services.GetRequiredService<Repository>();
        OpenAICanvas.Domain.Entities.SystemSetting? record = await repository.SystemSettingAsync("oss");
        Assert.NotNull(record);
        Assert.Contains("enc:v1:", record!.ValueJSON, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", record.ValueJSON, StringComparison.Ordinal);

        HttpResponseMessage reread = await admin.GetAsync("/api/admin/settings/oss");
        JsonElement rereadSetting = (await ReadDataAsync(reread)).GetProperty("setting");
        Assert.True(rereadSetting.GetProperty("hasAccessKeySecret").GetBoolean());
        Assert.False(rereadSetting.TryGetProperty("accessKeySecret", out _));
    }

    [Fact]
    public async Task 用户S3未经平台授权时拒绝保存和测试()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        using HttpClient user = await SignInAsUserAsync();
        object request = new
        {
            enabled = false,
            provider = "s3",
            endpoint = "https://s3.example.test",
            bucket = "canvas",
            accessKeyId = "key",
            accessKeySecret = "secret",
            publicBaseUrl = "https://cdn.example.test",
        };

        HttpResponseMessage save = await user.PatchAsJsonAsync("/api/settings/oss", request);
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        HttpResponseMessage test = await user.PostAsJsonAsync("/api/settings/oss/test", request);
        Assert.Equal(HttpStatusCode.Forbidden, test.StatusCode);
    }

    [Fact]
    public async Task S3启用前必须完成同配置连接测试()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage response = await admin.PatchAsJsonAsync("/api/admin/settings/oss", new
        {
            enabled = true,
            provider = "s3",
            endpoint = "https://s3.example.test",
            bucket = "canvas",
            accessKeyId = "key",
            accessKeySecret = "secret",
            pathPrefix = "open-ai-canvas",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("S3 关键配置尚未通过连接测试", await ReadMessageAsync(response));
    }
}
