using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Auth;

/// <summary>
/// 由组合根注入的宿主能力，避免 auth → app 回环。
/// </summary>
/// <remarks>对应 Go: <c>auth.Host</c>。未实现的方法回落到 Go 的 <c>nopHost</c> 语义。</remarks>
public interface IAuthHost
{
    /// <summary>校验当前用户是管理员。</summary>
    void RequireAdmin(User actor);

    /// <summary>设置加密密钥，用于验证码 HMAC。未配置时返回 null（与 Go 的 nopHost 一致）。</summary>
    byte[]? SettingsEncryptionKey();

    /// <summary>品牌名，用于邮件正文。</summary>
    string BrandName();

    /// <summary>确保注册奖励已发放。未接线时为无操作。</summary>
    Task EnsureSignupBonusAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>记录用户活跃事件。未接线时为无操作。</summary>
    void RecordActivity(string userId, string @event, int count);
}

/// <summary>默认宿主：与 Go 的 <c>nopHost</c> 行为一致。</summary>
public sealed class NullAuthHost : IAuthHost
{
    public const string DefaultBrandName = "影策";

    public void RequireAdmin(User actor)
    {
    }

    public byte[]? SettingsEncryptionKey() => null;

    public string BrandName() => DefaultBrandName;

    public Task EnsureSignupBonusAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void RecordActivity(string userId, string @event, int count)
    {
    }
}

/// <summary>
/// 认证服务。对应 Go: <c>internal/auth.Service</c> 的核心部分
/// （登录、注册、会话、公开设置）。第三方导入（LinuxDO / LibTV / TapNow）另行实现。
/// </summary>
public sealed partial class AuthService
{
    /// <summary>会话 Cookie 名。对应 Go: <c>auth.SessionCookieName</c>。</summary>
    public const string SessionCookieName = "open_ai_canvas_session";

    /// <summary>会话有效期 30 天。对应 Go: <c>auth.sessionMaxAge</c>。</summary>
    public static readonly TimeSpan SessionMaxAge = TimeSpan.FromDays(30);

    /// <summary>注册验证码有效期 10 分钟。对应 Go: <c>auth.registrationCodeTTL</c>。</summary>
    public static readonly TimeSpan RegistrationCodeTtl = TimeSpan.FromMinutes(10);

    /// <summary>同一邮箱的验证码冷却 1 分钟。对应 Go 的 <c>time.Minute</c> 判定。</summary>
    public static readonly TimeSpan EmailCodeCooldown = TimeSpan.FromMinutes(1);

    private const string RegistrationEmailPurpose = "registration";
    private const string PasswordResetEmailPurpose = "password_reset";
    private const string RegistrationSettingKey = "registration";
    private const string EmailSettingKey = "email";

    private readonly Repository _repository;
    private readonly IAuthHost _host;
    private readonly IMailSender _mailSender;

    private readonly SemaphoreSlim _registrationLock = new(1, 1);

    public AuthService(Repository repository, IAuthHost? host = null, IMailSender? mailSender = null)
    {
        _repository = repository;
        _host = host ?? new NullAuthHost();
        _mailSender = mailSender ?? new UnconfiguredMailSender();
    }

    // ------------------------------------------------------------ 公开设置

    /// <summary>公开认证设置。对应 Go: <c>Service.PublicAuthSettings</c>。</summary>
    public async Task<PublicAuthSettingsDto> PublicAuthSettingsAsync(CancellationToken cancellationToken = default)
    {
        long count = await _repository.UserCountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            // 首个用户免注册开关限制，也禁用第三方登录入口。
            return new PublicAuthSettingsDto
            {
                FirstUser = true,
                RegistrationEnabled = true,
                LinuxDoEnabled = false,
            };
        }

        bool registrationEnabled = await RegistrationEnabledAsync(cancellationToken).ConfigureAwait(false);
        bool emailEnabled = await EmailEnabledAsync(cancellationToken).ConfigureAwait(false);

