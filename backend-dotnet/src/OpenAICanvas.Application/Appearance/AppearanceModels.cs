#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Appearance;

/// <summary>外观设置（管理员可写）。对应 Go: <c>app.AppearanceSetting</c>。</summary>
public class AppearanceSetting
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = AppearanceText.SchemaVersion;

    [JsonPropertyName("brandName")]
    public string BrandName { get; set; } = AppearanceText.DefaultBrandName;

    [JsonPropertyName("brandSlug")]
    public string BrandSlug { get; set; } = AppearanceText.DefaultBrandSlug;

    [JsonPropertyName("authHeroTitle")]
    public string AuthHeroTitle { get; set; } = AppearanceText.DefaultHeroTitle;

    [JsonPropertyName("authHeroDescription")]
    public string AuthHeroDescription { get; set; } = "";

    [JsonPropertyName("logoResourceId")]
    public string LogoResourceID { get; set; } = "";

    [JsonPropertyName("darkLogoResourceId")]
    public string DarkLogoResourceID { get; set; } = "";

    [JsonPropertyName("logoFrameEnabled")]
    public bool LogoFrameEnabled { get; set; } = true;

    [JsonPropertyName("authVideoResourceId")]
    public string AuthVideoResourceID { get; set; } = "";

    [JsonPropertyName("authVideoPosterResourceId")]
    public string AuthVideoPosterResourceID { get; set; } = "";

    [JsonPropertyName("authVideoAutoplay")]
    public bool AuthVideoAutoplay { get; set; } = true;

    [JsonPropertyName("skinId")]
    public string SkinID { get; set; } = AppearanceSkins.DefaultSkinID;

    [JsonPropertyName("skinThemes")]
    public List<AppearanceSkinTheme> SkinThemes { get; set; } = [];

    [JsonPropertyName("seoTitle")]
    public string SEOTitle { get; set; } = "";

    [JsonPropertyName("seoDescription")]
    public string SEODescription { get; set; } = "";

    [JsonPropertyName("seoKeywords")]
    public string SEOKeywords { get; set; } = "";

    [JsonPropertyName("footerCopyright")]
    public string FooterCopyright { get; set; } = "";

    [JsonPropertyName("icpFilingEnabled")]
    public bool ICPFilingEnabled { get; set; }

    [JsonPropertyName("icpFilingNumber")]
    public string ICPFilingNumber { get; set; } = "";
}

/// <summary>对外（匿名可读）外观设置。对应 Go: <c>app.PublicAppearanceSetting</c>。</summary>
public sealed class PublicAppearanceSetting
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = AppearanceText.SchemaVersion;

    [JsonPropertyName("brandName")]
    public string BrandName { get; init; } = "";

    [JsonPropertyName("brandSlug")]
    public string BrandSlug { get; init; } = "";

    [JsonPropertyName("authHeroTitle")]
    public string AuthHeroTitle { get; init; } = "";

    [JsonPropertyName("authHeroDescription")]
    public string AuthHeroDescription { get; init; } = "";

    [JsonPropertyName("logoUrl")]
    public string LogoURL { get; set; } = "";

    [JsonPropertyName("darkLogoUrl")]
    public string DarkLogoURL { get; set; } = "";

    [JsonPropertyName("logoFrameEnabled")]
    public bool LogoFrameEnabled { get; init; }

    [JsonPropertyName("authVideoUrl")]
    public string AuthVideoURL { get; set; } = "";

    [JsonPropertyName("authVideoPosterUrl")]
    public string AuthVideoPosterURL { get; set; } = "";

    [JsonPropertyName("authVideoAutoplay")]
    public bool AuthVideoAutoplay { get; init; }

    [JsonPropertyName("skinId")]
    public string SkinID { get; init; } = "";

    [JsonPropertyName("activeSkin")]
    public AppearanceSkinTheme ActiveSkin { get; init; } = new();

    [JsonPropertyName("seoTitle")]
    public string SEOTitle { get; init; } = "";

    [JsonPropertyName("seoDescription")]
    public string SEODescription { get; init; } = "";

    [JsonPropertyName("seoKeywords")]
    public string SEOKeywords { get; init; } = "";

    [JsonPropertyName("footerCopyright")]
    public string FooterCopyright { get; init; } = "";

    [JsonPropertyName("icpFilingEnabled")]
    public bool ICPFilingEnabled { get; init; }

    [JsonPropertyName("icpFilingNumber")]
    public string ICPFilingNumber { get; init; } = "";

    [JsonPropertyName("logoConfigured")]
    public bool LogoConfigured { get; set; }

    [JsonPropertyName("darkLogoConfigured")]
    public bool DarkLogoConfigured { get; set; }

    [JsonPropertyName("authVideoConfigured")]
    public bool AuthVideoConfigured { get; set; }

    [JsonPropertyName("authVideoPosterConfigured")]
    public bool AuthVideoPosterConfigured { get; set; }

    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>管理端外观设置（含公开投影与元信息）。对应 Go: <c>app.AdminAppearanceSetting</c>。</summary>
