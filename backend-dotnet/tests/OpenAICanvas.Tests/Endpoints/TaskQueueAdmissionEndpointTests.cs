#nullable enable
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
/// 队列任务 admission 路由的端到端契约测试：前台模型路径、系统渠道路径、
/// 计费预留与守卫文案。对应 Go: <c>task_creation.go</c> 队列分支。
/// </summary>
public sealed class TaskQueueAdmissionEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public TaskQueueAdmissionEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-tq-{Guid.NewGuid():N}");
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

    private async Task EnableFrontendModelsAsync(HttpClient admin)
    {
        HttpResponseMessage patched = await admin.PatchAsJsonAsync(
            "/api/admin/settings/features", new { frontendModelsEnabled = true });
        patched.EnsureSuccessStatusCode();
    }

    private async Task<string> CreateLogicalModelAsync(HttpClient admin)
    {
        await using (Microsoft.Extensions.DependencyInjection.AsyncServiceScope scope =
            _factory.Services.CreateAsyncScope())
        {
            OpenAICanvas.Persistence.Repositories.Repository repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            DateTime now = DateTime.UtcNow;
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.ModelChannel
            {
                ID = "CH_SYS",
                UserID = "USR_SYSTEM",
                Scope = "system",
                Enabled = true,
                Name = "内置渠道",
                ModelsJSON = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
            const string textCapabilityJson = """
                {"version":1,"text":{"references":{"promptMaxChars":1000},"systemPrompt":{"supported":true,"default":""},"maxOutputTokens":{"selection":"range","min":1,"max":8192,"default":2048},"temperature":{"supported":true,"min":0,"max":2,"step":0.1,"default":1}}}
                """;
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.ChannelModel
            {
                ID = "CM_TASK",
                ChannelID = "CH_SYS",
                ModelKey = "gpt-text-exec",
                ProviderModelKey = "gpt-text-exec",
                DisplayName = "GPT 执行",
                Capability = "text",
                Protocol = "openai",
                BillingMode = "fixed_request",
                PriceConfigured = true,
                Enabled = true,
                PriceVersion = 1,
                CapabilityConfigJSON = textCapabilityJson,
                CapabilityVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/logical-models", new
        {
            code = "gpt-text",
            name = "GPT 文本",
            capability = "text",
            enabled = true,
            sortOrder = 1,
            pricePolicy = "unified",
            billingMode = "fixed_request",
            unitPriceMicrocredits = 100,
            capabilitySpec = new
            {
                version = 1,
                capability = "text",
                operations = new[] { "chapter_outline" },
                inputs = new { },
                options = new { },
            },
            routes = new[] { new { channelModelId = "CM_TASK", enabled = true, priority = 100, weight = 100 } },
        });
        if (!created.IsSuccessStatusCode)
        {
            Assert.Fail(await created.Content.ReadAsStringAsync());
        }
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("model").GetProperty("id").GetString()!;
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
    public async Task 队列任务_前台模型路径_无积分功能免费入队()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await EnableFrontendModelsAsync(admin);
        string logicalModelId = await CreateLogicalModelAsync(admin);

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            prompt = "写一首诗",
            logicalModelId,
            input = new
            {
                mode = "text",
                config = new { prompt = "x" },
            },
        });
        if (!created.IsSuccessStatusCode)
        {
            Assert.Fail(await created.Content.ReadAsStringAsync());
        }
        JsonElement task = await ReadDataAsync(created);
        Assert.Equal("queued", task.GetProperty("status").GetString());
        Assert.Equal("等待队列调度", task.GetProperty("stage").GetString());
        Assert.Equal("managed", task.GetProperty("provider").GetString());
        Assert.Equal("gpt-text", task.GetProperty("model").GetString());
        // 对外输出脱敏：供应线路内部字段清空（omitempty → 字段整体省略）。
        Assert.False(task.TryGetProperty("logicalModelRevisionId", out _));
        Assert.False(task.TryGetProperty("routeId", out _));
        Assert.False(task.TryGetProperty("channelModelId", out _));
        // 任务日志已写。
        HttpResponseMessage logs = await admin.GetAsync($"/api/tasks/{task.GetProperty("id").GetString()}/logs");
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        // Go: ok(c, logs) —— data 本身就是数组。
        Assert.True((await ReadDataAsync(logs)).GetArrayLength() > 0);
    }

    [Fact]
    public async Task 队列任务_前台未指定模型_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await EnableFrontendModelsAsync(admin);

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            prompt = "x",
            input = new { mode = "text", config = new { } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("前台模型模式下必须指定 logicalModelId", await ReadMessageAsync(created));
    }

    [Fact]
    public async Task 队列任务_系统渠道_缺配置_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await EnableFrontendModelsAsync(admin);

        // 非系统渠道且非自定义渠道（无 baseUrl+apiKey）→ 系统渠道 admission → 缺配置。
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            prompt = "x",
            input = new { mode = "text", config = new { } },
        });
        // 前台开关开启时优先报「必须指定 logicalModelId」；关闭时走系统渠道分支报「缺少模型配置」。
        string message = await ReadMessageAsync(created);
        Assert.True(
            message == "前台模型模式下必须指定 logicalModelId" || message == "缺少模型配置",
            message);
    }

    [Fact]
    public async Task 队列任务_视频模式缺配置_400()
    {
        // 前台关闭：自定义渠道形状（baseUrl+apiKey）在选路阶段直通，
        // 随后触发 Go 的视频模式检查。
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "video_doubao",
            prompt = "x",
            input = new
            {
                mode = "text",
                config = new { baseUrl = "https://api.example.com", apiKey = "k" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("视频任务必须使用 video 模式", await ReadMessageAsync(created));
    }

    [Fact]
    public async Task 队列任务_工作流插件未启用_403()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "canvas_image",
            prompt = "x",
            input = new
            {
                mode = "image",
                config = new { interfaceType = "runninghub", baseUrl = "https://x", apiKey = "k" },
            },
        });
        // Go 的 POST /tasks handler 对 CreateTask 所有错误统一 fail(c, 400, err)。
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("RunningHub 工作流插件未启用", await ReadMessageAsync(created));
    }

    [Fact]
    public async Task 队列任务_内嵌媒体_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        await EnableFrontendModelsAsync(admin);
        string logicalModelId = await CreateLogicalModelAsync(admin);

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tasks", new
        {
            type = "text",
            prompt = "x",
            logicalModelId,
            input = new
            {
                mode = "text",
                config = new { },
                metadata = new { note = "data:image/png;base64,AAAA" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal("任务输入不能包含内嵌媒体，请先上传到资源存储", await ReadMessageAsync(created));
    }
}
