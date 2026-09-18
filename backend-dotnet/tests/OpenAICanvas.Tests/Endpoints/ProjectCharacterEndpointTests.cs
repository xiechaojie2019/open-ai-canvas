#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 项目角色与配音路由的端到端契约测试。
/// 对应 Go: <c>handler/project.go</c> characters 部分 + <c>app/project_character.go</c>。
/// </summary>
public sealed class ProjectCharacterEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ProjectCharacterEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-char-{Guid.NewGuid():N}");
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
            username = "charmer",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _userClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _userClient.DefaultRequestHeaders.Add("Cookie", cookie);
        return _userClient;
    }

    private async Task<string> CreateProjectAsync(HttpClient user)
    {
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/projects", new { name = "角色项目" });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("project").GetProperty("id").GetString()!;
    }

    private async Task<(string ProjectId, string AssetId)> CreateCharacterAsync(
        HttpClient user, object? request = null)
    {
        string projectId = await CreateProjectAsync(user);
        HttpResponseMessage created = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/characters",
            request ?? new { name = "  林小雨  ", definition = new { gender = "female", age = 22 } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        JsonElement data = document.RootElement.GetProperty("data").Clone();
        return (projectId, data.GetProperty("asset").GetProperty("id").GetString()!);
    }

    /// <summary>经分片上传创建一张就绪图片资源。</summary>
    private async Task<string> UploadImageAsync(HttpClient user)
    {
        byte[] payload = new byte[2048];
        new Random(11).NextBytes(payload);
        HttpResponseMessage started = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "sheet.png",
            kind = "image",
            size = payload.LongLength,
            width = 16,
            height = 16,
            durationMs = 0,
        });
        started.EnsureSuccessStatusCode();
        using JsonDocument startDoc = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        string uploadId = startDoc.RootElement.GetProperty("data").GetProperty("uploadId").GetString()!;

        HttpResponseMessage chunk = await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload));
        chunk.EnsureSuccessStatusCode();
        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        using JsonDocument doneDoc = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        return doneDoc.RootElement.GetProperty("data").GetProperty("resource").GetProperty("id").GetString()!;
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
    public async Task 未登录访问角色路由返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/projects/p1/characters/a1")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/voice-profiles")).StatusCode);
    }

    [Fact]
    public async Task 角色创建读取更新_版本链与角色卡内嵌()
    {
        HttpClient user = await SignInAsync();
        string projectId = await CreateProjectAsync(user);

        // 创建：名称被修剪，definition 键序输出，visualStatus/voiceStatus 初始 missing。
        HttpResponseMessage created = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/characters",
            new { name = "  林小雨  ", definition = new { gender = "female", age = 22 } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement detail = await ReadDataAsync(created);
        JsonElement character = detail.GetProperty("character");
        JsonElement asset = detail.GetProperty("asset");
        string assetId = asset.GetProperty("id").GetString()!;
        Assert.Equal("林小雨", asset.GetProperty("title").GetString());
        Assert.Equal("entity", asset.GetProperty("mediaType").GetString());
        Assert.Equal("character", asset.GetProperty("category").GetString());
        Assert.Equal("confirmed", asset.GetProperty("status").GetString());
        Assert.Equal(1, character.GetProperty("version").GetInt64());
        Assert.Equal(1, asset.GetProperty("character").GetProperty("version").GetInt64());
        Assert.Equal(22, character.GetProperty("definition").GetProperty("age").GetInt64());
        Assert.Equal("female", character.GetProperty("definition").GetProperty("gender").GetString());
        Assert.Equal("missing", character.GetProperty("visualStatus").GetString());
        Assert.Equal("missing", character.GetProperty("voiceStatus").GetString());
        Assert.Equal(0, character.GetProperty("representations").GetArrayLength());
        Assert.False(character.TryGetProperty("voice", out _));

        // 素材列表的摘要同样内嵌角色卡。
        HttpResponseMessage assets = await user.GetAsync($"/api/projects/{projectId}/assets");
        Assert.Equal(HttpStatusCode.OK, assets.StatusCode);
        JsonElement assetsData = await ReadDataAsync(assets);
        Assert.Equal(1, assetsData.GetProperty("assets").GetArrayLength());
        Assert.Equal(
            1,
            assetsData.GetProperty("assets")[0].GetProperty("character").GetProperty("version").GetInt64());

        // 读取单卡。
        HttpResponseMessage fetched = await user.GetAsync(
            $"/api/projects/{projectId}/characters/{assetId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(
            1,
            (await ReadDataAsync(fetched)).GetProperty("character").GetProperty("version").GetInt64());

        // 更新：产生版本 2，保留设定。
        HttpResponseMessage updated = await user.PatchAsJsonAsync(
            $"/api/projects/{projectId}/characters/{assetId}",
            new { name = "林小雨·重制", definition = new { gender = "female", age = 23 } });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedData = await ReadDataAsync(updated);
        Assert.Equal(2, updatedData.GetProperty("character").GetProperty("version").GetInt64());
        Assert.Equal(23, updatedData.GetProperty("character").GetProperty("definition").GetProperty("age").GetInt64());
        Assert.Equal("林小雨·重制", updatedData.GetProperty("asset").GetProperty("title").GetString());

        // 空名称 → 400。
        HttpResponseMessage blank = await user.PatchAsJsonAsync(
            $"/api/projects/{projectId}/characters/{assetId}", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("角色名称不能为空", await ReadMessageAsync(blank));

        // 创建省略 definition → {}。
        HttpResponseMessage noDefinition = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/characters", new { name = "路人甲" });
        Assert.Equal(HttpStatusCode.OK, noDefinition.StatusCode);
        Assert.Equal(
            JsonValueKind.Object,
            (await ReadDataAsync(noDefinition)).GetProperty("character").GetProperty("definition").ValueKind);
        Assert.Equal(0, (await ReadDataAsync(noDefinition)).GetProperty("character").GetProperty("definition").EnumerateObject().Count());

        // 项目不存在 → Go 原样 failInternal（500 信封）。
        HttpResponseMessage missingProject = await user.GetAsync(
            "/api/projects/nonexistent/characters/" + assetId);
        Assert.Equal(HttpStatusCode.InternalServerError, missingProject.StatusCode);
        // 角色资产不存在 → 同样 500 信封。
        HttpResponseMessage missingAsset = await user.GetAsync(
            $"/api/projects/{projectId}/characters/nonexistent");
        Assert.Equal(HttpStatusCode.InternalServerError, missingAsset.StatusCode);
    }

    [Fact]
    public async Task 形象替换_校验与整体替换()
    {
        HttpClient user = await SignInAsync();
        (string projectId, string assetId) = await CreateCharacterAsync(user);
        string baseUri = $"/api/projects/{projectId}/characters/{assetId}/representations";

        // 数量为 0 / 超过 8 → 400。
        HttpResponseMessage empty = await user.PutAsJsonAsync(baseUri, new { representations = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("角色形象数量必须在 1 到 8 之间", await ReadMessageAsync(empty));
        HttpResponseMessage overflow = await user.PutAsJsonAsync(baseUri, new
        {
            representations = Enumerable.Range(0, 9)
                .Select(i => new { role = i == 0 ? "primary" : $"x{i}", resourceId = "r" }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);

        // 视角不合法 → 400。
        HttpResponseMessage badRole = await user.PutAsJsonAsync(baseUri, new
        {
            representations = new[] { new { role = "left", resourceId = "r" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Equal("不支持的角色形象视角", await ReadMessageAsync(badRole));

        // 资源不存在 → 400（Go 按输入顺序逐项校验：第一项的资源检查先于第二项的重复检查）。
        HttpResponseMessage missingResource = await user.PutAsJsonAsync(baseUri, new
        {
            representations = new[]
            {
                new { role = "front", resourceId = "r1" },
                new { role = "front", resourceId = "r2" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingResource.StatusCode);
        Assert.Equal("角色形象资源不可用", await ReadMessageAsync(missingResource));

        // 上传真实图片：同一视角重复 → 400。
        string resourceId = await UploadImageAsync(user);
        HttpResponseMessage duplicate = await user.PutAsJsonAsync(baseUri, new
        {
            representations = new[]
            {
                new { role = "front", resourceId },
                new { role = "front", resourceId },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal("同一角色形象视角不能重复", await ReadMessageAsync(duplicate));
        HttpResponseMessage replaced = await user.PutAsJsonAsync(baseUri, new
        {
            representations = new[]
            {
                new { role = "primary", resourceId, metadata = new { source = "test" } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        JsonElement data = await ReadDataAsync(replaced);
        JsonElement character = data.GetProperty("character");
        Assert.Equal("partial", character.GetProperty("visualStatus").GetString());
        Assert.Equal(2, character.GetProperty("version").GetInt64());
        JsonElement representation = character.GetProperty("representations")[0];
        Assert.Equal("primary", representation.GetProperty("role").GetString());
        Assert.Equal(resourceId, representation.GetProperty("resourceId").GetString());
        Assert.Equal("image", representation.GetProperty("mediaType").GetString());

        // 三视图齐备 → ready。
        HttpResponseMessage turnaround = await user.PutAsJsonAsync(baseUri, new
        {
            representations = new[] { new { role = "turnaround_sheet", resourceId } },
        });
        Assert.Equal(HttpStatusCode.OK, turnaround.StatusCode);
        Assert.Equal(
            "ready",
            (await ReadDataAsync(turnaround)).GetProperty("character").GetProperty("visualStatus").GetString());
    }

    [Fact]
    public async Task 声音档案播种与绑定解绑()
    {
        HttpClient user = await SignInAsync();

        // 首次列表播种 13 个内置声音；重复请求不重复插入。
        HttpResponseMessage first = await user.GetAsync("/api/voice-profiles");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        JsonElement profilesData = await ReadDataAsync(first);
        Assert.Equal(13, profilesData.GetProperty("profiles").GetArrayLength());
        JsonElement alloy = profilesData.GetProperty("profiles")
            .EnumerateArray()
            .Single(profile => profile.GetProperty("voiceKey").GetString() == "alloy");
        Assert.Equal("Alloy", alloy.GetProperty("name").GetString());
        Assert.Equal("openai_compatible", alloy.GetProperty("provider").GetString());
        Assert.Equal("active", alloy.GetProperty("status").GetString());
        Assert.False(alloy.TryGetProperty("sampleResourceId", out _));

        HttpResponseMessage second = await user.GetAsync("/api/voice-profiles");
        Assert.Equal(13, (await ReadDataAsync(second)).GetProperty("profiles").GetArrayLength());

        (string projectId, string assetId) = await CreateCharacterAsync(user);
        string voiceUri = $"/api/projects/{projectId}/characters/{assetId}/voice";

        // 无效档案 → 400。
        HttpResponseMessage missingProfile = await user.PutAsJsonAsync(voiceUri, new
        {
            voiceProfileId = "no-such-profile",
            instructions = "  语速平缓  ",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingProfile.StatusCode);
        Assert.Equal("选择的声音素材不可用", await ReadMessageAsync(missingProfile));

        // 样本资源不可用 → 400（音频格式提示）。
        HttpResponseMessage badSample = await user.PutAsJsonAsync(voiceUri, new
        {
            sampleResourceId = "no-such-resource",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badSample.StatusCode);
        Assert.Equal(
            "请选择已上传完成的支持格式音频：MP3、WAV、M4A/AAC、FLAC、OGG/Opus 或 WebM",
            await ReadMessageAsync(badSample));

        // 绑定内置声音 → ready，指令被修剪，版本 +1。
        HttpResponseMessage bound = await user.PutAsJsonAsync(voiceUri, new
        {
            voiceProfileId = alloy.GetProperty("id").GetString(),
            instructions = "  语速平缓  ",
        });
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        JsonElement boundData = await ReadDataAsync(bound);
        JsonElement voice = boundData.GetProperty("character").GetProperty("voice");
        Assert.Equal("alloy", voice.GetProperty("profile").GetProperty("voiceKey").GetString());
        Assert.Equal("语速平缓", voice.GetProperty("instructions").GetString());
        Assert.Equal("ready", boundData.GetProperty("character").GetProperty("voiceStatus").GetString());
        Assert.Equal(2, boundData.GetProperty("character").GetProperty("version").GetInt64());

        // 解绑 → missing，版本再 +1。
        HttpResponseMessage unbound = await user.DeleteAsync(voiceUri);
        Assert.Equal(HttpStatusCode.OK, unbound.StatusCode);
        JsonElement unboundData = await ReadDataAsync(unbound);
        Assert.Equal("missing", unboundData.GetProperty("character").GetProperty("voiceStatus").GetString());
        Assert.False(unboundData.GetProperty("character").TryGetProperty("voice", out _));
        Assert.Equal(3, unboundData.GetProperty("character").GetProperty("version").GetInt64());
    }
}
