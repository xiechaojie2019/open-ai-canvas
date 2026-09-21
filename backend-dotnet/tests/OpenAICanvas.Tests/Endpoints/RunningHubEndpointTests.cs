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
/// RunningHub 管理代理与插件状态路由的契约测试。
/// 对应 Go: <c>handler/runninghub.go</c>、<c>handler/plugin.go</c> 的 <c>GET /plugins/status</c>。
/// </summary>
public sealed class RunningHubEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public RunningHubEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-rh-{Guid.NewGuid():N}");
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
        if (_adminClient is not null)
        {
            return _adminClient;
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "runninghubadmin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();

        string setCookie = response.Headers.GetValues("Set-Cookie").First();
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        _adminClient.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        return _adminClient;
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

    [Fact]
    public async Task 匿名访问_管理代理与状态读取都返回_401()
    {
        HttpResponseMessage status = await _client.GetAsync("/api/plugins/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);

        HttpResponseMessage info = await _client.PostAsJsonAsync("/api/runninghub/workflow-info", new
        {
            workflowId = "123",
            apiKey = "k",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, info.StatusCode);
    }

    [Fact]
    public async Task 登录后默认禁用_状态为_disabled_代理返回_403()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage status = await admin.GetAsync("/api/plugins/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        JsonElement data = await ReadDataAsync(status);
        Assert.Equal(
            "disabled",
            data.GetProperty("statuses").GetProperty("runninghub-workflow-provider").GetString());
        JsonElement state = data.GetProperty("states").GetProperty("runninghub-workflow-provider");
        Assert.False(state.GetProperty("platformAvailable").GetBoolean());
        Assert.False(state.GetProperty("effectiveEnabled").GetBoolean());

        HttpResponseMessage info = await admin.PostAsJsonAsync("/api/runninghub/workflow-info", new
        {
            workflowId = "123",
            apiKey = "k",
        });
        Assert.Equal(HttpStatusCode.Forbidden, info.StatusCode);
        Assert.Equal("RunningHub 工作流插件未启用", await ReadMessageAsync(info));
    }

    [Fact]
    public async Task 平台行启用后_状态为_enabled_缺workflowId返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        await using (Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope =
            _factory.Services.CreateAsyncScope())
        {
            OpenAICanvas.Persistence.Repositories.Repository repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            await repository.SavePluginPlatformStateAsync(new OpenAICanvas.Domain.Entities.PluginPlatformState
            {
                PluginID = "runninghub-workflow-provider",
                Available = true,
                UpdatedBy = "USR_ADMIN",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        HttpResponseMessage status = await admin.GetAsync("/api/plugins/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        JsonElement data = await ReadDataAsync(status);
        Assert.Equal(
            "enabled",
            data.GetProperty("statuses").GetProperty("runninghub-workflow-provider").GetString());

        HttpResponseMessage missing = await admin.PostAsJsonAsync("/api/runninghub/workflow-info", new
        {
            apiKey = "k",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("workflowId 不能为空", await ReadMessageAsync(missing));
    }
}
