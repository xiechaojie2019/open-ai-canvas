#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 上游模型目录拉取（fetch/preview）与导入（import）的端到端契约测试。
/// 用本地 TcpListener 模拟上游 /models 接口，验证 SSRF 受控出站、错误映射与导入语义。
/// 对应 Go: <c>app/channel_model_catalog.go</c> 与 <c>handler/finance.go</c>。
/// </summary>
public sealed class ChannelModelCatalogTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private HttpClient? _adminClient;

    public ChannelModelCatalogTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-ch-cat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
            builder.UseSetting("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
        });
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");

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

    /// <summary>极简上游：响应一个固定 JSON；记录收到的 Authorization 头。</summary>
    private static async Task<(TcpListener Listener, string BaseUrl, ConcurrentQueue<string> AuthHeaders)>
        StartUpstreamAsync(string payload, int statusCode = 200)
    {
        TcpListener listener = new(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ConcurrentQueue<string> authHeaders = new();

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using TcpClient owned = client;
                            using NetworkStream stream = owned.GetStream();
                            byte[] buffer = new byte[16384];
                            StringBuilder head = new();
                            while (!head.ToString().Contains("\r\n\r\n"))
                            {
                                int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                                if (read == 0)
                                {
                                    break;
                                }
                                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }
                            foreach (string line in head.ToString().Split("\r\n"))
                            {
                                if (line.StartsWith("Authorization: ", StringComparison.OrdinalIgnoreCase))
                                {
                                    authHeaders.Enqueue(line["Authorization: ".Length..]);
                                }
                            }

                            byte[] body = Encoding.UTF8.GetBytes(payload);
                            string statusText = statusCode == 200 ? "200 OK" : $"{statusCode} Error";
                            byte[] responseHead = Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 {statusText}\r\n" +
                                "Content-Type: application/json\r\n" +
                                $"Content-Length: {body.Length}\r\n" +
                                "Connection: close\r\n\r\n");
                            await stream.WriteAsync(responseHead).ConfigureAwait(false);
                            await stream.WriteAsync(body).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                        }
                    });
                }
            }
            catch (SocketException)
            {
            }
        });

        await Task.CompletedTask.ConfigureAwait(false);
        return (listener, $"http://localhost:{port}/v1", authHeaders);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task 拉取上游目录_鉴权头与排序()
    {
        HttpClient admin = await SignInAsAdminAsync();
        (TcpListener listener, string baseUrl, ConcurrentQueue<string> authHeaders) = await StartUpstreamAsync(
            """{"data":[{"id":"zeta"},{"id":"alpha"},{"id":"models/mid"}],"code":0}""");
        try
        {
            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
            {
                name = "目录渠道",
                baseUrl,
                apiKey = "sk-upstream-test",
                enabled = true,
            });
            created.EnsureSuccessStatusCode();
            string channelId = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

            HttpResponseMessage fetch = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/fetch", new { });

            Console.WriteLine("FETCH_DEBUG=" + await fetch.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, fetch.StatusCode);
            JsonElement models = (await ReadDataAsync(fetch)).GetProperty("models");
            // 去重、剥 models/ 前缀、按字典序。
            Assert.Equal(
                ["alpha", "mid", "zeta"],
                models.EnumerateArray().Select(item => item.GetString()));
            // 渠道密钥以 Bearer 头发送给上游。
            Assert.Equal("Bearer sk-upstream-test", authHeaders.FirstOrDefault());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 导入_创建停用占位且重复导入不重复建()
    {
        HttpClient admin = await SignInAsAdminAsync();
        (TcpListener listener, string baseUrl, _) = await StartUpstreamAsync(
            """{"data":[{"id":"alpha"},{"id":"beta"}]}""");
        try
        {
            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
            {
                name = "导入渠道",
                baseUrl,
                apiKey = "sk-import-test",
                enabled = true,
            });
            string channelId = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

            HttpResponseMessage imported = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/import",
                new { models = new[] { "alpha", "models/beta" } });

            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            JsonElement result = await ReadDataAsync(imported);
            Assert.Equal(2, result.GetProperty("added").GetInt64());
            Assert.Equal(
                ["alpha", "beta"],
                result.GetProperty("models").EnumerateArray().Select(item => item.GetString()));

            // 占位模型已停用、未定价。
            HttpResponseMessage list = await admin.GetAsync($"/api/admin/channels/{channelId}/models");
            JsonElement modelList = (await ReadDataAsync(list)).GetProperty("models");
            Assert.Equal(2, modelList.GetArrayLength());
            Assert.All(modelList.EnumerateArray(), item =>
            {
                Assert.False(item.GetProperty("enabled").GetBoolean());
                Assert.False(item.GetProperty("priceConfigured").GetBoolean());
            });

            // 重复导入不再新增。
            HttpResponseMessage again = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/import",
                new { models = new[] { "alpha" } });
            Assert.Equal(0, (await ReadDataAsync(again)).GetProperty("added").GetInt64());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 导入校验_空选择与未知模型()
    {
        HttpClient admin = await SignInAsAdminAsync();
        (TcpListener listener, string baseUrl, _) = await StartUpstreamAsync(
            """{"data":[{"id":"alpha"}]}""");
        try
        {
            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
            {
                name = "校验渠道",
                baseUrl,
                apiKey = "sk-validate-test",
                enabled = true,
            });
            string channelId = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

            HttpResponseMessage empty = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/import",
                new { models = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Contains("请至少选择一个要导入的模型", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            HttpResponseMessage unknown = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/import",
                new { models = new[] { "no-such-model" } });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Contains(
                "所选模型不在上游模型目录中：no-such-model",
                await unknown.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 上游鉴权失败映射为_502()
    {
        HttpClient admin = await SignInAsAdminAsync();
        (TcpListener listener, string baseUrl, _) = await StartUpstreamAsync(
            """{"error":{"message":"bad key"}}""", statusCode: 401);
        try
        {
            HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/channels", new
            {
                name = "鉴权渠道",
                baseUrl,
                apiKey = "sk-bad-test",
                enabled = true,
            });
            string channelId = (await ReadDataAsync(created)).GetProperty("channel").GetProperty("id").GetString()!;

            HttpResponseMessage fetch = await admin.PostAsJsonAsync(
                $"/api/admin/channels/{channelId}/models/fetch", new { });

            Assert.Equal(HttpStatusCode.BadGateway, fetch.StatusCode);
            string body = await fetch.Content.ReadAsStringAsync();
            Assert.Contains("模型服务鉴权失败，请检查 API Key", body, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }
}
