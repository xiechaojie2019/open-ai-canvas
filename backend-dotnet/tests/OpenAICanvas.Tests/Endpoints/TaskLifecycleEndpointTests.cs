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
/// 任务重试与取消路由的端到端契约测试。
/// 对应 Go: <c>handler/routes.go</c> retry/cancel + <c>app/task_lifecycle.go</c>。
/// </summary>
public sealed class TaskLifecycleEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public TaskLifecycleEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-tlife-{Guid.NewGuid():N}");
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

    private async Task<HttpClient> SignInAsync()
    {
        if (_userClient is not null)
        {
            return _userClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "lifer",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _userClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _userClient;
    }

    /// <summary>播种一条指定状态的任务（仓储直写，规避 provider 引擎）。</summary>
    private async Task<string> SeedTaskAsync(string status, string providerRequestID = "")
    {
        await using Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope =
            _factory.Services.CreateAsyncScope();
        OpenAICanvas.Persistence.Repositories.Repository repository = scope.ServiceProvider
            .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
        OpenAICanvas.Domain.Entities.User? user = (await repository.UsersAsync())[0];
        DateTime now = DateTime.UtcNow;
        OpenAICanvas.Domain.Entities.Task task = new()
        {
            ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
            UserID = user.ID,
            Type = "canvas_video",
            Status = status,
            Stage = status switch
            {
                "failed" => "生成失败",
                "cancelled" => "任务已取消",
                _ => "等待队列调度",
            },
            Progress = status switch { "failed" => 100, "cancelled" => 100, _ => 5 },
            Prompt = "种子任务",
            InputJSON = "{}",
            Provider = "local",
            Model = "seed",
            Error = status switch
            {
                "failed" => "上游返回失败",
                "cancelled" => "任务已取消",
                _ => "",
            },
            CompletedAt = status is "failed" or "cancelled" ? now : null,
            ProviderRequestID = providerRequestID,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await repository.CreateTaskWithActiveLimitAsync(task, 20);
        return task.ID;
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
    public async Task 取消排队任务_退款与幂等()
    {
        using HttpClient user = await SignInAsync();
        string taskId = await SeedTaskAsync("queued");

        HttpResponseMessage cancelled = await user.PostAsJsonAsync($"/api/tasks/{taskId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        JsonElement task = await ReadDataAsync(cancelled);
        Assert.Equal("cancelled", task.GetProperty("status").GetString());
        Assert.Equal("任务已取消", task.GetProperty("error").GetString());

        // 幂等：再取消返回 cancelled 任务。
        HttpResponseMessage again = await user.PostAsJsonAsync($"/api/tasks/{taskId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("cancelled", (await ReadDataAsync(again)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task 取消完成任务_400()
    {
        using HttpClient user = await SignInAsync();
        string taskId = await SeedTaskAsync("succeeded");

        HttpResponseMessage response = await user.PostAsJsonAsync($"/api/tasks/{taskId}/cancel", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("任务当前状态为 succeeded，无法取消", await ReadMessageAsync(response));
    }

    [Fact]
    public async Task 重试失败任务_重新入队与幂等冲突()
    {
        using HttpClient user = await SignInAsync();
        string taskId = await SeedTaskAsync("failed");

        HttpResponseMessage retried = await user.PostAsJsonAsync($"/api/tasks/{taskId}/retry", new { });
        if (!retried.IsSuccessStatusCode)
        {
            Assert.Fail(await retried.Content.ReadAsStringAsync());
        }
        JsonElement task = await ReadDataAsync(retried);
        Assert.Equal("queued", task.GetProperty("status").GetString());
        Assert.Equal("", task.GetProperty("error").GetString());

        // 已回到 queued：再次重试 → only failed or cancelled。
        HttpResponseMessage again = await user.PostAsJsonAsync($"/api/tasks/{taskId}/retry", new { });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("only failed or cancelled tasks can be retried", await ReadMessageAsync(again));
    }

    [Fact]
    public async Task 重试运行中任务_400()
    {
        using HttpClient user = await SignInAsync();
        string taskId = await SeedTaskAsync("running");

        HttpResponseMessage response = await user.PostAsJsonAsync($"/api/tasks/{taskId}/retry", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("only failed or cancelled tasks can be retried", await ReadMessageAsync(response));
    }

    [Fact]
    public async Task 查询上游_非失败任务_400()
    {
        using HttpClient user = await SignInAsync();
        string taskId = await SeedTaskAsync("queued");

        HttpResponseMessage response = await user.PostAsJsonAsync($"/api/tasks/{taskId}/query-provider", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("只能人工查询状态为失败的任务", await ReadMessageAsync(response));
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/tasks/t1/retry", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/tasks/t1/cancel", new { })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/tasks/t1/query-provider", new { })).StatusCode);
    }
}

/// <summary>测试辅助：ID 生成入口。</summary>
internal static class ServiceProviderKey
{
    public static string NewId() => OpenAICanvas.Domain.Kernel.IdGenerator.NewId();
}
