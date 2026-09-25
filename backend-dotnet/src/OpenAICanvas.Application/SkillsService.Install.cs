#nullable enable

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application;

/// <summary>上传安装技能请求的表单字段。</summary>
public sealed class SkillInstallRequestDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; set; }
}

/// <summary>GitHub 技能安装请求。对应 Go: <c>SkillGitHubInstallRequest</c>。</summary>
public sealed class SkillGitHubInstallRequestDto
{
    [JsonPropertyName("url")] public string URL { get; set; } = "";
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("subdir")] public string Subdir { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("isPrivate")] public bool IsPrivate { get; set; }
    [JsonPropertyName("autoUpdate")] public bool AutoUpdate { get; set; }
}

/// <summary>
/// 技能包上传安装。解析、校验和归档均在进入数据库事务前完成。
/// </summary>
public sealed partial class SkillsService
{
    private const long MaxSkillPackageBytes = 20L << 20;
    private const int MaxSkillPackageFiles = 512;

    /// <summary>与 Go 的 SkillPackageUploadMaxBytes 一致：给 multipart 封装留 1MB 空间。</summary>
    public const long SkillPackageUploadMaxBytes = MaxSkillPackageBytes + (1L << 20);

    /// <summary>安装 Markdown 或 ZIP 技能。</summary>
    public async Task<SkillItemDto> InstallUploadAsync(
        string userId,
        string sourceType,
        byte[] data,
        SkillInstallRequestDto request,
        CancellationToken cancellationToken = default)
    {
        sourceType = (sourceType ?? "").Trim().ToLowerInvariant();
        if (sourceType.Length == 0)
        {
            throw AppError.BadAuthRequest("技能文件类型不能为空");
        }
        if (sourceType is not ("markdown" or "zip"))
        {
            throw AppError.BadAuthRequest("技能文件仅支持 Markdown 或 ZIP");
        }
        if (data.Length == 0 || data.LongLength > MaxSkillPackageBytes)
        {
            throw AppError.BadAuthRequest("技能文件大小必须在 1B-20MB 之间");
        }

        ImportedSkillArchive archive = sourceType == "markdown"
            ? ArchiveFromMarkdownPackage(data, request.Name, request.Description)
            : ArchiveFromZipPackage(data, "");

        return await CreateImportedSkillAsync(
            userId, archive, request.Name, request.Description, request.Tag, request.IsPrivate,
            sourceType, "", "", "", "", autoUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从已校验的归档创建技能及首个版本。</summary>
    private async Task<SkillItemDto> CreateImportedSkillAsync(
        string userId,
        ImportedSkillArchive archive,
        string? requestedName,
        string? requestedDescription,
        string? requestedTag,
        bool isPrivate,
        string sourceType,
        string sourceURL,
        string sourceRef,
        string sourceSubdir,
        string sourceCommit,
        bool autoUpdate,
        CancellationToken cancellationToken)
    {
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? archive.Name
            : requestedName.Trim();
        string description = string.IsNullOrWhiteSpace(requestedDescription)
            ? archive.Description
            : requestedDescription.Trim();
        ValidateImportedMetadata(name, description);

        string tag = (requestedTag ?? "").Trim();
        if (!CategoryLabels.ContainsKey(tag))
        {
            tag = "others";
        }

        string skillID = IdGenerator.NewId();
        string versionID = IdGenerator.NewId();
        (string packageKey, SkillVersion version, List<SkillFile> files) = await PersistArchiveAsync(
            skillID, versionID, archive.Files, archive.ContentHash, archive.TotalBytes, cancellationToken)
            .ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        string versionLabel = archive.Version;
        if (versionLabel.Length == 0)
        {
            versionLabel = sourceCommit.Length == 0 ? "1" : ShortCommit(sourceCommit);
        }
        version.VersionLabel = versionLabel;

        Skill skill = new()
        {
            ID = skillID,
            OwnerID = userId,
            Name = name,
            Description = description,
            Instruction = Encoding.UTF8.GetString(archive.Files["SKILL.md"]),
            CurrentVersionID = versionID,
            VersionLabel = versionLabel,
            ContentHash = archive.ContentHash,
            FileCount = archive.Files.Count,
            TotalBytes = archive.TotalBytes,
            SourceType = sourceType,
            SourceURL = sourceURL,
            SourceRef = sourceRef,
            SourceSubdir = sourceSubdir,
            SourceCommit = sourceCommit,
            SyncStatus = "synced",
            AutoUpdate = autoUpdate,
            LastCheckedAt = now,
            LastSyncedAt = now,
            Status = 1,
            Source = 1,
            Tag = tag,
            IsPrivate = isPrivate,
            MarkdownURL = sourceURL,
            ShowcaseMediaJSON = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        UserSkillState state = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            SkillID = skillID,
            Added = true,
            InstalledVersionID = versionID,
            AutoUpdate = autoUpdate,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            await _repository.CreateSkillWithPackageAsync(
                skill, version, files, state, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                File.Delete(Path.Combine(_dataDir, "skill-packages",
                    packageKey.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (IOException)
            {
                // 数据库写失败时尽力清理临时包，原始错误仍向上抛出。
            }
            throw;
        }

        return await SkillDetailAsync(userId, skillID, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateImportedMetadata(string name, string description)
    {
        if (name.EnumerateRunes().Count() == 0 || name.EnumerateRunes().Count() > 80)
        {
            throw AppError.BadAuthRequest("技能名称必须为 1-80 个字符");
        }
        if (description.EnumerateRunes().Count() == 0 || description.EnumerateRunes().Count() > 500)
        {
            throw AppError.BadAuthRequest("技能简介必须为 1-500 个字符");
        }
    }

    private static ImportedSkillArchive ArchiveFromMarkdownPackage(
        byte[] data, string? fallbackName, string? fallbackDescription)
    {
        if (data.Length == 0 || data.LongLength > MaxSkillFileBytes || !Utf8ValidPackage(data))
        {
            throw AppError.BadAuthRequest("Markdown 文件为空、过大或不是 UTF-8");
        }

        string text = Encoding.UTF8.GetString(data);
        (string name, string description, string version) = ParseSkillMetadata(text);
        name = string.IsNullOrWhiteSpace(name) ? (fallbackName ?? "").Trim() : name;
        description = string.IsNullOrWhiteSpace(description)
            ? (fallbackDescription ?? "").Trim()
            : description;
        return FinalizeImportedArchive(
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["SKILL.md"] = data },
            name, description, version);
    }

    private static ImportedSkillArchive ArchiveFromZipPackage(byte[] data, string subdir)
    {
        using MemoryStream stream = new(data, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException error)
        {
            throw AppError.BadAuthRequest("ZIP 文件无法解析").WithCause(error);
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxSkillPackageFiles + 64)
            {
                throw AppError.BadAuthRequest("技能包文件数量不能超过 512 个");
            }

            Dictionary<string, byte[]> raw = new(StringComparer.Ordinal);
            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (IsZipSymlinkOrSpecial(entry))
                {
                    throw AppError.BadAuthRequest("技能包不能包含软链接或特殊文件");
                }

                string entryPath = NormalizeSkillPath(entry.FullName);
                if (entryPath.StartsWith("__MACOSX/", StringComparison.Ordinal)
                    || string.Equals(Path.GetFileName(entryPath), ".DS_Store", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!raw.TryAdd(entryPath, []))
                {
                    throw AppError.BadAuthRequest("技能包包含重复文件路径");
                }
                if (entry.Length > MaxSkillFileBytes)
                {
                    throw AppError.BadAuthRequest("技能包中单个文件不能超过 8MB");
                }

                using Stream entryStream = entry.Open();
                using MemoryStream content = new();
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read = entryStream.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }
                    total += read;
                    if (total > MaxSkillPackageBytes)
                    {
                        throw AppError.BadAuthRequest("技能包解压后不能超过 20MB");
                    }
                    content.Write(buffer, 0, read);
                    if (content.Length > MaxSkillFileBytes)
                    {
                        throw AppError.BadAuthRequest("技能包中单个文件不能超过 8MB");
                    }
                }
                raw[entryPath] = content.ToArray();
            }

            Dictionary<string, byte[]> files = NormalizeImportedArchiveRoot(raw, subdir);
            string skillText = Encoding.UTF8.GetString(files["SKILL.md"]);
            (string name, string description, string version) = ParseSkillMetadata(skillText);
            return FinalizeImportedArchive(files, name, description, version);
        }
    }

    private static Dictionary<string, byte[]> NormalizeImportedArchiveRoot(
        Dictionary<string, byte[]> raw, string subdir)
    {
        if (raw.Count == 0)
        {
            throw AppError.BadAuthRequest("技能包为空");
        }

        string[] paths = raw.Keys.Order(StringComparer.Ordinal).ToArray();
        int slash = paths[0].IndexOf('/');
        if (slash > 0)
        {
            string wrapper = paths[0][..(slash + 1)];
            if (paths.All(path => path.StartsWith(wrapper, StringComparison.Ordinal)))
            {
                raw = raw.ToDictionary(
                    pair => pair.Key[wrapper.Length..], pair => pair.Value, StringComparer.Ordinal);
            }
        }

        string normalizedSubdir = subdir.Trim().Replace('\\', '/').Trim('/');
        if (normalizedSubdir.Length > 0)
        {
            normalizedSubdir = NormalizeSkillPath(normalizedSubdir);
            string prefix = normalizedSubdir + "/";
            Dictionary<string, byte[]> filtered = raw
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key[prefix.Length..], pair => pair.Value, StringComparer.Ordinal);
            if (filtered.Count == 0)
            {
                throw AppError.BadAuthRequest("GitHub 子目录不存在或为空");
            }
            raw = filtered;
        }

        List<string> roots = [];
        foreach (string path in raw.Keys)
        {
            if (string.Equals(path, "SKILL.md", StringComparison.Ordinal))
            {
                roots = [""];
                break;
            }
            if (string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.Ordinal))
            {
                string root = path[..^"SKILL.md".Length].TrimEnd('/');
                roots.Add(root);
            }
        }
        if (roots.Count == 0)
        {
            throw AppError.BadAuthRequest("技能包中缺少 SKILL.md");
        }
        if (roots.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw AppError.BadAuthRequest("技能包包含多个 SKILL.md，请指定单个技能目录");
        }

        string rootPrefix = roots[0].Length == 0 ? "" : roots[0] + "/";
        Dictionary<string, byte[]> result = raw
            .Where(pair => pair.Key.StartsWith(rootPrefix, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key[rootPrefix.Length..], pair => pair.Value, StringComparer.Ordinal);
        if (result.Count > MaxSkillPackageFiles)
        {
            throw AppError.BadAuthRequest("技能包文件数量不能超过 512 个");
        }
        return result;
    }

    private static ImportedSkillArchive FinalizeImportedArchive(
        Dictionary<string, byte[]> files, string name, string description, string version)
    {
        if (!files.ContainsKey("SKILL.md"))
        {
            throw AppError.BadAuthRequest("技能包入口必须是 SKILL.md");
        }
        name = KernelUtil.TruncateRunes(name.Trim(), 80);
        description = KernelUtil.TruncateRunes(description.Trim(), 500);
        ValidateImportedMetadata(name, description);

        using SHA256 hash = SHA256.Create();
        long total = 0;
        foreach (string path in files.Keys.Order(StringComparer.Ordinal))
        {
            byte[] content = files[path];
            total += content.LongLength;
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            hash.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
            hash.TransformBlock(content, 0, content.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
        }
        hash.TransformFinalBlock([], 0, 0);
        return new ImportedSkillArchive
        {
            Files = files,
            Name = name,
            Description = description,
            Version = KernelUtil.TruncateRunes(version.Trim(), 64),
            ContentHash = Convert.ToHexString(hash.Hash!).ToLowerInvariant(),
            TotalBytes = total,
        };
    }

    private static (string Name, string Description, string Version) ParseSkillMetadata(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string name = "";
        string description = "";
        string version = "";
        int bodyStart = 0;
        if (lines.Length > 2 && lines[0].Trim() == "---")
        {
            int metadataIndent = -1;
            for (int index = 1; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Trim() == "---")
                {
                    bodyStart = index + 1;
                    break;
                }
                int indent = line.Length - line.TrimStart(' ', '\t').Length;
                string trimmed = line.Trim();
                int separator = trimmed.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }
                string key = trimmed[..separator].Trim();
                string value = ParseYamlScalar(trimmed[(separator + 1)..]);
                if (indent == 0 && key == "name")
                {
                    name = value;
                }
                else if (indent == 0 && key == "description")
                {
                    description = value;
                }
                else if (indent == 0 && key == "metadata")
                {
                    metadataIndent = indent;
                }
                else if (metadataIndent >= 0 && indent > metadataIndent && key == "version")
                {
                    version = value;
                }
            }
        }

        string[] body = lines[bodyStart..];
        if (name.Length == 0)
        {
            name = body.Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? "";
        }
        if (description.Length == 0)
        {
            List<string> paragraph = [];
            foreach (string line in body)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    if (paragraph.Count > 0)
                    {
                        break;
                    }
                    continue;
                }
                if (trimmed.StartsWith('#') || trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    continue;
                }
                paragraph.Add(trimmed);
            }
            description = string.Join(' ', paragraph);
        }
        return (KernelUtil.TruncateRunes(name.Trim(), 80), KernelUtil.TruncateRunes(description.Trim(), 500),
            KernelUtil.TruncateRunes(version.Trim(), 64));
    }

    private static string ParseYamlScalar(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(value) ?? "";
            }
            catch (System.Text.Json.JsonException)
            {
                return value[1..^1].Trim();
            }
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Trim();
        }
        return value;
    }

    private static bool IsZipSymlinkOrSpecial(ZipArchiveEntry entry)
    {
        int mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if (mode == 0)
        {
            return false;
        }
        int fileType = mode & 0xF000;
        return fileType is not (0x8000 or 0x4000);
    }

    private static bool Utf8ValidPackage(byte[] data)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string ShortCommit(string value) => value.Length <= 12 ? value : value[..12];

    private sealed class ImportedSkillArchive
    {
        public Dictionary<string, byte[]> Files { get; init; } = new(StringComparer.Ordinal);
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string Version { get; init; } = "";
        public string ContentHash { get; init; } = "";
        public long TotalBytes { get; init; }
    }
}

internal static class AppErrorExtensions
{
    public static AppError WithCause(this AppError error, Exception cause) =>
        AppError.Wrap(error.Status, error.Message, cause);
}
