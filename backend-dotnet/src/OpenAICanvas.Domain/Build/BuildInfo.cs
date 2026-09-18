using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenAICanvas.Domain.Build;

/// <summary>
/// 构建信息，对应 Go 的 <c>internal/buildinfo</c>。
/// </summary>
/// <remarks>
/// 字段名与 Go 版完全一致（<c>version</c> / <c>commit</c> / <c>buildTime</c> / <c>goVersion</c>）。
/// <para>
/// <c>goVersion</c> 为契约兼容而保留：Go 版填 <c>runtime.Version()</c>，
/// .NET 版填 .NET 运行时版本（如 <c>8.0.27</c>）。前端只把它当展示字段。
/// </para>
/// </remarks>
public sealed class BuildInfo
{
    public string Version { get; init; } = "dev";

    public string Commit { get; init; } = "unknown";

    public string BuildTime { get; init; } = "unknown";

    public string GoVersion { get; init; } = string.Empty;
}

/// <summary>对应 Go: <c>buildinfo.Current()</c>。</summary>
public static class BuildInfoProvider
{
    /// <summary>由构建脚本通过 <c>-p:Version=</c> 等注入；未注入时回落到 Go 版同样的默认值。</summary>
    public static string Version { get; set; } = "dev";

    public static string Commit { get; set; } = "unknown";

    public static string BuildTime { get; set; } = "unknown";

    public static BuildInfo Current() => new()
    {
        Version = Normalized(Version, "dev"),
        Commit = Normalized(Commit, "unknown"),
        BuildTime = Normalized(BuildTime, "unknown"),
        GoVersion = RuntimeVersion(),
    };

    private static string Normalized(string? value, string fallback)
    {
        string trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    private static string RuntimeVersion()
    {
        string framework = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? string.Empty;
        string version = Environment.Version.ToString();
        return framework.Length > 0
            ? $"{framework} {version} ({RuntimeInformation.RuntimeIdentifier})"
            : $".NET {version} ({RuntimeInformation.RuntimeIdentifier})";
    }
}
