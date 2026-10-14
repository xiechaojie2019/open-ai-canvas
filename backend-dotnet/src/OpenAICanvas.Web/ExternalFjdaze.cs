using System.Text.Json;
using System.Net.Http.Headers;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Web;

internal sealed class FjdazeClient : IExternalSiteClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public FjdazeClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ExternalLoginInfo> GetLoginInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, _baseUrl + "/api/v1/User/GetLoginInfo");
        request.Headers.Add("token", token);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AppError.BadAuthRequest($"爱创登录信息 HTTP 请求失败({(int)response.StatusCode})");
        }
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        EnsureSiteSuccess(root, "爱创登录信息");
        JsonElement result = root.GetProperty("result");
        string userId = result.GetProperty("userId").GetString() ?? string.Empty;
        string account = result.GetProperty("account").GetString() ?? string.Empty;
        return new ExternalLoginInfo(userId, account);
    }

    public async Task<ExternalPaymentToken> GetPaymentTokenAsync(string token, string teamCode, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + "/api/v1/PayPackage/GetToken")
        {
            Content = JsonContent.Create(new { appType = 6 }),
        };
        request.Headers.Add("token", token);
        if (!string.IsNullOrEmpty(teamCode))
        {
            request.Headers.Add("teamcode", teamCode);
        }
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw AppError.BadAuthRequest($"爱创支付凭证 HTTP 请求失败({(int)response.StatusCode})");
        }
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        EnsureSiteSuccess(root, "爱创支付凭证");
        string bearer = root.GetProperty("result").GetString() ?? string.Empty;
        const string prefix = "Bearer ";
        if (!bearer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw AppError.BadAuthRequest("爱创支付凭证格式无效");
        }
        string jwt = bearer[prefix.Length..].Trim();
        string[] parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw AppError.BadAuthRequest("爱创支付凭证格式无效");
        }
        using JsonDocument claims = JsonDocument.Parse(Convert.FromBase64String(PadBase64(parts[1])));
        string externalUserId = claims.RootElement.GetProperty("UserId").GetString() ?? string.Empty;
        long expiresAt = claims.RootElement.GetProperty("exp").GetInt64();
        return new ExternalPaymentToken(jwt, externalUserId.StartsWith("kuaio_", StringComparison.Ordinal) ? externalUserId["kuaio_".Length..] : externalUserId, expiresAt);
    }

    /// <summary>业务 code!=200 时把上游 msg 透出为 AppError，避免 FailInternal 吞掉文案。</summary>
    private static void EnsureSiteSuccess(JsonElement root, string action)
    {
        int code = root.TryGetProperty("code", out JsonElement codeEl) && codeEl.TryGetInt32(out int parsed)
            ? parsed
            : 0;
        if (code == 200)
        {
            return;
        }

        string msg = root.TryGetProperty("msg", out JsonElement msgEl)
            ? (msgEl.GetString() ?? string.Empty).Trim()
            : string.Empty;
        if (msg.Length == 0)
        {
            msg = $"{action}失败";
        }
        throw AppError.BadAuthRequest($"{action}失败({code}): {msg}");
    }

    private static string PadBase64(string value) => value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
}

internal sealed class FjdazeCredentialStore : IExternalCredentialStore
{
    private readonly Coordinator _coordinator;

    public FjdazeCredentialStore(Coordinator coordinator) => _coordinator = coordinator;

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        _coordinator.SetCacheAsync(key, value, ttl, cancellationToken);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _coordinator.GetCacheAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _coordinator.DeleteCacheAsync(key, cancellationToken);
}

internal sealed class FjdazeBillingClient : IExternalBillingClient
{
    private readonly HttpClient _http;
    private readonly IExternalSiteClient _site;
    private readonly IExternalCredentialStore _credentials;
    private readonly string _baseUrl;
    private readonly int? _vasModuleId;
    private readonly string? _vasAppKey;