/// <remarks>Go 用的是内嵌结构（字段平铺）；这里保持同样形态以对齐 JSON 契约。</remarks>
public sealed class AdminAppearanceSetting : AppearanceSetting
{
    [JsonPropertyName("public")]
    public PublicAppearanceSetting Public { get; set; } = new();

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string UpdatedBy { get; set; } = "";

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>皮肤主题。对应 Go: <c>app.AppearanceSkinTheme</c>。</summary>
public sealed class AppearanceSkinTheme
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("tokens")]
    public AppearanceSkinTokens Tokens { get; set; } = new();

    /// <summary>反序列化后补齐可能缺失的嵌套对象（对应 Go 结构体的零值语义）。</summary>
    public void EnsureTokens()
    {
        Tokens ??= new AppearanceSkinTokens();
        Tokens.Light ??= new AppearanceSkinModeTokens();
        Tokens.Dark ??= new AppearanceSkinModeTokens();
        Tokens.Components ??= new AppearanceSkinComponentTokens();
    }
}

/// <summary>对应 Go: <c>app.AppearanceSkinTokens</c>。</summary>
public sealed class AppearanceSkinTokens
{
    [JsonPropertyName("light")]
    public AppearanceSkinModeTokens Light { get; set; } = new();

    [JsonPropertyName("dark")]
    public AppearanceSkinModeTokens Dark { get; set; } = new();

    [JsonPropertyName("components")]
    public AppearanceSkinComponentTokens Components { get; set; } = new();
}

/// <summary>浅色/深色模式颜色令牌。对应 Go: <c>app.AppearanceSkinModeTokens</c>。</summary>
public sealed record AppearanceSkinModeTokens
{
    [JsonPropertyName("canvas")] public string Canvas { get; set; } = "";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "";
    [JsonPropertyName("surfaceSubtle")] public string SurfaceSubtle { get; set; } = "";
    [JsonPropertyName("surfaceRaised")] public string SurfaceRaised { get; set; } = "";
    [JsonPropertyName("overlay")] public string Overlay { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("textMuted")] public string TextMuted { get; set; } = "";
    [JsonPropertyName("border")] public string Border { get; set; } = "";
    [JsonPropertyName("control")] public string Control { get; set; } = "";
    [JsonPropertyName("controlHover")] public string ControlHover { get; set; } = "";
    [JsonPropertyName("controlActive")] public string ControlActive { get; set; } = "";
    [JsonPropertyName("controlBorder")] public string ControlBorder { get; set; } = "";
    [JsonPropertyName("controlFocus")] public string ControlFocus { get; set; } = "";
    [JsonPropertyName("controlDisabledBackground")] public string ControlDisabledBackground { get; set; } = "";
    [JsonPropertyName("controlDisabledForeground")] public string ControlDisabledForeground { get; set; } = "";
    [JsonPropertyName("switchChecked")] public string SwitchChecked { get; set; } = "";
    [JsonPropertyName("switchCheckedHover")] public string SwitchCheckedHover { get; set; } = "";
    [JsonPropertyName("switchCheckedHandle")] public string SwitchCheckedHandle { get; set; } = "";
    [JsonPropertyName("switchUnchecked")] public string SwitchUnchecked { get; set; } = "";
    [JsonPropertyName("switchUncheckedHover")] public string SwitchUncheckedHover { get; set; } = "";
    [JsonPropertyName("switchUncheckedHandle")] public string SwitchUncheckedHandle { get; set; } = "";
    [JsonPropertyName("primary")] public string Primary { get; set; } = "";
    [JsonPropertyName("primaryHover")] public string PrimaryHover { get; set; } = "";
    [JsonPropertyName("primaryActive")] public string PrimaryActive { get; set; } = "";
    [JsonPropertyName("primaryForeground")] public string PrimaryForeground { get; set; } = "";
    [JsonPropertyName("selected")] public string Selected { get; set; } = "";
    [JsonPropertyName("selectedHover")] public string SelectedHover { get; set; } = "";
    [JsonPropertyName("selectedActive")] public string SelectedActive { get; set; } = "";
    [JsonPropertyName("selectedForeground")] public string SelectedForeground { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("iconMuted")] public string IconMuted { get; set; } = "";
    [JsonPropertyName("iconActive")] public string IconActive { get; set; } = "";
    [JsonPropertyName("success")] public string Success { get; set; } = "";
    [JsonPropertyName("warning")] public string Warning { get; set; } = "";
    [JsonPropertyName("danger")] public string Danger { get; set; } = "";
    [JsonPropertyName("dangerHover")] public string DangerHover { get; set; } = "";
    [JsonPropertyName("dangerActive")] public string DangerActive { get; set; } = "";
    [JsonPropertyName("dangerForeground")] public string DangerForeground { get; set; } = "";
    [JsonPropertyName("info")] public string Info { get; set; } = "";
    [JsonPropertyName("workspace")] public string Workspace { get; set; } = "";
    [JsonPropertyName("workspaceGrid")] public string WorkspaceGrid { get; set; } = "";
    [JsonPropertyName("adminBackground")] public string AdminBackground { get; set; } = "";
    [JsonPropertyName("adminSurface")] public string AdminSurface { get; set; } = "";
    [JsonPropertyName("adminSubtle")] public string AdminSubtle { get; set; } = "";
    [JsonPropertyName("adminStrong")] public string AdminStrong { get; set; } = "";
    [JsonPropertyName("authBackground")] public string AuthBackground { get; set; } = "";
    [JsonPropertyName("authPanel")] public string AuthPanel { get; set; } = "";
    [JsonPropertyName("authCard")] public string AuthCard { get; set; } = "";
    [JsonPropertyName("authAccent")] public string AuthAccent { get; set; } = "";
    [JsonPropertyName("authMuted")] public string AuthMuted { get; set; } = "";
}

