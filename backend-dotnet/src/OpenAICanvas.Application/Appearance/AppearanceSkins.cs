#nullable enable
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Appearance;

/// <summary>
/// 皮肤主题令牌与内置主题定义。
/// 对应 Go: <c>app/appearance_skins.go</c>。
/// </summary>
/// <remarks>
/// 令牌刻意采用白名单而非任意 CSS：每个值在使用前都会校验为 6/8 位十六进制颜色。
/// </remarks>
public static partial class AppearanceSkins
{
    /// <summary>对应 Go: <c>maxAppearanceSkinThemes</c>。</summary>
    public const int MaxThemes = 16;

    /// <summary>对应 Go: <c>defaultAppearanceSkinID</c>。</summary>
    public const string DefaultSkinID = "classic";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex SkinIDPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?$")]
    private static partial Regex ColorPattern();

    private static readonly string[] ModeColorFields =
    [
        nameof(AppearanceSkinModeTokens.Canvas),
        nameof(AppearanceSkinModeTokens.Surface),
        nameof(AppearanceSkinModeTokens.SurfaceSubtle),
        nameof(AppearanceSkinModeTokens.SurfaceRaised),
        nameof(AppearanceSkinModeTokens.Overlay),
        nameof(AppearanceSkinModeTokens.Text),
        nameof(AppearanceSkinModeTokens.TextMuted),
        nameof(AppearanceSkinModeTokens.Border),
        nameof(AppearanceSkinModeTokens.Control),
        nameof(AppearanceSkinModeTokens.ControlHover),
        nameof(AppearanceSkinModeTokens.ControlActive),
        nameof(AppearanceSkinModeTokens.ControlBorder),
        nameof(AppearanceSkinModeTokens.ControlFocus),
        nameof(AppearanceSkinModeTokens.ControlDisabledBackground),
        nameof(AppearanceSkinModeTokens.ControlDisabledForeground),
        nameof(AppearanceSkinModeTokens.SwitchChecked),
        nameof(AppearanceSkinModeTokens.SwitchCheckedHover),
        nameof(AppearanceSkinModeTokens.SwitchCheckedHandle),
        nameof(AppearanceSkinModeTokens.SwitchUnchecked),
        nameof(AppearanceSkinModeTokens.SwitchUncheckedHover),
        nameof(AppearanceSkinModeTokens.SwitchUncheckedHandle),
        nameof(AppearanceSkinModeTokens.Primary),
        nameof(AppearanceSkinModeTokens.PrimaryHover),
        nameof(AppearanceSkinModeTokens.PrimaryActive),
        nameof(AppearanceSkinModeTokens.PrimaryForeground),
        nameof(AppearanceSkinModeTokens.Selected),
        nameof(AppearanceSkinModeTokens.SelectedHover),
        nameof(AppearanceSkinModeTokens.SelectedActive),
        nameof(AppearanceSkinModeTokens.SelectedForeground),
        nameof(AppearanceSkinModeTokens.Icon),
        nameof(AppearanceSkinModeTokens.IconMuted),
        nameof(AppearanceSkinModeTokens.IconActive),
        nameof(AppearanceSkinModeTokens.Success),
        nameof(AppearanceSkinModeTokens.Warning),
        nameof(AppearanceSkinModeTokens.Danger),
        nameof(AppearanceSkinModeTokens.DangerHover),
        nameof(AppearanceSkinModeTokens.DangerActive),
        nameof(AppearanceSkinModeTokens.DangerForeground),
        nameof(AppearanceSkinModeTokens.Info),
        nameof(AppearanceSkinModeTokens.Workspace),
        nameof(AppearanceSkinModeTokens.WorkspaceGrid),
        nameof(AppearanceSkinModeTokens.AdminBackground),
        nameof(AppearanceSkinModeTokens.AdminSurface),
        nameof(AppearanceSkinModeTokens.AdminSubtle),
        nameof(AppearanceSkinModeTokens.AdminStrong),
        nameof(AppearanceSkinModeTokens.AuthBackground),
        nameof(AppearanceSkinModeTokens.AuthPanel),
        nameof(AppearanceSkinModeTokens.AuthCard),
        nameof(AppearanceSkinModeTokens.AuthAccent),
        nameof(AppearanceSkinModeTokens.AuthMuted),
    ];

