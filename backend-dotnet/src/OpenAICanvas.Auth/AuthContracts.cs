using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Auth;

/// <summary>注册请求。对应 Go: <c>auth.RegisterRequest</c>。</summary>
public sealed class RegisterRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("emailCode")]
    public string EmailCode { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>登录请求。对应 Go: <c>auth.LoginRequest</c>。</summary>
public sealed class LoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 公开认证设置。对应 Go: <c>auth.PublicAuthSettings</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class PublicAuthSettingsDto
{
    [JsonPropertyName("firstUser")]
    public bool FirstUser { get; init; }

    [JsonPropertyName("registrationEnabled")]
    public bool RegistrationEnabled { get; init; }

    /// <summary>注意：Go 的 json tag 是 <c>linuxdoEnabled</c>（全小写 do），不是 <c>linuxDOEnabled</c>。</summary>
    [JsonPropertyName("linuxdoEnabled")]
    public bool LinuxDoEnabled { get; init; }

    [JsonPropertyName("emailEnabled")]
    public bool EmailEnabled { get; init; }

    [JsonPropertyName("emailCodeRequired")]
    public bool EmailCodeRequired { get; init; }
}

/// <summary>
/// 认证用户。对应 Go: <c>auth.AuthUser</c>。
/// </summary>
/// <remarks>
/// Go 用<b>结构体嵌入</b>（<c>model.User</c>）——<c>encoding/json</c> 会把嵌入结构体的字段
/// 平铺到同一层 JSON 对象里，而不是嵌套。C# 没有对应机制，因此这里按 User 的字段顺序
/// 显式展开，保证输出字段名、顺序与 omitempty 行为与 Go 一致。
/// <para>
/// User 的字段顺序：id, username, email(omitempty), displayName, role, status,
/// passwordHash(忽略), lastLoginAt, createdAt, updatedAt；随后是 4 个身份字段。
/// </para>
/// </remarks>
public sealed class AuthUserDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    [GoOmitEmpty]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("avatarUrl")]
    [GoOmitEmpty]
    public string AvatarUrl { get; init; } = string.Empty;

    [JsonPropertyName("identityProvider")]
    [GoOmitEmpty]
    public string IdentityProvider { get; init; } = string.Empty;

    [JsonPropertyName("identityId")]
    [GoOmitEmpty]
    public string IdentityId { get; init; } = string.Empty;

    [JsonPropertyName("identityUsername")]
    [GoOmitEmpty]
    public string IdentityUsername { get; init; } = string.Empty;

    /// <summary>把实体与可选的第三方身份投影成认证用户。</summary>
    public static AuthUserDto From(User user, UserIdentity? identity = null) => new()
    {
        Id = user.ID,
        Username = user.Username,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role,
        Status = user.Status,
        LastLoginAt = user.LastLoginAt,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        AvatarUrl = identity?.AvatarURL ?? string.Empty,
        IdentityProvider = identity?.Provider ?? string.Empty,
        IdentityId = identity?.Subject ?? string.Empty,
        IdentityUsername = identity?.ProviderUsername ?? string.Empty,
    };
}

/// <summary>登录/注册成功后的会话结果。对应 Go: <c>auth.AuthSessionResult</c>。</summary>
public sealed class AuthSessionResultDto
{
    [JsonPropertyName("user")]
    public required AuthUserDto User { get; init; }

    /// <summary>Cookie 值，格式为 <c>会话ID.令牌</c>。</summary>
    [JsonPropertyName("session")]
    public string Session { get; init; } = string.Empty;

    [JsonPropertyName("maxAgeSecs")]
    public int MaxAgeSecs { get; init; }
}
