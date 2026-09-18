#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Auth;

public sealed partial class AuthService
{
    private const string LinuxDOSettingKey = "linuxdo_oauth";
    private static readonly Regex OAuthUsernameSanitizer = new(@"[^a-zA-Z0-9_-]+", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ------------------------------------------------------------ DTO（顶层，JSON 契约与 Go 对齐）

    public sealed class LinuxDOCallbackResult
    {
        public required AuthSessionResultDto Session { get; init; }
        public string Next { get; init; } = "/create";
    }

    private sealed class LinuxDOSettingValue
    {
        public bool Enabled { get; set; }
        public string ClientID { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AuthorizationURL { get; set; } = "";
        public string TokenURL { get; set; } = "";
        public string UserInfoURL { get; set; } = "";
        public string RedirectURL { get; set; } = "";
        public List<string> Scopes { get; set; } = [];
        public string ClientAuthMethod { get; set; } = "";
        public string SubjectField { get; set; } = "";
        public string UsernameField { get; set; } = "";
        public string DisplayNameField { get; set; } = "";
        public string EmailField { get; set; } = "";
        public string AvatarField { get; set; } = "";
    }

    // ------------------------------------------------------------ 公开方法

    public async Task<PublicLinuxDOSetting> AdminLinuxDOSettingAsync(User actor, CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? setting, LinuxDOSettingValue value) = await ReadLinuxDOSettingAsync(cancellationToken).ConfigureAwait(false);
        return PublicLinuxDOSettingFrom(setting, value);
    }

    public async Task<PublicLinuxDOSetting> UpdateLinuxDOSettingAsync(
        User actor,
        LinuxDOSettingRequest req,
        CancellationToken cancellationToken = default)
    {
        _host.RequireAdmin(actor);
        (SystemSetting? currentSetting, LinuxDOSettingValue current) = await ReadLinuxDOSettingAsync(cancellationToken).ConfigureAwait(false);

        LinuxDOSettingValue next = NormalizeLinuxDOSetting(new LinuxDOSettingValue
        {
            Enabled = req.Enabled,
            ClientID = req.ClientID,
            ClientSecret = req.ClientSecret,
            AuthorizationURL = req.AuthorizationURL,
            TokenURL = req.TokenURL,
            UserInfoURL = req.UserInfoURL,
            RedirectURL = req.RedirectURL,
            Scopes = req.Scopes,
            ClientAuthMethod = req.ClientAuthMethod,
            SubjectField = req.SubjectField,
            UsernameField = req.UsernameField,
            DisplayNameField = req.DisplayNameField,
            EmailField = req.EmailField,
            AvatarField = req.AvatarField,
        });

        if (string.IsNullOrEmpty(next.ClientSecret))
        {
            next.ClientSecret = current.ClientSecret;
        }

        ValidateLinuxDOSetting(next);

        LinuxDOSettingValue stored = new()
        {
            Enabled = next.Enabled,
            ClientID = next.ClientID,
            ClientSecret = next.ClientSecret,
            AuthorizationURL = next.AuthorizationURL,
            TokenURL = next.TokenURL,
            UserInfoURL = next.UserInfoURL,
            RedirectURL = next.RedirectURL,
            Scopes = next.Scopes,
            ClientAuthMethod = next.ClientAuthMethod,
            SubjectField = next.SubjectField,
            UsernameField = next.UsernameField,
            DisplayNameField = next.DisplayNameField,
            EmailField = next.EmailField,
            AvatarField = next.AvatarField,
        };
        stored.ClientSecret = EncryptSecret(next.ClientSecret);
        string encoded = JsonSerializer.Serialize(stored, _jsonOptions);

        SystemSetting setting = new()
        {
            Key = LinuxDOSettingKey,
            ValueJSON = encoded,
            UpdatedBy = actor.ID,
            CreatedAt = currentSetting?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return PublicLinuxDOSettingFrom(setting, next);
    }

    public async Task<bool> LinuxDOEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            (_, LinuxDOSettingValue value) = await ReadLinuxDOSettingAsync(cancellationToken).ConfigureAwait(false);
            return value.Enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>对应 Go: <c>Service.BeginLinuxDOLogin</c>。返回授权 URL（需 302 重定向）。</summary>
    public async Task<string> BeginLinuxDOLoginAsync(string nextPath, CancellationToken cancellationToken = default)
    {
        long count = await _repository.UserCountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            throw AppError.Forbidden("请先创建本地管理员账号，再开放 Linux.do 登录");
        }

        (_, LinuxDOSettingValue setting) = await ReadLinuxDOSettingAsync(cancellationToken).ConfigureAwait(false);
        if (!setting.Enabled)
        {
            throw AppError.Forbidden("Linux.do 登录尚未启用");
        }

        string state = IdGenerator.RandomToken();
        string verifier = IdGenerator.RandomToken();
        byte[] challengeBytes = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        string challenge = Base64UrlEncode(challengeBytes);

        await _repository.CreateOAuthStateAsync(new OAuthState
        {
            ID = IdGenerator.NewId(),
            Provider = "linuxdo",
            StateHash = IdGenerator.HashToken(state),
            CodeVerifier = verifier,
            NextPath = SafeOAuthNext(nextPath),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = setting.ClientID,
            ["redirect_uri"] = setting.RedirectURL,
            ["response_type"] = "code",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        if (setting.Scopes.Count > 0)
        {
            query["scope"] = string.Join(" ", setting.Scopes);
        }

        return AppendQueryString(setting.AuthorizationURL, query);
    }

    /// <summary>对应 Go: <c>Service.CompleteLinuxDOLogin</c>。</summary>
    public async Task<LinuxDOCallbackResult> CompleteLinuxDOLoginAsync(
        string stateValue,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stateValue) || string.IsNullOrWhiteSpace(code))
        {
            throw AppError.BadAuthRequest("Linux.do 登录回调缺少必要参数");
        }

        OAuthState? state = await _repository.ConsumeOAuthStateAsync(
            "linuxdo", IdGenerator.HashToken(stateValue), cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw AppError.BadAuthRequest("Linux.do 登录状态无效或已过期");
        }

        (_, LinuxDOSettingValue setting) = await ReadLinuxDOSettingAsync(cancellationToken).ConfigureAwait(false);
        string accessToken = await ExchangeLinuxDOCodeAsync(setting, code, state.CodeVerifier, cancellationToken).ConfigureAwait(false);
        Dictionary<string, object?> profile = await FetchLinuxDOProfileAsync(setting, accessToken, cancellationToken).ConfigureAwait(false);

        string subject = ProfileString(profile, setting.SubjectField);
        if (string.IsNullOrEmpty(subject))
        {
            throw new InvalidOperationException("Linux.do 用户信息缺少稳定用户 ID");
        }

        string providerUsername = ProfileString(profile, setting.UsernameField);
        string displayName = FirstNonEmpty(
            ProfileString(profile, setting.DisplayNameField),
            providerUsername,
            "Linux.do 用户");
        string avatarURL = ProfileString(profile, setting.AvatarField);

        UserIdentity? identity = await _repository.UserIdentityAsync("linuxdo", subject, cancellationToken).ConfigureAwait(false);
        User user;

        if (identity is not null)
        {
            User? existing = await _repository.UserAsync(identity.UserID, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("身份关联的用户不存在");

            identity.ProviderUsername = providerUsername;
            identity.AvatarURL = avatarURL;
            identity.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            user = existing;
        }
        else
        {
            bool registrationEnabled = await RegistrationEnabledAsync(cancellationToken).ConfigureAwait(false);
            if (!registrationEnabled)
            {
                throw AppError.Forbidden("管理员未开放新用户注册");
            }

            (user, identity) = await CreateLinuxDOUserAsync(
                subject, providerUsername, displayName,
                ProfileString(profile, setting.EmailField), avatarURL, cancellationToken).ConfigureAwait(false);

            await _repository.CreateOAuthUserAsync(user, identity, cancellationToken).ConfigureAwait(false);
        }

        if (user.Status != UserStatus.UserStatusActive)
        {
            throw AppError.Forbidden("该账号已被禁用");
        }

        await _host.EnsureSignupBonusAsync(user.ID, cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await _repository.SaveAsync(user, cancellationToken).ConfigureAwait(false);

        _host.RecordActivity(user.ID, "login", 1);

        AuthSessionResultDto session = await CreateAuthSessionAsync(user, cancellationToken).ConfigureAwait(false);
        return new LinuxDOCallbackResult
        {
            Session = session,
            Next = SafeOAuthNext(state.NextPath),
        };
    }

    // ------------------------------------------------------------ 内部方法

    private async Task<(User User, UserIdentity Identity)> CreateLinuxDOUserAsync(
        string subject,
        // 上游用户信息的用户名可能缺失；displayName 由调用点保证非空。
        string? providerUsername,
        string displayName,
        string email,
        // 上游用户信息的头像字段可能缺失，这里显式允许空值。
        string? avatarURL,
        CancellationToken cancellationToken = default)
    {
        string baseName = OAuthUsernameSanitizer.Replace(
            (providerUsername ?? "").Trim(), "_").Trim('_', '-');
        if (baseName.Length < 3)
        {
            baseName = "linuxdo_" + ShortSubject(subject);
        }
        if (baseName.Length > 24)
        {
            baseName = baseName[..24];
        }

        string username = baseName;
        User? existing = await _repository.UserByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            username = KernelUtil.TruncateRunes(baseName, 23) + "_" + ShortSubject(subject);
        }

        email = NormalizeEmail(email);
        if (!string.IsNullOrEmpty(email))
        {
            try
            {
                ValidateEmail(email);
                User? emailUser = await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false);
                if (emailUser is not null)
                {
                    // 相同邮箱不自动合并身份，避免第三方邮箱状态不明导致账号接管。
                    email = "";
                }
            }
            catch (AppError)
            {
                email = "";
            }
        }

        User user = new()
        {
            ID = IdGenerator.NewId(),
            Username = username,
            Email = email,
            DisplayName = NormalizeDisplayName(displayName, username),
            Role = UserRole.UserRoleUser,
            Status = UserStatus.UserStatusActive,
        };

        UserIdentity identity = new()
        {
            ID = IdGenerator.NewId(),
            UserID = user.ID,
            Provider = "linuxdo",
            Subject = subject,
            ProviderUsername = providerUsername ?? "",
            AvatarURL = avatarURL ?? "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        return (user, identity);
    }

    private async Task<(SystemSetting? Setting, LinuxDOSettingValue Value)> ReadLinuxDOSettingAsync(CancellationToken cancellationToken = default)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(LinuxDOSettingKey, cancellationToken).ConfigureAwait(false);
        if (setting is null)
        {
            return (null, DefaultLinuxDOSetting());
        }

        LinuxDOSettingValue value = DefaultLinuxDOSetting();
        if (!string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            try
            {
                value = JsonSerializer.Deserialize<LinuxDOSettingValue>(setting.ValueJSON, _jsonOptions)
                    ?? DefaultLinuxDOSetting();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Linux.do OAuth 配置格式无效");
            }
        }

        value.ClientSecret = DecryptSecret(value.ClientSecret);
        return (setting, NormalizeLinuxDOSetting(value));
    }

    private static void ValidateLinuxDOSetting(LinuxDOSettingValue value)
    {
        if (!value.Enabled)
        {
            return;
        }

        if (string.IsNullOrEmpty(value.ClientID) || string.IsNullOrEmpty(value.ClientSecret)
            || string.IsNullOrEmpty(value.AuthorizationURL) || string.IsNullOrEmpty(value.TokenURL)
            || string.IsNullOrEmpty(value.UserInfoURL) || string.IsNullOrEmpty(value.RedirectURL))
        {
            throw AppError.BadAuthRequest("启用 Linux.do 登录前请完整填写 Client、端点和回调配置");
        }

        foreach (string rawURL in new[] { value.AuthorizationURL, value.TokenURL, value.UserInfoURL })
        {
            if (!Uri.TryCreate(rawURL, UriKind.Absolute, out Uri? parsed)
                || parsed.Scheme != "https" || string.IsNullOrEmpty(parsed.Host))
            {
                throw AppError.BadAuthRequest("Linux.do 授权、Token 和用户信息地址必须是有效的 HTTPS URL");
            }
        }

        if (!Uri.TryCreate(value.RedirectURL, UriKind.Absolute, out Uri? redirect)
            || string.IsNullOrEmpty(redirect.Host)
            || (redirect.Scheme != "https" && !(redirect.Scheme == "http" && IsLoopbackOAuthHost(redirect.Host))))
        {
            throw AppError.BadAuthRequest("Linux.do 回调地址必须使用 HTTPS，本地回环地址可使用 HTTP");
        }

        if (value.ClientAuthMethod != "client_secret_post" && value.ClientAuthMethod != "client_secret_basic")
        {
            throw AppError.BadAuthRequest("请选择有效的 OAuth 客户端鉴权方式");
        }
    }

    private static bool IsLoopbackOAuthHost(string host)
    {
        host = host.ToLowerInvariant().Trim();
        return host is "localhost" or "127.0.0.1" or "::1";
    }

    private static LinuxDOSettingValue NormalizeLinuxDOSetting(LinuxDOSettingValue value)
    {
        value.ClientID = (value.ClientID ?? "").Trim();
        value.ClientSecret = (value.ClientSecret ?? "").Trim();
        value.AuthorizationURL = (value.AuthorizationURL ?? "").Trim();
        value.TokenURL = (value.TokenURL ?? "").Trim();
        value.UserInfoURL = (value.UserInfoURL ?? "").Trim();
        value.RedirectURL = (value.RedirectURL ?? "").Trim();
        value.Scopes = UniqueNonEmpty(value.Scopes);
        value.ClientAuthMethod = (value.ClientAuthMethod ?? "").Trim();
        value.SubjectField = (value.SubjectField ?? "").Trim();
        value.UsernameField = (value.UsernameField ?? "").Trim();
        value.DisplayNameField = (value.DisplayNameField ?? "").Trim();
        value.EmailField = (value.EmailField ?? "").Trim();
        value.AvatarField = (value.AvatarField ?? "").Trim();

        if (string.IsNullOrEmpty(value.ClientAuthMethod))
        {
            value.ClientAuthMethod = "client_secret_post";
        }
        if (string.IsNullOrEmpty(value.SubjectField))
        {
            value.SubjectField = "id";
        }
        if (string.IsNullOrEmpty(value.UsernameField))
        {
            value.UsernameField = "username";
        }
        if (string.IsNullOrEmpty(value.DisplayNameField))
        {
            value.DisplayNameField = "name";
        }
        if (string.IsNullOrEmpty(value.EmailField))
        {
            value.EmailField = "email";
        }
        if (string.IsNullOrEmpty(value.AvatarField))
        {
            value.AvatarField = "avatar_url";
        }
        return value;
    }

    private static LinuxDOSettingValue DefaultLinuxDOSetting() => NormalizeLinuxDOSetting(new LinuxDOSettingValue());

    private static PublicLinuxDOSetting PublicLinuxDOSettingFrom(SystemSetting? setting, LinuxDOSettingValue value)
    {
        PublicLinuxDOSetting result = new()
        {
            Enabled = value.Enabled,
            ClientID = value.ClientID,
            HasClientSecret = !string.IsNullOrEmpty(value.ClientSecret),
            AuthorizationURL = value.AuthorizationURL,
            TokenURL = value.TokenURL,
            UserInfoURL = value.UserInfoURL,
            RedirectURL = value.RedirectURL,
            Scopes = value.Scopes,
            ClientAuthMethod = value.ClientAuthMethod,
            SubjectField = value.SubjectField,
            UsernameField = value.UsernameField,
            DisplayNameField = value.DisplayNameField,
            EmailField = value.EmailField,
            AvatarField = value.AvatarField,
        };
        if (setting is not null)
        {
            result.UpdatedBy = setting.UpdatedBy;
            result.CreatedAt = setting.CreatedAt;
            result.UpdatedAt = setting.UpdatedAt;
        }
        return result;
    }

    private static async Task<string> ExchangeLinuxDOCodeAsync(
        LinuxDOSettingValue setting,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = setting.ClientID,
            ["redirect_uri"] = setting.RedirectURL,
            ["code_verifier"] = verifier,
        };
        if (setting.ClientAuthMethod == "client_secret_post")
        {
            form["client_secret"] = setting.ClientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, setting.TokenURL)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Add("Accept", "application/json");
        if (setting.ClientAuthMethod == "client_secret_basic")
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{setting.ClientID}:{setting.ClientSecret}")));
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(body);
        string? accessToken = doc.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException("Linux.do Token 响应无效");
        }
        return accessToken;
    }

    private static async Task<Dictionary<string, object?>> FetchLinuxDOProfileAsync(
        LinuxDOSettingValue setting,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, setting.UserInfoURL);
        request.Headers.Add("Authorization", $"Bearer {accessToken}");
        request.Headers.Add("Accept", "application/json");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body, _jsonOptions)
            ?? [];
    }

    private static string ProfileString(Dictionary<string, object?> profile, string field)
    {
        object? value = profile;
        foreach (string segment in field.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value is not Dictionary<string, object?> dict || !dict.TryGetValue(segment, out value) || value is null)
            {
                return "";
            }
        }
        return (value?.ToString() ?? "").Trim();
    }

    private static string SafeOAuthNext(string value)
    {
        value = value.Trim();
        if (string.IsNullOrEmpty(value)
            || !Uri.TryCreate(value, UriKind.Relative, out Uri? parsed)
            || parsed.IsAbsoluteUri
            || !string.IsNullOrEmpty(parsed.Host)
            || !parsed.PathAndQuery.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\'))
        {
            return "/create";
        }
        return parsed.ToString();
    }

    private static string ShortSubject(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string AppendQueryString(string baseUrl, Dictionary<string, string?> query)
    {
        var parts = new List<string>();
        foreach (var kv in query)
        {
            if (kv.Value is not null)
            {
                parts.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
            }
        }
        string qs = string.Join("&", parts);
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + qs;
    }

    private static List<string> UniqueNonEmpty(List<string> values)
    {
        return values.Select(v => v.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }
        return "";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>LinuxDO 设置请求。对应 Go: <c>auth.LinuxDOSettingRequest</c>。</summary>
public sealed class LinuxDOSettingRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientID { get; set; } = "";

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = "";

    [JsonPropertyName("authorizationUrl")]
    public string AuthorizationURL { get; set; } = "";

    [JsonPropertyName("tokenUrl")]
    public string TokenURL { get; set; } = "";

    [JsonPropertyName("userInfoUrl")]
    public string UserInfoURL { get; set; } = "";

    [JsonPropertyName("redirectUrl")]
    public string RedirectURL { get; set; } = "";

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];

    [JsonPropertyName("clientAuthMethod")]
    public string ClientAuthMethod { get; set; } = "";

    [JsonPropertyName("subjectField")]
    public string SubjectField { get; set; } = "";

    [JsonPropertyName("usernameField")]
    public string UsernameField { get; set; } = "";

    [JsonPropertyName("displayNameField")]
    public string DisplayNameField { get; set; } = "";

    [JsonPropertyName("emailField")]
    public string EmailField { get; set; } = "";

    [JsonPropertyName("avatarField")]
    public string AvatarField { get; set; } = "";
}

/// <summary>公开 LinuxDO 设置。对应 Go: <c>auth.PublicLinuxDOSetting</c>（字段顺序即输出顺序）。</summary>
public sealed class PublicLinuxDOSetting
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientID { get; set; } = "";

    [JsonPropertyName("hasClientSecret")]
    public bool HasClientSecret { get; set; }

    [JsonPropertyName("authorizationUrl")]
    public string AuthorizationURL { get; set; } = "";

    [JsonPropertyName("tokenUrl")]
    public string TokenURL { get; set; } = "";

    [JsonPropertyName("userInfoUrl")]
    public string UserInfoURL { get; set; } = "";

    [JsonPropertyName("redirectUrl")]
    public string RedirectURL { get; set; } = "";

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = [];

    [JsonPropertyName("clientAuthMethod")]
    public string ClientAuthMethod { get; set; } = "";

    [JsonPropertyName("subjectField")]
    public string SubjectField { get; set; } = "";

    [JsonPropertyName("usernameField")]
    public string UsernameField { get; set; } = "";

    [JsonPropertyName("displayNameField")]
    public string DisplayNameField { get; set; } = "";

    [JsonPropertyName("emailField")]
    public string EmailField { get; set; } = "";

    [JsonPropertyName("avatarField")]
    public string AvatarField { get; set; } = "";

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