    /// <summary>内置主题集合（classic / studio / warm / violet）。对应 Go: <c>defaultAppearanceSkinThemes</c>。</summary>
    public static List<AppearanceSkinTheme> DefaultThemes()
    {
        AppearanceSkinTheme classic = DefaultClassicSkin();

        AppearanceSkinTheme studio = CloneSkin(classic, "studio-indigo", "青瓷工作室", "雾白青瓷 · 珊瑚点睛");
        Tint(studio.Tokens.Light, new AppearanceSkinPalette(
            "#f6f8f9", "#ffffff", "#edf4f3", "#d7e8e5", "#ffffff", "#142026", "#607079", "#d5e0e2",
            "#087f76", "#076d66", "#095c57", "#ffffff", "#dff4f0", "#ccebe6", "#b9e3dd", "#075f59", "#dd7a38",
            "#087f76", "#076d66", "#ffffff", "#a8b7b9", "#899b9e", "#ffffff",
            "#16866f", "#c46722", "#c83f3a", "#ad3532", "#922e2c", "#ffffff",
            "#ffffff", "#e8f2f0", "#eef3f4", "#ffffff", "#f5f9f9", "#dce9e8",
            "#071a1d", "#0b2226", "#0d272b", "#72eadc", "#8db9b4"));
        Tint(studio.Tokens.Dark, new AppearanceSkinPalette(
            "#0b1215", "#121d21", "#142428", "#203a3b", "#172328", "#e7f4f2", "#8ca6a7", "#294044",
            "#43d8c7", "#72eadc", "#2db9aa", "#052724", "#193735", "#214743", "#28554f", "#baf5ee", "#ff9e57",
            "#31bfae", "#43d8c7", "#052724", "#3f5559", "#526a6d", "#e7f4f2",
            "#4ade80", "#ffb454", "#ff7875", "#ff9a98", "#df5e5b", "#2d0808",
            "#111d21", "#17292c", "#0c171a", "#132126", "#192a2e", "#21383a",
            "#061416", "#091c20", "#0d272b", "#72eadc", "#8db9b4"));
        studio.Tokens.Components = ComponentPreset(8, 8, 14, 14, 10, 5);

        AppearanceSkinTheme warm = CloneSkin(classic, "warm-persimmon", "暖柿纸境", "米纸暖棕 · 柿橙强调");
        Tint(warm.Tokens.Light, new AppearanceSkinPalette(
            "#fbf7f2", "#fffdf9", "#f6ebe3", "#ead2c2", "#fffdf9", "#35261f", "#806b61", "#e5d6cb",
            "#b94f2f", "#9e4128", "#843621", "#fffaf6", "#f8e0cf", "#f1d1bb", "#e9c1a6", "#8c3d25", "#c58a3b",
            "#a84a2f", "#bd5b3c", "#fffaf6", "#c7b3a7", "#aa9182", "#fffdf9",
            "#4d7f50", "#bd6819", "#bd3d32", "#a33229", "#892a23", "#fffaf6",
            "#fffdf9", "#f3e9e1", "#f6eee7", "#fffdf9", "#faf3ed", "#ead8ca",
            "#1f1410", "#291813", "#361f17", "#ffc08b", "#b99d89"));
        Tint(warm.Tokens.Dark, new AppearanceSkinPalette(
            "#1b1210", "#291a16", "#33211b", "#493027", "#31201a", "#f8e9df", "#b89c8c", "#50372e",
            "#ef8a61", "#ffab83", "#d87350", "#37140a", "#4b2a20", "#5b3427", "#6b3e2e", "#ffd9c4", "#f0b36c",
            "#df7752", "#ef8a61", "#37140a", "#65483d", "#7b5a4c", "#f8e9df",
            "#8dcc72", "#f2b35f", "#ff8375", "#ffa094", "#df6a5e", "#32100b",
            "#241714", "#31201b", "#1c1210", "#291b17", "#35231d", "#493027",
            "#160d0a", "#21120e", "#361f17", "#ffc08b", "#b99d89"));
        warm.Tokens.Components = ComponentPreset(10, 10, 16, 18, 12, 5);

        AppearanceSkinTheme violet = CloneSkin(classic, "brand-violet", "霓光紫境", "冷白雾紫 · 夜幕电光");
        Tint(violet.Tokens.Light, new AppearanceSkinPalette(
            "#f8f7fc", "#ffffff", "#f0eefb", "#dfd9f5", "#ffffff", "#211b35", "#716a86", "#ddd8ec",
            "#6656d9", "#5847c7", "#4939b2", "#ffffff", "#ebe8ff", "#ded9ff", "#d0c9ff", "#4f3fb5", "#8f61e8",
            "#6656d9", "#5847c7", "#ffffff", "#b4afc5", "#9891ae", "#ffffff",
            "#2f966e", "#b96f16", "#c73559", "#ac2c4b", "#912640", "#ffffff",
            "#ffffff", "#f0eef8", "#f1f0f7", "#ffffff", "#f7f6fb", "#e4e0f1",
            "#110d20", "#17112b", "#211936", "#b4a8ff", "#958dad"));
        Tint(violet.Tokens.Dark, new AppearanceSkinPalette(
            "#0f0c19", "#171321", "#201a2f", "#302746", "#1c1734", "#f2efff", "#a9a2bd", "#39304d",
            "#9a90ff", "#b5adff", "#8175ed", "#171126", "#292347", "#352d59", "#40366a", "#ddd9ff", "#c45dff",
            "#8175ed", "#9a90ff", "#171126", "#504967", "#665d7f", "#f2efff",
            "#5bd6a2", "#ffc46b", "#ff7795", "#ff99ae", "#df607f", "#310b17",
            "#15111f", "#211b30", "#110e1a", "#191524", "#211b30", "#302746",
            "#0b0812", "#120d20", "#211936", "#b4a8ff", "#958dad"));
        violet.Tokens.Components = ComponentPreset(7, 7, 12, 14, 9, 4);

        return [classic, studio, warm, violet];
    }

