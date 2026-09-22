#nullable enable

using System.IO.Compression;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 可执行协议适配器注册表。对应 Go: <c>protocol.Registry</c>。
/// 与 <see cref="ProtocolRegistry"/>（只读元数据投影）分离：本注册表承载实际可执行的
/// <see cref="IProtocolAdapter"/>，支持安装/卸载插件与按别名解析。
/// </summary>
public sealed class ProtocolAdapterRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IProtocolAdapter> _adapters = new(StringComparer.Ordinal);

    public ProtocolAdapterRegistry(params IProtocolAdapter[] adapters)
    {
        foreach (IProtocolAdapter adapter in adapters)
        {
            Register(adapter);
        }
    }

    /// <summary>对应 Go: <c>Registry.Register</c>。元数据必须有 ID 和版本，重复注册报错。</summary>
    public void Register(IProtocolAdapter adapter)
    {
        if (adapter is null)
        {
            throw new InvalidOperationException("protocol adapter is nil");
        }
        Metadata metadata = adapter.Metadata();
        string id = metadata.ID.Trim();
        string version = metadata.Version.Trim();
        if (id.Length == 0 || version.Length == 0)
        {
            throw new InvalidOperationException("protocol adapter metadata requires id and version");
        }
        lock (_lock)
        {
            if (_adapters.ContainsKey(id))
            {
                throw new InvalidOperationException($"protocol adapter \"{id}\" is already registered");
            }
            _adapters[id] = adapter;
        }
    }

    /// <summary>
    /// 对应 Go: <c>Registry.Unregister</c>。注册表只表示当前插件快照，持久化由调用方负责。
    /// </summary>
    public bool Unregister(string id)
    {
        lock (_lock)
        {
            return _adapters.Remove(id.Trim());
        }
    }

    public void RegisterManifest(byte[] data)
    {
        Register(ProtocolManifestCodec.LoadManifest(data));
    }

    /// <summary>对应 Go: <c>Registry.Get</c>。</summary>
    public IProtocolAdapter? Get(string id)
    {
        lock (_lock)
        {
            return _adapters.TryGetValue(id.Trim(), out IProtocolAdapter? adapter) ? adapter : null;
        }
    }

    /// <summary>对应 Go: <c>Registry.Resolve</c>。先按 ID，再按历史别名。</summary>
    public IProtocolAdapter? Resolve(string id)
    {
        IProtocolAdapter? direct = Get(id);
        if (direct is not null)
        {
            return direct;
        }
        string trimmed = id.Trim();
        lock (_lock)
        {
            foreach (IProtocolAdapter adapter in _adapters.Values)
            {
                foreach (string alias in adapter.Metadata().LegacyAliases)
                {
                    if (alias.Trim() == trimmed)
                    {
                        return adapter;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>对应 Go: <c>Registry.List</c>。过滤后按 ID 排序。</summary>
    public List<Metadata> List(string surface = "", string capability = "", bool includeUnavailable = false)
    {
        List<Metadata> items = [];
        lock (_lock)
        {
            foreach (IProtocolAdapter adapter in _adapters.Values)
            {
                Metadata metadata = adapter.Metadata();
                if (!includeUnavailable && (!metadata.Enabled || metadata.UnavailableReason.Length != 0))
                {
                    continue;
                }
                if (capability.Length != 0 && !metadata.Categories.Contains(capability))
                {
                    continue;
                }
                if (surface.Length != 0 && !metadata.Scopes.Contains(surface))
                {
                    continue;
                }
                items.Add(metadata);
            }
        }
        items.Sort((left, right) => string.Compare(left.ID, right.ID, StringComparison.Ordinal));
        return items;
    }

    /// <summary>对应 Go: <c>Registry.IsCapability</c>。</summary>
    public bool IsCapability(string id, string capability)
    {
        IProtocolAdapter? adapter = Resolve(id);
        if (adapter is null)
        {
            return false;
        }
        Metadata metadata = adapter.Metadata();
        return metadata.Enabled && metadata.UnavailableReason.Length == 0 && metadata.Categories.Contains(capability);
    }

    /// <summary>
    /// 从官方插件包目录构建注册表。对应 Go: <c>generation.LoadOfficialFallbackRegistry</c>。
    /// 与 Go 的差异：坏包跳过而不是整体失败（见 PENDING-CONFIRMATIONS.md #7）。
    /// </summary>
    public static ProtocolAdapterRegistry LoadOfficialFallbackRegistry(string? directory = null)
    {
        ProtocolAdapterRegistry registry = new();
        string? resolved = directory ?? ProtocolPluginPackageDir.Find();
        if (resolved is null)
        {
            return registry;
        }
        foreach (string packageFile in Directory.GetFiles(resolved, "*.yingce-plugin").OrderBy(item => item, StringComparer.Ordinal))
        {
            foreach (byte[] manifest in ReadPluginManifests(packageFile))
            {
                try
                {
                    foreach (IProtocolAdapter adapter in ProtocolManifestCodec.LoadInstalledProviders(
                                 manifest, resolve: name => null))
                    {
                        registry.Register(adapter);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
                {
                    // 跳过坏包继续加载其余插件。
                }
            }
        }
        return registry;
    }

    private static List<byte[]> ReadPluginManifests(string packageFile)
    {
        List<byte[]> manifests = [];
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packageFile);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using MemoryStream buffer = new();
                using (Stream source = entry.Open())
                {
                    source.CopyTo(buffer);
                }
                manifests.Add(buffer.ToArray());
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            // 包损坏或不可读：跳过。
        }
        return manifests;
    }
}

/// <summary>官方插件包目录定位。对应 Go: <c>generation.OfficialPluginPackageDir</c>。</summary>
public static class ProtocolPluginPackageDir
{
    public static string? Find()
    {
        string configured = (Environment.GetEnvironmentVariable("CANVAS_OFFICIAL_PLUGIN_DIR") ?? "").Trim();
        if (configured.Length != 0)
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
}

/// <summary>
/// 官方声明式接口映射与 fallback 注册表的懒加载入口。
/// 对应 Go: <c>generation.registry</c> 中的接口映射函数与 <c>LoadOfficialFallbackRegistry</c> 缓存。
/// </summary>
public static class ProtocolAdapterLookup
{
    private static readonly Lazy<ProtocolAdapterRegistry> Fallback = new(LoadFallback);

    /// <summary>官方 fallback 注册表（进程内单例，坏包跳过）。</summary>
    public static ProtocolAdapterRegistry OfficialFallback => Fallback.Value;

    /// <summary>
    /// 对应 Go: <c>DeclarativeProtocolAdapterForContext</c>：解析到且 Execution 为 declarative 才返回。
    /// </summary>
    public static IProtocolAdapter? DeclarativeAdapter(string id)
    {
        IProtocolAdapter? adapter = OfficialFallback.Resolve(id);
        return adapter is not null && adapter.Metadata().Execution == "declarative" ? adapter : null;
    }

    /// <summary>官方声明式视频接口映射。对应 Go: <c>OfficialDeclarativeVideoInterface</c>。</summary>
    public static string? OfficialDeclarativeVideoInterface(string interfaceType) => interfaceType.Trim() switch
    {
        "agnes-video" => "Agnes",
        "minimax-video" => "MiniMax",
        "gemini-veo" => "Gemini Veo",
        "novita-video" => "Novita",
        "newapi-channel-2" => "NewAPI Video Generations",
        "newapi-channel-1" => "NewAPI 媒体任务",
        "xai-video" => "xAI",
        "volcengine-ark-video" => "火山方舟",
        "volcengine-jimeng-video" => "即梦",
        "newapi-video" => "OpenAI Videos",
        _ => null,
    };

    /// <summary>官方声明式图片接口映射。对应 Go: <c>OfficialDeclarativeImageInterface</c>。</summary>
    public static string? OfficialDeclarativeImageInterface(string interfaceType) => interfaceType.Trim() switch
    {
        "gemini-image" => "Gemini Images",
        "openai-image" => "OpenAI Images",
        "grok-image" => "Grok Images",
        "volcengine-ark-image" => "火山方舟图片",
        "volcengine-jimeng-image" => "即梦图片",
        _ => null,
    };

    /// <summary>官方声明式音频接口映射。对应 Go: <c>OfficialDeclarativeAudioInterface</c>。</summary>
    public static string? OfficialDeclarativeAudioInterface(string interfaceType) => interfaceType.Trim() switch
    {
        "openai-audio" => "OpenAI Audio",
        "async-audio" => "异步音频",
        _ => null,
    };

    private static ProtocolAdapterRegistry LoadFallback() =>
        ProtocolAdapterRegistry.LoadOfficialFallbackRegistry();
}
