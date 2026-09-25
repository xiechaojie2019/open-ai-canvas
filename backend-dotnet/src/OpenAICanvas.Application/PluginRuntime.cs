#nullable enable

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Application;

/// <summary>插件管理视图（manifest + 安装来源 + 状态）。对应 Go: <c>PluginView</c>。</summary>
public sealed class PluginView
{
    [JsonPropertyName("manifest")] public PluginManifestView Manifest { get; set; } = new();
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("fileName")] [Domain.Serialization.GoOmitEmpty] public string FileName { get; set; } = "";
    [JsonPropertyName("package")] [Domain.Serialization.GoOmitEmpty] public byte[]? Package { get; set; }
    [JsonPropertyName("sha256")] [Domain.Serialization.GoOmitEmpty] public string Sha256 { get; set; } = "";
    [JsonPropertyName("installedAt")] public DateTime InstalledAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("error")] [Domain.Serialization.GoOmitEmpty] public string Error { get; set; } = "";
    [JsonPropertyName("management")] public PluginManagementView? Management { get; set; }
}

/// <summary>插件清单的管理面投影。对应 Go: <c>PluginManifestView</c>。</summary>
public sealed class PluginManifestView
{
    [JsonPropertyName("apiVersion")] public string APIVersion { get; set; } = "";
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("entry")] [Domain.Serialization.GoOmitEmpty] public string Entry { get; set; } = "";
    [JsonPropertyName("surfaces")] public List<string> Surfaces { get; set; } = [];
    [JsonPropertyName("description")] [Domain.Serialization.GoOmitEmpty] public string Description { get; set; } = "";
    [JsonPropertyName("documentation")] [Domain.Serialization.GoOmitEmpty] public string Documentation { get; set; } = "";
    [JsonPropertyName("author")] [Domain.Serialization.GoOmitEmpty] public string Author { get; set; } = "";
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = [];
    [JsonPropertyName("trusted")] public bool Trusted { get; set; }
    [JsonPropertyName("runtime")] [Domain.Serialization.GoOmitEmpty] public PluginRuntimeView? Runtime { get; set; }
    [JsonPropertyName("configuration")] [Domain.Serialization.GoOmitEmpty] public ManifestConfiguration? Configuration { get; set; }
    [JsonPropertyName("contributes")] public ManifestContributions Contributes { get; set; } = new();
}

public sealed class PluginRuntimeView
{
    [JsonPropertyName("backend")] [Domain.Serialization.GoOmitEmpty] public string Backend { get; set; } = "";
    [JsonPropertyName("backendEntry")] [Domain.Serialization.GoOmitEmpty] public string BackendEntry { get; set; } = "";
    [JsonPropertyName("web")] [Domain.Serialization.GoOmitEmpty] public string Web { get; set; } = "";
}

/// <summary>插件管理策略视图。对应 Go: <c>PluginManagementView</c>。</summary>
public sealed class PluginManagementView
{
    [JsonPropertyName("origin")] public string Origin { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("activationScope")] public string ActivationScope { get; set; } = "";
    [JsonPropertyName("configurationScope")] public string ConfigurationScope { get; set; } = "";
}