    /// <summary>经典黑白（系统默认，不可修改）。对应 Go: <c>defaultClassicAppearanceSkin</c>。</summary>
    public static AppearanceSkinTheme DefaultClassicSkin() => new()
    {
        ID = "classic",
        Name = "经典黑白",
        Description = "项目原始样式 · 不可修改",
        Locked = true,
        Tokens = new AppearanceSkinTokens
        {
            Light = new AppearanceSkinModeTokens
            {
                Canvas = "#ffffff", Surface = "#ffffff", SurfaceSubtle = "#f7f7f7", SurfaceRaised = "#ececec",
                Overlay = "#ffffff", Text = "#171717", TextMuted = "#737373", Border = "#e5e5e5",
                Control = "#ffffff", ControlHover = "#f5f5f5", ControlActive = "#ececec", ControlBorder = "#d1d1d1",
                ControlFocus = "#171717", ControlDisabledBackground = "#f2f2f2", ControlDisabledForeground = "#a3a3a3",
                SwitchChecked = "#16a34a", SwitchCheckedHover = "#15803d", SwitchCheckedHandle = "#ffffff",
                SwitchUnchecked = "#b8b8b8", SwitchUncheckedHover = "#9f9f9f", SwitchUncheckedHandle = "#ffffff",
                Primary = "#171717", PrimaryHover = "#303030", PrimaryActive = "#404040", PrimaryForeground = "#ffffff",
                Selected = "#e8e8e8", SelectedHover = "#dedede", SelectedActive = "#d5d5d5", SelectedForeground = "#171717",
                Icon = "#3f3f46", IconMuted = "#a1a1aa", IconActive = "#171717",
                Success = "#16a34a", Warning = "#d97706", Danger = "#dc2626", DangerHover = "#b91c1c",
                DangerActive = "#991b1b", DangerForeground = "#ffffff", Info = "#2563eb",
                Workspace = "#ffffff", WorkspaceGrid = "#f3f3f3",
                AdminBackground = "#f3f4f6", AdminSurface = "#ffffff", AdminSubtle = "#f7f8fa", AdminStrong = "#eceff3",
                AuthBackground = "#08090c", AuthPanel = "#0b0c10", AuthCard = "#121318", AuthAccent = "#93c5fd", AuthMuted = "#8a8b91",
            },
            Dark = new AppearanceSkinModeTokens
            {
                Canvas = "#0a0a0a", Surface = "#181818", SurfaceSubtle = "#202020", SurfaceRaised = "#2a2a2a",
                Overlay = "#1f1f20", Text = "#f5f5f5", TextMuted = "#a3a3a3", Border = "#2d2d2d",
                Control = "#202020", ControlHover = "#292929", ControlActive = "#333333", ControlBorder = "#4a4a4a",
                ControlFocus = "#f5f5f5", ControlDisabledBackground = "#252525", ControlDisabledForeground = "#737373",
                SwitchChecked = "#22c55e", SwitchCheckedHover = "#4ade80", SwitchCheckedHandle = "#071a0f",
                SwitchUnchecked = "#525252", SwitchUncheckedHover = "#686868", SwitchUncheckedHandle = "#f5f5f5",
                Primary = "#f5f5f5", PrimaryHover = "#ffffff", PrimaryActive = "#e5e5e5", PrimaryForeground = "#171717",
                Selected = "#2b2b2b", SelectedHover = "#343434", SelectedActive = "#3d3d3d", SelectedForeground = "#f5f5f5",
                Icon = "#d4d4d8", IconMuted = "#71717a", IconActive = "#ffffff",
                Success = "#4ade80", Warning = "#fbbf24", Danger = "#f87171", DangerHover = "#fca5a5",
                DangerActive = "#ef4444", DangerForeground = "#2b0808", Info = "#60a5fa",
                Workspace = "#181818", WorkspaceGrid = "#222222",
                AdminBackground = "#101010", AdminSurface = "#181818", AdminSubtle = "#202020", AdminStrong = "#2a2a2a",
                AuthBackground = "#08090c", AuthPanel = "#0b0c10", AuthCard = "#121318", AuthAccent = "#93c5fd", AuthMuted = "#8a8b91",
            },
            Components = ComponentPreset(6, 6, 12, 12, 8, 4),
        },
    };