    public FjdazeBillingClient(
        string? baseUrl,
        int? vasModuleId,
        string? vasAppKey,
        IExternalSiteClient site,
        IExternalCredentialStore credentials,
        Func<HttpClient>? httpFactory = null)
    {
        _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        _vasModuleId = vasModuleId;
        _vasAppKey = string.IsNullOrWhiteSpace(vasAppKey) ? null : vasAppKey.Trim();
        _site = site;
        _credentials = credentials;
        _http = (httpFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) }))();
    }

    public Task<ExternalBalance> GetBalanceAsync(string userId, CancellationToken cancellationToken = default) =>
        SendAsync<ExternalBalance>(userId, HttpMethod.Get, "/api/v1/Billing/GetBalance", null,
            static result => new ExternalBalance(
                result.GetProperty("availableMicrocredits").GetInt64(),
                result.GetProperty("reservedMicrocredits").GetInt64(),
                result.TryGetProperty("debtMicrocredits", out JsonElement debt) ? debt.GetInt64() : 0),
            cancellationToken);

    public Task<ExternalHold> HoldAsync(
        string userId, string orderId, long amountMicrocredits, string taskId, string remarks,
        CancellationToken cancellationToken = default)
    {
        ValidateAmount(amountMicrocredits);
        Dictionary<string, object?> body = new()
        {
            ["amountMicrocredits"] = amountMicrocredits,
            ["orderId"] = ValidateText(orderId, 128, nameof(orderId)),
            ["idempotencyKey"] = orderId,
        };
        AddOptional(body, "taskId", taskId, 128);
        AddOptional(body, "remarks", remarks, 200);
        if (_vasModuleId is not null) body["vasModuleId"] = _vasModuleId.Value;
        AddOptional(body, "vasAppKey", _vasAppKey, 128);
        return SendAsync<ExternalHold>(userId, HttpMethod.Post, "/api/v1/Billing/Hold", body, result =>
            new ExternalHold(
                result.GetProperty("holdId").GetString() ?? "",
                result.GetProperty("orderId").GetString() ?? "",
                result.GetProperty("status").GetString() ?? "",
                result.GetProperty("amountMicrocredits").GetInt64(),
                ReadDate(result, "expiresAt")), cancellationToken);
    }

    public Task<ExternalBillingResult> ConfirmAsync(
        string userId, string orderId, string holdId, long amountMicrocredits,
        CancellationToken cancellationToken = default)
    {
        ValidateAmount(amountMicrocredits);
        Dictionary<string, object?> body = new()
        {
            ["holdId"] = ValidateText(holdId, 128, nameof(holdId)),
            ["orderId"] = ValidateText(orderId, 128, nameof(orderId)),
            ["idempotencyKey"] = orderId + ":settle",
            ["amountMicrocredits"] = amountMicrocredits,
        };
        return SendAsync<ExternalBillingResult>(userId, HttpMethod.Post, "/api/v1/Billing/Confirm", body,
            ParseBillingResult, cancellationToken);
    }

    public Task<ExternalBillingResult> ReleaseAsync(
        string userId, string orderId, string holdId, string reason,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?> body = new()
        {
            ["holdId"] = ValidateText(holdId, 128, nameof(holdId)),
            ["orderId"] = ValidateText(orderId, 128, nameof(orderId)),
            ["idempotencyKey"] = orderId + ":release",
            ["reason"] = ValidateText(reason, 200, nameof(reason)),
        };
        return SendAsync<ExternalBillingResult>(userId, HttpMethod.Post, "/api/v1/Billing/Release", body,
            ParseBillingResult, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        string userId,
        HttpMethod method,
        string path,
        object? body,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
    {
        if (_baseUrl.Length == 0)
        {
            throw new InvalidOperationException("爱创积分计费未配置");
        }

        string? token = await GetPaymentTokenAsync(userId, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ExternalBillingException? retryable = null;
            for (int busyAttempt = 0; busyAttempt < 3; busyAttempt++)
            {
                try
                {
                    using HttpRequestMessage request = new(method, _baseUrl + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (body is not null)
                    {
                        request.Content = JsonContent.Create(body);
                    }
                    using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ExternalBillingException(0, "爱创积分接口 HTTP 请求失败", retryable: true);
                    }
                    using JsonDocument document = JsonDocument.Parse(text);
                    JsonElement root = document.RootElement;
                    int code = root.GetProperty("code").GetInt32();
                    string message = root.TryGetProperty("msg", out JsonElement msg)
                        ? msg.GetString() ?? "爱创积分接口失败" : "爱创积分接口失败";
                    if (code == 200)
                    {
                        return parse(root.GetProperty("result"));
                    }
                    bool busy = message.Contains("处理中", StringComparison.Ordinal);
                    if (code == 5000 || busy)
                    {
                        retryable = new ExternalBillingException(code, message, retryable: true);
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (code == 4010 && attempt == 0)
                    {
                        retryable = new ExternalBillingException(code, message, retryable: true);
                        break;
                    }
                    throw new ExternalBillingException(code, message, retryable: false);
                }
                catch (ExternalBillingException error) when (error.Code == 4010 && attempt == 0)
                {
                    retryable = error;
                    break;
                }
                catch (HttpRequestException error)
                {
                    throw new ExternalBillingException(0, "爱创积分接口网络请求失败：" + error.Message, retryable: true, error);
                }
            }
            if (retryable?.Code == 4010)
            {
                token = await GetPaymentTokenAsync(userId, forceRefresh: true, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (retryable is not null)
            {
                throw retryable;
            }
        }
        throw new InvalidOperationException("爱创积分请求未完成");
    }

    private async Task<string> GetPaymentTokenAsync(string userId, bool forceRefresh, CancellationToken cancellationToken)
    {
        string key = AuthService.PaymentCacheKey(userId);
        if (!forceRefresh)
        {
            string? cached = await _credentials.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                using JsonDocument document = JsonDocument.Parse(cached);
                JsonElement root = document.RootElement;
                string accessToken = root.GetProperty("accessToken").GetString() ?? "";
                long expiresAt = root.GetProperty("expiresAt").GetInt64();
                if (accessToken.Length > 0 && expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120)
                {
                    return accessToken;
                }
            }
        }

        string? loginValue = await _credentials.GetAsync(AuthService.LoginCacheKey(userId), cancellationToken).ConfigureAwait(false);
        if (loginValue is null)
        {
            throw new InvalidOperationException("爱创登录凭证已失效，请重新登录");
        }
        using JsonDocument login = JsonDocument.Parse(loginValue);
        JsonElement loginRoot = login.RootElement;
        string loginToken = loginRoot.GetProperty("token").GetString() ?? "";
        string teamCode = loginRoot.GetProperty("teamCode").GetString() ?? "";
        ExternalPaymentToken payment = await _site.GetPaymentTokenAsync(loginToken, teamCode, cancellationToken).ConfigureAwait(false);
        TimeSpan ttl = TimeSpan.FromSeconds(payment.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
        if (payment.AccessToken.Length == 0 || ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("爱创支付凭证已过期");
        }
        await _credentials.SetAsync(key,
            JsonSerializer.Serialize(new { accessToken = payment.AccessToken, payment.ExternalUserId, teamCode, expiresAt = payment.ExpiresAt }),
            ttl, cancellationToken).ConfigureAwait(false);
        return payment.AccessToken;
    }

    private static ExternalBillingResult ParseBillingResult(JsonElement result) => new(
        result.GetProperty("holdId").GetString() ?? "",
        result.GetProperty("orderId").GetString() ?? "",
        result.GetProperty("status").GetString() ?? "",
        result.GetProperty("amountMicrocredits").GetInt64(),
        result.TryGetProperty("releasedAmountMicrocredits", out JsonElement released) ? released.GetInt64() : 0,
        result.TryGetProperty("extraChargedAmountMicrocredits", out JsonElement extra) ? extra.GetInt64() : 0,
        result.TryGetProperty("debtMicrocredits", out JsonElement debt) ? debt.GetInt64() : 0);

    private static DateTimeOffset? ReadDate(JsonElement result, string name) =>
        result.TryGetProperty(name, out JsonElement value)
            && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed) ? parsed : null;

    private static void ValidateAmount(long amount)
    {
        if (amount <= 0 || amount > 2_147_483_647L * 1_000_000L)
        {
            throw new InvalidOperationException("爱创积分金额必须大于 0 且不超过单笔上限");
        }
    }

    private static string ValidateText(string value, int maxLength, string name)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maxLength)
        {
            throw new InvalidOperationException($"爱创积分字段 {name} 无效");
        }
        return normalized;
    }

    private static void AddOptional(Dictionary<string, object?> body, string name, string? value, int maxLength)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            body[name] = ValidateText(value, maxLength, name);
        }
    }

}

internal sealed class ExternalBillingException : Exception
{
    public ExternalBillingException(int code, string message, bool retryable, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public int Code { get; }
    public bool Retryable { get; }
}
