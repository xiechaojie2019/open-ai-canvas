#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 任务创建（文本回放路径）/ SSE / 时间线转写路由的端到端契约测试。
/// 对应 Go: <c>handler/routes.go</c> 任务写路径 + <c>app/task_creation.go</c> /
/// <c>app/task_timeline.go</c>。
/// </summary>
public sealed class TaskCreationEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public TaskCreationEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-tcreate-{Guid.NewGuid():N}");
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
            username = "creator",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _userClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _userClient;
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
    public async Task 文本回放任务_创建增量与SSE()
    {
        using HttpClient user = await SignInAsync();

        // 缺 prompt → Go 原始英文错误。
        HttpResponseMessage missingPrompt = await user.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            input = new { replay = true },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingPrompt.StatusCode);
        Assert.Equal("prompt is required", await ReadMessageAsync(missingPrompt));

        // 未知类型 → 400。
        HttpResponseMessage badType = await user.PostAsJsonAsync("/api/tasks", new
        {
            type = "no_such_type",
            prompt = "p",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Contains("不支持的任务类型", await ReadMessageAsync(badType));

        // 创建回放任务。
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/tasks", new
        {
            type = "canvas_text",
            prompt = "写一个故事",
            operation = "op-1",
            input = new { replay = true, apiKey = "sk-secret-value", mode = "text" },
        });
        if (!created.IsSuccessStatusCode)
        {
            Assert.Fail(await created.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement task = await ReadDataAsync(created);
        string taskId = task.GetProperty("id").GetString()!;
        Assert.Equal("text_replay", task.GetProperty("status").GetString());
        Assert.Equal("文本持久化（前端自管）", task.GetProperty("stage").GetString());

        // 输出脱敏：inputJSON 只保留白名单字段（mode 在白名单内，apiKey 剔除）。
        JsonElement inputJson = JsonDocument.Parse(task.GetProperty("inputJson").GetString()!).RootElement;
        Assert.Equal("text", inputJson.GetProperty("mode").GetString());
        Assert.False(inputJson.TryGetProperty("apiKey", out _));

        // 追加增量 → 文本回放可读。
        HttpResponseMessage appended = await user.PostAsJsonAsync(
            $"/api/tasks/{taskId}/text-deltas", new { content = "第一章" });
        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);

        HttpResponseMessage replayed = await user.GetAsync($"/api/tasks/{taskId}/text-deltas");
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(
            "第一章",
            (await ReadDataAsync(replayed)).GetProperty("deltas")[0].GetProperty("content").GetString());

        // 非法游标 → 400。
        HttpResponseMessage badCursor = await user.GetAsync(
            $"/api/tasks/{taskId}/text-events?after=abc");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        Assert.Equal("after 或 Last-Event-ID 必须是非负整数", await ReadMessageAsync(badCursor));

        // SSE：未完成任务时不终结，先发出 connected + progress；读到首块后中止。
        HttpRequestMessage sse = new(HttpMethod.Get, $"/api/tasks/{taskId}/text-events");
        using HttpResponseMessage sseResponse = await user.SendAsync(
            sse, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);
        Assert.Contains(
            "text/event-stream",
            sseResponse.Content.Headers.ContentType?.MediaType ?? "",
            StringComparison.Ordinal);
        System.IO.Stream sseStream = await sseResponse.Content.ReadAsStreamAsync();
        System.Text.StringBuilder headBuilder = new();
        byte[] buffer = new byte[1024];
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int read = await sseStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read <= 0)
            {
                break;
            }
            headBuilder.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            if (headBuilder.ToString().Contains("event: progress", StringComparison.Ordinal))
            {
                break;
            }
        }
        string head = headBuilder.ToString();
        Assert.Contains(": connected", head, StringComparison.Ordinal);
        Assert.Contains("event: progress", head, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 队列任务路径返回维护信封()
    {
        using HttpClient user = await SignInAsync();

        // 非回放任务走队列 admission（未移植）→ Go handler 对 CreateTask 所有错误
        // 统一 fail(c, 400, err)：HTTP 400 + 维护文案。
        HttpResponseMessage queued = await user.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            prompt = "普通任务",
        });
        Assert.Equal(HttpStatusCode.BadRequest, queued.StatusCode);
        Assert.Equal("服务正在维护，暂不接受新的生成任务", await ReadMessageAsync(queued));
    }

    [Fact]
    public async Task 未登录创建任务返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/tasks", new { type = "text", prompt = "p" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/tasks/t1/text-events")).StatusCode);
    }
}