    /// <summary>对应 Go: <c>appearanceSkinComponentPreset</c>。</summary>
    public static AppearanceSkinComponentTokens ComponentPreset(
        int buttonRadius, int inputRadius, int cardRadius, int overlayRadius, int menuRadius, int checkboxRadius) => new()
    {
        ButtonRadius = buttonRadius,
        InputRadius = inputRadius,
        CardRadius = cardRadius,
        OverlayRadius = overlayRadius,
        MenuRadius = menuRadius,
        CheckboxRadius = checkboxRadius,
        ControlHeight = 36,
        ControlHeightSmall = 30,
        ControlHeightLarge = 42,
        BorderWidth = 1,
        FocusRingWidth = 2,
        IconSize = 16,
        ButtonFontWeight = 500,
        HoverLift = 1,
        MotionFast = 120,
        MotionNormal = 180,
        ShadowStyle = "soft",
    };

    /// <summary>对应 Go: <c>cloneAppearanceSkin</c>（深拷贝后改写身份）。</summary>
    private static AppearanceSkinTheme CloneSkin(
        AppearanceSkinTheme source, string id, string name, string description) => new()
    {
        ID = id,
        Name = name,
        Description = description,
        Locked = false,
        Tokens = CloneTokens(source.Tokens),
    };

    private static AppearanceSkinTokens CloneTokens(AppearanceSkinTokens source) => new()
    {
        Light = CloneMode(source.Light),
        Dark = CloneMode(source.Dark),
        Components = CloneComponents(source.Components),
    };

    private static AppearanceSkinModeTokens CloneMode(AppearanceSkinModeTokens source) => (AppearanceSkinModeTokens)source with { };

    private static AppearanceSkinComponentTokens CloneComponents(AppearanceSkinComponentTokens source) => source with { };

    /// <summary>对应 Go: <c>tintAppearanceSkinMode</c>。</summary>
    private static void Tint(AppearanceSkinModeTokens value, AppearanceSkinPalette palette)
    {
        value.Canvas = palette.Canvas;
        value.Surface = palette.Surface;
        value.SurfaceSubtle = palette.Subtle;
        value.SurfaceRaised = palette.Raised;
        value.Overlay = palette.Overlay;
        value.Text = palette.Text;
        value.TextMuted = palette.Muted;
        value.Border = palette.Border;
        value.Control = palette.Surface;
        value.ControlHover = palette.Subtle;
        value.ControlActive = palette.Raised;
        value.ControlBorder = palette.Border;
        value.ControlFocus = palette.Primary;
        value.ControlDisabledBackground = palette.Subtle;
        value.ControlDisabledForeground = palette.Muted;
        value.SwitchChecked = palette.SwitchChecked;
        value.SwitchCheckedHover = palette.SwitchCheckedHover;
        value.SwitchCheckedHandle = palette.SwitchCheckedHandle;
        value.SwitchUnchecked = palette.SwitchUnchecked;
        value.SwitchUncheckedHover = palette.SwitchUncheckedHover;
        value.SwitchUncheckedHandle = palette.SwitchUncheckedHandle;
        value.Primary = palette.Primary;
        value.PrimaryHover = palette.PrimaryHover;
        value.PrimaryActive = palette.PrimaryActive;
        value.PrimaryForeground = palette.PrimaryForeground;
        value.Selected = palette.Selected;
        value.SelectedHover = palette.SelectedHover;
        value.SelectedActive = palette.SelectedActive;
        value.SelectedForeground = palette.SelectedForeground;
        value.Icon = palette.Text;
        value.IconMuted = palette.Muted;
        value.IconActive = palette.Primary;
        value.Success = palette.Success;
        value.Warning = palette.Warning;
        value.Danger = palette.Danger;
        value.DangerHover = palette.DangerHover;
        value.DangerActive = palette.DangerActive;
        value.DangerForeground = palette.DangerForeground;
        value.Info = palette.Info;
        value.Workspace = palette.Workspace;
        value.WorkspaceGrid = palette.Grid;
        value.AdminBackground = palette.AdminBackground;
        value.AdminSurface = palette.AdminSurface;
        value.AdminSubtle = palette.AdminSubtle;
        value.AdminStrong = palette.AdminStrong;
        value.AuthBackground = palette.AuthBackground;
        value.AuthPanel = palette.AuthPanel;
        value.AuthCard = palette.AuthCard;
        value.AuthAccent = palette.AuthAccent;
        value.AuthMuted = palette.AuthMuted;
    }

