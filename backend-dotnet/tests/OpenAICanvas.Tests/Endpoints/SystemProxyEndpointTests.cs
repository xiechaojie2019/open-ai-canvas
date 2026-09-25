#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 系统渠道服务端代理：路径/模型授权、SSRF 与 Key 守卫、非流式和 SSE 透传。
/// </summary>
public sealed class SystemProxyEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SystemProxyEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-system-proxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "localhost");
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

    [Fact]
    public async Task 系统代理_拒绝未授权模型和路径()
    {
        using HttpClient client = await SignInAsync();
        string channelId = await SeedChannelAsync("https://example.com/v1", "sk-proxy");

        HttpResponseMessage model = await client.PostAsJsonAsync(
            $"/api/ai/system/{channelId}/chat/completions",
            new { model = "not-authorized", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, model.StatusCode);

        HttpResponseMessage path = await client.PostAsJsonAsync(
            $"/api/ai/system/{channelId}/account",
            new { model = "proxy-model" });
        Assert.Equal(HttpStatusCode.Forbidden, path.StatusCode);
    }

    [Fact]
    public async Task 系统代理_拒绝空Key和SSRF地址()
    {
        using HttpClient client = await SignInAsync();
        string noKey = await SeedChannelAsync("https://example.com/v1", "");
        HttpResponseMessage noKeyResponse = await client.PostAsJsonAsync(
            $"/api/ai/system/{noKey}/chat/completions",
            new { model = "proxy-model", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, noKeyResponse.StatusCode);
        Assert.Contains("API Key", await noKeyResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        string ssrf = await SeedChannelAsync("http://127.0.0.1:1/v1", "sk-proxy");
        HttpResponseMessage ssrfResponse = await client.PostAsJsonAsync(
            $"/api/ai/system/{ssrf}/chat/completions",
            new { model = "proxy-model", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, ssrfResponse.StatusCode);
    }

    [Fact]
    public async Task 系统代理_非流式透传状态RetryAfter和鉴权()
    {
        (TcpListener listener, string baseUrl, ConcurrentQueue<string> requests) =
            await StartUpstreamAsync("{\"ok\":true}", "application/json", 201, retryAfter: "7");
        try
        {
            using HttpClient client = await SignInAsync();
            string channelId = await SeedChannelAsync(baseUrl, "sk-proxy");
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"/api/ai/system/{channelId}/chat/completions?token=should-not-forward&mode=test")
            {
                Content = JsonContent.Create(new { model = "proxy-model", messages = Array.Empty<object>() }),
            };

            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retry));
            Assert.Contains("7", retry!);
            string upstreamRequest = await WaitForRequestAsync(requests);
            Assert.Contains("POST /v1/chat/completions?mode=test ", upstreamRequest, StringComparison.Ordinal);
            Assert.DoesNotContain("should-not-forward", upstreamRequest, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer sk-proxy", upstreamRequest, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 系统代理_SSE透传并关闭缓冲()
    {
        (TcpListener listener, string baseUrl, _) = await StartUpstreamAsync(
            "data: first\n\ndata: second\n\n", "text/event-stream", 200, sse: true);
        try
        {
            using HttpClient client = await SignInAsync();
            string channelId = await SeedChannelAsync(baseUrl, "sk-proxy");
            using HttpRequestMessage request = new(
                HttpMethod.Post, $"/api/ai/system/{channelId}/chat/completions")
            {
                Content = JsonContent.Create(new { model = "proxy-model", messages = Array.Empty<object>() }),
            };

            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out IEnumerable<string>? buffering));
            Assert.Contains("no", buffering!, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("data: first", body, StringComparison.Ordinal);
            Assert.Contains("data: second", body, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<HttpClient> SignInAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "admin",
            password = "password123",
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<string> SeedChannelAsync(string baseUrl, string apiKey)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        User user = (await repository.UserByUsernameAsync("admin"))!;
        string id = "CHANNEL_PROXY_" + Guid.NewGuid().ToString("N")[..8];
        DateTime now = DateTime.UtcNow;
        await repository.CreateAsync(new ModelChannel
        {
            ID = id,
            UserID = user.ID,
            Scope = "system",
            Enabled = true,
            Name = "系统代理测试渠道",
            BaseURL = baseUrl,
            APIKey = apiKey,
            APIFormat = "openai",
            ModelsJSON = "[\"proxy-model\"]",
            HeadersJSON = "[]",
            ConcurrencyLimit = 2,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await repository.CreateAsync(new ChannelModel
        {
            ID = "MODEL_PROXY_" + Guid.NewGuid().ToString("N")[..8],
            ChannelID = id,
            ModelKey = "proxy-model",
            ProviderModelKey = "proxy-model",
            DisplayName = "proxy-model",
            Capability = "text",
            Protocol = ChannelInterfaceType.ChannelInterfaceChatCompletion,
            BillingMode = "fixed_request",
            Enabled = true,
            PriceVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        return id;
    }

    private static async Task<(TcpListener Listener, string BaseUrl, ConcurrentQueue<string> Requests)>
        StartUpstreamAsync(
            string payload,
            string contentType,
            int statusCode,
            string? retryAfter = null,
            bool sse = false)
    {
        TcpListener listener = new(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ConcurrentQueue<string> requests = new();
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
                            using (client)
                            {
                                using NetworkStream stream = client.GetStream();
                                byte[] buffer = new byte[16384];
                                StringBuilder head = new();
                                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                                    if (read == 0)
                                    {
                                        return;
                                    }
                                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                                }
                                requests.Enqueue(head.ToString());
                                string status = statusCode == 200 ? "200 OK" : $"{statusCode} Test";
                                string retry = retryAfter is null ? "" : $"Retry-After: {retryAfter}\r\n";
                                if (!sse)
                                {
                                    byte[] body = Encoding.UTF8.GetBytes(payload);
                                    byte[] responseHead = Encoding.ASCII.GetBytes(
                                        $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                                        $"{retry}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                                    await stream.WriteAsync(responseHead).ConfigureAwait(false);
                                    await stream.WriteAsync(body).ConfigureAwait(false);
                                    return;
                                }

                                byte[] sseHead = Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                                    "Cache-Control: no-cache\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(sseHead).ConfigureAwait(false);
                                string[] chunks = payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
                                foreach (string chunk in chunks)
                                {
                                    byte[] data = Encoding.UTF8.GetBytes(chunk + "\n\n");
                                    await stream.WriteAsync(data).ConfigureAwait(false);
                                    await stream.FlushAsync().ConfigureAwait(false);
                                    await Task.Delay(30).ConfigureAwait(false);
                                }
                            }
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
        return (listener, $"http://localhost:{port}/v1", requests);
    }

    private static async Task<string> WaitForRequestAsync(
        ConcurrentQueue<string> requests, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (requests.TryDequeue(out string? request))
            {
                return request;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new Xunit.Sdk.XunitException("upstream request was not observed");
    }
}
