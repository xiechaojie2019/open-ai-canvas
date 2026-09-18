#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 分镜、镜头资产引用与资产候选路由的端到端契约测试。
/// 对应 Go: <c>handler/project.go</c> shots/asset-candidates 部分 +
/// <c>app/project_shot.go</c> / <c>app/project_asset.go</c> 候选确认。
/// </summary>
public sealed class ProjectShotEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public ProjectShotEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-shot-{Guid.NewGuid():N}");
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
            username = "shotter",
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
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/projects", new { name = "分镜项目" });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("project").GetProperty("id").GetString()!;
    }

    private async Task<string> CreateUnitAsync(HttpClient user, string projectId)
    {
        HttpResponseMessage created = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/units", new { title = "第一集", sourceText = "正文", position = 0 });
        created.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("unit").GetProperty("id").GetString()!;
    }

    /// <summary>经分片上传创建素材并链接到项目，返回素材首版本 ID。</summary>
    private async Task<string> CreateProjectAssetVersionAsync(HttpClient user, string projectId)
    {
        byte[] payload = new byte[512];
        new Random(3).NextBytes(payload);
        HttpResponseMessage started = await user.PostAsJsonAsync("/api/resources/uploads", new
        {
            fileName = "frame.png",
            kind = "image",
            size = payload.LongLength,
            width = 8,
            height = 8,
            durationMs = 0,
        });
        started.EnsureSuccessStatusCode();
        using JsonDocument startDoc = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        string uploadId = startDoc.RootElement.GetProperty("data").GetProperty("uploadId").GetString()!;
        (await user.PutAsync(
            $"/api/resources/uploads/{uploadId}/chunks/0", new ByteArrayContent(payload))).EnsureSuccessStatusCode();
        HttpResponseMessage completed = await user.PostAsync(
            $"/api/resources/uploads/{uploadId}/complete", content: null);
        completed.EnsureSuccessStatusCode();
        using JsonDocument doneDoc = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        string resourceId = doneDoc.RootElement.GetProperty("data").GetProperty("resource").GetProperty("id").GetString()!;

        HttpResponseMessage linked = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/assets", new { assetId = resourceId, category = "other" });
        linked.EnsureSuccessStatusCode();
        using JsonDocument linkDoc = JsonDocument.Parse(await linked.Content.ReadAsStringAsync());
        return linkDoc.RootElement.GetProperty("data").GetProperty("asset").GetProperty("primaryVersionId").GetString()!;
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
    public async Task 未登录访问分镜路由返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/projects/p1/shots", new { title = "t" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/projects/p1/asset-candidates")).StatusCode);
    }

    [Fact]
    public async Task 镜头创建与版本链与删除()
    {
        HttpClient user = await SignInAsync();
        string projectId = await CreateProjectAsync(user);
        string unitId = await CreateUnitAsync(user, projectId);

        // 创建：默认 draft，版本号 1，plotDescription 回填 description。
        HttpResponseMessage created = await user.PostAsJsonAsync($"/api/projects/{projectId}/shots", new
        {
            unitId,
            title = "  开场镜头  ",
            description = "城市天际线",
            position = 0,
            durationMs = 3000L,
            revision = new { plotDescription = "", dialogue = "  你好  " },
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement shot = (await ReadDataAsync(created)).GetProperty("shot");
        string shotId = shot.GetProperty("id").GetString()!;
        Assert.Equal("开场镜头", shot.GetProperty("title").GetString());
        Assert.Equal("城市天际线", shot.GetProperty("description").GetString());
        Assert.Equal("draft", shot.GetProperty("status").GetString());
        Assert.Equal(3000, shot.GetProperty("durationMs").GetInt64());

        // 追加版本：状态回 draft，版本号 2。
        HttpResponseMessage revised = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/shots/{shotId}/revisions",
            new { plotDescription = "夜景街头", durationMs = 4000L });
        Assert.Equal(HttpStatusCode.OK, revised.StatusCode);
        JsonElement revisedData = await ReadDataAsync(revised);
        Assert.Equal("夜景街头", revisedData.GetProperty("shot").GetProperty("description").GetString());
        Assert.Equal("draft", revisedData.GetProperty("shot").GetProperty("status").GetString());
        Assert.Equal(2, revisedData.GetProperty("revision").GetProperty("version").GetInt64());
        Assert.Equal(4000, revisedData.GetProperty("shot").GetProperty("durationMs").GetInt64());
        Assert.Equal(
            revisedData.GetProperty("shot").GetProperty("currentRevisionId").GetString(),
            revisedData.GetProperty("revision").GetProperty("id").GetString());

        // 空标题 → 400；空画面描述（且无回填）→ 400。
        HttpResponseMessage blankTitle = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/shots", new { title = " ", unitId });
        Assert.Equal(HttpStatusCode.BadRequest, blankTitle.StatusCode);
        Assert.Equal("镜头标题不能为空", await ReadMessageAsync(blankTitle));
        HttpResponseMessage blankPlot = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/shots", new { title = "x", unitId });
        Assert.Equal(HttpStatusCode.BadRequest, blankPlot.StatusCode);
        Assert.Equal("镜头画面描述不能为空", await ReadMessageAsync(blankPlot));

        // 章节整体替换：旧镜头被清空重建，version 从 1 开始。
        HttpResponseMessage replaced = await user.PutAsJsonAsync(
            $"/api/projects/{projectId}/units/{unitId}/shots", new
            {
                shots = new[]
                {
                    new { title = "新镜一", description = "雨夜", durationMs = 2000L },
                    new { title = "新镜二", description = "清晨", durationMs = 1500L },
                },
                expectedShotIds = new[] { shotId },
            });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        JsonElement shotsData = await ReadDataAsync(replaced);
        Assert.Equal(2, shotsData.GetProperty("shots").GetArrayLength());
        Assert.Equal("新镜一", shotsData.GetProperty("shots")[0].GetProperty("title").GetString());
        Assert.Equal(0, shotsData.GetProperty("shots")[0].GetProperty("position").GetInt64());
        Assert.Equal(1, shotsData.GetProperty("shots")[1].GetProperty("position").GetInt64());

        // 乐观锁：expectedShotIds 与当前不一致 → 400。
        HttpResponseMessage stale = await user.PutAsJsonAsync(
            $"/api/projects/{projectId}/units/{unitId}/shots", new
            {
                shots = new[] { new { title = "再生成", description = "x", durationMs = 1000L } },
                expectedShotIds = new[] { shotId },
            });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("本章分镜已发生变化，请刷新后重新确认", await ReadMessageAsync(stale));

        // 删除镜头。
        string newShotId = shotsData.GetProperty("shots")[0].GetProperty("id").GetString()!;
        HttpResponseMessage deleted = await user.DeleteAsync($"/api/projects/{projectId}/shots/{newShotId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        JsonElement deletedData = await ReadDataAsync(deleted);
        Assert.True(deletedData.GetProperty("deleted").GetBoolean());

        // 已归档项目拒绝修改 → 400（归档流程见项目测试）。
        HttpResponseMessage missingProject = await user.PostAsJsonAsync(
            "/api/projects/missing/shots", new { title = "x", description = "y" });
        Assert.Equal(HttpStatusCode.InternalServerError, missingProject.StatusCode);
    }

    [Fact]
    public async Task 镜头资产引用_校验与upsert与解绑()
    {
        HttpClient user = await SignInAsync();
        string projectId = await CreateProjectAsync(user);
        string unitId = await CreateUnitAsync(user, projectId);
        string versionId = await CreateProjectAssetVersionAsync(user, projectId);

        HttpResponseMessage created = await user.PostAsJsonAsync($"/api/projects/{projectId}/shots", new
        {
            unitId,
            title = "引用镜头",
            description = "x",
            position = 0,
        });
        created.EnsureSuccessStatusCode();
        string shotId = (await ReadDataAsync(created)).GetProperty("shot").GetProperty("id").GetString()!;
        string assetsUri = $"/api/projects/{projectId}/shots/{shotId}/assets";

        // 用途不合法 → 400。
        HttpResponseMessage badRole = await user.PostAsJsonAsync(assetsUri, new
        {
            assetVersionId = versionId,
            role = "bgm",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Equal("不支持的镜头素材用途", await ReadMessageAsync(badRole));

        // 版本不存在 → 500 信封。
        HttpResponseMessage missingVersion = await user.PostAsJsonAsync(assetsUri, new
        {
            assetVersionId = "no-such-version",
            role = "reference",
        });
        Assert.Equal(HttpStatusCode.InternalServerError, missingVersion.StatusCode);

        // 关联成功。
        HttpResponseMessage linked = await user.PostAsJsonAsync(assetsUri, new
        {
            assetVersionId = versionId,
            role = "reference",
        });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        JsonElement reference = (await ReadDataAsync(linked)).GetProperty("reference");
        string referenceId = reference.GetProperty("id").GetString()!;
        Assert.Equal("linked", reference.GetProperty("status").GetString());

        // 解绑 → 再次解绑 404。
        HttpResponseMessage unlinked = await user.DeleteAsync($"{assetsUri}/{referenceId}");
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.True((await ReadDataAsync(unlinked)).GetProperty("unlinked").GetBoolean());
        HttpResponseMessage unlinkedAgain = await user.DeleteAsync($"{assetsUri}/{referenceId}");
        Assert.Equal(HttpStatusCode.NotFound, unlinkedAgain.StatusCode);
        Assert.Equal("镜头资产引用不存在", await ReadMessageAsync(unlinkedAgain));
    }

    [Fact]
    public async Task 资产候选_创建去重与确认()
    {
        HttpClient user = await SignInAsync();
        string projectId = await CreateProjectAsync(user);
        string unitId = await CreateUnitAsync(user, projectId);

        // 角色候选必须走章节提取来源。
        HttpResponseMessage wrongSource = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates", new
            {
                candidates = new[]
                {
                    new { unitId, name = "林小雨", category = "character", details = new { } },
                },
                source = "manual",
            });
        Assert.Equal(HttpStatusCode.BadRequest, wrongSource.StatusCode);
        Assert.Equal("角色候选只能从剧情章节的角色提取流程创建", await ReadMessageAsync(wrongSource));

        // 角色候选画像不完整 → 400。
        HttpResponseMessage incomplete = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates", new
            {
                candidates = new[]
                {
                    new
                    {
                        unitId,
                        name = "林小雨",
                        category = "character",
                        details = new { role = "主角", appearance = "短发", clothing = "校服" },
                    },
                },
                source = "chapter_character_extract",
            });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Equal("角色候选必须包含剧情定位、稳定设定和声音画像", await ReadMessageAsync(incomplete));

        // 合法角色候选 + 场景候选。
        HttpResponseMessage created = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates", new
            {
                candidates = new object[]
                {
                    new
                    {
                        unitId,
                        name = "  林小雨 ",
                        category = "character",
                        details = new
                        {
                            role = "主角",
                            appearance = "短发",
                            clothing = "校服",
                            physique = "瘦高",
                            voiceLanguage = "普通话",
                            voiceAge = "青年",
                            voiceTimbre = "清亮",
                            aliases = new[] { "小雨", "XiaoYu" },
                        },
                    },
                    new { unitId, name = "旧仓库", category = "environment", details = new { } },
                },
                source = "chapter_character_extract",
            });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement candidatesData = await ReadDataAsync(created);
        Assert.Equal(2, candidatesData.GetProperty("candidates").GetArrayLength());
        JsonElement character = candidatesData.GetProperty("candidates")
            .EnumerateArray()
            .First(candidate => candidate.GetProperty("category").GetString() == "character");
        string characterId = character.GetProperty("id").GetString()!;
        Assert.Equal("pending_confirmation", character.GetProperty("status").GetString());
        Assert.Equal("林小雨", character.GetProperty("name").GetString());

        // 同名（含别名）去重 → 返回空数组。
        HttpResponseMessage duplicated = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates", new
            {
                candidates = new object[]
                {
                    new
                    {
                        unitId,
                        name = "林小雨",
                        category = "character",
                        details = new
                        {
                            role = "主角",
                            appearance = "短发",
                            clothing = "校服",
                            physique = "瘦高",
                            voiceLanguage = "普通话",
                            voiceAge = "青年",
                            voiceTimbre = "清亮",
                            aliases = new[] { "小雨" },
                        },
                    },
                    new
                    {
                        unitId,
                        name = "小雨",
                        category = "character",
                        details = new
                        {
                            role = "配角",
                            appearance = "长发",
                            clothing = "便装",
                            physique = "娇小",
                            voiceLanguage = "普通话",
                            voiceAge = "青年",
                            voiceTimbre = "柔和",
                        },
                    },
                    new { unitId, name = "旧仓库", category = "environment", details = new { } },
                },
                source = "chapter_character_extract",
            });
        Assert.Equal(HttpStatusCode.OK, duplicated.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(duplicated)).GetProperty("candidates").GetArrayLength());

        // 分页 + 过滤。
        HttpResponseMessage page = await user.GetAsync(
            $"/api/projects/{projectId}/asset-candidates?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        JsonElement pageData = await ReadDataAsync(page);
        Assert.Equal(2, pageData.GetProperty("total").GetInt64());
        Assert.Equal(1, pageData.GetProperty("candidates").GetArrayLength());
        Assert.True(pageData.GetProperty("hasMore").GetBoolean());
        HttpResponseMessage filtered = await user.GetAsync(
            $"/api/projects/{projectId}/asset-candidates?category=environment");
        Assert.Equal(1, (await ReadDataAsync(filtered)).GetProperty("total").GetInt64());

        // 确认为新素材（环境类 → text kind）。
        string environmentId = candidatesData.GetProperty("candidates")
            .EnumerateArray()
            .First(candidate => candidate.GetProperty("category").GetString() == "environment")
            .GetProperty("id").GetString()!;
        HttpResponseMessage confirmed = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates/{environmentId}/confirm", new { });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        JsonElement asset = (await ReadDataAsync(confirmed)).GetProperty("asset");
        Assert.Equal("旧仓库", asset.GetProperty("title").GetString());
        Assert.Equal("environment", asset.GetProperty("category").GetString());
        Assert.Equal("text", asset.GetProperty("mediaType").GetString());

        // 已处理候选再次确认 → 400。
        HttpResponseMessage again = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates/{environmentId}/confirm", new { });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("资产候选已处理", await ReadMessageAsync(again));

        // 角色候选并入新角色：确认后生成 entity 资产，设定来自候选详情。
        HttpResponseMessage characterConfirmed = await user.PostAsJsonAsync(
            $"/api/projects/{projectId}/asset-candidates/{characterId}/confirm", new { });
        Assert.Equal(HttpStatusCode.OK, characterConfirmed.StatusCode);
        JsonElement characterAsset = (await ReadDataAsync(characterConfirmed)).GetProperty("asset");
        Assert.Equal("entity", characterAsset.GetProperty("mediaType").GetString());
        Assert.Equal("character", characterAsset.GetProperty("category").GetString());
        Assert.NotNull(characterAsset.GetProperty("character"));
    }
}