    /// <summary>对应 Go: <c>normalizeAppearanceSkinThemes</c>。</summary>
    public static List<AppearanceSkinTheme> NormalizeThemes(IReadOnlyList<AppearanceSkinTheme> themes)
    {
        List<AppearanceSkinTheme> result = [.. themes.Select(CloneTheme)];
        List<AppearanceSkinTheme> builtins = DefaultThemes();
        foreach (AppearanceSkinTheme theme in result)
        {
            theme.EnsureTokens();
        }
        foreach (AppearanceSkinTheme theme in result)
        {
            theme.ID = theme.ID.Trim().ToLowerInvariant();
            theme.Name = theme.Name.Trim();
            theme.Description = theme.Description.Trim();
            theme.Locked = theme.ID == DefaultSkinID;

            AppearanceSkinTokens fallback = new();
            foreach (AppearanceSkinTheme builtin in builtins)
            {
                if (builtin.ID == theme.ID)
                {
                    fallback = builtin.Tokens;
                    break;
                }
            }
            BackfillModeColors(theme.Tokens.Light, fallback.Light);
            BackfillModeColors(theme.Tokens.Dark, fallback.Dark);
            NormalizeModeColors(theme.Tokens.Light);
            NormalizeModeColors(theme.Tokens.Dark);
            theme.Tokens.Components.ShadowStyle = theme.Tokens.Components.ShadowStyle.Trim().ToLowerInvariant();
        }
        return result;
    }

    private static AppearanceSkinTheme CloneTheme(AppearanceSkinTheme source)
    {
        // 请求体可能省略 tokens（JSON null 或缺字段），按 Go 结构体零值语义补齐。
        source.EnsureTokens();
        return new AppearanceSkinTheme
        {
            ID = source.ID,
            Name = source.Name,
            Description = source.Description,
            Locked = source.Locked,
            Tokens = CloneTokens(source.Tokens),
        };
    }

    /// <summary>对应 Go: <c>backfillAppearanceSkinModeColors</c>。</summary>
    private static void BackfillModeColors(AppearanceSkinModeTokens mode, AppearanceSkinModeTokens fallback)
    {
        (string Current, string Fallback, string Derived)[] fields =
        [
            (mode.SwitchChecked, fallback.SwitchChecked, mode.Primary),
            (mode.SwitchCheckedHover, fallback.SwitchCheckedHover, mode.PrimaryHover),
            (mode.SwitchCheckedHandle, fallback.SwitchCheckedHandle, mode.PrimaryForeground),
            (mode.SwitchUnchecked, fallback.SwitchUnchecked, mode.ControlBorder),
            (mode.SwitchUncheckedHover, fallback.SwitchUncheckedHover, mode.ControlActive),
            (mode.SwitchUncheckedHandle, fallback.SwitchUncheckedHandle, mode.SelectedForeground),
            (mode.DangerHover, fallback.DangerHover, mode.Danger),
            (mode.DangerActive, fallback.DangerActive, mode.Danger),
            (mode.DangerForeground, fallback.DangerForeground, mode.PrimaryForeground),
        ];
        mode.SwitchChecked = Backfill(fields[0]);
        mode.SwitchCheckedHover = Backfill(fields[1]);
        mode.SwitchCheckedHandle = Backfill(fields[2]);
        mode.SwitchUnchecked = Backfill(fields[3]);
        mode.SwitchUncheckedHover = Backfill(fields[4]);
        mode.SwitchUncheckedHandle = Backfill(fields[5]);
        mode.DangerHover = Backfill(fields[6]);
        mode.DangerActive = Backfill(fields[7]);
        mode.DangerForeground = Backfill(fields[8]);

        static string Backfill((string Current, string Fallback, string Derived) field)
        {
            if (field.Current.Trim().Length > 0)
            {
                return field.Current;
            }
            return field.Fallback.Length > 0 ? field.Fallback : field.Derived;
        }
    }

