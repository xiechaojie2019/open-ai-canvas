#nullable enable
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenAICanvas.Auth;
using OpenAICanvas.Web;
using Xunit;

namespace OpenAICanvas.Tests.Finance;

public sealed class ExternalBillingClientTests
{
    [Fact]
    public async Task Hold_按文档发送幂等键与可选功能字段()
    {
        Dictionary<string, object?>? body = null;
        StubHandler handler = new(request =>
        {
            body = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(new
            {
                code = 200,
                msg = "请求成功",
                result = new
                {
                    holdId = "HOLD_1",
                    orderId = "ORDER_1",
                    status = "held",
                    amountMicrocredits = 2_000_000L,
                    expiresAt = "2026-09-29T12:00:00+08:00",
                },
            });
        });
        FjdazeBillingClient client = CreateClient(handler);

        ExternalHold result = await client.HoldAsync(
            "user", "ORDER_1", 2_000_000, "TASK_1", "首图");

        Assert.Equal("HOLD_1", result.HoldId);
        Assert.Equal("ORDER_1", body!["orderId"]?.ToString());
        Assert.Equal("ORDER_1", body["idempotencyKey"]?.ToString());
        Assert.False(body.ContainsKey("vasModuleId"));
        Assert.False(body.ContainsKey("vasAppKey"));
        Assert.Equal("Bearer cached-jwt", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Confirm_4010后使用登录凭证刷新JWT并重试原请求()
    {
        int calls = 0;
        StubHandler handler = new(request =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(new { code = 4010, msg = "授权过期", result = new { } })
                : JsonResponse(new
                {
                    code = 200,
                    msg = "请求成功",
                    result = new
                    {
                        holdId = "HOLD_1", orderId = "ORDER_1", status = "confirmed",
                        amountMicrocredits = 1_000_000L,
                        releasedAmountMicrocredits = 0L,
                        extraChargedAmountMicrocredits = 0L,
                        debtMicrocredits = 0L,
                    },
                });
        });
        FakeSiteClient site = new();
        FakeCredentials credentials = new();
        credentials.Values[AuthService.PaymentCacheKey("user")] = JsonSerializer.Serialize(
            new { accessToken = "expired-jwt", expiresAt = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds() });
        credentials.Values[AuthService.LoginCacheKey("user")] = JsonSerializer.Serialize(
            new { token = "login-token", teamCode = "team" });
        FjdazeBillingClient client = new(
            "https://billing.test", null, null, site, credentials,
            () => new HttpClient(handler));

        ExternalBillingResult result = await client.ConfirmAsync("user", "ORDER_1", "HOLD_1", 1_000_000);

        Assert.Equal("confirmed", result.Status);
        Assert.Equal(2, calls);
        Assert.Equal("login-token", site.LastToken);
        Assert.Equal("team", site.LastTeamCode);
    }

    private static FjdazeBillingClient CreateClient(StubHandler handler)
    {
        FakeCredentials credentials = new();
        credentials.Values[AuthService.PaymentCacheKey("user")] = JsonSerializer.Serialize(
            new { accessToken = "cached-jwt", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() });
        return new FjdazeBillingClient(
            "https://billing.test", null, null, new FakeSiteClient(), credentials,
            () => new HttpClient(handler));
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FakeCredentials : IExternalCredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);
        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSiteClient : IExternalSiteClient
    {
        public string? LastToken { get; private set; }
        public string? LastTeamCode { get; private set; }
        public Task<ExternalLoginInfo> GetLoginInfoAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExternalLoginInfo("user", "account"));
        public Task<ExternalPaymentToken> GetPaymentTokenAsync(
            string token, string teamCode, CancellationToken cancellationToken = default)
        {
            LastToken = token;
            LastTeamCode = teamCode;
            return Task.FromResult(new ExternalPaymentToken(
                "refreshed-jwt", "user", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()));
        }
    }
}
