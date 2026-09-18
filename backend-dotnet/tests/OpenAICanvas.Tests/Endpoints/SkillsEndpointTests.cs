#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 技能库读取与状态路由的端到端契约测试。
/// </summary>
public sealed class SkillsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SkillsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-skills-{Guid.NewGuid():N}");
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
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 基础

    [Fact]
    public async Task 未登录访问技能库返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/skills");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 空技能库返回空列表与分类()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/skills");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(0, data.GetProperty("skills").GetArrayLength());
        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.False(data.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, data.GetProperty("nextOffset").GetInt32());
        Assert.Equal(20, data.GetProperty("pageSize").GetInt32());

        // 五个固定分类必须下发，前端据此渲染筛选。
        JsonElement categories = data.GetProperty("categories");
        Assert.Equal(5, categories.GetArrayLength());
        Assert.Equal("drama", categories[0].GetProperty("value").GetString());
        Assert.Equal("短剧影视", categories[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task 列表分页参数非法返回_400()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/skills?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------ 可见性

    [Fact]
    public async Task 公开技能对所有人可见私有技能仅作者可见()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-public", "公开技能", isPrivate: false);
        await CreateSkillAsync(aliceId, "skill-private", "私有技能", isPrivate: true);

        using HttpClient bob = await SignInAsync("bob");

        // bob 只看到公开技能。
        JsonElement bobList = await ReadDataAsync(await bob.GetAsync("/api/skills"));
        Assert.Equal(1, bobList.GetProperty("totalCount").GetInt64());
        Assert.Equal("skill-public", bobList.GetProperty("skills")[0].GetProperty("skillId").GetString());

        // 默认 scope=public 对所有人（含作者）都只返回公开技能。
        JsonElement alicePublic = await ReadDataAsync(await alice.GetAsync("/api/skills"));
        Assert.Equal(1, alicePublic.GetProperty("totalCount").GetInt64());

        // 作者用 scope=created 才能看到自己的私有技能。
        JsonElement aliceCreated = await ReadDataAsync(await alice.GetAsync("/api/skills?scope=created"));
        Assert.Equal(2, aliceCreated.GetProperty("totalCount").GetInt64());

        // bob 直接访问私有技能详情被拒。
        HttpResponseMessage denied = await bob.GetAsync("/api/skills/skill-private");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("该技能未公开", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // alice 自己可以看。
        HttpResponseMessage allowed = await alice.GetAsync("/api/skills/skill-private");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task 详情带正文列表不带正文()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一", instruction: "这是正文内容");

        JsonElement listItem = (await ReadDataAsync(await alice.GetAsync("/api/skills")))
            .GetProperty("skills")[0];
        // 列表不下发 instruction（omitempty）。
        Assert.False(listItem.TryGetProperty("instruction", out _));

        JsonElement detail = (await ReadDataAsync(await alice.GetAsync("/api/skills/skill-1")))
            .GetProperty("skill");
        Assert.Equal("这是正文内容", detail.GetProperty("instruction").GetString());
    }

    [Fact]
    public async Task 不存在的技能返回_400()
    {
        using HttpClient user = await SignInAsync("alice");

        HttpResponseMessage response = await user.GetAsync("/api/skills/not-exist");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("技能不存在或已删除", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 加入与收藏

    [Fact]
    public async Task 加入与移出他人技能()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一");

        using HttpClient bob = await SignInAsync("bob");

        // 加入
        HttpResponseMessage added = await bob.PostAsync("/api/skills/skill-1/add", null);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        JsonElement addedSkill = (await ReadDataAsync(added)).GetProperty("skill");
        Assert.True(addedSkill.GetProperty("isAdded").GetBoolean());
        Assert.False(addedSkill.GetProperty("isOwner").GetBoolean());
        Assert.Equal(1, addedSkill.GetProperty("addedCount").GetInt64());

        // 出现在「我加入的」列表
        JsonElement addedList = await ReadDataAsync(await bob.GetAsync("/api/skills/added"));
        Assert.Equal(1, addedList.GetProperty("skills").GetArrayLength());

        // 移出
        HttpResponseMessage removed = await bob.DeleteAsync("/api/skills/skill-1/add");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        JsonElement removedSkill = (await ReadDataAsync(removed)).GetProperty("skill");
        Assert.False(removedSkill.GetProperty("isAdded").GetBoolean());
        Assert.Equal(0, removedSkill.GetProperty("addedCount").GetInt64());
    }

    [Fact]
    public async Task 自己创建的技能不能移出()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一");

        HttpResponseMessage response = await alice.DeleteAsync("/api/skills/skill-1/add");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("自己创建的技能始终保留", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 自己创建的技能天然 isAdded。
        JsonElement detail = (await ReadDataAsync(await alice.GetAsync("/api/skills/skill-1")))
            .GetProperty("skill");
        Assert.True(detail.GetProperty("isAdded").GetBoolean());
        Assert.True(detail.GetProperty("isOwner").GetBoolean());
    }

    [Fact]
    public async Task 收藏与取消收藏()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一");

        using HttpClient bob = await SignInAsync("bob");

        HttpResponseMessage liked = await bob.PostAsync("/api/skills/skill-1/like", null);
        JsonElement likedSkill = (await ReadDataAsync(liked)).GetProperty("skill");
        Assert.True(likedSkill.GetProperty("isLike").GetBoolean());
        Assert.Equal(1, likedSkill.GetProperty("likeCount").GetInt64());

        // 收藏不影响加入状态。
        Assert.False(likedSkill.GetProperty("isAdded").GetBoolean());

        HttpResponseMessage unliked = await bob.DeleteAsync("/api/skills/skill-1/like");
        JsonElement unlikedSkill = (await ReadDataAsync(unliked)).GetProperty("skill");
        Assert.False(unlikedSkill.GetProperty("isLike").GetBoolean());
        Assert.Equal(0, unlikedSkill.GetProperty("likeCount").GetInt64());
    }

    [Fact]
    public async Task 计数包含内置初始值()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一", initialLikeCount: 100, initialAddedCount: 50);

        using HttpClient bob = await SignInAsync("bob");
        await bob.PostAsync("/api/skills/skill-1/like", null);

        JsonElement detail = (await ReadDataAsync(await bob.GetAsync("/api/skills/skill-1")))
            .GetProperty("skill");

        // 100 内置 + 1 实时
        Assert.Equal(101, detail.GetProperty("likeCount").GetInt64());
        Assert.Equal(50, detail.GetProperty("addedCount").GetInt64());
    }

    // ------------------------------------------------------------ 作用域与搜索

    [Fact]
    public async Task 作用域_created_只看自己创建的()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-alice", "Alice 的技能");

        using HttpClient bob = await SignInAsync("bob");
        string bobId = await CurrentUserIdAsync(bob);
        await CreateSkillAsync(bobId, "skill-bob", "Bob 的技能");

        JsonElement aliceCreated = await ReadDataAsync(await alice.GetAsync("/api/skills?scope=created"));
        Assert.Equal(1, aliceCreated.GetProperty("totalCount").GetInt64());
        Assert.Equal("skill-alice", aliceCreated.GetProperty("skills")[0].GetProperty("skillId").GetString());
    }

    [Fact]
    public async Task 按名称搜索()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "短剧分镜助手");
        await CreateSkillAsync(aliceId, "skill-2", "电商文案生成");

        JsonElement data = await ReadDataAsync(await alice.GetAsync("/api/skills?search=分镜"));

        Assert.Equal(1, data.GetProperty("totalCount").GetInt64());
        Assert.Equal("短剧分镜助手", data.GetProperty("skills")[0].GetProperty("skillName").GetString());
    }

    [Fact]
    public async Task 按分类过滤且未知分类被忽略()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "短剧技能", tag: "drama");
        await CreateSkillAsync(aliceId, "skill-2", "电商技能", tag: "ecommerce");

        JsonElement filtered = await ReadDataAsync(await alice.GetAsync("/api/skills?tag=drama"));
        Assert.Equal(1, filtered.GetProperty("totalCount").GetInt64());

        // 未知分类静默忽略（等价于不过滤），与 Go 一致。
        JsonElement unknown = await ReadDataAsync(await alice.GetAsync("/api/skills?tag=nonexistent"));
        Assert.Equal(2, unknown.GetProperty("totalCount").GetInt64());
    }

    // ------------------------------------------------------------ 删除

    [Fact]
    public async Task 只有作者可以删除技能()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一");

        using HttpClient bob = await SignInAsync("bob");

        HttpResponseMessage denied = await bob.DeleteAsync("/api/skills/skill-1");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("只有作者可以修改或删除", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage deleted = await alice.DeleteAsync("/api/skills/skill-1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        JsonElement data = await ReadDataAsync(deleted);
        Assert.True(data.GetProperty("deleted").GetBoolean());

        // 删除后详情返回「不存在」。
        HttpResponseMessage after = await alice.GetAsync("/api/skills/skill-1");
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
    }

    [Fact]
    public async Task 删除技能同时清理用户状态()
    {
        using HttpClient alice = await SignInAsync("alice");
        string aliceId = await CurrentUserIdAsync(alice);
        await CreateSkillAsync(aliceId, "skill-1", "技能一");

        using HttpClient bob = await SignInAsync("bob");
        await bob.PostAsync("/api/skills/skill-1/add", null);

        await alice.DeleteAsync("/api/skills/skill-1");

        // bob 的「我加入的」列表应清空（状态行被级联删除）。
        JsonElement added = await ReadDataAsync(await bob.GetAsync("/api/skills/added"));
        Assert.Equal(0, added.GetProperty("skills").GetArrayLength());
    }

    // ------------------------------------------------------------ 辅助

    private async Task<HttpClient> SignInAsync(string username)
    {
        HttpResponseMessage register = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username,
            password = "password123",
        });

        if (register.IsSuccessStatusCode)
        {
            return Authenticated(register);
        }

        // 非首个用户：直接落库再登录。
        await CreateUserAsync(username);
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        return Authenticated(login);
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
        JsonElement data = await ReadDataAsync(await client.GetAsync("/api/auth/session"));
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

    private async Task CreateSkillAsync(
        string ownerId,
        string id,
        string name,
        bool isPrivate = false,
        string tag = "drama",
        string instruction = "正文",
        long initialLikeCount = 0,
        long initialAddedCount = 0)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new Skill
        {
            ID = id,
            OwnerID = ownerId,
            AuthorName = "作者",
            Name = name,
            Description = name + " 的简介",
            Instruction = instruction,
            Status = 1,
            Source = 1,
            Tag = tag,
            SortWeight = 0,
            IsPrivate = isPrivate,
            InitialLikeCount = initialLikeCount,
            InitialAddedCount = initialAddedCount,
            ShowcaseMediaJSON = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }
}