    /// <summary>对应 Go: <c>normalizeAppearanceSkinModeColors</c>（全部字段小写去空白）。</summary>
    private static void NormalizeModeColors(AppearanceSkinModeTokens mode)
    {
        foreach (string field in ModeColorFields)
        {
            string current = GetColor(mode, field);
            SetColor(mode, field, current.Trim().ToLowerInvariant());
        }
    }

    private static string GetColor(AppearanceSkinModeTokens mode, string field) => field switch
    {
        "Canvas" => mode.Canvas,
        "Surface" => mode.Surface,
        "SurfaceSubtle" => mode.SurfaceSubtle,
        "SurfaceRaised" => mode.SurfaceRaised,
        "Overlay" => mode.Overlay,
        "Text" => mode.Text,
        "TextMuted" => mode.TextMuted,
        "Border" => mode.Border,
        "Control" => mode.Control,
        "ControlHover" => mode.ControlHover,
        "ControlActive" => mode.ControlActive,
        "ControlBorder" => mode.ControlBorder,
        "ControlFocus" => mode.ControlFocus,
        "ControlDisabledBackground" => mode.ControlDisabledBackground,
        "ControlDisabledForeground" => mode.ControlDisabledForeground,
        "SwitchChecked" => mode.SwitchChecked,
        "SwitchCheckedHover" => mode.SwitchCheckedHover,
        "SwitchCheckedHandle" => mode.SwitchCheckedHandle,
        "SwitchUnchecked" => mode.SwitchUnchecked,
        "SwitchUncheckedHover" => mode.SwitchUncheckedHover,
        "SwitchUncheckedHandle" => mode.SwitchUncheckedHandle,
        "Primary" => mode.Primary,
        "PrimaryHover" => mode.PrimaryHover,
        "PrimaryActive" => mode.PrimaryActive,
        "PrimaryForeground" => mode.PrimaryForeground,
        "Selected" => mode.Selected,
        "SelectedHover" => mode.SelectedHover,
        "SelectedActive" => mode.SelectedActive,
        "SelectedForeground" => mode.SelectedForeground,
        "Icon" => mode.Icon,
        "IconMuted" => mode.IconMuted,
        "IconActive" => mode.IconActive,
        "Success" => mode.Success,
        "Warning" => mode.Warning,
        "Danger" => mode.Danger,
        "DangerHover" => mode.DangerHover,
        "DangerActive" => mode.DangerActive,
        "DangerForeground" => mode.DangerForeground,
        "Info" => mode.Info,
        "Workspace" => mode.Workspace,
        "WorkspaceGrid" => mode.WorkspaceGrid,
        "AdminBackground" => mode.AdminBackground,
        "AdminSurface" => mode.AdminSurface,
        "AdminSubtle" => mode.AdminSubtle,
        "AdminStrong" => mode.AdminStrong,
        "AuthBackground" => mode.AuthBackground,
        "AuthPanel" => mode.AuthPanel,
        "AuthCard" => mode.AuthCard,
        "AuthAccent" => mode.AuthAccent,
        "AuthMuted" => mode.AuthMuted,
        _ => "",
    };