/// <summary>插件注册表持久化记录。对应 Go: <c>pluginRegistryRecord</c>。</summary>
public sealed class PluginRegistryRecord
{
    [JsonPropertyName("id")] public string ID { get; set; } = "";
    [JsonPropertyName("manifest")] public byte[] Manifest { get; set; } = [];
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("fileName")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }
    [JsonPropertyName("packagePath")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackagePath { get; set; }
    [JsonPropertyName("packageSha256")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageSha256 { get; set; }
    [JsonPropertyName("installedAt")] public DateTime InstalledAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

internal sealed class PluginRecord
{
    public PluginRegistryRecord Registry { get; set; } = new();
    public Manifest Manifest { get; set; } = new();
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// 插件运行时：注册表加载、安装/卸载/启停与包缓存。
/// 对应 Go: <c>internal/app/plugin_runtime.go</c> + <c>plugin_registry.go</c>。
/// </summary>
public sealed class PluginRuntime
{
    private static readonly string[] BuiltInSources = ["bundled", "official", "system"];

    private readonly object _lock = new();
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly Repository _repository;
    private readonly string _registryPath;
    private readonly string _packageDir;
    private Dictionary<string, PluginRecord> _records = new(StringComparer.Ordinal);
    private readonly PaymentRegistry _paymentRegistry = new();

    public PluginRuntime(Repository? repository, string? dataDir)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        string root = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir;
        _registryPath = Path.Combine(root, "plugin_registry.json");
        _packageDir = Path.Combine(root, "plugin-packages");
    }

    /// <summary>当前官方 RPC 支付适配器快照；插件重载时原位更新。</summary>
    public PaymentRegistry PaymentRegistry => _paymentRegistry;

    /// <summary>当前可执行协议适配器快照（含不可用项，供目录展示）。</summary>
    public ProtocolAdapterRegistry RegistrySnapshot()
    {
        lock (_lock)
        {
            ProtocolAdapterRegistry registry = new();
            foreach (PluginRecord record in _records.Values.OrderBy(item => item.Manifest.Metadata.ID, StringComparer.Ordinal))
            {
                try
                {
                    foreach (IProtocolAdapter adapter in ProtocolManifestCodec.LoadInstalledProviders(
                                 record.Registry.Manifest, resolve: HostAdapterResolver))
                    {
                        registry.Register(adapter);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
                {
                    // 快照构建遇到坏插件时跳过（状态已在 reload 时标记 invalid）。
                }
            }
            return registry;
        }
    }

    /// <summary>宿主内置适配器解析：协议插件的 host: 后端。</summary>
    private IProtocolAdapter? HostAdapterResolver(string name) => null;

    /// <summary>
    /// 启动加载：官方目录扫描 → 包缓存 → bundled 清单合并。对应 Go: <c>Bootstrap</c>。
    /// 与 Go 的差异：官方目录缺失时降级为空注册表继续启动（不硬失败）。
    /// </summary>
    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, PluginRegistryRecord> byID = new(StringComparer.Ordinal);
        List<PluginRegistryRecord> stored = ReadRegistry();
        foreach (PluginRegistryRecord record in stored)
        {
            byID[record.ID] = record;
        }

        string? officialDir = ProtocolPluginPackageDir.Find();
        HashSet<string> builtInIDs = new(StringComparer.Ordinal);
        if (officialDir is not null)
        {
            foreach (string packageFile in Directory.GetFiles(officialDir, "*.yingce-plugin").OrderBy(item => item, StringComparer.Ordinal))
            {
                byte[] packageBytes = await File.ReadAllBytesAsync(packageFile, cancellationToken).ConfigureAwait(false);
                ProtocolPluginPackage package;
                try
                {
                    package = ProtocolPluginPackage.Parse(packageBytes);
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
                {
                    continue;
                }
                string pluginID = package.Manifest.Metadata.ID.Trim();
                if (pluginID.StartsWith("host:", StringComparison.Ordinal))
                {
                    continue;
                }
                // 非支付插件先做执行器预检，坏包不进注册表。
                try
                {
                    ProtocolManifestCodec.LoadInstalledProviders(package.ManifestRaw, resolve: HostAdapterResolver);
                }
                catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
                {
                    continue;
                }

                string sha = PluginHash(packageBytes);
                string cachedPath = Path.Combine(_packageDir, sha + ".yingce-plugin");
                if (!File.Exists(cachedPath))
                {
                    WriteFileAtomic(cachedPath, packageBytes);
                }

                builtInIDs.Add(pluginID);
                DateTime now = DateTime.UtcNow;
                PluginRegistryRecord? existing = byID.TryGetValue(pluginID, out PluginRegistryRecord? found) ? found : null;
                byte[] manifestRaw = package.ManifestRaw;
                if (existing is not null && existing.Manifest.Length > 0)
                {
                    try
                    {
                        Manifest previous = ProtocolManifestCodec.Decode(existing.Manifest);
                        Manifest current = package.Manifest;
                        current.Metadata.Enabled = previous.Metadata.Enabled;
                        manifestRaw = ProtocolManifestCodec.Encode(current);
                    }
                    catch (InvalidOperationException)
                    {
                        // 损坏的旧注册表状态不能覆盖官方包的有效清单。
                    }
                }
                PluginRegistryRecord record = new()
                {
                    ID = pluginID,
                    Manifest = manifestRaw,
                    Source = "official",
                    FileName = Path.GetFileName(packageFile),
                    PackagePath = cachedPath,
                    PackageSha256 = sha,
                    InstalledAt = existing?.InstalledAt ?? now,
                    UpdatedAt = now,
                };
                byID[pluginID] = record;
            }
        }

        // bundled 清单：工作流 + 支付（官方目录已提供同名包时跳过 bundled 支付记录）。
        List<Manifest> bundled = [BundledWorkflowManifest()];
        foreach (Manifest payment in BundledPaymentManifests())
        {
            string paymentID = payment.Metadata.ID.Trim();
            bool officialCovered = byID.TryGetValue(paymentID, out PluginRegistryRecord? officialRecord)
                && !string.IsNullOrEmpty(officialRecord.PackagePath);
            if (!officialCovered)
            {
                bundled.Add(payment);
            }
            builtInIDs.Add(paymentID);
        }
        foreach (Manifest manifest in bundled)
        {
            string pluginID = manifest.Metadata.ID.Trim();
            builtInIDs.Add(pluginID);
            DateTime now = DateTime.UtcNow;
            PluginRegistryRecord? existing = byID.TryGetValue(pluginID, out PluginRegistryRecord? found) ? found : null;
            Manifest bundledManifest = manifest;
            if (existing is not null && existing.Manifest.Length > 0)
            {
                try
                {
                    Manifest previous = ProtocolManifestCodec.Decode(existing.Manifest);
                    bundledManifest.Metadata.Enabled = previous.Metadata.Enabled;
                }
                catch (InvalidOperationException)
                {
                    // 损坏的旧注册表状态不能覆盖内置回退清单。
                }
            }
            byID[pluginID] = new PluginRegistryRecord
            {
                ID = pluginID,
                Manifest = ProtocolManifestCodec.Encode(bundledManifest),
                Source = "bundled",
                InstalledAt = existing?.InstalledAt ?? now,
                UpdatedAt = now,
            };
        }

        foreach (string key in byID.Keys.Where(id =>
                     BuiltInSources.Contains((byID[id].Source ?? "").Trim(), StringComparer.Ordinal)
                     && !builtInIDs.Contains(id)).ToList())
        {
            byID.Remove(key);
        }

        List<PluginRegistryRecord> records = byID.Values
            .OrderBy(record => record.ID, StringComparer.Ordinal).ToList();
        WriteRegistry(records);
        await ReloadAsync(records, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按清单 ID 排序的插件视图列表。对应 Go: <c>ListPlugins</c>。</summary>
    public List<PluginView> List()
    {
        lock (_lock)
        {
            return _records.Values
                .OrderBy(record => record.Manifest.Metadata.ID, StringComparer.Ordinal)
                .Select(ToView)
                .ToList();
        }
    }

    private static PluginView ToView(PluginRecord record)
    {
        Manifest metadata = record.Manifest;
        return new PluginView
        {
            Manifest = new PluginManifestView
            {
                APIVersion = metadata.APIVersion,
                ID = metadata.Metadata.ID,
                Name = metadata.Metadata.Name,
                Version = metadata.Metadata.Version,
                Entry = metadata.Entry,
                Surfaces = metadata.Surfaces,
                Description = metadata.Metadata.Description,
                Documentation = metadata.Metadata.Documentation,
                Author = metadata.Metadata.Vendor,
                Permissions = metadata.Permissions,
                Trusted = metadata.Metadata.LegacyAliases.Count > 0,
                Runtime = new PluginRuntimeView
                {
                    Backend = metadata.Runtime.Backend,
                    BackendEntry = metadata.Runtime.BackendEntry,
                    Web = metadata.Runtime.Web,
                },
                Configuration = metadata.Configuration,
                Contributes = metadata.Contributes,
            },
            Source = record.Registry.Source,
            FileName = record.Registry.FileName ?? "",
            Sha256 = record.Registry.PackageSha256 ?? "",
            InstalledAt = record.Registry.InstalledAt,
            UpdatedAt = record.Registry.UpdatedAt,
            Status = record.Status,
            Error = record.Error,
        };
    }

    /// <summary>重建内存注册表。对应 Go: <c>reload</c>。</summary>
    private void Reload(List<PluginRegistryRecord> records)
    {
        Dictionary<string, PluginRecord> next = new(StringComparer.Ordinal);
        List<IPaymentProvider> paymentProviders = [];
        foreach (PluginRegistryRecord record in records)
        {
            PluginRecord entry = new() { Registry = record };
            if (record.Manifest.Length > ProtocolPluginPackage.MaxManifestBytes)
            {
                entry.Status = "invalid";
                entry.Error = "manifest.json 超过 512KB 上限";
                next[record.ID] = entry;
                continue;
            }
            Manifest manifest;
            try
            {
                manifest = ProtocolManifestCodec.Decode(record.Manifest);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                entry.Status = "invalid";
                entry.Error = error.Message;
                next[record.ID] = entry;
                continue;
            }
            entry.Manifest = manifest;

            bool isPaymentOnly = manifest.Contributes.Providers.Count == 0
                && manifest.Contributes.PaymentProviders.Count > 0;
            if (isPaymentOnly)
            {
                // 支付插件走独立 RPC 进程，不进通用协议注册表。
                string backend = manifest.Runtime.Backend.Trim();
                if (record.Source.Trim() == "uploaded")
                {
                    entry.Status = "invalid";
                    entry.Error = "上传支付插件不能使用系统支付适配器";
                }
                else if (backend != "rpc")
                {
                    entry.Status = "invalid";
                    entry.Error = "支付插件必须声明 rpc 后端";
                }
                else if (!manifest.Metadata.Enabled)
                {
                    entry.Status = "disabled";
                }
                else
                {
                    try
                    {
                        paymentProviders.AddRange(LoadPaymentProviders(record, manifest));
                        entry.Status = "enabled";
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                                  or InvalidOperationException or NotSupportedException
                                                  or ArgumentException)
                    {
                        entry.Status = "invalid";
                        entry.Error = error.Message;
                    }
                }
                next[record.ID] = entry;
                continue;
            }

            try
            {
                ProtocolManifestCodec.LoadInstalledProviders(record.Manifest, resolve: HostAdapterResolver);
                entry.Status = "enabled";
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                entry.Status = "invalid";
                entry.Error = error.Message;
            }
            next[record.ID] = entry;
        }

        lock (_lock)
        {
            _records = next;
        }
        _paymentRegistry.Replace(paymentProviders.ToArray());
    }

    private IEnumerable<IPaymentProvider> LoadPaymentProviders(
        PluginRegistryRecord record, Manifest manifest)
    {
        string digest = (record.PackageSha256 ?? "").Trim().ToLowerInvariant();
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("支付插件包摘要无效");
        }

        string packagePath = Path.IsPathRooted(record.PackagePath ?? "")
            ? record.PackagePath!
            : Path.Combine(_packageDir, Path.GetFileName(record.PackagePath ?? ""));
        byte[] packageBytes = File.ReadAllBytes(packagePath);
        if (!string.Equals(PluginHash(packageBytes), digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("支付插件包完整性校验失败");
        }

        ProtocolPluginPackage package = ProtocolPluginPackage.Parse(packageBytes);
        if (!string.Equals(package.Manifest.Metadata.ID, manifest.Metadata.ID, StringComparison.Ordinal)
            || !string.Equals(package.Manifest.Runtime.Backend, "rpc", StringComparison.Ordinal)
            || !string.Equals(package.Manifest.Runtime.BackendEntry, manifest.Runtime.BackendEntry, StringComparison.Ordinal)
            || !package.Manifest.Contributes.PaymentProviders.Select(item => item.ID)
                .SequenceEqual(manifest.Contributes.PaymentProviders.Select(item => item.ID), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("支付插件运行清单与包内容不匹配");
        }

        string runtimeDir = MaterializePaymentBackend(digest, package);
        return manifest.Contributes.PaymentProviders.Select(contribution =>
            new RpcPaymentProvider(
                PaymentPluginManifests.DescriptorFromManifest(manifest, contribution),
                runtimeDir,
                manifest.Runtime.BackendEntry));
    }

    private string MaterializePaymentBackend(string digest, ProtocolPluginPackage package)
    {
        string runtimeRoot = Path.Combine(_packageDir, "runtime");
        string target = Path.Combine(runtimeRoot, digest);
        string entry = package.Manifest.Runtime.BackendEntry;
        if (PaymentRuntimeReady(target, digest, entry))
        {
            return target;
        }

        Directory.CreateDirectory(runtimeRoot);
        SetPrivateDirectory(runtimeRoot);
        string temporary = Path.Combine(runtimeRoot, ".payment-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        SetPrivateDirectory(temporary);
        try
        {
            foreach ((string name, byte[] content) in package.Files)
            {
                if (!name.StartsWith("backend/", StringComparison.Ordinal))
                {
                    continue;
                }

                string destination = Path.GetFullPath(Path.Combine(
                    temporary, name.Replace('/', Path.DirectorySeparatorChar)));
                string relative = Path.GetRelativePath(temporary, destination);
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("支付插件运行文件路径无效");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                SetPrivateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, content);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destination,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            string marker = Path.Combine(temporary, ".ready");
            File.WriteAllText(marker, digest + "\n", Encoding.ASCII);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(temporary, target);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }

        if (!PaymentRuntimeReady(target, digest, entry))
        {
            throw new InvalidOperationException("支付插件运行文件未完整写入");
        }
        return target;
    }

    private static bool PaymentRuntimeReady(string root, string digest, string entry)
    {
        try
        {
            string marker = Path.Combine(root, ".ready");
            string readyDigest = File.ReadAllText(marker, Encoding.ASCII).Trim();
            string executable = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            string relative = Path.GetRelativePath(root, executable);
            return string.Equals(readyDigest, digest, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(executable)
                && !Directory.Exists(executable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private Task ReloadAsync(List<PluginRegistryRecord> records, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reload(records);
        return Task.CompletedTask;
    }

    /// <summary>管理员安装插件包。对应 Go: <c>InstallPlugin</c>。</summary>
    public async Task<PluginView> InstallAsync(byte[] packageBytes, string fileName, CancellationToken cancellationToken = default)
    {
        if (packageBytes.Length > ProtocolPluginPackage.MaxPackageBytes)
        {
            throw AppError.New(400, "插件包超过 16MB 上限");
        }
        ProtocolPluginPackage package = ProtocolPluginPackage.Parse(packageBytes);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string pluginID = package.Manifest.Metadata.ID.Trim();
            if (pluginID.StartsWith("host:", StringComparison.Ordinal))
            {
                throw AppError.New(400, "上传插件不能使用宿主内置执行器");
            }
            try
            {
                ProtocolManifestCodec.LoadInstalledProviders(package.ManifestRaw, resolve: HostAdapterResolver);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                throw AppError.New(400, error.Message);
            }

            List<PluginRegistryRecord> records = ReadRegistry();
            PluginRegistryRecord? existing = records.FirstOrDefault(record => record.ID == pluginID);
            if (existing is not null
                && BuiltInSources.Contains(existing.Source.Trim(), StringComparer.Ordinal)
                && !IsPaymentManifest(package.Manifest))
            {
                throw AppError.New(400, $"内置插件 {pluginID} 不能通过上传覆盖");
            }

            string sha = PluginHash(packageBytes);
            string packagePath = Path.Combine(_packageDir, sha + ".yingce-plugin");
            WriteFileAtomic(packagePath, packageBytes);

            PluginRegistryRecord record = new()
            {
                ID = pluginID,
                Manifest = package.ManifestRaw,
                Source = "uploaded",
                FileName = string.IsNullOrWhiteSpace(fileName) ? pluginID + ".yingce-plugin" : fileName,
                PackagePath = packagePath,
                PackageSha256 = sha,
                InstalledAt = existing?.InstalledAt ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            List<PluginRegistryRecord> next = records.Where(item => item.ID != pluginID).ToList();
            next.Add(record);
            next.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            WriteRegistry(next);
            try
            {
                await ReloadAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                WriteRegistry(records);
                Reload(records);
                if (File.Exists(packagePath) && existing?.PackagePath != packagePath)
                {
                    File.Delete(packagePath);
                }
                throw;
            }
            PluginView view = ToView(_records[pluginID]);
            view.Package = packageBytes;
            return view;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>修改系统插件的清单启用位并重载运行时。支付停用后不会在重启时被官方包恢复。</summary>
    public async Task SetEnabledAsync(string pluginID, bool enabled, CancellationToken cancellationToken = default)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PluginRegistryRecord> records = ReadRegistry();
            PluginRegistryRecord record = records.FirstOrDefault(item => item.ID == pluginID.Trim())
                ?? throw AppError.NotFound($"插件 {pluginID} 不存在");
            Manifest manifest = ProtocolManifestCodec.Decode(record.Manifest);
            manifest.Metadata.Enabled = enabled;
            record.Manifest = ProtocolManifestCodec.Encode(manifest);
            record.UpdatedAt = DateTime.UtcNow;
            WriteRegistry(records);
            await ReloadAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }


    /// <summary>管理员卸载插件。对应 Go: <c>UninstallPlugin</c>。</summary>
    public async Task<string?> UninstallAsync(string pluginID, CancellationToken cancellationToken = default)
    {
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PluginRegistryRecord> records = ReadRegistry();
            PluginRegistryRecord? record = records.FirstOrDefault(item => item.ID == pluginID);
            if (record is null)
            {
                throw AppError.NotFound($"插件 {pluginID} 不存在");
            }
            if (BuiltInSources.Contains(record.Source.Trim(), StringComparer.Ordinal))
            {
                throw AppError.New(400, $"内置插件 {pluginID} 不能卸载，可停用该插件");
            }
            List<PluginRegistryRecord> next = records.Where(item => item.ID != pluginID).ToList();
            WriteRegistry(next);
            await ReloadAsync(next, cancellationToken).ConfigureAwait(false);
            string? packagePath = record.PackagePath;
            if (packagePath is not null && File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
            return packagePath;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>读取插件包字节（下载用）。对应 Go: <c>PluginPackageBytes</c>。</summary>
    public async Task<byte[]> PackageBytesAsync(string pluginID, CancellationToken cancellationToken = default)
    {
        List<PluginRegistryRecord> records = ReadRegistry();
        PluginRegistryRecord? record = records.FirstOrDefault(item => item.ID == pluginID)
            ?? throw AppError.NotFound($"插件 {pluginID} 不存在");
        if (string.IsNullOrEmpty(record.PackagePath) || !File.Exists(record.PackagePath))
        {
            throw AppError.NotFound($"插件 {pluginID} 没有可下载的包文件");
        }
        return await File.ReadAllBytesAsync(record.PackagePath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPaymentManifest(Manifest manifest) =>
        manifest.Contributes.PaymentProviders.Count > 0;

    internal static string PluginHash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private List<PluginRegistryRecord> ReadRegistry()
    {
        if (!File.Exists(_registryPath))
        {
            return [];
        }
        try
        {
            byte[] bytes = File.ReadAllBytes(_registryPath);
            List<PluginRegistryRecord>? records = JsonSerializer.Deserialize<List<PluginRegistryRecord>>(bytes);
            return records ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteRegistry(List<PluginRegistryRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(records,
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        WriteFileAtomic(_registryPath, bytes);
    }

    private static void WriteFileAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    // ---------------------------------------------------------- bundled 清单

    /// <summary>内置 RunningHub 工作流清单。对应 Go: <c>bundledWorkflowManifest</c>。</summary>
    internal static Manifest BundledWorkflowManifest()
    {
        string id = "runninghub-workflow-provider";
        List<ManifestWorkflow> workflows = [];
        foreach (string capability in (string[])["image", "video", "audio"])
        {
            workflows.Add(new ManifestWorkflow
            {
                ID = id + "-" + capability,
                Label = "RunningHub 工作流 · " + capability,
                ProviderID = id,
                Capability = capability,
                Parameters = [],
            });
        }
        return new Manifest
        {
            APIVersion = "yingce.plugin/v1",
            Metadata = new Metadata
            {
                ID = id,
                Version = "1.0.0",
                Name = "RunningHub 工作流",
                Vendor = "内置工作流",
                Documentation = "# RunningHub 工作流\n\n## 宿主运行时合同\n\n该工作流能力由插件运行时统一管理。",
                Enabled = false,
                Installable = true,
            },
            Surfaces = ["node", "settings"],
            Runtime = new ManifestRuntime { Backend = "trusted-backend" },
            Permissions = ["generation.run", "external.open"],
            Contributes = new ManifestContributions { Workflows = workflows },
        };
    }

    /// <summary>
    /// 内置支付清单的完整 Manifest 组装。投影字段来自 <see cref="PaymentPluginManifests"/>，
    /// 完整清单属于插件系统阶段 10。
    /// </summary>
    internal static List<Manifest> BundledPaymentManifests()
    {
        List<Manifest> manifests = [];
        foreach (PaymentPluginManifest payment in PaymentPluginManifests.Bundled())
        {
            manifests.Add(new Manifest
            {
                APIVersion = "yingce.plugin/v1",
                Metadata = new Metadata
                {
                    ID = payment.PluginID,
                    Version = payment.PluginVersion,
                    Name = payment.Name,
                    Vendor = payment.Vendor,
                    Description = payment.Description,
                    Documentation = "# " + payment.Name + "\n\n系统宿主支付适配器。后端负责密钥、验签、查单、关单和对账。",
                    Enabled = payment.Enabled,
                    Installable = payment.Installable,
                },
                Surfaces = ["wallet", "settings"],
                Runtime = new ManifestRuntime { Backend = payment.Runtime, BackendEntry = "backend/provider" },
                Permissions = ["payment.create", "payment.query", "payment.close", "payment.reconcile"],
                Configuration = new ManifestConfiguration { Fields = payment.ConfigFields },
                Contributes = new ManifestContributions { PaymentProviders = [payment.Contribution] },
            });
        }
        return manifests;
    }
}
