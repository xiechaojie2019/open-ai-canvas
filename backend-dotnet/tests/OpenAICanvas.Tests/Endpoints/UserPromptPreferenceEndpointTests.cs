#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 用户提示词偏好路由的端到端契约测试。
/// 对应 Go: <c>handler/user_data.go</c> settings/prompt-templates + <c>prompts/prompt_template.go</c>。
/// </summary>
public sealed class UserPromptPreferenceEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public UserPromptPreferenceEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-pref-{Guid.NewGuid():N}");
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
            username = "prefuser",
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
    public async Task 提示词偏好_列表定制与重置()
    {
        using HttpClient user = await SignInAsync();

        // 初始列表：定义齐备，无启用模板时 template 为 null。
        HttpResponseMessage initial = await user.GetAsync("/api/settings/prompt-templates");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement preferences = (await ReadDataAsync(initial)).GetProperty("preferences");
        JsonElement planPreference = preferences.EnumerateArray()
            .First(preference => preference.GetProperty("definition").GetProperty("operation").GetString() == "storyboard_plan");
        // 启动时已播种内置默认模板：存在启用版本，无定制。
        Assert.Equal(JsonValueKind.Object, planPreference.GetProperty("template").ValueKind);
        Assert.False(planPreference.GetProperty("outdated").GetBoolean());
        // customization 是 omitempty 指针：无定制时整个字段省略（与 Go 一致）。
        Assert.False(planPreference.TryGetProperty("customization", out _));

        // 未知操作 → 400。
        HttpResponseMessage unknown = await user.PatchAsJsonAsync(
            "/api/settings/prompt-templates/no-such-op", new { mode = "append", content = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("不支持的提示词模板类型", await ReadMessageAsync(unknown));

        // 追加定制成功。
        HttpResponseMessage appended = await user.PatchAsJsonAsync(
            "/api/settings/prompt-templates/storyboard_plan", new { mode = "append", content = "  多用特写  " });
        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
        JsonElement customization = (await ReadDataAsync(appended)).GetProperty("customization");
        Assert.Equal("append", customization.GetProperty("mode").GetString());
        Assert.Equal("多用特写", customization.GetProperty("content").GetString());
        Assert.False(customization.GetProperty("baseTemplateId").GetString().Length == 0);

        // 列表里出现定制且不 outdated。
        HttpResponseMessage listed = await user.GetAsync("/api/settings/prompt-templates");
        JsonElement listedPreference = (await ReadDataAsync(listed)).GetProperty("preferences")
            .EnumerateArray()
            .First(preference => preference.GetProperty("definition").GetProperty("operation").GetString() == "storyboard_plan");
        Assert.Equal("多用特写", listedPreference.GetProperty("customization").GetProperty("content").GetString());
        Assert.False(listedPreference.GetProperty("outdated").GetBoolean());

        // append 空 content → 400。
        HttpResponseMessage blank = await user.PatchAsJsonAsync(
            "/api/settings/prompt-templates/storyboard_plan", new { mode = "append", content = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("请填写个人提示词要求", await ReadMessageAsync(blank));

        // inherit 模式清空 content。
        HttpResponseMessage inherit = await user.PatchAsJsonAsync(
            "/api/settings/prompt-templates/storyboard_plan", new { mode = "inherit", content = "x" });
        Assert.Equal(HttpStatusCode.OK, inherit.StatusCode);
        Assert.Equal(
            "",
            (await ReadDataAsync(inherit)).GetProperty("customization").GetProperty("content").GetString());

        // 重置。
        HttpResponseMessage reset = await user.DeleteAsync("/api/settings/prompt-templates/storyboard_plan");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True((await ReadDataAsync(reset)).GetProperty("ok").GetBoolean());
        HttpResponseMessage afterReset = await user.GetAsync("/api/settings/prompt-templates");
        Assert.False((await ReadDataAsync(afterReset)).GetProperty("preferences")
            .EnumerateArray()
            .First(preference => preference.GetProperty("definition").GetProperty("operation").GetString() == "storyboard_plan")
            .TryGetProperty("customization", out _));
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/settings/prompt-templates")).StatusCode);
    }
}
