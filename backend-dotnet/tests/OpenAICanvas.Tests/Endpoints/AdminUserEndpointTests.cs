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
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 管理后台用户管理 + 口令重置路由的端到端契约测试。
/// </summary>
public sealed class AdminUserEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AdminUserEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-admin-{Guid.NewGuid():N}");
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
            // 连接池释放有延迟，重试几次避免句柄占用导致清理失败。
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
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
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

    private async Task<Repository> RepositoryAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<Repository>();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    // ------------------------------------------------------------ 管理后台用户

    [Fact]
    public async Task 未登录访问管理后台用户列表返回_401()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"reason\":\"unauthorized\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 非管理员访问管理后台用户列表返回_403()
    {
        await SignInAsAdminAsync();
        await CreateUserAsync("bob", UserRole.UserRoleUser);

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "bob",
            password = "password123",
        });
        using HttpClient bob = Authenticated(login);

        HttpResponseMessage response = await bob.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("需要管理员权限", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 用户列表返回分页与积分字段()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/admin/users?page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(1, data.GetProperty("total").GetInt64());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        // 注意 json tag 是 pageSize，不是 limit。
        Assert.Equal(20, data.GetProperty("pageSize").GetInt32());

        JsonElement user = data.GetProperty("users")[0];
        Assert.Equal("admin", user.GetProperty("username").GetString());
        Assert.Equal("admin", user.GetProperty("role").GetString());
        // AdminUser 平铺 User 字段后追加两个积分字段。
        Assert.True(user.TryGetProperty("availableMicrocredits", out _));
        Assert.True(user.TryGetProperty("reservedMicrocredits", out _));
        // 空邮箱带 omitempty，必须被省略。
        Assert.False(user.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task 创建用户返回管理员视图()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "carol",
            email = "carol@example.com",
            password = "password123",
            displayName = "Carol",
            role = "user",
            status = "active",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement user = (await ReadDataAsync(response)).GetProperty("user");
        Assert.Equal("carol", user.GetProperty("username").GetString());
        Assert.Equal("carol@example.com", user.GetProperty("email").GetString());
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Equal("Carol", user.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task 创建用户校验角色与状态()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage badRole = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "carol",
            password = "password123",
            role = "superuser",
            status = "active",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
        Assert.Contains("用户角色无效", await badRole.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        HttpResponseMessage badStatus = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "carol",
            password = "password123",
            role = "user",
            status = "pending",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Contains("用户状态无效", await badStatus.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 创建重名用户返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "admin",
            password = "password123",
            role = "user",
            status = "active",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("用户名已存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引用数据返回用户与渠道两个数组()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/admin/references");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(JsonValueKind.Array, data.GetProperty("users").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("channels").ValueKind);
        Assert.Equal("admin", data.GetProperty("users")[0].GetProperty("username").GetString());
    }

    [Fact]
    public async Task 更新用户可改显示名与角色()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("dave", UserRole.UserRoleUser);

        HttpResponseMessage response = await admin.PatchAsync(
            $"/api/admin/users/{target.ID}",
            new StringContent("{\"displayName\":\"Dave 2\",\"role\":\"admin\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement user = (await ReadDataAsync(response)).GetProperty("user");
        Assert.Equal("Dave 2", user.GetProperty("displayName").GetString());
        Assert.Equal("admin", user.GetProperty("role").GetString());
    }

    [Fact]
    public async Task 不能禁用当前登录的管理员()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        Repository repository = await RepositoryAsync();
        User self = (await repository.UserByUsernameAsync("admin"))!;

        HttpResponseMessage response = await admin.PatchAsync(
            $"/api/admin/users/{self.ID}",
            new StringContent("{\"status\":\"disabled\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不能禁用当前管理员账号", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不能把最后一个管理员降级()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        Repository repository = await RepositoryAsync();
        User self = (await repository.UserByUsernameAsync("admin"))!;

        HttpResponseMessage response = await admin.PatchAsync(
            $"/api/admin/users/{self.ID}",
            new StringContent("{\"role\":\"user\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("至少需要保留一个管理员", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不能删除当前登录的管理员()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        Repository repository = await RepositoryAsync();
        User self = (await repository.UserByUsernameAsync("admin"))!;

        HttpResponseMessage response = await admin.DeleteAsync($"/api/admin/users/{self.ID}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不能删除当前登录的管理员账号", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 删除用户实际是停用并清除会话()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("erin", UserRole.UserRoleUser);

        // 先让目标用户登录一次，产生会话。
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "erin",
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        using HttpClient erin = Authenticated(login);

        HttpResponseMessage response = await admin.DeleteAsync($"/api/admin/users/{target.ID}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"code\":0,\"data\":{\"ok\":true},\"msg\":\"ok\"}",
            await response.Content.ReadAsStringAsync());

        // 用户主体必须保留（有资金流水），只是被停用。
        Repository repository = await RepositoryAsync();
        User? reloaded = await repository.UserAsync(target.ID);
        Assert.NotNull(reloaded);
        Assert.Equal(UserStatus.UserStatusDisabled, reloaded!.Status);

        // 旧会话必须失效。
        HttpResponseMessage afterDelete = await erin.GetAsync("/api/features");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
    }

    [Fact]
    public async Task 批量停用成功并返回停用数量()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User first = await CreateUserAsync("frank", UserRole.UserRoleUser);
        User second = await CreateUserAsync("grace", UserRole.UserRoleUser);

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/users/bulk-disable", new
        {
            userIds = new[] { first.ID, second.ID },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(2, data.GetProperty("disabledCount").GetInt32());
        Assert.Equal(2, data.GetProperty("users").GetArrayLength());
    }

    [Fact]
    public async Task 批量停用去重且拒绝空列表()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User first = await CreateUserAsync("frank", UserRole.UserRoleUser);

        // 同一个 ID 传两次只算一个。
        HttpResponseMessage duplicated = await admin.PostAsJsonAsync("/api/admin/users/bulk-disable", new
        {
            userIds = new[] { first.ID, first.ID },
        });
        Assert.Equal(1, (await ReadDataAsync(duplicated)).GetProperty("disabledCount").GetInt32());

        HttpResponseMessage empty = await admin.PostAsJsonAsync("/api/admin/users/bulk-disable", new
        {
            userIds = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("请选择要停用的用户", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量停用包含当前管理员时被拒()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        Repository repository = await RepositoryAsync();
        User self = (await repository.UserByUsernameAsync("admin"))!;

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/users/bulk-disable", new
        {
            userIds = new[] { self.ID },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不能停用当前登录的管理员账号", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 批量停用不存在的用户时被拒()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/users/bulk-disable", new
        {
            userIds = new[] { "NOT_EXIST" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("部分用户不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分页参数非法返回_400()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/users?page=abc");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("page", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 口令重置

    [Fact]
    public async Task 未启用邮件时取口令重置验证码返回_403()
    {
        await SignInAsAdminAsync();

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/password-reset-code", new
        {
            email = "admin@example.com",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("管理员尚未启用密码找回", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 口令重置验证码长度不对返回_400()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/password-reset", new
        {
            email = "someone@example.com",
            emailCode = "123",
            password = "newpassword123",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // 所有失败路径统一文案，不区分账号是否存在。
        Assert.Contains("验证码无效或已过期", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 口令重置口令过短返回_400()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/password-reset", new
        {
            email = "someone@example.com",
            emailCode = "123456",
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("密码至少 8 位", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不存在的账号与错误验证码返回同一文案()
    {
        HttpResponseMessage missing = await _client.PostAsJsonAsync("/api/auth/password-reset", new
        {
            email = "nobody@example.com",
            emailCode = "123456",
            password = "newpassword123",
        });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("验证码无效或已过期", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }


    // ------------------------------------------------------------ 用户详情类

    [Fact]
    public async Task 用户账本返回账户与分页字段()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);
        await CreateCreditAccountAsync(target.ID, available: 100 * CreditPolicyScale, reserved: 5);

        HttpResponseMessage response = await admin.GetAsync($"/api/admin/users/{target.ID}/ledger");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(100 * CreditPolicyScale, data.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());
        Assert.Equal(5, data.GetProperty("account").GetProperty("reservedMicrocredits").GetInt64());
        Assert.Equal(0, data.GetProperty("total").GetInt64());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        // 注意 json tag 是 pageSize。
        Assert.Equal(20, data.GetProperty("pageSize").GetInt32());

        // 公开积分策略随账本一起返回，默认值与 Go 一致。
        JsonElement policy = data.GetProperty("policy");
        Assert.Equal(100 * CreditPolicyScale, policy.GetProperty("signupBonusMicrocredits").GetInt64());
        Assert.Equal(10 * CreditPolicyScale, policy.GetProperty("checkinBonusMicrocredits").GetInt64());
        Assert.False(policy.GetProperty("checkedInToday").GetBoolean());
    }

    [Fact]
    public async Task 用户账本按类型过滤()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);
        await CreateCreditAccountAsync(target.ID, available: 0, reserved: 0);
        await CreateLedgerEntryAsync(target.ID, "signup_bonus", 100 * CreditPolicyScale);
        await CreateLedgerEntryAsync(target.ID, "consume", -30 * CreditPolicyScale);
        await CreateLedgerEntryAsync(target.ID, "reserve", -10 * CreditPolicyScale);

        // 不过滤：reserve 永远被排除。
        HttpResponseMessage all = await admin.GetAsync($"/api/admin/users/{target.ID}/ledger");
        Assert.Equal(2, (await ReadDataAsync(all)).GetProperty("total").GetInt64());

        HttpResponseMessage income = await admin.GetAsync($"/api/admin/users/{target.ID}/ledger?type=income");
        Assert.Equal(1, (await ReadDataAsync(income)).GetProperty("total").GetInt64());

        HttpResponseMessage consume = await admin.GetAsync($"/api/admin/users/{target.ID}/ledger?type=consume");
        Assert.Equal(1, (await ReadDataAsync(consume)).GetProperty("total").GetInt64());

        HttpResponseMessage refund = await admin.GetAsync($"/api/admin/users/{target.ID}/ledger?type=refund");
        Assert.Equal(0, (await ReadDataAsync(refund)).GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task 用户账本对不存在的用户返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/users/NOT_EXIST/ledger");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("用户不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 用户任务列表返回分页字段()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);

        HttpResponseMessage response = await admin.GetAsync($"/api/admin/users/{target.ID}/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("tasks").ValueKind);
        Assert.Equal(0, data.GetProperty("total").GetInt64());
        Assert.Equal(20, data.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task 审计事件记录创建与更新操作()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 创建用户会写入一条 user.create 审计事件。
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            username = "frank",
            password = "password123",
            role = "user",
            status = "active",
        });
        JsonElement createdUser = (await ReadDataAsync(created)).GetProperty("user");
        string targetId = createdUser.GetProperty("id").GetString()!;

        // 更新再写一条 user.update。
        await admin.PatchAsync(
            $"/api/admin/users/{targetId}",
            new StringContent("{\"displayName\":\"Frank\"}", Encoding.UTF8, "application/json"));

        HttpResponseMessage response = await admin.GetAsync($"/api/admin/users/{targetId}/audit-events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(2, data.GetProperty("total").GetInt64());

        JsonElement events = data.GetProperty("events");
        // 按 created_at DESC，最新的更新在前。
        Assert.Equal("user.update", events[0].GetProperty("action").GetString());
        Assert.Equal("user", events[0].GetProperty("targetType").GetString());
        Assert.Equal(targetId, events[0].GetProperty("targetId").GetString());
        Assert.Equal("user.create", events[1].GetProperty("action").GetString());
    }

    [Fact]
    public async Task 详情类路由对非管理员返回_403()
    {
        await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "frank",
            password = "password123",
        });
        using HttpClient frank = Authenticated(login);

        foreach (string path in new[]
        {
            $"/api/admin/users/{target.ID}/ledger",
            $"/api/admin/users/{target.ID}/tasks",
            $"/api/admin/users/{target.ID}/audit-events",
        })
        {
            HttpResponseMessage response = await frank.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }


    // ------------------------------------------------------------ 用户详情

    [Fact]
    public async Task 用户详情返回聚合数据()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);
        await CreateCreditAccountAsync(target.ID, available: 100 * CreditPolicyScale, reserved: 5);

        HttpResponseMessage response = await admin.GetAsync($"/api/admin/users/{target.ID}/detail");
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK) { Assert.Fail(body); }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);

        Assert.Equal(target.ID, data.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(100 * CreditPolicyScale, data.GetProperty("account").GetProperty("availableMicrocredits").GetInt64());
        Assert.Equal(0, data.GetProperty("counts").GetProperty("ledgerEntries").GetInt64());
        Assert.Equal(0, data.GetProperty("storageUsage").GetProperty("assetCount").GetInt64());
        Assert.Equal(0, data.GetProperty("storedFileBytes").GetInt64());
        Assert.Equal(0, data.GetProperty("dailyUploadBytes").GetInt64());
        Assert.True(data.GetProperty("quota").GetProperty("resourceUploadMB").GetInt64() > 0);
    }

    [Fact]
    public async Task 用户详情对不存在的用户返回_404()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/users/NOT_EXIST/detail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 用户详情对非管理员返回_403()
    {
        await SignInAsAdminAsync();
        User target = await CreateUserAsync("frank", UserRole.UserRoleUser);

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "frank",
            password = "password123",
        });
        using HttpClient frank = Authenticated(login);

        HttpResponseMessage response = await frank.GetAsync($"/api/admin/users/{target.ID}/detail");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }



    // ------------------------------------------------------------ 系统渠道

    [Fact]
    public async Task 系统渠道列表对管理员返回空数组()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage response = await admin.GetAsync("/api/channels/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = await ReadDataAsync(response);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("channels").ValueKind);
        Assert.Equal(0, data.GetProperty("channels").GetArrayLength());
    }

    [Fact]
    public async Task 系统渠道列表对非管理员返回_403()
    {
        await SignInAsAdminAsync();
        User plain = await CreateUserAsync("bob", UserRole.UserRoleUser);

        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "bob",
            password = "password123",
        });
        using HttpClient bob = Authenticated(login);

        HttpResponseMessage response = await bob.GetAsync("/api/channels/system");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ------------------------------------------------------------ 辅助


    /// <summary>对应 Go 的 app.CreditScale。</summary>
    private const long CreditPolicyScale = 1_000_000;

    private async Task CreateCreditAccountAsync(string userId, long available, long reserved)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new CreditAccount
        {
            UserID = userId,
            AvailableMicrocredits = available,
            ReservedMicrocredits = reserved,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task CreateLedgerEntryAsync(string userId, string type, long amount)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        await repository.CreateAsync(new CreditLedgerEntry
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Type = type,
            AmountMicrocredits = amount,
            AvailableDeltaMicrocredits = amount,
            AvailableAfterMicrocredits = amount,
            Scene = "test",
            Note = "test",
            ReferenceKey = IdGenerator.NewId(),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task<User> CreateUserAsync(string username, string role)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        User user = new()
        {
            ID = IdGenerator.NewId(),
            Username = username,
            DisplayName = username,
            Email = username + "@example.com",
            Role = role,
            Status = UserStatus.UserStatusActive,
            PasswordHash = OpenAICanvas.Auth.AuthService.HashPassword("password123"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await repository.CreateAsync(user);
        return user;
    }
}