/// <summary>组件令牌。对应 Go: <c>app.AppearanceSkinComponentTokens</c>。</summary>
public sealed record AppearanceSkinComponentTokens
{
    [JsonPropertyName("buttonRadius")] public int ButtonRadius { get; set; }
    [JsonPropertyName("inputRadius")] public int InputRadius { get; set; }
    [JsonPropertyName("cardRadius")] public int CardRadius { get; set; }
    [JsonPropertyName("overlayRadius")] public int OverlayRadius { get; set; }
    [JsonPropertyName("menuRadius")] public int MenuRadius { get; set; }
    [JsonPropertyName("checkboxRadius")] public int CheckboxRadius { get; set; }
    [JsonPropertyName("controlHeight")] public int ControlHeight { get; set; }
    [JsonPropertyName("controlHeightSmall")] public int ControlHeightSmall { get; set; }
    [JsonPropertyName("controlHeightLarge")] public int ControlHeightLarge { get; set; }
    [JsonPropertyName("borderWidth")] public int BorderWidth { get; set; }
    [JsonPropertyName("focusRingWidth")] public int FocusRingWidth { get; set; }
    [JsonPropertyName("iconSize")] public int IconSize { get; set; }
    [JsonPropertyName("buttonFontWeight")] public int ButtonFontWeight { get; set; }
    [JsonPropertyName("hoverLift")] public int HoverLift { get; set; }
    [JsonPropertyName("motionFast")] public int MotionFast { get; set; }
    [JsonPropertyName("motionNormal")] public int MotionNormal { get; set; }
    [JsonPropertyName("shadowStyle")] public string ShadowStyle { get; set; } = "";
}

/// <summary>
/// 内置主题的调色板（仅用于构造默认主题，不参与 API 契约）。
/// 对应 Go: <c>appearanceSkinPalette</c>。
/// </summary>
internal sealed record AppearanceSkinPalette(
    string Canvas, string Surface, string Subtle, string Raised, string Overlay, string Text, string Muted, string Border,
    string Primary, string PrimaryHover, string PrimaryActive, string PrimaryForeground,
    string Selected, string SelectedHover, string SelectedActive, string SelectedForeground, string Info,
    string SwitchChecked, string SwitchCheckedHover, string SwitchCheckedHandle,
    string SwitchUnchecked, string SwitchUncheckedHover, string SwitchUncheckedHandle,
    string Success, string Warning, string Danger, string DangerHover, string DangerActive, string DangerForeground,
    string Workspace, string Grid, string AdminBackground, string AdminSurface, string AdminSubtle, string AdminStrong,
    string AuthBackground, string AuthPanel, string AuthCard, string AuthAccent, string AuthMuted);

/// <summary>外观常量与文本处理。对应 Go: <c>app/appearance.go</c> 的常量及归一化函数。</summary>
public static class AppearanceText
{
    public const string SettingKey = "appearance";
    public const int SchemaVersion = 7;
    public const long LogoMaxBytes = 5L << 20;
    public const long PosterMaxBytes = 10L << 20;
    public const long VideoMaxBytes = 256L << 20;

    public const string Logo = "logo";
    public const string DarkLogo = "logo-dark";
    public const string Video = "video";
    public const string Poster = "poster";

