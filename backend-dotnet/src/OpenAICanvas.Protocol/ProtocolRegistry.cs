#nullable enable
using System.IO.Compression;
using System.Text.Json;

namespace OpenAICanvas.Protocol;

/// <summary>协议适配器元数据（写路径校验所需子集）。对应 Go: <c>protocol.Metadata</c>。</summary>
public sealed record ProtocolMetadata(
    string Id,
    string Name,
    string Vendor,
    IReadOnlyList<string> Categories,
    bool Enabled,
    string UnavailableReason,
    IReadOnlyList<string> LegacyAliases);

/// <summary>
/// 协议元数据注册表：内置协议 + 官方插件包（.yingce-plugin）的声明式元数据。
/// 对应 Go: <c>protocol.Builtins()</c> 与 <c>generation.LoadOfficialFallbackRegistry()</c>
/// 的元数据投影。
/// </summary>
/// <remarks>
/// 本注册表只承载<b>元数据</b>（解析协议 ID、能力类别、启用状态），供渠道模型保存的
/// 合同校验使用；声明式协议的请求执行引擎属阶段 4/10，另行移植。
/// Go 在插件目录缺失时 fallback 注册表为 nil（一切协议都不可解析）；
/// 生产部署始终携带 plugin-packages，因此这里合并内置与插件元数据，不做 nil 降级。
/// </remarks>
public sealed class ProtocolRegistry
{
    public const string CapabilityText = "text";
    public const string CapabilityImage = "image";
    public const string CapabilityVideo = "video";
    public const string CapabilityAudio = "audio";

    private static readonly Lazy<ProtocolRegistry> Instance = new(Load);

    private readonly Dictionary<string, ProtocolMetadata> _byId;
    private readonly Dictionary<string, ProtocolMetadata> _byAlias;

    private ProtocolRegistry(
        Dictionary<string, ProtocolMetadata> byId,
        Dictionary<string, ProtocolMetadata> byAlias)
    {
        _byId = byId;
        _byAlias = byAlias;
    }

    public static ProtocolRegistry Shared => Instance.Value;

    /// <summary>对应 Go: <c>Registry.Resolve</c>。先按 ID，再按历史别名。</summary>
    public bool TryResolve(string id, out ProtocolMetadata metadata)
    {
        string trimmed = id.Trim();
        if (_byId.TryGetValue(trimmed, out metadata!))
        {
            return true;
        }
        return _byAlias.TryGetValue(trimmed, out metadata!);
    }

    /// <summary>对应 Go: <c>protocolCapabilityFromMetadata</c>。类别列表首项即能力。</summary>
    public static string CapabilityOf(ProtocolMetadata metadata) =>
        metadata.Categories.Count == 0 ? "" : metadata.Categories[0];

    // ------------------------------------------------------------ 组装

    private static ProtocolRegistry Load()
    {
        Dictionary<string, ProtocolMetadata> byId = new(StringComparer.Ordinal);
        Dictionary<string, ProtocolMetadata> byAlias = new(StringComparer.Ordinal);

        foreach (ProtocolMetadata metadata in BuiltinMetadata())
        {
            byId[metadata.Id] = metadata;
            foreach (string alias in metadata.LegacyAliases)
            {
                byAlias[alias.Trim()] = metadata;
            }
        }

        string? directory = FindPluginPackageDir();
        if (directory is not null)
        {
            foreach (string packageFile in Directory.GetFiles(directory, "*.yingce-plugin"))
            {
                foreach (ProtocolMetadata metadata in LoadPluginMetadata(packageFile))
                {
                    byId[metadata.Id] = metadata;
                    foreach (string alias in metadata.LegacyAliases)
                    {
                        byAlias[alias.Trim()] = metadata;
                    }
                }
            }
        }

        return new ProtocolRegistry(byId, byAlias);
    }

