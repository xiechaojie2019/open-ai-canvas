#nullable enable
using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>技能包文件项。对应 Go: <c>skills.SkillPackageFileItem</c>。</summary>
public sealed class SkillPackageFileItemDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string SHA256 { get; set; } = "";
}

/// <summary>技能文件内容（文本预览或二进制标记）。对应 Go: <c>skills.SkillPackageFileContent</c>。</summary>
public sealed class SkillPackageFileContentDto
{
    [JsonPropertyName("file")]
    public SkillPackageFileItemDto File { get; set; } = new();

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("binary")]
    public bool Binary { get; set; }
}

/// <summary>技能包内嵌文件（base64）。对应 Go: <c>skills.SkillPackageBundleFile</c>。</summary>
public sealed class SkillPackageBundleFileDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("contentBase64")]
    public string ContentBase64 { get; set; } = "";
}

/// <summary>技能包聚合。对应 Go: <c>skills.SkillPackageBundle</c>。</summary>
public sealed class SkillPackageBundleDto
{
    [JsonPropertyName("skillId")]
    public string SkillID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("versionId")]
    public string VersionID { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";

    [JsonPropertyName("files")]
    public List<SkillPackageBundleFileDto> Files { get; set; } = [];
}

/// <summary>文件搜索结果。对应 Go: <c>skills.SkillFileSearchResult</c>。</summary>
public sealed class SkillFileSearchResultDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("line")]
    public long Line { get; set; }

    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = "";
}

/// <summary>
/// 技能包文件读取。对应 Go: <c>internal/skills/skill_packages.go</c> 的读路径。
/// </summary>
/// <remarks>
/// 安装/同步（upload/GitHub）依赖上传解析与出站客户端，随下一批接入（PENDING #65）；
/// 当前包数据来源为 Go 版部署已落盘的 <c>&lt;dataDir&gt;/skill-packages/&lt;skillId&gt;/&lt;versionId&gt;.zip</c>。
/// </remarks>
public sealed partial class SkillsService
{
    /// <summary>对应 Go: <c>maxSkillPreviewBytes</c>。</summary>
    private const long MaxSkillPreviewBytes = 512L << 10;

    /// <summary>对应 Go: <c>maxSkillFileBytes</c>。</summary>
    private const long MaxSkillFileBytes = 8L << 20;