    public const string DefaultBrandName = "影策";
    public const string DefaultBrandSlug = "open-ai-canvas";
    public const string DefaultLogoURL = "/logo.svg";
    public const string DefaultVideoURL =
        "https://boss-shjd.biliapi.net/updream/aniforge/video/video_bbcb00bd-650d-4249-9346-5cd21fd2484c_m1hc-u0-1pu13x-3v1s.mp4";
    public const string DefaultPosterURL =
        "https://i0.hdslb.com/bfs/aitool/aniforge/image/02933f26-5f1b-49ff-a811-b7f95ee5e5b8_m1hc-u0-sau.jpg";
    public const string DefaultHeroTitle = "让一个故事，\n从文字走向银幕。";

    /// <summary>对应 Go: <c>appearanceAssetURL</c>。</summary>
    public static string AssetURL(string slot, string revision) =>
        "/api/public/appearance/assets/" + Uri.EscapeDataString(slot) + "?v=" + Uri.EscapeDataString(revision);

    /// <summary>对应 Go: <c>normalizeAppearanceCopy</c>（换行统一，去首尾空白）。</summary>
    public static string NormalizeCopy(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

    /// <summary>对应 Go: <c>normalizeAppearanceSingleLine</c>（换行转空格）。</summary>
    public static string NormalizeSingleLine(string value) =>
        value.Replace("\r\n", " ").Replace("\r", " ").Trim();

    /// <summary>对应 Go 的 <c>unicode.IsControl</c>。</summary>
    public static bool HasControlChar(string value, bool allowNewline = false)
    {
        foreach (char ch in value)
        {
            if (allowNewline && ch == '\n')
            {
                continue;
            }
            if (char.IsControl(ch))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>validAppearanceBrandSlug</c>。</summary>
    public static bool IsValidBrandSlug(string value)
    {
        if (value.Length == 0 || value.Length > 48 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }
        foreach (char ch in value)
        {
            if (ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>对应 Go: <c>validateAppearanceCopy</c>。</summary>
    public static void ValidateCopy(string value, string label, int maxRunes, bool required)
    {
        if (required && value.Length == 0)
        {
            throw AppError.BadAuthRequest(label + "不能为空");
        }
        if (AppearanceSkins.RuneCount(value) > maxRunes)
        {
            throw AppError.BadAuthRequest($"{label}不能超过 {maxRunes} 个字符");
        }
        if (HasControlChar(value, allowNewline: true))
        {
            throw AppError.BadAuthRequest(label + "不能包含控制字符");
        }
    }

    /// <summary>默认外观设置。对应 Go: <c>defaultAppearanceSetting</c>。</summary>
    public static AppearanceSetting Default() => new()
    {
        SchemaVersion = SchemaVersion,
        BrandName = DefaultBrandName,
        BrandSlug = DefaultBrandSlug,
        AuthHeroTitle = DefaultHeroTitle,
        AuthVideoAutoplay = true,
        LogoFrameEnabled = true,
        SkinID = AppearanceSkins.DefaultSkinID,
        SkinThemes = AppearanceSkins.DefaultThemes(),
    };

    /// <summary>对应 Go: <c>AppearanceAssetMaxBytes</c>。</summary>
    public static long AssetMaxBytes(string slot) => slot.Trim() switch
    {
        Logo or DarkLogo => LogoMaxBytes,
        Poster => PosterMaxBytes,
        Video => VideoMaxBytes,
        _ => throw AppError.BadAuthRequest("外观资源类型无效"),
    };

    /// <summary>对应 Go: <c>appearanceAllowedMIMETypes</c>。</summary>
    public static HashSet<string> AllowedMimeTypes(string slot) =>
        slot == Video
            ? new HashSet<string>(StringComparer.Ordinal) { "video/mp4", "video/webm" }
            : new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/webp" };

    /// <summary>对应 Go: <c>appearanceAssetLabel</c>。</summary>
    public static string AssetLabel(string slot) => slot switch
    {
        Logo => "浅色模式品牌 Logo",
        DarkLogo => "深色模式品牌 Logo",
        Poster => "视频封面",
        Video => "品牌视频",
        _ => "外观资源",
    };

    /// <summary>对应 Go: <c>appearanceResourceID</c>。</summary>
    public static string ResourceID(AppearanceSetting value, string slot) => slot switch
    {
        Logo => value.LogoResourceID,
        DarkLogo => value.DarkLogoResourceID,
        Video => value.AuthVideoResourceID,
        Poster => value.AuthVideoPosterResourceID,
        _ => "",
    };

    /// <summary>按槽位写回资源 ID。对应 Go 中 <c>resolveAvailableAppearanceAssets</c> 的 switch。</summary>
    public static void SetResourceID(AppearanceSetting value, string slot, string resourceId)
    {
        switch (slot)
        {
            case Logo: value.LogoResourceID = resourceId; break;
            case DarkLogo: value.DarkLogoResourceID = resourceId; break;
            case Video: value.AuthVideoResourceID = resourceId; break;
            case Poster: value.AuthVideoPosterResourceID = resourceId; break;
        }
    }
}
