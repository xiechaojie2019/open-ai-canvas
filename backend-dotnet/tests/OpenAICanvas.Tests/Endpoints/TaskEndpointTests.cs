#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 任务读取与文本回放路由的端到端契约测试。
/// </summary>
/// <remarks>
/// 覆盖 Go <c>handler/routes.go</c> 中不依赖 provider 执行引擎的部分：
/// 列表、详情、日志、文本增量、文本回放收尾、回放统计。
/// </remarks>
public sealed class TaskEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TaskEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-task-{Guid.NewGuid():N}");
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

    // ------------------------------------------------------------ 列表与详情

    [Fact]
    public async Task 未登录访问任务列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 空任务列表返回空数组()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Equal(0, data.GetArrayLength());
    }

    [Fact]
    public async Task 任务列表返回摘要且不含渠道内部字段()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", prompt: new string('x', 600));

        HttpResponseMessage response = await user.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetArrayLength());

        JsonElement item = data[0];
        Assert.Equal("task-1", item.GetProperty("id").GetString());
        Assert.Equal("text", item.GetProperty("type").GetString());
        Assert.Equal("queued", item.GetProperty("status").GetString());

        // prompt 超过 500 rune 必须截断并追加省略号（Go kernel.TruncateRunes 语义）。
        string prompt = item.GetProperty("prompt").GetString()!;
        Assert.Equal(503, prompt.Length);
        Assert.EndsWith("...", prompt, StringComparison.Ordinal);

        // 渠道模型与供应线路属于管理员内部信息，绝不能出现在普通用户接口。
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("channelModelId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("routeId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("logicalModelRevisionId", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 任务列表按项目过滤()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-a", projectId: "proj-1");
        await CreateTaskAsync(userId, "task-b", projectId: "proj-2");

        HttpResponseMessage response = await user.GetAsync("/api/tasks?projectId=proj-1");

        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("task-a", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task 任务列表_pageSize_非正整数返回_400()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/tasks?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 任务详情返回投影并抹掉渠道字段()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", channelModelId: "cm-secret", routeId: "route-secret");

        HttpResponseMessage response = await user.GetAsync("/api/tasks/task-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal("task-1", data.GetProperty("id").GetString());

        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("cm-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("route-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 任务详情对他人任务返回_404()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateTaskAsync(aliceId, "alice-task");

        using HttpClient bob = await SignInAsync("bob", secondUser: true);
        HttpResponseMessage response = await bob.GetAsync("/api/tasks/alice-task");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 任务日志返回数组()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1");
        await CreateTaskLogAsync(userId, "task-1", "info", "开始生成");

        HttpResponseMessage response = await user.GetAsync("/api/tasks/task-1/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("开始生成", data[0].GetProperty("message").GetString());

        // TraceID / RequestID 是 json:"-"，不能出现在响应里。
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("traceId", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 文本增量

    [Fact]
    public async Task 文本任务可以追加并重放增量()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text_generation", status: TaskStatus.TaskStatusRunning);

        HttpResponseMessage append = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-deltas", new { content = "你好" });
        Assert.Equal(HttpStatusCode.OK, append.StatusCode);
        JsonElement delta = await ReadDataAsync(append);
        Assert.Equal(1, delta.GetProperty("sequence").GetInt64());
        Assert.Equal("你好", delta.GetProperty("content").GetString());

        await user.PostAsJsonAsync("/api/tasks/task-1/text-deltas", new { content = "世界" });

        HttpResponseMessage replay = await user.GetAsync("/api/tasks/task-1/text-deltas");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        JsonElement result = await ReadDataAsync(replay);
        Assert.Equal(2, result.GetProperty("deltas").GetArrayLength());
        Assert.False(result.GetProperty("complete").GetBoolean());
        Assert.Equal("running", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 文本增量游标只返回其后的条目()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusRunning);

        await user.PostAsJsonAsync("/api/tasks/task-1/text-deltas", new { content = "A" });
        await user.PostAsJsonAsync("/api/tasks/task-1/text-deltas", new { content = "B" });

        HttpResponseMessage replay = await user.GetAsync("/api/tasks/task-1/text-deltas?after=1");

        JsonElement result = await ReadDataAsync(replay);
        Assert.Equal(1, result.GetProperty("deltas").GetArrayLength());
        Assert.Equal("B", result.GetProperty("deltas")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task 文本增量游标非法返回_400()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text");

        HttpResponseMessage response = await user.GetAsync("/api/tasks/task-1/text-deltas?after=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 非文本任务不能追加增量()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "image_generation");

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-deltas", new { content = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("只有文本生成任务支持增量回放", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 已结束任务不能追加增量()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusSucceeded);

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-deltas", new { content = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("已结束任务不能继续写入文本增量", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空增量返回_400()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusRunning);

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-deltas", new { content = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ 文本回放收尾

    [Fact]
    public async Task 文本回放收尾写入正文并置为成功()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusTextReplay);

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-replay-complete", new { text = "最终正文" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement task = await ReadDataAsync(response);
        Assert.Equal("succeeded", task.GetProperty("status").GetString());
        Assert.Equal(100, task.GetProperty("progress").GetInt64());

        // 重放接口此时应能读到 finalText。
        HttpResponseMessage replay = await user.GetAsync("/api/tasks/task-1/text-deltas");
        JsonElement result = await ReadDataAsync(replay);
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.Equal("最终正文", result.GetProperty("finalText").GetString());
    }

    [Fact]
    public async Task 文本回放收尾对非回放态任务返回_400()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusRunning);

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-replay-complete", new { text = "正文" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("该文本任务已结束或不属于你", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 文本回放收尾拒绝空正文()
    {
        using HttpClient user = await SignInAsync("alice");
        string userId = await CurrentUserIdAsync(user);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusTextReplay);

        HttpResponseMessage response = await user.PostAsJsonAsync(
            "/api/tasks/task-1/text-replay-complete", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("文本内容不能为空", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 回放统计

    [Fact]
    public async Task 回放统计对管理员返回统计字段()
    {
        using HttpClient admin = await SignInAsync("admin");
        string userId = await CurrentUserIdAsync(admin);
        await CreateTaskAsync(userId, "task-1", type: "text", status: TaskStatus.TaskStatusRunning);
        await admin.PostAsJsonAsync("/api/tasks/task-1/text-deltas", new { content = "abc" });

        HttpResponseMessage response = await admin.GetAsync("/api/admin/text-replay-stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(1, data.GetProperty("eventCount").GetInt64());
        Assert.Equal(1, data.GetProperty("taskCount").GetInt64());
        Assert.Equal(3, data.GetProperty("byteCount").GetInt64());
    }

    [Fact]
    public async Task 回放统计对非管理员返回_403()
    {
        // 首个注册用户会成为管理员，所以要先占位，再建普通用户。
        using HttpClient _ = await SignInAsync("root");
        using HttpClient user = await SignInAsync("bob", secondUser: true);

        HttpResponseMessage response = await user.GetAsync("/api/admin/text-replay-stats");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------ 辅助

    private async Task<HttpClient> SignInAsync(string username, bool secondUser = false)
    {
        if (secondUser)
        {
            // 非首个用户走 API 注册需要邮箱验证码，直接落库更简单。
            await CreateUserAsync(username);
            HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                username,
                password = "password123",
            });
            login.EnsureSuccessStatusCode();
            return Authenticated(login);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        return Authenticated(response);
    }

    private HttpClient Authenticated(HttpResponseMessage response)
    {
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<string> CurrentUserIdAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/auth/session");
        JsonElement data = await ReadDataAsync(response);
        return data.GetProperty("user").GetProperty("id").GetString()!;
    }

    private async Task CreateUserAsync(string username)
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
    }

    private async Task CreateTaskAsync(
        string userId,
        string id,
        string type = "text",
        string status = TaskStatus.TaskStatusQueued,
        string prompt = "hello",
        string projectId = "",
        string channelModelId = "",
        string routeId = "")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new TaskEntity
        {
            ID = id,
            UserID = userId,
            ProjectID = projectId,
            Type = type,
            Status = status,
            Stage = "排队中",
            Progress = 0,
            Prompt = prompt,
            ChannelModelID = channelModelId,
            RouteID = routeId,
            LogicalModelRevisionID = "rev-secret",
            InputJSON = "{}",
            ResultJSON = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CreateTaskLogAsync(string userId, string taskId, string level, string message)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new TaskLog
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            TaskID = taskId,
            TraceID = "trace-1",
            RequestID = "request-1",
            Level = level,
            Message = message,
            Payload = "",
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