    private static void SetColor(AppearanceSkinModeTokens mode, string field, string value)
    {
        switch (field)
        {
            case "Canvas": mode.Canvas = value; break;
            case "Surface": mode.Surface = value; break;
            case "SurfaceSubtle": mode.SurfaceSubtle = value; break;
            case "SurfaceRaised": mode.SurfaceRaised = value; break;
            case "Overlay": mode.Overlay = value; break;
            case "Text": mode.Text = value; break;
            case "TextMuted": mode.TextMuted = value; break;
            case "Border": mode.Border = value; break;
            case "Control": mode.Control = value; break;
            case "ControlHover": mode.ControlHover = value; break;
            case "ControlActive": mode.ControlActive = value; break;
            case "ControlBorder": mode.ControlBorder = value; break;
            case "ControlFocus": mode.ControlFocus = value; break;
            case "ControlDisabledBackground": mode.ControlDisabledBackground = value; break;
            case "ControlDisabledForeground": mode.ControlDisabledForeground = value; break;
            case "SwitchChecked": mode.SwitchChecked = value; break;
            case "SwitchCheckedHover": mode.SwitchCheckedHover = value; break;
            case "SwitchCheckedHandle": mode.SwitchCheckedHandle = value; break;
            case "SwitchUnchecked": mode.SwitchUnchecked = value; break;
            case "SwitchUncheckedHover": mode.SwitchUncheckedHover = value; break;
            case "SwitchUncheckedHandle": mode.SwitchUncheckedHandle = value; break;
            case "Primary": mode.Primary = value; break;
            case "PrimaryHover": mode.PrimaryHover = value; break;
            case "PrimaryActive": mode.PrimaryActive = value; break;
            case "PrimaryForeground": mode.PrimaryForeground = value; break;
            case "Selected": mode.Selected = value; break;
            case "SelectedHover": mode.SelectedHover = value; break;
            case "SelectedActive": mode.SelectedActive = value; break;
            case "SelectedForeground": mode.SelectedForeground = value; break;
            case "Icon": mode.Icon = value; break;
            case "IconMuted": mode.IconMuted = value; break;
            case "IconActive": mode.IconActive = value; break;
            case "Success": mode.Success = value; break;
            case "Warning": mode.Warning = value; break;
            case "Danger": mode.Danger = value; break;
            case "DangerHover": mode.DangerHover = value; break;
            case "DangerActive": mode.DangerActive = value; break;
            case "DangerForeground": mode.DangerForeground = value; break;
            case "Info": mode.Info = value; break;
            case "Workspace": mode.Workspace = value; break;
            case "WorkspaceGrid": mode.WorkspaceGrid = value; break;
            case "AdminBackground": mode.AdminBackground = value; break;
            case "AdminSurface": mode.AdminSurface = value; break;
            case "AdminSubtle": mode.AdminSubtle = value; break;
            case "AdminStrong": mode.AdminStrong = value; break;
            case "AuthBackground": mode.AuthBackground = value; break;
            case "AuthPanel": mode.AuthPanel = value; break;
            case "AuthCard": mode.AuthCard = value; break;
            case "AuthAccent": mode.AuthAccent = value; break;
            case "AuthMuted": mode.AuthMuted = value; break;
        }
    }

