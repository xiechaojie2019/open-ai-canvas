#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Endpoints;

/// <summary>
/// 渠道模型报价路由契约测试。对应 Go: <c>credit_cost_routes_test.go</c> 的 quote 部分。
/// </summary>
public sealed class ModelQuoteEndpointTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private string _userId = "";
    private HttpClient? _adminClient;

    public ModelQuoteEndpointTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-quote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        Environment.SetEnvironmentVariable("CANVAS_SKIP_BUILTIN_SKILLS", "true");
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
        Environment.SetEnvironmentVariable("CANVAS_SKIP_BUILTIN_SKILLS", null);
        GC.SuppressFinalize(this);
    }

    private async Task<HttpClient> SignInAsync()
    {
        if (_adminClient is not null)
        {
            return _adminClient;
        }
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "quoter",
            password = "password123",
            acceptedTerms = true,
        });
        response.EnsureSuccessStatusCode();
        string cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        _adminClient = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _adminClient.DefaultRequestHeaders.Add("Cookie", cookie);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        _userId = (await repository.UserByUsernameAsync("quoter"))!.ID;
        return _adminClient;
    }

    private async Task SeedChannelModelAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();
        DateTime now = DateTime.UtcNow;
        await repository.CreateAsync(new ModelChannel
        {
            ID = "CH_Q",
            Name = "报价渠道",
            BaseURL = "https://api.example.com/v1",
            APIFormat = "openai",
            Enabled = true,
            Scope = "system",
            CreatedAt = now,
            UpdatedAt = now,
        });
        // 价格配置在 ChannelModel 本体上（旧库默认档由 attach 按本体字段合成）。
        await repository.CreateAsync(new ChannelModel
        {
            ID = "CM_Q",
            ChannelID = "CH_Q",
            ModelKey = "quote-model",
            ProviderModelKey = "quote-model",
            Capability = "image",
            UnitPriceMicrocredits = 200_000,
            Protocol = "openai",
            CapabilityConfigJSON = """
                {"version":1,"image":{"references":{"promptMaxChars":1000,"maxImages":4},"size":{"parameter":"none","presets":[{"name":"1:1","ratio":"1:1","tier":"1k","width":1024,"height":1024}]},"maxOutputs":4}}
                """,
            CapabilityVersion = 1,
            Enabled = true,
            PriceConfigured = true,
            PriceVersion = 1,
            BillingMode = "fixed_request",
            PriceTiers =
            [
                new ChannelModelPriceTier
                {
                    ID = "TIER_Q",
                    ChannelModelID = "CM_Q",
                    ProviderModelKey = "quote-model",
                    BillingMode = "fixed_request",
                    UnitPriceMicrocredits = 200_000,
                    Enabled = true,
                    PriceConfigured = true,
                    PriceVersion = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    [Fact]
    public async Task 报价_固定价格模型返回金额()
    {
        HttpClient user = await SignInAsync();
        await SeedChannelModelAsync();
        HttpResponseMessage response = await user.PostAsJsonAsync("/api/model-catalog/quote", new
        {
            channelId = "CH_Q",
            modelKey = "quote-model",
            intent = new { capability = "image" },
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement quote = document.RootElement.GetProperty("data").GetProperty("quote");
        Assert.Equal(200_000, quote.GetProperty("amountMicrocredits").GetInt64());
        Assert.Equal(1, quote.GetProperty("quantity").GetInt64());
        Assert.Equal(200_000, quote.GetProperty("amountMicrocredits").GetInt64());
        Assert.False(quote.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task 报价_缺渠道或模型被拒绝()
    {
        HttpClient user = await SignInAsync();
        HttpResponseMessage response = await user.PostAsJsonAsync("/api/model-catalog/quote", new
        {
            channelId = "",
            modelKey = "",
            intent = new { capability = "image" },
        });
        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task 报价_未登录_401()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/model-catalog/quote", new
        {
            channelId = "CH_Q",
            modelKey = "quote-model",
            intent = new { capability = "image" },
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
