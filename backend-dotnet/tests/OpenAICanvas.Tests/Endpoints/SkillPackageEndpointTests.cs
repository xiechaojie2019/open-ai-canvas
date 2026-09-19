#nullable enable
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 技能包文件读取路由的端到端契约测试。
/// 对应 Go: <c>handler/skills.go</c> files/file/file-raw/bundle/search + <c>skill_packages.go</c> 读路径。
/// </summary>
public sealed class SkillPackageEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _userClient;

    public SkillPackageEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-skp-{Guid.NewGuid():N}");
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

    /// <summary>播种技能 + 版本 + 文件清单 + 落盘 ZIP 包（仓储直写）。</summary>
    private async Task<string> SeedSkillAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        OpenAICanvas.Persistence.Repositories.Repository repository = scope.ServiceProvider
            .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
        OpenAICanvas.Domain.Entities.User? user = (await repository.UsersAsync())[0];
        DateTime now = DateTime.UtcNow;
        string skillId = OpenAICanvas.Domain.Kernel.IdGenerator.NewId();
        string versionId = OpenAICanvas.Domain.Kernel.IdGenerator.NewId();
        string packageKey = $"{skillId}/{versionId}.zip";

        string skillDir = Path.Combine(_dataDir, "skill-packages", skillId);
        Directory.CreateDirectory(skillDir);
        string zipPath = Path.Combine(skillDir, versionId + ".zip");
        using (ZipArchive archive = new(new FileStream(zipPath, FileMode.CreateNew), ZipArchiveMode.Create))
        {
            ZipArchiveEntry skillEntry = archive.CreateEntry("SKILL.md");
            await using (System.IO.StreamWriter writer = new(skillEntry.Open()))
            {
                await writer.WriteAsync("# 翻译技能\n把中文翻译成英文。");
            }
            ZipArchiveEntry codeEntry = archive.CreateEntry("scripts/run.py");
            await using (System.IO.StreamWriter writer = new(codeEntry.Open()))
            {
                await writer.WriteAsync("print('hello translation')");
            }
        }

        await repository.CreateAsync(new OpenAICanvas.Domain.Entities.Skill
        {
            ID = skillId,
            OwnerID = user.ID,
            Name = "翻译技能",
            Description = "中译英",
            CurrentVersionID = versionId,
            VersionLabel = "1",
            FileCount = 2,
            TotalBytes = 60,
            SourceType = "markdown",
            SyncStatus = "synced",
            Status = 1,
            Source = 1,
            Tag = "others",
            ShowcaseMediaJSON = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await repository.CreateAsync(new OpenAICanvas.Domain.Entities.SkillVersion
        {
            ID = versionId,
            SkillID = skillId,
            ContentHash = "hash-1",
            EntryPath = "SKILL.md",
            PackageKey = packageKey,
            FileCount = 2,
            TotalBytes = 60,
            CreatedAt = now,
        });
        await repository.CreateAsync(new OpenAICanvas.Domain.Entities.SkillFile
        {
            ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
            SkillVersionID = versionId,
            Path = "SKILL.md",
            Kind = "markdown",
            MimeType = "text/markdown; charset=utf-8",
            Size = Encoding.UTF8.GetByteCount("# 翻译技能\n把中文翻译成英文。"),
            SHA256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("# 翻译技能\n把中文翻译成英文。"))).ToLowerInvariant(),
        });
        await repository.CreateAsync(new OpenAICanvas.Domain.Entities.SkillFile
        {
            ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
            SkillVersionID = versionId,
            Path = "scripts/run.py",
            Kind = "code",
            MimeType = "text/x-python; charset=utf-8",
            Size = Encoding.UTF8.GetByteCount("print('hello translation')"),
            SHA256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("print('hello translation')"))).ToLowerInvariant(),
        });
        return skillId;
    }

    private async Task<HttpClient> SignInAsync()
    {
        if (_userClient is not null)
        {
            return _userClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "skiller",
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

    [Fact]
    public async Task 技能包文件_清单读取与搜索与聚合()
    {
        using HttpClient user = await SignInAsync();
        string skillId = await SeedSkillAsync();

        // files 清单。
        HttpResponseMessage files = await user.GetAsync($"/api/skills/{skillId}/files");
        Assert.Equal(HttpStatusCode.OK, files.StatusCode);
        JsonElement fileList = (await ReadDataAsync(files)).GetProperty("files");
        Assert.Equal(2, fileList.GetArrayLength());
        Assert.Equal("SKILL.md", fileList[0].GetProperty("path").GetString());
        Assert.Equal("markdown", fileList[0].GetProperty("kind").GetString());

        // file 文本预览。
        HttpResponseMessage file = await user.GetAsync(
            $"/api/skills/{skillId}/file?path={Uri.EscapeDataString("SKILL.md")}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        // Go: ok(c, gin.H{"file": file}) —— data.file 才是内容对象。
        JsonElement fileData = (await ReadDataAsync(file)).GetProperty("file");
        Assert.False(fileData.GetProperty("binary").GetBoolean());
        Assert.Contains("翻译技能", fileData.GetProperty("content").GetString(), StringComparison.Ordinal);

        // file/raw 二进制直出 + 安全响应头。
        HttpResponseMessage raw = await user.GetAsync(
            $"/api/skills/{skillId}/file/raw?path={Uri.EscapeDataString("scripts/run.py")}");
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Contains(
            "hello translation",
            await raw.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        // ASP.NET 合成顺序与 Go 不同但语义一致（同 #54）。
        string cacheControl = raw.Headers.GetValues("Cache-Control").FirstOrDefault() ?? "";
        Assert.Contains("no-store", cacheControl, StringComparison.Ordinal);
        Assert.Contains("private", cacheControl, StringComparison.Ordinal);

        // search。
        HttpResponseMessage search = await user.GetAsync(
            $"/api/skills/{skillId}/search?q={Uri.EscapeDataString("hello")}");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        JsonElement results = (await ReadDataAsync(search)).GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("scripts/run.py", results[0].GetProperty("path").GetString());
        Assert.Equal(1, results[0].GetProperty("line").GetInt64());

        // bundle。
        HttpResponseMessage bundle = await user.GetAsync($"/api/skills/{skillId}/bundle");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        JsonElement bundleData = (await ReadDataAsync(bundle)).GetProperty("bundle");
        Assert.Equal(2, bundleData.GetProperty("files").GetArrayLength());
        Assert.Equal("hash-1", bundleData.GetProperty("contentHash").GetString());

        // 路径越界 → 400。
        HttpResponseMessage escape = await user.GetAsync(
            $"/api/skills/{skillId}/file?path={Uri.EscapeDataString("../escape.md")}");
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
        Assert.Equal("技能文件路径越界或包含禁止目录", await escape.Content.ReadAsStringAsync().ContinueWith(t =>
            JsonDocument.Parse(t.Result).RootElement.GetProperty("msg").GetString()));

        // 不存在文件 → 400。
        HttpResponseMessage missing = await user.GetAsync(
            $"/api/skills/{skillId}/file?path={Uri.EscapeDataString("nope.md")}");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/skills/s1/files")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/skills/s1/bundle")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/skills/s1/search?q=x")).StatusCode);
    }
}
