#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 平台设置路由的端到端契约测试：运行时策略、绘图工具、响应拦截、方舟素材库、公告配图上传。
/// 对应 Go: <c>internal/platform/runtime_policy.go</c> / <c>app/response_interception.go</c> /
/// <c>app/settings_ark_assets.go</c> / <c>handler/announcement.go</c> 配图上传。
/// </summary>
public sealed class AdminPlatformSettingsEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AdminPlatformSettingsEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-plat-{Guid.NewGuid():N}");
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
    public async Task 运行时策略_读写重置与校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 默认值：未配置，回落内置默认（activeTaskLimit=5）。
        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/runtime-policy");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement initialSetting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(initialSetting.GetProperty("configured").GetBoolean());
        Assert.Equal(5, initialSetting.GetProperty("task").GetProperty("activeTaskLimit").GetInt64());

        // 更新：合法值持久化并标记 configured。
        HttpResponseMessage updated = await admin.PutAsJsonAsync(
            "/api/admin/settings/runtime-policy", new
            {
                resource = new { resourceUploadMB = 80, generatedFileMB = 64, dailyUploadMB = 2048, storedFileGB = 20, structuredDataMB = 256, taskDataGB = 1, assetCount = 2000, canvasCount = 1000, taskCount = 20000, apiCallLogCount = 100000, recycleBinRetentionDays = 30 },
                task = new { workerConcurrency = 3, channelConcurrency = 3, activeTaskLimit = 8, imageTimeoutMinutes = 8, textTimeoutMinutes = 8, audioTimeoutMinutes = 8, videoTimeoutMinutes = 60, storyboardTimeoutMinutes = 20, defaultTimeoutMinutes = 10 },
                request = new { taskCreatePerMinute = 30, resourceUploadPerMinute = 30, resourceImportPerMinute = 30, assetWritePerMinute = 120, canvasWritePerMinute = 120, registerPerHour = 30, emailCodePerHour = 60, loginIpPerTenMinutes = 50, loginAccountPerTenMinutes = 10, systemRelayPerMinute = 120, customRelayPerMinute = 120, customRelayConcurrency = 4, customRelayRequestMB = 32, customRelayResponseMB = 32, customRelayTimeoutMinutes = 10, systemRelayRequestMB = 64, systemRelayResponseMB = 128, channelCircuitFailureCount = 5, channelCircuitOpenSeconds = 60 },
            });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        JsonElement updatedSetting = (await ReadDataAsync(updated)).GetProperty("setting");
        Assert.True(updatedSetting.GetProperty("configured").GetBoolean());
        Assert.Equal(8, updatedSetting.GetProperty("task").GetProperty("activeTaskLimit").GetInt64());
        Assert.Equal(80, updatedSetting.GetProperty("resource").GetProperty("resourceUploadMB").GetInt64());

        // 校验：单文件 0MB → 400（Go 文案）。
        HttpResponseMessage invalid = await admin.PutAsJsonAsync(
            "/api/admin/settings/runtime-policy", new
            {
                resource = new { resourceUploadMB = 0, generatedFileMB = 64, dailyUploadMB = 2048, storedFileGB = 20, structuredDataMB = 256, taskDataGB = 1, assetCount = 2000, canvasCount = 1000, taskCount = 20000, apiCallLogCount = 100000, recycleBinRetentionDays = 30 },
                task = new { workerConcurrency = 3, channelConcurrency = 3, activeTaskLimit = 5, imageTimeoutMinutes = 8, textTimeoutMinutes = 8, audioTimeoutMinutes = 8, videoTimeoutMinutes = 60, storyboardTimeoutMinutes = 20, defaultTimeoutMinutes = 10 },
                request = new { taskCreatePerMinute = 30, resourceUploadPerMinute = 30, resourceImportPerMinute = 30, assetWritePerMinute = 120, canvasWritePerMinute = 120, registerPerHour = 30, emailCodePerHour = 60, loginIpPerTenMinutes = 50, loginAccountPerTenMinutes = 10, systemRelayPerMinute = 120, customRelayPerMinute = 120, customRelayConcurrency = 4, customRelayRequestMB = 32, customRelayResponseMB = 32, customRelayTimeoutMinutes = 10, systemRelayRequestMB = 64, systemRelayResponseMB = 128, channelCircuitFailureCount = 5, channelCircuitOpenSeconds = 60 },
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("普通资源单文件必须是 1-999 MB 的整数", await ReadMessageAsync(invalid));

        // 自用模板：策略上限（activeTaskLimit=999）。
        HttpResponseMessage selfUse = await admin.GetAsync("/api/admin/settings/runtime-policy/self-use");
        Assert.Equal(HttpStatusCode.OK, selfUse.StatusCode);
        Assert.Equal(
            999,
            (await ReadDataAsync(selfUse)).GetProperty("setting").GetProperty("task").GetProperty("activeTaskLimit").GetInt64());

        // 重置 → 回到默认且未配置。
        HttpResponseMessage reset = await admin.DeleteAsync("/api/admin/settings/runtime-policy");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        JsonElement resetSetting = (await ReadDataAsync(reset)).GetProperty("setting");
        Assert.False(resetSetting.GetProperty("configured").GetBoolean());
        Assert.Equal(5, resetSetting.GetProperty("task").GetProperty("activeTaskLimit").GetInt64());
    }

    [Fact]
    public async Task 绘图工具与响应拦截设置()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 绘图工具默认 excalidraw、未配置。
        HttpResponseMessage engine = await admin.GetAsync("/api/admin/settings/drawing-engine");
        Assert.Equal(HttpStatusCode.OK, engine.StatusCode);
        JsonElement engineSetting = (await ReadDataAsync(engine)).GetProperty("setting");
        Assert.Equal("excalidraw", engineSetting.GetProperty("defaultEngine").GetString());
        Assert.False(engineSetting.GetProperty("configured").GetBoolean());

        // 更新为 tldraw。
        HttpResponseMessage engineUpdated = await admin.PatchAsJsonAsync(
            "/api/admin/settings/drawing-engine", new { defaultEngine = "tldraw", tldrawLicenseKey = " lic " });
        Assert.Equal(HttpStatusCode.OK, engineUpdated.StatusCode);
        JsonElement engineUpdatedSetting = (await ReadDataAsync(engineUpdated)).GetProperty("setting");
        Assert.Equal("tldraw", engineUpdatedSetting.GetProperty("defaultEngine").GetString());
        Assert.True(engineUpdatedSetting.GetProperty("configured").GetBoolean());

        // 响应拦截默认空规则。
        HttpResponseMessage interception = await admin.GetAsync("/api/admin/settings/response-interception");
        Assert.Equal(HttpStatusCode.OK, interception.StatusCode);
        Assert.Equal(0, (await ReadDataAsync(interception)).GetProperty("setting").GetProperty("rules").GetArrayLength());

        // 更新规则。
        HttpResponseMessage interceptionUpdated = await admin.PatchAsJsonAsync(
            "/api/admin/settings/response-interception", new
            {
                enabled = true,
                rules = new[] { new { contains = "  内部错误  ", replace = "  服务繁忙  " } },
            });
        Assert.Equal(HttpStatusCode.OK, interceptionUpdated.StatusCode);
        JsonElement interceptionData = await ReadDataAsync(interceptionUpdated);
        Assert.True(interceptionData.GetProperty("setting").GetProperty("enabled").GetBoolean());
        Assert.Equal("内部错误", interceptionData.GetProperty("setting").GetProperty("rules")[0].GetProperty("contains").GetString());
        Assert.Equal("服务繁忙", interceptionData.GetProperty("setting").GetProperty("rules")[0].GetProperty("replace").GetString());

        // 校验：空匹配文案 → 400。
        HttpResponseMessage invalidRule = await admin.PatchAsJsonAsync(
            "/api/admin/settings/response-interception", new
            {
                enabled = true,
                rules = new[] { new { contains = "", replace = "x" } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRule.StatusCode);
        Assert.Equal("第 1 条拦截规则的匹配文案不能为空", await ReadMessageAsync(invalidRule));
    }

    [Fact]
    public async Task 方舟素材库设置_校验与密钥脱敏()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/ark-private-assets");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement initialSetting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(initialSetting.GetProperty("enabled").GetBoolean());
        Assert.False(initialSetting.GetProperty("hasAccessKeySecret").GetBoolean());

        // 启用但缺 Region → 400。
        HttpResponseMessage missingRegion = await admin.PatchAsJsonAsync(
            "/api/admin/settings/ark-private-assets", new
            {
                enabled = true,
                projectName = "proj",
                accessKeyId = "ak",
                accessKeySecret = "sk",
            });
        Assert.Equal(HttpStatusCode.BadRequest, missingRegion.StatusCode);
        Assert.Equal("请填写方舟 Region", await ReadMessageAsync(missingRegion));

        // 完整启用：密钥只回 hasAccessKeySecret。
        HttpResponseMessage enabled = await admin.PatchAsJsonAsync(
            "/api/admin/settings/ark-private-assets", new
            {
                enabled = true,
                region = "cn-beijing",
                projectName = "proj",
                accessKeyId = "ak",
                accessKeySecret = "sk-secret",
            });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        JsonElement enabledSetting = (await ReadDataAsync(enabled)).GetProperty("setting");
        Assert.True(enabledSetting.GetProperty("enabled").GetBoolean());
        Assert.True(enabledSetting.GetProperty("hasAccessKeySecret").GetBoolean());
        Assert.False(enabledSetting.TryGetProperty("accessKeySecret", out _));

        // 回读：密钥仍脱敏（已加密落库）。
        HttpResponseMessage reread = await admin.GetAsync("/api/admin/settings/ark-private-assets");
        JsonElement rereadSetting = (await ReadDataAsync(reread)).GetProperty("setting");
        Assert.True(rereadSetting.GetProperty("hasAccessKeySecret").GetBoolean());
        Assert.False(rereadSetting.TryGetProperty("accessKeySecret", out _));
    }

    [Fact]
    public async Task LibTV设置_读取保存脱敏清除与连接测试校验()
    {
        using HttpClient admin = await SignInAsAdminAsync();
        HttpResponseMessage initial = await admin.GetAsync("/api/admin/settings/libtv");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        JsonElement initialSetting = (await ReadDataAsync(initial)).GetProperty("setting");
        Assert.False(initialSetting.GetProperty("enabled").GetBoolean());
        Assert.False(initialSetting.GetProperty("hasToken").GetBoolean());

        HttpResponseMessage enabledWithoutToken = await admin.PatchAsJsonAsync(
            "/api/admin/settings/libtv", new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, enabledWithoutToken.StatusCode);
        Assert.Equal("启用 LibTV 前请先配置 Token", await ReadMessageAsync(enabledWithoutToken));

        HttpResponseMessage saved = await admin.PatchAsJsonAsync(
            "/api/admin/settings/libtv", new { enabled = false, token = "libtv-secret" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain("libtv-secret", await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True((await ReadDataAsync(saved)).GetProperty("setting").GetProperty("hasToken").GetBoolean());

        HttpResponseMessage preserved = await admin.PatchAsJsonAsync(
            "/api/admin/settings/libtv", new { enabled = false, token = " " });
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.True((await ReadDataAsync(preserved)).GetProperty("setting").GetProperty("hasToken").GetBoolean());

        HttpResponseMessage invalidUUID = await admin.PostAsJsonAsync(
            "/api/admin/settings/libtv/test", new { uuid = "not-a-uuid" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUUID.StatusCode);
        Assert.Equal("LibTV 画布 UUID 格式无效", await ReadMessageAsync(invalidUUID));

        HttpResponseMessage cleared = await admin.PatchAsJsonAsync(
            "/api/admin/settings/libtv", new { enabled = false, clearToken = true });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.False((await ReadDataAsync(cleared)).GetProperty("setting").GetProperty("hasToken").GetBoolean());
    }

    [Fact]
    public async Task 公告配图上传_校验与草稿登记()
    {
        using HttpClient admin = await SignInAsAdminAsync();

        // 真实 PNG 头的小图。
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(png), "file", "cover.png");
        HttpResponseMessage uploaded = await admin.PostAsync("/api/admin/announcement-images", form);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        JsonElement resource = (await ReadDataAsync(uploaded)).GetProperty("resource");
        Assert.Equal("ready", resource.GetProperty("status").GetString());
        Assert.Equal("image/png", resource.GetProperty("mimeType").GetString());

        // 非图片内容 → 400。
        using MultipartFormDataContent badForm = new();
        badForm.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "fake.png");
        HttpResponseMessage badUpload = await admin.PostAsync("/api/admin/announcement-images", badForm);
        Assert.Equal(HttpStatusCode.BadRequest, badUpload.StatusCode);
        Assert.Equal("公告配图必须是真实图片文件", await ReadMessageAsync(badUpload));
    }

    [Fact]
    public async Task 未登录访问返回_401()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/settings/runtime-policy")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/settings/drawing-engine")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/settings/response-interception")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/admin/settings/ark-private-assets")).StatusCode);
    }
}
