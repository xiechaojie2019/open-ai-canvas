#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 风格档案与声音档案路由的端到端契约测试。
/// 对应 Go: <c>handler/style_profile.go</c> 与 <c>internal/prompts/style_profile.go</c>。
/// </summary>
public sealed class StyleProfileEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public StyleProfileEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-style-{Guid.NewGuid():N}");
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

    /// <summary>构造合法风格文档（schemaVersion/presetId/title/prompt/revision/assets 齐备）。</summary>
    private static object ProfileDocument(
        string presetId = "preset-a", string title = "电影感", string coverUrl = "https://example.com/c.png") => new
    {
        schemaVersion = 1,
        presetId,
        title,
        description = "简介",
        tags = new[] { "cinematic", "moody" },
        prompt = "cinematic lighting",
        negativePrompt = "blurry",
        coverUrl,
        assets = Array.Empty<object>(),
        executionPolicy = "compatible-fallback",
        source = "builtin",
        revision = 1,
    };

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 未登录访问风格列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/style-profiles");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 风格CRUD_归一化与收藏与使用()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 创建：source 被归一为 user，revision 归一为 1。
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/style-profiles", new
        {
            profileJson = JsonSerializer.Serialize(ProfileDocument()),
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement profile = (await ReadDataAsync(created)).GetProperty("profile");
        string id = profile.GetProperty("id").GetString()!;
        Assert.Equal("电影感", profile.GetProperty("name").GetString());
        Assert.False(profile.GetProperty("favorite").GetBoolean());
        Assert.Equal(1, profile.GetProperty("revision").GetInt64());
        // 标签以 JSON 数组存储。
        Assert.Contains("cinematic", profile.GetProperty("tagsJson").GetString(), StringComparison.Ordinal);

        // 列表。
        HttpResponseMessage list = await admin.GetAsync("/api/style-profiles");
        Assert.Equal(1, (await ReadDataAsync(list)).GetProperty("profiles").GetArrayLength());

        // 更新：revision +1。
        HttpResponseMessage updated = await admin.PatchAsJsonAsync($"/api/style-profiles/{id}", new
        {
            profileJson = JsonSerializer.Serialize(ProfileDocument(title: "电影感 V2")),
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedProfile = (await ReadDataAsync(updated)).GetProperty("profile");
        Assert.Equal("电影感 V2", updatedProfile.GetProperty("name").GetString());
        Assert.Equal(2, updatedProfile.GetProperty("revision").GetInt64());

        // 收藏。
        HttpResponseMessage favorite = await admin.PatchAsJsonAsync(
            $"/api/style-profiles/{id}/favorite", new { favorite = true });
        Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        Assert.True((await ReadDataAsync(favorite)).GetProperty("favorite").GetBoolean());

        // 使用（记录 lastUsedAt）。
        HttpResponseMessage used = await admin.PostAsJsonAsync($"/api/style-profiles/{id}/use", new { });
        Assert.Equal(HttpStatusCode.OK, used.StatusCode);

        // 删除。
        HttpResponseMessage deleted = await admin.DeleteAsync($"/api/style-profiles/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(await admin.GetAsync("/api/style-profiles")))
            .GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public async Task 风格校验_必填字段与来源与封面()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 缺 presetId → 400。
        HttpResponseMessage missingPreset = await admin.PostAsJsonAsync("/api/style-profiles", new
        {
            profileJson = """{"schemaVersion":1,"title":"t","prompt":"p","revision":1,"assets":[],"source":"builtin"}""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingPreset.StatusCode);
        Assert.Contains(
            "项目画风资产配置缺少必要字段",
            await missingPreset.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // 非法来源 → 400。
        HttpResponseMessage badSource = await admin.PostAsJsonAsync("/api/style-profiles", new
        {
            profileJson = """{"schemaVersion":1,"presetId":"p","title":"t","prompt":"p","revision":1,"assets":[],"source":"imported"}""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);
        Assert.Contains("项目画风来源不支持", await badSource.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 非法封面 → 400。
        HttpResponseMessage badCover = await admin.PostAsJsonAsync("/api/style-profiles", new
        {
            profileJson = JsonSerializer.Serialize(ProfileDocument(coverUrl: "ftp://example.com/c.png")),
        });
        Assert.Equal(HttpStatusCode.BadRequest, badCover.StatusCode);
        Assert.Contains(
            "风格封面只支持 http(s)、站内路径或图片 Data URL",
            await badCover.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 风格不存在时返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage favorite = await admin.PatchAsJsonAsync(
            "/api/style-profiles/STYLE_NOPE/favorite", new { favorite = true });
        Assert.Equal(HttpStatusCode.NotFound, favorite.StatusCode);

        HttpResponseMessage deleted = await admin.DeleteAsync("/api/style-profiles/STYLE_NOPE");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    /// <summary>
    /// 对应 Go: <c>ListVoiceProfiles</c>——首次调用播种 13 个内置声音，重复请求不重复插入。
    /// </summary>
    [Fact]
    public async Task 声音档案列表_播种内置声音()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/voice-profiles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement profiles = (await ReadDataAsync(response)).GetProperty("profiles");
        Assert.Equal(13, profiles.GetArrayLength());
        JsonElement alloy = profiles.EnumerateArray()
            .Single(profile => profile.GetProperty("voiceKey").GetString() == "alloy");
        Assert.Equal("Alloy", alloy.GetProperty("name").GetString());
        Assert.Equal("openai_compatible", alloy.GetProperty("provider").GetString());
        Assert.Equal("多语言", alloy.GetProperty("language").GetString());
        Assert.Equal("active", alloy.GetProperty("status").GetString());
        Assert.False(alloy.TryGetProperty("sampleResourceId", out _));

        HttpResponseMessage again = await admin.GetAsync("/api/voice-profiles");
        Assert.Equal(13, (await ReadDataAsync(again)).GetProperty("profiles").GetArrayLength());
    }
}