    /// <summary>对应 Go: <c>validateAppearanceSkinThemes</c>。</summary>
    public static void ValidateThemes(IReadOnlyList<AppearanceSkinTheme> themes, string selectedId)
    {
        if (themes.Count == 0 || themes.Count > MaxThemes)
        {
            throw AppError.BadAuthRequest($"皮肤主题数量必须为 1 到 {MaxThemes} 套");
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool foundSelected = false;
        bool foundClassic = false;
        AppearanceSkinTheme classic = DefaultClassicSkin();
        foreach (AppearanceSkinTheme skin in themes)
        {
            skin.EnsureTokens();
            if (!SkinIDPattern().IsMatch(skin.ID))
            {
                throw AppError.BadAuthRequest("皮肤主题 ID 无效");
            }
            if (!seen.Add(skin.ID))
            {
                throw AppError.BadAuthRequest("皮肤主题 ID 不能重复");
            }
            if (skin.ID == selectedId)
            {
                foundSelected = true;
            }
            if (skin.ID == DefaultSkinID)
            {
                foundClassic = true;
                if (skin.Name != classic.Name || skin.Description != classic.Description
                    || !TokensEqual(skin.Tokens, classic.Tokens))
                {
                    throw AppError.BadAuthRequest("经典黑白为系统默认主题，不能修改或删除");
                }
            }
            ValidateSkinText(skin.Name, "皮肤主题名称", 40, required: true);
            ValidateSkinText(skin.Description, "皮肤主题说明", 100, required: false);
            ValidateSkinMode(skin.Tokens.Light);
            ValidateSkinMode(skin.Tokens.Dark);
            ValidateSkinComponents(skin.Tokens.Components);
        }
        if (!foundClassic)
        {
            throw AppError.BadAuthRequest("经典黑白为系统默认主题，不能修改或删除");
        }
        if (!foundSelected)
        {
            throw AppError.BadAuthRequest("当前启用的皮肤主题不存在");
        }
    }

    /// <summary>对应 Go 的 <c>reflect.DeepEqual(skin.Tokens, classic.Tokens)</c>。</summary>
    private static bool TokensEqual(AppearanceSkinTokens left, AppearanceSkinTokens right) =>
        System.Text.Json.JsonSerializer.Serialize(left) == System.Text.Json.JsonSerializer.Serialize(right);

    /// <summary>对应 Go: <c>validateAppearanceSkinMode</c>（每个字段必须是合法十六进制色）。</summary>
    private static void ValidateSkinMode(AppearanceSkinModeTokens mode)
    {
        foreach (string field in ModeColorFields)
        {
            if (!ColorPattern().IsMatch(GetColor(mode, field)))
            {
                throw AppError.BadAuthRequest("皮肤颜色必须使用 6 或 8 位十六进制颜色");
            }
        }
    }

    /// <summary>对应 Go: <c>validateAppearanceSkinComponents</c>。</summary>
    private static void ValidateSkinComponents(AppearanceSkinComponentTokens value)
    {
        (int Value, int Min, int Max, string Label)[] candidates =
        [
            (value.ButtonRadius, 0, 32, "按钮圆角"),
            (value.InputRadius, 0, 32, "输入框圆角"),
            (value.CardRadius, 0, 40, "卡片圆角"),
            (value.OverlayRadius, 0, 40, "弹层圆角"),
            (value.MenuRadius, 0, 32, "菜单圆角"),
            (value.CheckboxRadius, 0, 12, "勾选框圆角"),
            (value.ControlHeight, 30, 48, "控件高度"),
            (value.ControlHeightSmall, 24, 40, "小控件高度"),
            (value.ControlHeightLarge, 36, 56, "大控件高度"),
            (value.BorderWidth, 1, 3, "描边宽度"),
            (value.FocusRingWidth, 1, 4, "焦点环宽度"),
            (value.IconSize, 12, 24, "图标尺寸"),
            (value.ButtonFontWeight, 400, 700, "按钮字重"),
            (value.HoverLift, 0, 4, "悬停抬升"),
            (value.MotionFast, 0, 400, "快速动效时长"),
            (value.MotionNormal, 0, 800, "常规动效时长"),
        ];
        foreach ((int candidate, int min, int max, string label) in candidates)
        {
            if (candidate < min || candidate > max)
            {
                throw AppError.BadAuthRequest($"{label}必须在 {min} 到 {max} 之间");
            }
        }
        if (value.ControlHeightSmall > value.ControlHeight || value.ControlHeight > value.ControlHeightLarge)
        {
            throw AppError.BadAuthRequest("控件高度须满足小号不大于标准、标准不大于大号");
        }
        if (value.MotionFast > value.MotionNormal)
        {
            throw AppError.BadAuthRequest("快速动效时长不能大于常规动效时长");
        }
        if (value.ShadowStyle is not ("none" or "soft" or "strong"))
        {
            throw AppError.BadAuthRequest("阴影风格无效");
        }
    }

    /// <summary>对应 Go: <c>validateAppearanceSkinText</c>。</summary>
    private static void ValidateSkinText(string value, string label, int maxRunes, bool required)
    {
        if (required && value.Length == 0)
        {
            throw AppError.BadAuthRequest(label + "不能为空");
        }
        if (RuneCount(value) > maxRunes)
        {
            throw AppError.BadAuthRequest($"{label}不能超过 {maxRunes} 个字符");
        }
        if (AppearanceText.HasControlChar(value))
        {
            throw AppError.BadAuthRequest(label + "不能包含控制字符");
        }
    }

    /// <summary>对应 Go: <c>activeAppearanceSkin</c>。</summary>
    public static AppearanceSkinTheme ActiveSkin(IReadOnlyList<AppearanceSkinTheme> themes, string id)
    {
        foreach (AppearanceSkinTheme skin in themes)
        {
            if (skin.ID == id)
            {
                return skin;
            }
        }
        return DefaultClassicSkin();
    }

    /// <summary>对应 Go 的 <c>utf8.RuneCountInString</c>。</summary>
    internal static int RuneCount(string value) => value.Length == 0 ? 0 : value.EnumerateRunes().Count();
}
