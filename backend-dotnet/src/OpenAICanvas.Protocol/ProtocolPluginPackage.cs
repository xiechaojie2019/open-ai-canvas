#nullable enable

using System.IO.Compression;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 插件包（.yingce-plugin，zip 容器）的解析与校验。
/// 对应 Go: <c>protocol.PluginPackage</c> / <c>ParsePluginPackage</c>。
/// </summary>
/// <remarks>
/// 与 Go 的差异：Go 侧会拒绝符号链接条目；.NET 的 <see cref="ZipArchive"/> 没有符号链接
/// 概念（条目只是数据流），因此跳过该检查，路径穿越由白名单 + 全路径等价校验兜底。
/// </remarks>
public sealed class ProtocolPluginPackage
{
    public const long MaxPackageBytes = 16L << 20;
    public const long MaxManifestBytes = 512L << 10;
    public const long MaxEntryBytes = 8L << 20;
    private const int MaxFiles = 256;

    /// <summary>包内允许的顶层前缀（与 Go 的 allowedPrefixes 一致）。</summary>
    private static readonly string[] AllowedPrefixes =
        ["manifest.json", "web/", "backend/", "assets/", "docs/", "README.md", "LICENSE"];

    public required Manifest Manifest { get; init; }

    /// <summary>manifest.json 的原始字节（写入注册表 / 交给执行器加载）。</summary>
    public required byte[] ManifestRaw { get; init; }

    /// <summary>包内文件（相对路径 → 内容）；目录条目不收录。</summary>
    public required Dictionary<string, byte[]> Files { get; init; }

    /// <summary>解析并校验插件包字节。对应 Go: <c>ParsePluginPackage</c>。</summary>
    public static ProtocolPluginPackage Parse(byte[] data)
    {
        if (data.Length == 0)
        {
            throw new InvalidOperationException("插件包为空");
        }
        if (data.Length > MaxPackageBytes)
        {
            throw new InvalidOperationException("插件包超过 16MB 上限");
        }

        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        byte[]? manifestRaw = null;
        try
        {
            using ZipArchive archive = new(new MemoryStream(data), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxFiles)
            {
                throw new InvalidOperationException("插件包文件数超过 256 上限");
            }
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = ValidateEntryName(entry.FullName);
                if (name.Length == 0)
                {
                    // 目录条目跳过。
                    continue;
                }
                if (files.ContainsKey(name))
                {
                    throw new InvalidOperationException($"插件包内重复文件：{name}");
                }
                if (entry.Length > MaxEntryBytes)
                {
                    throw new InvalidOperationException($"插件包文件超过 8MB 上限：{name}");
                }
                using MemoryStream buffer = new();
                using (Stream source = entry.Open())
                {
                    source.CopyTo(buffer);
                }
                if (buffer.Length > MaxEntryBytes)
                {
                    throw new InvalidOperationException($"插件包文件超过 8MB 上限：{name}");
                }
                if (name == "manifest.json")
                {
                    manifestRaw = buffer.ToArray();
                }
                else
                {
                    files[name] = buffer.ToArray();
                }
            }
        }
        catch (InvalidDataException error)
        {
            throw new InvalidOperationException($"插件包不是合法的 zip 容器：{error.Message}", error);
        }

        if (manifestRaw is null)
        {
            throw new InvalidOperationException("插件包缺少 manifest.json");
        }
        if (manifestRaw.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException("manifest.json 超过 512KB 上限");
        }

        Manifest manifest;
        try
        {
            // Decode 内部含 Validate；此处不 Normalize（由 LoadInstalledProviders 决定）。
            manifest = ProtocolManifestCodec.Decode(manifestRaw);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException($"manifest.json 校验失败：{error.Message}", error);
        }

        ValidateRuntimeEntries(manifest, files);
        return new ProtocolPluginPackage
        {
            Manifest = manifest,
            ManifestRaw = manifestRaw,
            Files = files,
        };
    }

    /// <summary>条目名白名单 + 路径穿越校验。返回空字符串表示目录条目。</summary>
    private static string ValidateEntryName(string rawName)
    {
        string name = rawName.Trim().Replace(Path.DirectorySeparatorChar, '/');
        if (name.Length == 0 || name.EndsWith('/'))
        {
            return "";
        }
        if (name.StartsWith('/'))
        {
            throw new InvalidOperationException($"插件包内出现绝对路径条目：{rawName}");
        }
        if (name.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"插件包内出现非法路径条目：{rawName}");
        }
        // 全路径等价校验：展开成绝对路径再回到相对形式，必须与原名一致。
        const string root = "/pkg-root";
        string full = Path.GetFullPath(Path.Combine(root, name));
        string normalized = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
        if (normalized != name)
        {
            throw new InvalidOperationException($"插件包内出现非法路径条目：{rawName}");
        }
        if (!AllowedPrefixes.Any(prefix => name == prefix || name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"插件包内出现不允许的条目：{rawName}");
        }
        return name;
    }

    /// <summary>包内相对路径的清洗校验（存在性由调用方检查）。</summary>
    private static string CleanRelative(string candidate)
    {
        const string root = "/pkg-root";
        string full = Path.GetFullPath(Path.Combine(root, candidate));
        return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>runtime 声明与包文件的交叉校验。对应 Go: <c>validateRuntimeEntries</c>。</summary>
    private static void ValidateRuntimeEntries(Manifest manifest, Dictionary<string, byte[]> files)
    {
        string backend = manifest.Runtime.Backend.Trim();
        string backendEntry = manifest.Runtime.BackendEntry.Trim();
        string web = manifest.Runtime.Web.Trim();

        if (backend is "rpc" or "wasm")
        {
            if (backendEntry.Length == 0)
            {
                throw new InvalidOperationException("runtime.backendEntry 不能为空");
            }
            if (!backendEntry.StartsWith("backend/", StringComparison.Ordinal)
                || CleanRelative(backendEntry) != backendEntry
                || !files.ContainsKey(backendEntry))
            {
                throw new InvalidOperationException($"runtime.backendEntry 必须指向包内 backend/ 下的真实文件：{backendEntry}");
            }
            if (backend == "wasm" && !backendEntry.EndsWith(".wasm", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("wasm 后端的 backendEntry 必须是 .wasm 文件");
            }
        }

        if (web.Length == 0)
        {
            if (manifest.Entry.Trim().Length != 0)
            {
                throw new InvalidOperationException("声明 metadata.entry 时必须提供 runtime.web 入口");
            }
            if (files.Keys.Any(key => key.StartsWith("web/", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("包含 web/ 资源时必须声明 runtime.web 入口");
            }
            return;
        }

        if (web is not ("sandbox" or "worker"))
        {
            throw new InvalidOperationException("runtime.web 只允许 sandbox 或 worker");
        }
        string entry = manifest.Entry.Trim();
        if (entry.Length == 0
            || !entry.StartsWith("web/", StringComparison.Ordinal)
            || CleanRelative(entry) != entry
            || !files.ContainsKey(entry))
        {
            throw new InvalidOperationException($"runtime.web 需要配合包内 web/ 下的 metadata.entry 使用：{entry}");
        }
    }
}
