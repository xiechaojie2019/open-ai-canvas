#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAICanvas.Auth;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 邮箱验证码发送链路的端到端测试：注入可编程的 <see cref="IMailSender"/> 替身，
/// 验证"取码 → 发送 → 用码注册"全链路与"发送失败回删验证码"的语义。
/// 对应 Go: <c>auth.email.go</c> 的 <c>deliverEmail</c> 注入点。
/// </summary>
public sealed class AuthEmailCodeTests : IDisposable
{
    private readonly string _dataDir;
    private readonly RecordingMailSender _mailSender = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public AuthEmailCodeTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-mail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<OpenAICanvas.Application.CanvasService>();
                services.AddSingleton(serviceProvider => new OpenAICanvas.Application.CanvasService(
                    serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
                    runtimePolicy: serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
                    authHost: serviceProvider.GetRequiredService<OpenAICanvas.Application.CanvasAuthHost>(),
                    mailSender: _mailSender));
            });
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

    /// <summary>注册首个管理员（即成管理员），打开注册开关并启用邮件。</summary>
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

        HttpResponseMessage registration = await _adminClient.PatchAsJsonAsync(
            "/api/admin/settings/registration", new { enabled = true });
        registration.EnsureSuccessStatusCode();

        HttpResponseMessage email = await _adminClient.PatchAsJsonAsync("/api/admin/settings/email", new
        {
            enabled = true,
            host = "smtp.example.com",
            port = 587,
            username = "noreply@example.com",
            password = "secret",
            encryption = "starttls",
            fromEmail = "noreply@example.com",
            fromName = "影策",
        });
        email.EnsureSuccessStatusCode();

        return _adminClient;
    }

    [Fact]
    public async Task 取码走真实发送接口且验证码可完成注册()
    {
        await SignInAsAdminAsync();

        HttpResponseMessage issue = await _client.PostAsJsonAsync("/api/auth/email-code", new
        {
            email = "new@qq.com",
        });

        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
        JsonElement data = await ReadDataAsync(issue);
        Assert.True(data.GetProperty("sent").GetBoolean());

        // 发送器被真实调用：收件人、主题、正文验证码。
        SentMail mail = Assert.Single(_mailSender.Sent);
        Assert.Equal("new@qq.com", mail.Recipient);
        Assert.EndsWith("注册验证码", mail.Subject, StringComparison.Ordinal);
        // 只取"验证码："后的 6 位，避免把"10 分钟内有效"的数字算进来。
        string code = Regex.Match(mail.Body, @"验证码：(\d{6})").Groups[1].Value;
        Assert.Equal(6, code.Length);

        HttpResponseMessage registered = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "bob",
            password = "password123",
            email = "new@qq.com",
            emailCode = code,
        });

        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        JsonElement envelope = JsonDocument.Parse(await registered.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, envelope.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 发送失败回删验证码且不进入冷却()
    {
        await SignInAsAdminAsync();
        _mailSender.Failure = new InvalidOperationException("SMTP 服务器不可达");

        HttpResponseMessage failed = await _client.PostAsJsonAsync("/api/auth/email-code", new
        {
            email = "new@qq.com",
        });

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        // 5xx 只回固定文案（与 Go 的 safeInternalErrorMessage 一致），细节仅进日志。
        JsonElement failedEnvelope = JsonDocument.Parse(await failed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(500, failedEnvelope.GetProperty("code").GetInt32());
        Assert.Equal("系统处理失败，请稍后重试", failedEnvelope.GetProperty("msg").GetString());

        // 回删后立即可重试：若记录未删，这里会命中 1 分钟冷却（429）。
        _mailSender.Failure = null;
        HttpResponseMessage retried = await _client.PostAsJsonAsync("/api/auth/email-code", new
        {
            email = "new@qq.com",
        });

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        // 失败的那次不会留下记录，替身里只有重试成功的这一封。
        Assert.Single(_mailSender.Sent);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record SentMail(string Recipient, string Subject, string Body);

    /// <summary>记录式邮件替身：默认成功并记录，可切换为按次抛错。</summary>
    private sealed class RecordingMailSender : IMailSender
    {
        public List<SentMail> Sent { get; } = [];

        public Exception? Failure { get; set; }

        public Task SendAsync(
            EmailSettingValue setting,
            string recipient,
            string subject,
            string body,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            Sent.Add(new SentMail(recipient, subject, body));
            return Task.CompletedTask;
        }
    }
}
