#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 清单的线格式编解码与安装入口。对应 Go: <c>internal/protocol/types.go</c> 的
/// <c>Manifest.MarshalJSON</c>/<c>UnmarshalJSON</c> 与 <c>manifest.go</c> 的
/// <c>LoadManifest</c>/<c>LoadInstalledPlugin</c>/<c>LoadInstalledProviders</c>/
/// <c>decodeManifest</c>/<c>loadDeclarativeManifest(Provider)</c>。
/// </summary>
/// <remarks>
/// 顶层 <c>id/name/version/author</c> 是线格式，运行时只认 <see cref="Metadata"/>，
/// 因此这里用独立的 wire DTO 做双向映射，而不是给 <see cref="Manifest"/> 挂 JSON 特性。
/// </remarks>
public static class ProtocolManifestCodec
{
    /// <summary>解析并校验清单。对应 Go: <c>decodeManifest</c>。</summary>
    /// <exception cref="InvalidOperationException">JSON 不合法或清单校验失败。</exception>
    public static Manifest Decode(ReadOnlySpan<byte> data)
    {
        ManifestReadWire wire;
        try
        {
            wire = JsonSerializer.Deserialize<ManifestReadWire>(data, ProtocolManifestJson.ReadOptions)
                ?? throw new InvalidOperationException("decode plugin manifest: unexpected null document");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"decode plugin manifest: {error.Message}", error);
        }

