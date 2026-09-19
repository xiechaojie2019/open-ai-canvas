#nullable enable
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 技能创建/更新路由的端到端契约测试。
/// 对应 Go: <c>handler/skills.go</c> POST /skills + PUT /skills/:id。
/// </summary>
public sealed class SkillWriteEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public SkillWriteEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-skw-{Guid.NewGuid():N}");
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
            username = "skillwriter",
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
    public async Task 技能创建_校验_版本落盘与更新()
    {
        using HttpClient user = await SignInAsync();

        // 校验：无效分类 → 400。
        HttpResponseMessage invalidTag = await user.PostAsJsonAsync("/api/skills", new
        {
            skillName = "翻译",
            description = "中译英",
            instruction = "把中文翻译成英文。",
            tag = "not-a-tag",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidTag.StatusCode);
        Assert.Equal("请选择有效的技能分类", await ReadMessageAsync(invalidTag));

        // 校验：缺指令 → 400。
        HttpResponseMessage missingInstruction = await user.PostAsJsonAsync("/api/skills", new
        {
            skillName = "翻译",
            description = "中译英",
            tag = "drama",
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingInstruction.StatusCode);
        Assert.Equal("技能指令必须为 1-100000 个字符", await ReadMessageAsync(missingInstruction));

        // 创建成功：落盘 ZIP + 版本 + 文件清单。
        HttpResponseMessage created = await user.PostAsJsonAsync("/api/skills", new
        {
            skillName = "  翻译技能  ",
            description = "中译英",
            instruction = "把中文翻译成英文。",
            tag = "drama",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        JsonElement skill = (await ReadDataAsync(created)).GetProperty("skill");
        string skillId = skill.GetProperty("skillId").GetString()!;
        Assert.Equal("翻译技能", skill.GetProperty("skillName").GetString());
        Assert.Equal("markdown", skill.GetProperty("sourceType").GetString());

        // bundle：SKILL.md 落盘。
        HttpResponseMessage bundle = await user.GetAsync($"/api/skills/{skillId}/bundle");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        JsonElement bundleData = (await ReadDataAsync(bundle)).GetProperty("bundle");
        Assert.Equal(1, bundleData.GetProperty("files").GetArrayLength());
        Assert.Equal(
            "SKILL.md",
            bundleData.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Contains(
            "把中文翻译成英文",
            Encoding.UTF8.GetString(Convert.FromBase64String(
                bundleData.GetProperty("files")[0].GetProperty("contentBase64").GetString()!)),
            StringComparison.Ordinal);

        // 更新（instruction 变化 → 追加版本）。
        HttpResponseMessage updated = await user.PutAsJsonAsync($"/api/skills/{skillId}", new
        {
            skillName = "翻译技能",
            description = "中译英",
            instruction = "把中文翻译成英文，保持语气。",
            tag = "drama",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedSkill = (await ReadDataAsync(updated)).GetProperty("skill");
        Assert.Contains(
            "保持语气",
            updatedSkill.GetProperty("instruction").GetString(),
            StringComparison.Ordinal);

        // bundle 反映新版本内容。
        HttpResponseMessage bundleAfter = await user.GetAsync($"/api/skills/{skillId}/bundle");
        string contentAfter = Encoding.UTF8.GetString(Convert.FromBase64String(
            (await ReadDataAsync(bundleAfter)).GetProperty("bundle").GetProperty("files")[0]
                .GetProperty("contentBase64").GetString()!));
        Assert.Contains("保持语气", contentAfter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未登录创建技能返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/skills", new { skillName = "x" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PutAsJsonAsync("/api/skills/s1", new { skillName = "x" })).StatusCode);
    }
}