    /// <summary>内置协议元数据。对应 Go: <c>protocol/builtin.go</c> 的 <c>metadata(...)</c> 调用。</summary>
    private static IEnumerable<ProtocolMetadata> BuiltinMetadata()
    {
        (string Id, string Name, string Vendor, string Capability)[] builtins =
        [
            ("chat-completion", "OpenAI Chat Completions", "OpenAI", CapabilityText),
            ("openai-response", "OpenAI Responses", "OpenAI", CapabilityText),
            ("claude-api", "Claude API", "Anthropic", CapabilityText),
            ("newapi", "OpenAI Videos", "OpenAI compatible", CapabilityVideo),
            ("newapi-channel-2", "NewAPI Video Generations", "NewAPI", CapabilityVideo),
            ("newapi-channel-1", "NewAPI 媒体任务", "NewAPI", CapabilityVideo),
            ("xai-video", "xAI 官方视频", "xAI", CapabilityVideo),
            ("volcengine-ark-video", "火山方舟视频", "Volcengine Ark", CapabilityVideo),
            ("volcengine-jimeng-video", "即梦官方视频", "Volcengine Jimeng", CapabilityVideo),
            ("gemini-veo", "Gemini Veo", "Google", CapabilityVideo),
            ("novita-video", "Novita 视频", "Novita", CapabilityVideo),
            ("minimax-video", "MiniMax 视频", "MiniMax", CapabilityVideo),
            ("agnes-video", "Agnes 视频", "Agnes AI", CapabilityVideo),
        ];
        foreach ((string id, string name, string vendor, string capability) in builtins)
        {
            yield return new ProtocolMetadata(
                Id: id,
                Name: name,
                Vendor: vendor,
                Categories: [capability],
                Enabled: true,
                UnavailableReason: "",
                LegacyAliases: []);
        }
    }

    /// <summary>
    /// 定位官方插件包目录。对应 Go: <c>generation.OfficialPluginPackageDir</c>。
    /// </summary>
    private static string? FindPluginPackageDir()
    {
        string configured = (Environment.GetEnvironmentVariable("CANVAS_OFFICIAL_PLUGIN_DIR") ?? "").Trim();
        if (configured.Length > 0)
        {
            return Directory.Exists(configured) ? configured : null;
        }

        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase) { "/app/plugin-packages" };
        foreach (string root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            string? current = root;
            for (int depth = 0; depth < 8 && current is not null; depth++)
            {
                candidates.Add(Path.Combine(current, "plugin-packages"));
                current = Path.GetDirectoryName(current);
            }
        }
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// 读取插件包内声明式 provider 的元数据。
    /// 对应 Go: <c>LoadInstalledProviders</c> + <c>normalizeManifestForProvider</c> 的元数据投影
    /// （provider.id 覆盖元数据 ID，provider.capabilities 覆盖类别）。
    /// </summary>
    private static IEnumerable<ProtocolMetadata> LoadPluginMetadata(string packageFile)
    {
        List<ProtocolMetadata> result = [];
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packageFile);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(item =>
                item.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return result;
            }
            using StreamReader reader = new(entry.Open());
            using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            JsonElement root = document.RootElement;

            string name = root.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
            string vendor = root.TryGetProperty("author", out JsonElement authorElement) ? authorElement.GetString() ?? "" : "";

            if (!root.TryGetProperty("contributes", out JsonElement contributes) ||
                !contributes.TryGetProperty("providers", out JsonElement providers) ||
                providers.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement provider in providers.EnumerateArray())
            {
                string providerId = provider.TryGetProperty("id", out JsonElement pid) ? pid.GetString() ?? "" : "";
                if (providerId.Length == 0)
                {
                    continue;
                }
                List<string> categories = [];
                if (provider.TryGetProperty("capabilities", out JsonElement capabilities) &&
                    capabilities.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement capability in capabilities.EnumerateArray())
                    {
                        string? value = capability.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            categories.Add(value);
                        }
                    }
                }
                result.Add(new ProtocolMetadata(
                    Id: providerId,
                    Name: name,
                    Vendor: vendor,
                    Categories: categories,
                    Enabled: true,
                    UnavailableReason: "",
                    LegacyAliases: []));
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            // 注意：Go 的 fallback 加载在任一包损坏时整体失败（registry 为 nil，全部协议不可解析）；
            // 这里选择跳过坏包继续加载其余元数据（行为差异已记入 PENDING-CONFIRMATIONS.md #7）。
        }
        return result;
    }
}
