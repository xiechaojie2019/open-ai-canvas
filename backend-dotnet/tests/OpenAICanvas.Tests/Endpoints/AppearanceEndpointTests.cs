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
/// 外观配置路由的契约测试。
/// 对应 Go: <c>handler/appearance.go</c> 与 <c>app/appearance.go</c> / <c>app/appearance_skins.go</c>。
/// </summary>
public sealed class AppearanceEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    /// <summary>1x1 PNG，用作外观图片资源。</summary>
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    public AppearanceEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-appearance-{Guid.NewGuid():N}");
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
        _adminClient?.Dispose();
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

    private async Task<HttpClient> SignInAdminAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "appearanceadmin",
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

    /// <summary>构造一份最小可用的 PATCH 请求体（以公开默认值为基底）。</summary>
    private static Dictionary<string, object?> PatchBody(string brandName = "测试品牌", string brandSlug = "test-brand") => new()
    {
        ["brandName"] = brandName,
        ["brandSlug"] = brandSlug,
        ["authHeroTitle"] = "自定义主标题",
        ["authHeroDescription"] = "",
        ["logoResourceId"] = "",
        ["darkLogoResourceId"] = "",
        ["logoFrameEnabled"] = true,
        ["authVideoResourceId"] = "",
        ["authVideoPosterResourceId"] = "",
        ["authVideoAutoplay"] = false,
        ["skinId"] = "classic",
        ["skinThemes"] = Array.Empty<object>(),
        ["seoTitle"] = "",
        ["seoDescription"] = "",
        ["seoKeywords"] = "",
        ["footerCopyright"] = "",
        ["icpFilingEnabled"] = false,
        ["icpFilingNumber"] = "",
    };

    [Fact]
    public async Task 公开外观_未配置时返回内置默认值()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/public/appearance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").First());

        JsonElement appearance = (await ReadDataAsync(response)).GetProperty("appearance");
        Assert.Equal("影策", appearance.GetProperty("brandName").GetString());
        Assert.Equal("open-ai-canvas", appearance.GetProperty("brandSlug").GetString());
        Assert.Equal("/logo.svg", appearance.GetProperty("logoUrl").GetString());
        Assert.Equal("builtin", appearance.GetProperty("revision").GetString());
        Assert.False(appearance.GetProperty("configured").GetBoolean());
        // 未配置时四类资源均未就绪。
        Assert.False(appearance.GetProperty("logoConfigured").GetBoolean());
        Assert.False(appearance.GetProperty("authVideoConfigured").GetBoolean());
        // SEO 标题回落到品牌名。
        Assert.Equal("影策", appearance.GetProperty("seoTitle").GetString());
        // 版权信息按年份生成。
        Assert.Contains(DateTime.Now.Year.ToString(), appearance.GetProperty("footerCopyright").GetString()!,
            StringComparison.Ordinal);
        // 内置 4 套主题，activeSkin 为经典黑白。
        Assert.Equal("classic", appearance.GetProperty("skinId").GetString());
        Assert.Equal("classic", appearance.GetProperty("activeSkin").GetProperty("id").GetString());
        Assert.True(appearance.GetProperty("activeSkin").GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task 管理端外观_未登录401_非管理员403()
    {
        HttpResponseMessage anonymous = await _client.GetAsync("/api/admin/settings/appearance");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage opened = await admin.PatchAsJsonAsync(
            "/api/admin/settings/registration", new { enabled = true });
        opened.EnsureSuccessStatusCode();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>();
            await repository.CreateAsync(new OpenAICanvas.Domain.Entities.User
            {
                ID = OpenAICanvas.Domain.Kernel.IdGenerator.NewId(),
                Username = "appearanceuser",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                Role = "user",
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        HttpResponseMessage login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "appearanceuser",
            password = "password123",
        });
        login.EnsureSuccessStatusCode();
        using HttpClient plain = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        plain.DefaultRequestHeaders.Add(
            "Cookie", login.Headers.GetValues("Set-Cookie").First().Split(';')[0]);

        HttpResponseMessage forbidden = await plain.GetAsync("/api/admin/settings/appearance");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task 管理端外观_读取返回管理字段与公开投影()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage response = await admin.GetAsync("/api/admin/settings/appearance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement setting = (await ReadDataAsync(response)).GetProperty("setting");
        Assert.Equal("影策", setting.GetProperty("brandName").GetString());
        Assert.False(setting.GetProperty("configured").GetBoolean());
        // 管理端平铺字段 + public 投影同时存在。
        Assert.True(setting.TryGetProperty("public", out JsonElement publicView));
        Assert.Equal("影策", publicView.GetProperty("brandName").GetString());
        Assert.Equal(4, setting.GetProperty("skinThemes").GetArrayLength());
    }

    [Fact]
    public async Task 更新外观_成功并持久化且审计可查()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage response = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody("新品牌", "new-brand"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement setting = (await ReadDataAsync(response)).GetProperty("setting");
        Assert.Equal("新品牌", setting.GetProperty("brandName").GetString());
        Assert.Equal("new-brand", setting.GetProperty("brandSlug").GetString());
        Assert.True(setting.GetProperty("configured").GetBoolean());
        // 已配置后 revision 不再是 builtin。
        Assert.NotEqual("builtin", setting.GetProperty("public").GetProperty("revision").GetString());

        // 公开端可见新品牌与自定义主标题。
        HttpResponseMessage publicResponse = await _client.GetAsync("/api/public/appearance");
        JsonElement appearance = (await ReadDataAsync(publicResponse)).GetProperty("appearance");
        Assert.Equal("新品牌", appearance.GetProperty("brandName").GetString());
        Assert.Equal("自定义主标题", appearance.GetProperty("authHeroTitle").GetString());
        Assert.True(appearance.GetProperty("configured").GetBoolean());

        // 审计事件落在 system_setting/appearance 上。
        HttpResponseMessage audit = await admin.GetAsync(
            "/api/admin/audit-events?targetType=system_setting&targetId=appearance&page=1&pageSize=20");
        if (audit.StatusCode == HttpStatusCode.OK)
        {
            Assert.Contains("appearance.update", await audit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 更新外观_品牌名与slug校验()
    {
        HttpClient admin = await SignInAdminAsync();

        // 品牌名为空。
        HttpResponseMessage emptyName = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody(brandName: ""));
        Assert.Equal(HttpStatusCode.BadRequest, emptyName.StatusCode);
        Assert.Contains("品牌名称必须为 1 到 40 个字符", await emptyName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 品牌名过长（41 个字符）。
        HttpResponseMessage longName = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody(brandName: new string('长', 41)));
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Contains("品牌名称必须为 1 到 40 个字符", await longName.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // slug 含大写与空格（归一化后仍非法字符则报错）。
        HttpResponseMessage badSlug = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody(brandSlug: "bad_slug"));
        Assert.Equal(HttpStatusCode.BadRequest, badSlug.StatusCode);
        Assert.Contains("英文品牌标识", await badSlug.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // slug 以连字符开头。
        HttpResponseMessage leadingDash = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody(brandSlug: "-leading"));
        Assert.Equal(HttpStatusCode.BadRequest, leadingDash.StatusCode);

        // 主标题为空（required）。
        Dictionary<string, object?> noHero = PatchBody();
        noHero["authHeroTitle"] = "";
        HttpResponseMessage badHero = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", noHero);
        Assert.Equal(HttpStatusCode.BadRequest, badHero.StatusCode);
        Assert.Contains("登录页主标题不能为空", await badHero.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 启用备案号但未填号码。
        Dictionary<string, object?> icp = PatchBody();
        icp["icpFilingEnabled"] = true;
        icp["icpFilingNumber"] = "";
        HttpResponseMessage badIcp = await admin.PatchAsJsonAsync("/api/admin/settings/appearance", icp);
        Assert.Equal(HttpStatusCode.BadRequest, badIcp.StatusCode);
        Assert.Contains("显示备案号前请先填写备案号", await badIcp.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 品牌名含控制字符（换行）。
        HttpResponseMessage controlChar = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody(brandName: "bad\u0001name"));
        Assert.Equal(HttpStatusCode.BadRequest, controlChar.StatusCode);
        Assert.Contains("品牌名称不能包含控制字符", await controlChar.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新外观_皮肤主题约束()
    {
        HttpClient admin = await SignInAdminAsync();

        // 缺少 classic 主题。
        Dictionary<string, object?> noClassic = PatchBody();
        noClassic["skinThemes"] = new[]
        {
            new { id = "only-one", name = "仅此一套", description = "", tokens = (object?)null },
        };
        HttpResponseMessage missingClassic = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", noClassic);
        Assert.Equal(HttpStatusCode.BadRequest, missingClassic.StatusCode);

        // 通过公开默认皮肤拿到合法 tokens，再验证"经典黑白不可修改"。
        JsonElement defaults = (await ReadDataAsync(
            await admin.GetAsync("/api/admin/settings/appearance"))).GetProperty("setting");
        JsonElement classic = defaults.GetProperty("skinThemes").EnumerateArray()
            .First(theme => theme.GetProperty("id").GetString() == "classic");

        Dictionary<string, object?> tampered = PatchBody();
        tampered["skinThemes"] = new[]
        {
            new
            {
                id = "classic",
                name = "被改过的名字",
                description = classic.GetProperty("description").GetString(),
                locked = true,
                tokens = classic.GetProperty("tokens"),
            },
        };
        HttpResponseMessage modified = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", tampered);
        Assert.Equal(HttpStatusCode.BadRequest, modified.StatusCode);
        Assert.Contains("经典黑白为系统默认主题", await modified.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // 启用的皮肤不存在。
        Dictionary<string, object?> badSelected = PatchBody();
        badSelected["skinId"] = "not-exists";
        HttpResponseMessage notFound = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", badSelected);
        Assert.Equal(HttpStatusCode.BadRequest, notFound.StatusCode);
        Assert.Contains("当前启用的皮肤主题不存在", await notFound.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 重置外观_恢复默认值()
    {
        HttpClient admin = await SignInAdminAsync();
        HttpResponseMessage updated = await admin.PatchAsJsonAsync(
            "/api/admin/settings/appearance", PatchBody("将被重置", "will-reset"));
        updated.EnsureSuccessStatusCode();

        HttpResponseMessage reset = await admin.DeleteAsync("/api/admin/settings/appearance");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        JsonElement setting = (await ReadDataAsync(reset)).GetProperty("setting");
        Assert.Equal("影策", setting.GetProperty("brandName").GetString());
        Assert.False(setting.GetProperty("configured").GetBoolean());

        // 公开端 revision 回到 builtin。
        JsonElement appearance = (await ReadDataAsync(
            await _client.GetAsync("/api/public/appearance"))).GetProperty("appearance");
        Assert.Equal("builtin", appearance.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task 上传外观资源_成功后公开端可见并可下发()
    {
        HttpClient admin = await SignInAdminAsync();

        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(PngBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "logo.png");

        HttpResponseMessage upload = await admin.PostAsync(
            "/api/admin/settings/appearance/assets/logo", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        JsonElement resource = (await ReadDataAsync(upload)).GetProperty("resource");
        string resourceId = resource.GetProperty("id").GetString()!;
        Assert.Equal("ready", resource.GetProperty("status").GetString());
        Assert.Equal("local", resource.GetProperty("provider").GetString());

        // 绑定到外观并保存。
        Dictionary<string, object?> body = PatchBody();
        body["logoResourceId"] = resourceId;
        HttpResponseMessage patched = await admin.PatchAsJsonAsync("/api/admin/settings/appearance", body);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);

        // 公开端 logoUrl 指向内容寻址的外观资源。
        JsonElement appearance = (await ReadDataAsync(
            await _client.GetAsync("/api/public/appearance"))).GetProperty("appearance");
        Assert.True(appearance.GetProperty("logoConfigured").GetBoolean());
        string logoUrl = appearance.GetProperty("logoUrl").GetString()!;
        Assert.StartsWith("/api/public/appearance/assets/logo?v=", logoUrl, StringComparison.Ordinal);

        // 匿名可下发，且带 v= 时长缓存。
        HttpResponseMessage asset = await _client.GetAsync(logoUrl);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("image/png", asset.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", asset.Headers.GetValues("Cache-Control").First(), StringComparison.Ordinal);
        Assert.Equal(PngBytes, await asset.Content.ReadAsByteArrayAsync());

        // 不带 v= 时须逐次校验，并支持 Range。
        using HttpRequestMessage ranged = new(HttpMethod.Get, "/api/public/appearance/assets/logo");
        ranged.Headers.TryAddWithoutValidation("Range", "bytes=0-7");
        HttpResponseMessage partial = await _client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("bytes 0-7/" + PngBytes.Length, partial.Content.Headers.GetValues("Content-Range").First());
    }

    [Fact]
    public async Task 上传外观资源_大小与类型校验()
    {
        HttpClient admin = await SignInAdminAsync();

        // 非法槽位。
        using (MultipartFormDataContent badSlot = new())
        {
            badSlot.Add(new ByteArrayContent(PngBytes), "file", "x.png");
            HttpResponseMessage response = await admin.PostAsync(
                "/api/admin/settings/appearance/assets/unknown", badSlot);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("外观资源类型无效", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 类型不受支持（文本当图片）。
        using (MultipartFormDataContent wrongType = new())
        {
            wrongType.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not an image at all")), "file", "x.png");
            HttpResponseMessage response = await admin.PostAsync(
                "/api/admin/settings/appearance/assets/logo", wrongType);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("文件类型不受支持", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 视频槽位接受结构合法的 ftyp 盒（Go 的通用嗅探器认不出时由该分支兜底）。
        byte[] mp4 = new byte[64];
        mp4[3] = 32; // boxSize = 32
        mp4[4] = (byte)'f';
        mp4[5] = (byte)'t';
        mp4[6] = (byte)'y';
        mp4[7] = (byte)'p';
        using (MultipartFormDataContent videoForm = new())
        {
            videoForm.Add(new ByteArrayContent(mp4), "file", "clip.mp4");
            HttpResponseMessage response = await admin.PostAsync(
                "/api/admin/settings/appearance/assets/video", videoForm);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement resource = (await ReadDataAsync(response)).GetProperty("resource");
            Assert.Equal("video", resource.GetProperty("kind").GetString());
            Assert.Equal("video/mp4", resource.GetProperty("mimeType").GetString());
        }
    }

    [Fact]
    public async Task 上传外观资源_未配置槽位下发404()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/public/appearance/assets/poster");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("未配置该外观资源", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void 外观MIME判定_视频槽位接受ftyp盒()
    {
        byte[] mp4 = new byte[64];
        mp4[3] = 32;
        mp4[4] = (byte)'f';
        mp4[5] = (byte)'t';
        mp4[6] = (byte)'y';
        mp4[7] = (byte)'p';

        // 通用嗅探给不出 mp4 时，视频槽位靠 ftyp 判定。
        Assert.Equal("video/mp4", OpenAICanvas.Application.Appearance.AppearanceService.DetectAppearanceMime(
            "video", mp4, mp4.Length, "application/octet-stream"));

        // 非视频槽位不做该兜底。
        Assert.Equal("application/octet-stream", OpenAICanvas.Application.Appearance.AppearanceService.DetectAppearanceMime(
            "logo", mp4, mp4.Length, "application/octet-stream"));

        // boxSize 非法（不足 12）时不认可。
        byte[] tiny = new byte[64];
        tiny[3] = 8;
        tiny[4] = (byte)'f';
        tiny[5] = (byte)'t';
        tiny[6] = (byte)'y';
        tiny[7] = (byte)'p';
        Assert.Equal("application/octet-stream", OpenAICanvas.Application.Appearance.AppearanceService.DetectAppearanceMime(
            "video", tiny, tiny.Length, "application/octet-stream"));

        // boxSize 超过文件长度时不认可。
        byte[] oversize = new byte[64];
        oversize[3] = 200;
        oversize[4] = (byte)'f';
        oversize[5] = (byte)'t';
        oversize[6] = (byte)'y';
        oversize[7] = (byte)'p';
        Assert.Equal("application/octet-stream", OpenAICanvas.Application.Appearance.AppearanceService.DetectAppearanceMime(
            "video", oversize, oversize.Length, "application/octet-stream"));
    }

    [Fact]
    public async Task 更新外观_引用不存在资源时报错()
    {
        HttpClient admin = await SignInAdminAsync();
        Dictionary<string, object?> body = PatchBody();
        body["logoResourceId"] = "no-such-resource";

        HttpResponseMessage response = await admin.PatchAsJsonAsync("/api/admin/settings/appearance", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("选择的外观资源不存在", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 更新外观_资源失效后公开端自动清理引用()
    {
        HttpClient admin = await SignInAdminAsync();

        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(PngBytes), "file", "logo.png");
        HttpResponseMessage upload = await admin.PostAsync(
            "/api/admin/settings/appearance/assets/logo", form);
        upload.EnsureSuccessStatusCode();
        string resourceId = (await ReadDataAsync(upload)).GetProperty("resource").GetProperty("id").GetString()!;

        Dictionary<string, object?> body = PatchBody();
        body["logoResourceId"] = resourceId;
        (await admin.PatchAsJsonAsync("/api/admin/settings/appearance", body)).EnsureSuccessStatusCode();

        // 从磁盘删除物理文件：公开端应视该资源为不可用并清理引用。
        string objectKey = (await ReadDataAsync(
            await admin.GetAsync("/api/admin/resources"))).GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == resourceId)
            .GetProperty("objectKey").GetString()!;
        File.Delete(Path.Combine(_dataDir, "resources", objectKey.Replace('/', Path.DirectorySeparatorChar)));

        JsonElement appearance = (await ReadDataAsync(
            await _client.GetAsync("/api/public/appearance"))).GetProperty("appearance");
        Assert.False(appearance.GetProperty("logoConfigured").GetBoolean());
        Assert.Equal("/logo.svg", appearance.GetProperty("logoUrl").GetString());
    }
}