        Manifest manifest = new()
        {
            APIVersion = wire.APIVersion,
            Entry = wire.Entry,
            Surfaces = wire.Surfaces,
            Runtime = wire.Runtime,
            Permissions = wire.Permissions,
            Configuration = wire.Configuration,
            Contributes = wire.Contributes,
            Metadata = new Metadata
            {
                ID = wire.ID,
                Name = wire.Name,
                Version = wire.Version,
                Vendor = wire.Author,
                Description = wire.Description,
                Documentation = wire.Documentation,
                // Go 的 enabled 是 *bool，缺省视为启用。
                Enabled = wire.Enabled ?? true,
                Installable = wire.Installable,
            },
        };
        ProtocolManifestValidation.Validate(manifest);
        return manifest;
    }

    /// <summary>把运行时清单写回线格式。对应 Go: <c>Manifest.MarshalJSON</c>。</summary>
    public static byte[] Encode(Manifest manifest)
    {
        ManifestWriteWire wire = new()
        {
            APIVersion = manifest.APIVersion,
            ID = manifest.Metadata.ID,
            Name = manifest.Metadata.Name,
            Version = manifest.Metadata.Version,
            Author = manifest.Metadata.Vendor,
            Description = manifest.Metadata.Description,
            Documentation = manifest.Metadata.Documentation,
            Entry = manifest.Entry,
            Surfaces = manifest.Surfaces,
            Enabled = manifest.Metadata.Enabled,
            Installable = manifest.Metadata.Installable,
            Runtime = manifest.Runtime,
            Permissions = manifest.Permissions,
            Configuration = manifest.Configuration,
            Contributes = manifest.Contributes,
        };
        return JsonSerializer.SerializeToUtf8Bytes(wire, ProtocolManifestJson.GoWriteOptions);
    }

    /// <summary>加载单贡献点清单。对应 Go: <c>LoadManifest</c>。</summary>
    public static IProtocolAdapter LoadManifest(byte[] data) => LoadDeclarativeManifest(Decode(data));

    /// <summary>
    /// 加载一个插件包里的首个可执行 provider。对应 Go: <c>LoadInstalledPlugin</c>。
    /// </summary>
    public static IProtocolAdapter LoadInstalledPlugin(byte[] data, Func<string, IProtocolAdapter?>? resolve)
    {
        List<IProtocolAdapter> adapters = LoadInstalledProviders(data, resolve);
        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("plugin does not provide an executable provider");
        }
        return adapters[0];
    }

    /// <summary>
    /// 加载插件包里的全部 provider 贡献点。包是生命周期与权限单位，provider 是运行时索引里的
    /// 独立执行条目。对应 Go: <c>LoadInstalledProviders</c>。
    /// </summary>
    public static List<IProtocolAdapter> LoadInstalledProviders(byte[] data, Func<string, IProtocolAdapter?>? resolve)
    {
        Manifest manifest = Decode(data);
        string backend = manifest.Runtime.Backend.Trim();
        if (backend.StartsWith("host:", StringComparison.Ordinal))
        {
            if (manifest.Contributes.Providers.Count != 1)
            {
                throw new InvalidOperationException("host-backed plugin must declare exactly one provider");
            }
            if (resolve is null)
            {
                throw new InvalidOperationException($"plugin \"{manifest.Metadata.ID}\" requires a host execution engine");
            }
            string name = backend["host:".Length..];
            IProtocolAdapter? adapter = resolve(name);
            if (adapter is null)
            {
                throw new InvalidOperationException($"plugin host execution \"{name}\" is unavailable");
            }
            ProtocolManifestValidation.Normalize(manifest);
            return [new MetadataAdapter(manifest.Metadata, adapter)];
        }

        if (manifest.Contributes.Providers.Count == 0)
        {
            return [LoadDeclarativeManifest(manifest)];
        }

        List<IProtocolAdapter> result = new(manifest.Contributes.Providers.Count);
        for (int index = 0; index < manifest.Contributes.Providers.Count; index++)
        {
            result.Add(LoadDeclarativeManifestProvider(manifest, index));
        }
        return result;
    }

    /// <summary>对应 Go: <c>loadDeclarativeManifest</c>。</summary>
    public static IProtocolAdapter LoadDeclarativeManifest(Manifest manifest)
    {
        manifest.Runtime.Backend = "declarative";
        ProtocolManifestValidation.Normalize(manifest);
        return new ManifestAdapter(manifest);
    }

    /// <summary>对应 Go: <c>loadDeclarativeManifestProvider</c>。</summary>
    public static IProtocolAdapter LoadDeclarativeManifestProvider(Manifest manifest, int index)
    {
        manifest.Runtime.Backend = "declarative";
        ProtocolManifestValidation.NormalizeForProvider(manifest, index);
        return new ManifestAdapter(manifest);
    }

    /// <summary>读方向 wire：<c>enabled</c> 用可空类型区分「缺省」与 <c>false</c>。</summary>
    private sealed class ManifestReadWire
    {
        [JsonPropertyName("apiVersion")]
        public string APIVersion { get; set; } = "";

        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("author")]
        public string Author { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("documentation")]
        public string Documentation { get; set; } = "";

        [JsonPropertyName("entry")]
        public string Entry { get; set; } = "";

        [JsonPropertyName("surfaces")]
        public List<string> Surfaces { get; set; } = [];

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("installable")]
        public bool Installable { get; set; }

        [JsonPropertyName("runtime")]
        public ManifestRuntime Runtime { get; set; } = new();

        [JsonPropertyName("permissions")]
        public List<string> Permissions { get; set; } = [];

        [JsonPropertyName("configuration")]
        public ManifestConfiguration Configuration { get; set; } = new();

        [JsonPropertyName("contributes")]
        public ManifestContributions Contributes { get; set; } = new();
    }

    /// <summary>写方向 wire：字段顺序与 <c>omitempty</c> 与 Go 逐字对齐。</summary>
    private sealed class ManifestWriteWire
    {
        [JsonPropertyName("apiVersion")]
        public string APIVersion { get; set; } = "";

        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("author")]
        [GoOmitEmpty]
        public string Author { get; set; } = "";

        [JsonPropertyName("description")]
        [GoOmitEmpty]
        public string Description { get; set; } = "";

        [JsonPropertyName("documentation")]
        [GoOmitEmpty]
        public string Documentation { get; set; } = "";

        [JsonPropertyName("entry")]
        [GoOmitEmpty]
        public string Entry { get; set; } = "";

        [JsonPropertyName("surfaces")]
        [GoOmitEmpty]
        public List<string> Surfaces { get; set; } = [];

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("installable")]
        [GoOmitEmpty]
        public bool Installable { get; set; }

        [JsonPropertyName("runtime")]
        [GoOmitEmpty]
        public ManifestRuntime Runtime { get; set; } = new();

        [JsonPropertyName("permissions")]
        [GoOmitEmpty]
        public List<string> Permissions { get; set; } = [];

        [JsonPropertyName("configuration")]
        [GoOmitEmpty]
        public ManifestConfiguration Configuration { get; set; } = new();

        [JsonPropertyName("contributes")]
        public ManifestContributions Contributes { get; set; } = new();
    }
}