    /// <summary>版本文件清单。对应 Go: <c>SkillPackageFiles</c>。</summary>
    public async Task<List<SkillPackageFileItemDto>> SkillPackageFilesAsync(
        string userId, string skillId, CancellationToken cancellationToken = default)
    {
        Skill skill = await VisibleSkillAsync(userId, skillId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SkillFile> files = await _repository
            .SkillFilesAsync(skill.CurrentVersionID, cancellationToken).ConfigureAwait(false);
        return files.Select(SkillFileItem).ToList();
    }

    /// <summary>读取文件（文本预览/二进制标记）。对应 Go: <c>SkillPackageFile</c>。</summary>
    public async Task<SkillPackageFileContentDto> SkillPackageFileAsync(
        string userId, string skillId, string filePath, CancellationToken cancellationToken = default)
    {
        (_, SkillVersion version, SkillFile file, byte[] content) =
            await ReadSkillPackageFileAsync(userId, skillId, filePath, cancellationToken).ConfigureAwait(false);
        bool binary = !IsPreviewText(file.MimeType, file.Path);
        if (binary)
        {
            return new SkillPackageFileContentDto { File = SkillFileItem(file), Binary = true };
        }
        if (content.Length > MaxSkillPreviewBytes)
        {
            throw AppError.BadAuthRequest("文件超过 512KB，请下载后查看");
        }
        return new SkillPackageFileContentDto
        {
            File = SkillFileItem(file),
            Content = Encoding.UTF8.GetString(content),
        };
    }

    /// <summary>原始文件下载。对应 Go: <c>SkillPackageRawFile</c>。</summary>
    public async Task<(byte[] Content, string MimeType, string FileName)> SkillPackageRawFileAsync(
        string userId, string skillId, string filePath, CancellationToken cancellationToken = default)
    {
        (Skill _, SkillVersion _, SkillFile file, byte[] content) =
            await ReadSkillPackageFileAsync(userId, skillId, filePath, cancellationToken).ConfigureAwait(false);
        return (content, file.MimeType, Path.GetFileName(file.Path));
    }

    /// <summary>技能包聚合（全文件 base64）。对应 Go: <c>SkillPackageBundle</c>。</summary>
    public async Task<SkillPackageBundleDto> SkillPackageBundleAsync(
        string userId, string skillId, CancellationToken cancellationToken = default)
    {
        Skill skill = await VisibleSkillAsync(userId, skillId, cancellationToken).ConfigureAwait(false);
        SkillVersion? version = await _repository
            .SkillVersionAsync(skill.CurrentVersionID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("技能不存在或已删除");
        IReadOnlyList<SkillFile> files = await _repository
            .SkillFilesAsync(version.ID, cancellationToken).ConfigureAwait(false);
        SkillPackageBundleDto bundle = new()
        {
            SkillID = skill.ID,
            Name = skill.Name,
            Description = skill.Description,
            VersionID = version.ID,
            Version = version.VersionLabel,
            ContentHash = version.ContentHash,
        };
        foreach (SkillFile file in files)
        {
            byte[] content = await ReadSkillArchiveEntryAsync(version, file.Path, cancellationToken)
                .ConfigureAwait(false);
            bundle.Files.Add(new SkillPackageBundleFileDto
            {
                Path = file.Path,
                MimeType = file.MimeType,
                ContentBase64 = Convert.ToBase64String(content),
            });
        }
        return bundle;
    }

    /// <summary>文本文件内容搜索（≤50 条）。对应 Go: <c>SearchSkillPackage</c>。</summary>
    public async Task<List<SkillFileSearchResultDto>> SearchSkillPackageAsync(
        string userId, string skillId, string query, CancellationToken cancellationToken = default)
    {
        Skill skill = await VisibleSkillAsync(userId, skillId, cancellationToken).ConfigureAwait(false);
        query = query.Trim();
        if (query.Length == 0 || query.EnumerateRunes().Count() > 120)
        {
            throw AppError.BadAuthRequest("搜索关键词必须为 1-120 个字符");
        }
        SkillVersion? version = await _repository
            .SkillVersionAsync(skill.CurrentVersionID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("技能不存在或已删除");
        IReadOnlyList<SkillFile> files = await _repository
            .SkillFilesAsync(version.ID, cancellationToken).ConfigureAwait(false);
        string needle = query.ToLowerInvariant();
        List<SkillFileSearchResultDto> results = [];
        foreach (SkillFile file in files)
        {
            if (results.Count >= 50 || !IsPreviewText(file.MimeType, file.Path)
                || file.Size > MaxSkillPreviewBytes)
            {
                continue;
            }
            byte[] content = await ReadSkillArchiveEntryAsync(version, file.Path, cancellationToken)
                .ConfigureAwait(false);
            string[] lines = Encoding.UTF8.GetString(content).Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (lines[index].ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
                {
                    results.Add(new SkillFileSearchResultDto
                    {
                        Path = file.Path,
                        Line = index + 1,
                        Snippet = KernelUtil.TruncateRunes(lines[index].Trim(), 240),
                    });
                    if (results.Count >= 50)
                    {
                        break;
                    }
                }
            }
        }
        return results;
    }

    // ------------------------------------------------------------ 内部

    /// <summary>读取包内文件。对应 Go: <c>readSkillPackageFile</c>。</summary>
    private async Task<(Skill Skill, SkillVersion Version, SkillFile File, byte[] Content)> ReadSkillPackageFileAsync(
        string userId, string skillId, string filePath, CancellationToken cancellationToken)
    {
        Skill skill = await VisibleSkillAsync(userId, skillId, cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeSkillPath(filePath);
        SkillVersion? version = await _repository
            .SkillVersionAsync(skill.CurrentVersionID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("技能不存在或已删除");
        IReadOnlyList<SkillFile> files = await _repository
            .SkillFilesAsync(version.ID, cancellationToken).ConfigureAwait(false);
        foreach (SkillFile file in files)
        {
            if (file.Path != normalized)
            {
                continue;
            }
            byte[] content = await ReadSkillArchiveEntryAsync(version, filePath, cancellationToken)
                .ConfigureAwait(false);
            return (skill, version, file, content);
        }
        throw AppError.BadAuthRequest("技能文件不存在");
    }

    /// <summary>读 ZIP 内条目（限 8MB+1）。对应 Go: <c>readSkillArchiveEntry</c>。</summary>
    private async Task<byte[]> ReadSkillArchiveEntryAsync(
        SkillVersion version, string filePath, CancellationToken cancellationToken)
    {
        string absolute = Path.Combine(_dataDir, "skill-packages",
            version.PackageKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            throw AppError.BadAuthRequest("技能文件不存在");
        }
        await using FileStream stream = new(absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName != filePath)
            {
                continue;
            }
            await using Stream entryStream = entry.Open();
            byte[] buffer = new byte[MaxSkillFileBytes + 1];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = await entryStream.ReadAsync(
                    buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }
                read += chunk;
            }
            if (read > buffer.Length)
            {
                read = buffer.Length;
            }
            byte[] result = new byte[read];
            Buffer.BlockCopy(buffer, 0, result, 0, read);
            return result;
        }
        throw AppError.BadAuthRequest("技能文件不存在");
    }

    /// <summary>路径规范化与越界检查。对应 Go: <c>normalizeSkillPath</c>。</summary>
    private static string NormalizeSkillPath(string value)
    {
        value = value.Trim().Replace('\\', '/');
        if (value.Length == 0 || value.Contains('\0') || value.StartsWith('/'))
        {
            throw AppError.BadAuthRequest("技能文件路径无效");
        }
        // 与 Go path.Clean 一致：保持相对路径，不做绝对化。
        List<string> cleanedSegments = [];
        foreach (string segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (cleanedSegments.Count == 0 || cleanedSegments[^1] == "..")
                {
                    throw AppError.BadAuthRequest("技能文件路径越界或包含禁止目录");
                }
                cleanedSegments.RemoveAt(cleanedSegments.Count - 1);
                continue;
            }
            cleanedSegments.Add(segment);
        }
        string clean = string.Join('/', cleanedSegments);
        if (clean == "." || clean == ".." || clean.StartsWith("../", StringComparison.Ordinal)
            || clean.Contains("/../", StringComparison.Ordinal)
            || clean.StartsWith(".git/", StringComparison.Ordinal)
            || clean == ".git")
        {
            throw AppError.BadAuthRequest("技能文件路径越界或包含禁止目录");
        }
        if (clean.EnumerateRunes().Count() > 1000)
        {
            throw AppError.BadAuthRequest("技能文件路径过长");
        }
        return clean;
    }

    /// <summary>文件项投影。对应 Go: <c>skillFileItem</c>。</summary>
    private static SkillPackageFileItemDto SkillFileItem(SkillFile file) => new()
    {
        Path = file.Path,
        Kind = file.Kind,
        MimeType = file.MimeType,
        Size = file.Size,
        SHA256 = file.SHA256,
    };

    /// <summary>文本预览判定。对应 Go: <c>isPreviewText</c>。</summary>
    private static bool IsPreviewText(string mimeType, string filePath) =>
        mimeType.StartsWith("text/", StringComparison.Ordinal)
        || mimeType.Contains("json", StringComparison.Ordinal)
        || mimeType.Contains("yaml", StringComparison.Ordinal)
        || mimeType.Contains("toml", StringComparison.Ordinal)
        || SkillFileKind(filePath) == "code";

    /// <summary>文件种类。对应 Go: <c>skillFileKind</c>。</summary>
    internal static string SkillFileKind(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".md" or ".mdx" => "markdown",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => "image",
            ".mp4" or ".webm" or ".mov" => "video",
            ".mp3" or ".wav" or ".m4a" or ".ogg" => "audio",
            ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".go" or ".sh"
                or ".json" or ".yaml" or ".yml" or ".toml" or ".html" or ".css" => "code",
            ".txt" or ".csv" => "text",
            _ => "binary",
        };
    }
}