        return new PublicAuthSettingsDto
        {
            FirstUser = false,
            RegistrationEnabled = registrationEnabled,
            LinuxDoEnabled = LinuxDoEnabled(),
            EmailEnabled = emailEnabled,
            EmailCodeRequired = true,
        };
    }

    /// <summary>第三方登录是否可用。当前未接线，与 Go 在未配置时的行为一致。</summary>
    public bool LinuxDoEnabled() => false;

    // ------------------------------------------------------------ 注册

    /// <summary>注册。对应 Go: <c>Service.Register</c>。</summary>
    public async Task<AuthSessionResultDto> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        string username = NormalizeUsername(request.Username);
        string email = NormalizeEmail(request.Email);
        string displayName = NormalizeDisplayName(request.DisplayName, username);

        ValidateUsername(username);
        ValidatePassword(request.Password);
        if (email.Length > 0)
        {
            ValidateEmail(email);
        }

        // 首个管理员判定与创建必须串行，否则并发注册会创建出两个管理员。
        await _registrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long count = await _repository.UserCountAsync(cancellationToken).ConfigureAwait(false);

            EmailVerificationCode? verifiedCode = null;
            if (count > 0)
            {
                if (!await RegistrationEnabledAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw AppError.Forbidden("管理员未开放新用户注册");
                }

                if (email.Length == 0)
                {
                    throw AppError.BadAuthRequest("请输入邮箱");
                }

                await ValidateRegistrationEmailDomainAsync(email, cancellationToken).ConfigureAwait(false);
                verifiedCode = await VerifyRegistrationEmailCodeAsync(email, request.EmailCode, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await _repository.UserByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw AppError.BadAuthRequest("用户名已存在");
            }

            if (email.Length > 0
                && await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw AppError.BadAuthRequest("邮箱已被注册");
            }

            string passwordHash = HashPassword(request.Password);
            DateTime now = DateTime.UtcNow;

            User user = new()
            {
                ID = IdGenerator.NewId(),
                Username = username,
                Email = email,
                DisplayName = displayName,
                Role = UserRole.UserRoleUser,
                Status = UserStatus.UserStatusActive,
                PasswordHash = passwordHash,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // 第一个用户直接成为管理员。
            if (count == 0)
            {
                user.Role = UserRole.UserRoleAdmin;
            }

            if (verifiedCode is not null)
            {
                await _repository.CreateUserWithEmailVerificationAsync(
                    user, verifiedCode.ID, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _repository.CreateAsync(user, cancellationToken).ConfigureAwait(false);
            }

            await _host.EnsureSignupBonusAsync(user.ID, cancellationToken).ConfigureAwait(false);

            return await CreateAuthSessionAsync(user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    // ------------------------------------------------------------ 登录 / 登出

    /// <summary>登录。对应 Go: <c>Service.Login</c>。</summary>
    public async Task<AuthSessionResultDto> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        string account = request.Username.Trim();
        User? user = await _repository.UserByAccountAsync(account, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // 用户不存在与口令错误返回同一文案，避免账号枚举。
            throw AppError.Unauthorized("用户名、邮箱或密码不正确");
        }

        if (user.Status != UserStatus.UserStatusActive)
        {
            throw AppError.Forbidden("该账号已被禁用");
        }

        if (!VerifyPassword(request.Password, user.PasswordHash))
        {
            throw AppError.Unauthorized("用户名、邮箱或密码不正确");
        }

        DateTime now = DateTime.UtcNow;
        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await _repository.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);

        await _host.EnsureSignupBonusAsync(user.ID, cancellationToken).ConfigureAwait(false);
        _host.RecordActivity(user.ID, "login", 1);

        return await CreateAuthSessionAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>登出。对应 Go: <c>Service.Logout</c>：Cookie 无效时不报错。</summary>
    public async Task LogoutAsync(string? cookieValue, CancellationToken cancellationToken = default)
    {
        (string sessionId, _) = ParseSessionCookie(cookieValue);
        if (sessionId.Length == 0)
        {
            return;
        }

        await _repository.DeleteAuthSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取当前登录用户。对应 Go: <c>Service.CurrentUser</c>。</summary>
    public async Task<User> CurrentUserAsync(string? cookieValue, CancellationToken cancellationToken = default)
    {
        (string sessionId, string token) = ParseSessionCookie(cookieValue);
        if (sessionId.Length == 0 || token.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }

        AuthSession? session = await _repository.AuthSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            throw AppError.Unauthorized("登录状态已失效");
        }

        // 过期或令牌不匹配都要顺手清理会话，避免失效记录长期堆积。
        if (DateTime.UtcNow > session.ExpiresAt
            || !string.Equals(session.TokenHash, IdGenerator.HashToken(token), StringComparison.Ordinal))
        {
            await _repository.DeleteAuthSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            throw AppError.Unauthorized("登录状态已失效");
        }

        User? user = await _repository.UserAsync(session.UserID, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw AppError.Unauthorized("登录状态已失效");
        }

        if (user.Status != UserStatus.UserStatusActive)
        {
            throw AppError.Forbidden("该账号已被禁用");
        }

        return user;
    }

    /// <summary>
    /// 认证响应只补充当前用户自己的第三方公开身份，不暴露身份表或密钥字段。
    /// 对应 Go: <c>Service.PublicAuthUser</c>。
    /// </summary>
    public async Task<AuthUserDto> PublicAuthUserAsync(User user, CancellationToken cancellationToken = default)
    {
        UserIdentity? identity = await _repository.UserIdentityForUserAsync(
            user.ID, "linuxdo", cancellationToken).ConfigureAwait(false);

        return AuthUserDto.From(user, identity);
    }

    /// <summary>创建会话。对应 Go: <c>Service.createAuthSession</c>。</summary>
    private async Task<AuthSessionResultDto> CreateAuthSessionAsync(
        User user,
        CancellationToken cancellationToken)
    {
        AuthUserDto publicUser = await PublicAuthUserAsync(user, cancellationToken).ConfigureAwait(false);

        string token = IdGenerator.RandomToken();
        DateTime now = DateTime.UtcNow;

        AuthSession session = new()
        {
            ID = IdGenerator.NewId(),
            UserID = user.ID,
            TokenHash = IdGenerator.HashToken(token),
            ExpiresAt = now.Add(SessionMaxAge),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.CreateAsync(session, cancellationToken).ConfigureAwait(false);

        return new AuthSessionResultDto
        {
            User = publicUser,
            Session = session.ID + "." + token,
            MaxAgeSecs = (int)SessionMaxAge.TotalSeconds,
        };
    }

    // ------------------------------------------------------------ 口令与令牌

    /// <summary>
    /// bcrypt 哈希（cost 10）。对应 Go: <c>auth.HashPassword</c>。
    /// Go 用 <c>bcrypt.DefaultCost</c>，与 BCrypt.Net 的默认 workFactor 一致，两者哈希互通。
    /// </summary>
    public static string HashPassword(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10);

    /// <summary>校验口令。对应 Go: <c>auth.verifyPassword</c>。</summary>
    public static bool VerifyPassword(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // 哈希格式非法时视为校验失败，不把内部异常抛给调用方。
            return false;
        }
    }

    // ------------------------------------------------------------ 归一化与校验

    public static string NormalizeUsername(string value) => value.Trim();

    // ------------------------------------------------------------ 设置加密（占位：当前直接返回原值，后续补 AES-GCM）

    /// <summary>加密敏感设置值。对应 Go: <c>authHost.EncryptSecret</c>。</summary>
    public static string EncryptSecret(string value) => value;

    /// <summary>解密敏感设置值。对应 Go: <c>authHost.DecryptSecret</c>。</summary>
    public static string DecryptSecret(string value) => value;

    public static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();

    /// <summary>显示名归一化：空则回落到用户名，超过 40 个字符（按字符计）截断。</summary>
    public static string NormalizeDisplayName(string value, string fallback)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            trimmed = fallback;
        }

        // Go 用 []rune 截断，等价于按 Unicode 标量值截断。
        int count = trimmed.EnumerateRunes().Count();
        if (count <= 40)
        {
            return trimmed;
        }

        return string.Concat(trimmed.EnumerateRunes().Take(40).Select(r => r.ToString()));
    }

    public static void ValidateUsername(string value)
    {
        if (!UsernamePattern().IsMatch(value))
        {
            throw AppError.BadAuthRequest("用户名需为 3-32 位字母、数字、下划线或连字符");
        }
    }

    public static void ValidatePassword(string value)
    {
        if (value.EnumerateRunes().Count() < 8)
        {
            throw AppError.BadAuthRequest("密码至少 8 位");
        }
    }

    public static void ValidateEmail(string value)
    {
        // Go 用 net/mail.ParseAddress，比简单的正则更宽松（接受 "Name <a@b>"）。
        // 这里做等价强度的校验：必须存在且只存在一个 @，两侧非空，域名含点。
        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
        {
            throw AppError.BadAuthRequest("邮箱格式不正确");
        }

        string domain = value[(at + 1)..];
        if (!domain.Contains('.', StringComparison.Ordinal) || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            throw AppError.BadAuthRequest("邮箱格式不正确");
        }

        if (value.Any(char.IsWhiteSpace))
        {
            throw AppError.BadAuthRequest("邮箱格式不正确");
        }
    }

    /// <summary>解析 Cookie 值 <c>会话ID.令牌</c>。对应 Go: <c>auth.parseSessionCookie</c>。</summary>
    public static (string SessionId, string Token) ParseSessionCookie(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (string.Empty, string.Empty);
        }

        int separator = value.IndexOf('.');
        if (separator < 0)
        {
            return (string.Empty, string.Empty);
        }

        return (value[..separator].Trim(), value[(separator + 1)..].Trim());
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{3,32}$")]
    private static partial Regex UsernamePattern();
}
