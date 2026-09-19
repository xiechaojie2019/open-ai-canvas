#nullable enable
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>技能创建/更新请求。对应 Go: <c>skills.SkillMutationRequest</c>。</summary>
public sealed class SkillMutationRequestDto
{
    [JsonPropertyName("skillName")]
    public string SkillName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("markdownUrl")]
    public string MarkdownURL { get; set; } = "";

    [JsonPropertyName("showcaseMedia")]
    public List<SkillShowcaseMediaDto>? ShowcaseMedia { get; set; }

    [JsonPropertyName("extraInfo")]
    public string ExtraInfo { get; set; } = "";
}

/// <summary>
/// 技能创建/更新（单 Markdown 技能）。对应 Go: <c>skills.go</c> 的
/// CreateSkill / UpdateSkill / createSingleMarkdownSkill / updateSingleMarkdownSkill。
/// </summary>
public sealed partial class SkillsService
{
    /// <summary>创建技能。对应 Go: <c>CreateSkill</c>。</summary>
    public async Task<SkillItemDto> CreateSkillAsync(
        string userId, SkillMutationRequestDto request, CancellationToken cancellationToken = default)
    {
        (SkillMutationRequestDto normalized, string mediaJSON) =
            NormalizeMutation(request, requireInstruction: true);
        (Skill skill, SkillVersion version, List<SkillFile> files, UserSkillState state) =
            await CreateSingleMarkdownSkillAsync(
                userId, normalized, mediaJSON, cancellationToken).ConfigureAwait(false);
        await _repository.CreateSkillWithPackageAsync(
            skill, version, files, state, cancellationToken).ConfigureAwait(false);
        return await SkillDetailAsync(userId, skill.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新技能。对应 Go: <c>UpdateSkill</c>。</summary>
    public async Task<SkillItemDto> UpdateSkillAsync(
        string userId, string id, SkillMutationRequestDto request, CancellationToken cancellationToken = default)
    {
        Skill skill = await OwnedSkillAsync(userId, id, cancellationToken).ConfigureAwait(false);
        bool requireInstruction = skill.SourceType is "markdown" or "builtin" or "";
        (SkillMutationRequestDto normalized, string mediaJSON) =
            NormalizeMutation(request, requireInstruction);
        skill.Name = normalized.SkillName;
        skill.Description = normalized.Description;
        skill.Tag = normalized.Tag;
        skill.IsPrivate = normalized.IsPrivate;
        skill.MarkdownURL = normalized.MarkdownURL;
        skill.ShowcaseMediaJSON = mediaJSON;
        skill.ExtraInfo = normalized.ExtraInfo;
        if (skill.SourceType is "markdown" or "builtin" or "")
        {
            if (normalized.Instruction.Length == 0)
            {
                await _repository.SaveSkillAsync(skill, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                skill.Instruction = normalized.Instruction;
                (SkillVersion version, List<SkillFile> files) = await AddMarkdownArchiveVersionAsync(
                    skill, normalized, cancellationToken).ConfigureAwait(false);
                await _repository.AddSkillVersionAsync(skill, version, files, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await _repository.SaveSkillAsync(skill, cancellationToken).ConfigureAwait(false);
        }
        return await SkillDetailAsync(userId, skill.ID, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>变更请求归一化与校验。对应 Go: <c>normalizeSkillMutationRequest</c>。</summary>
    private static (SkillMutationRequestDto Normalized, string MediaJSON) NormalizeMutation(
        SkillMutationRequestDto request, bool requireInstruction)
    {
        request.SkillName = request.SkillName.Trim();
        request.Description = request.Description.Trim();
        request.Instruction = request.Instruction.Trim();
        request.Tag = request.Tag.Trim();
        request.MarkdownURL = request.MarkdownURL.Trim();
        request.ExtraInfo = request.ExtraInfo.Trim();
        if (request.SkillName.Length == 0 || request.SkillName.EnumerateRunes().Count() > 80)
        {
            throw AppError.BadAuthRequest("技能名称必须为 1-80 个字符");
        }
        if (request.Description.Length == 0 || request.Description.EnumerateRunes().Count() > 500)
        {
            throw AppError.BadAuthRequest("技能简介必须为 1-500 个字符");
        }
        if ((requireInstruction && request.Instruction.Length == 0)
            || request.Instruction.EnumerateRunes().Count() > 100_000)
        {
            throw AppError.BadAuthRequest("技能指令必须为 1-100000 个字符");
        }
        if (!CategoryLabels.ContainsKey(request.Tag))
        {
            throw AppError.BadAuthRequest("请选择有效的技能分类");
        }
        if (request.MarkdownURL.Length > 0 && !ValidSkillURL(request.MarkdownURL))
        {
            throw AppError.BadAuthRequest("Markdown 地址必须是有效的 HTTP(S) 链接");
        }
        if (request.ExtraInfo.EnumerateRunes().Count() > 2000)
        {
            throw AppError.BadAuthRequest("补充信息不能超过 2000 个字符");
        }
        if (request.ShowcaseMedia is { Count: > 8 })
        {
            throw AppError.BadAuthRequest("展示媒体最多添加 8 个");
        }
        foreach (SkillShowcaseMediaDto media in request.ShowcaseMedia ?? [])
        {
            media.Type = media.Type.Trim();
            media.ShowcaseURI = media.ShowcaseURI.Trim();
            media.ShowcaseURL = media.ShowcaseURL.Trim();
            if (media.Type is not ("image" or "video"))
            {
                throw AppError.BadAuthRequest("展示媒体类型仅支持图片或视频");
            }
            if (!ValidSkillURL(media.ShowcaseURL))
            {
                throw AppError.BadAuthRequest("展示媒体必须填写有效的 HTTP(S) 链接");
            }
            if (media.ShowcaseURI.EnumerateRunes().Count() > 500
                || media.ShowcaseURL.EnumerateRunes().Count() > 2000)
            {
                throw AppError.BadAuthRequest("展示媒体地址过长");
            }
        }
        string mediaJSON = System.Text.Json.JsonSerializer.Serialize(request.ShowcaseMedia ?? []);
        return (request, mediaJSON);
    }

    /// <summary>URL 有效性。对应 Go: <c>validSkillURL</c>。</summary>
    private static bool ValidSkillURL(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
        && parsed.Host.Length > 0;

    /// <summary>扩展名 → MIME（常用表 + 兜底 octet-stream）。对应 Go: <c>mime.TypeByExtension</c>。</summary>
    internal static string SkillFileMime(string filePath, byte[] content)
    {
        if (filePath == "SKILL.md" || filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return "text/markdown; charset=utf-8";
        }
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        string? detected = extension switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".xml" => "text/xml; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".wasm" => "application/wasm",
            ".yaml" or ".yml" => "application/yaml",
            ".toml" => "application/toml",
            ".py" => "text/x-python; charset=utf-8",
            ".sh" => "text/x-shellscript; charset=utf-8",
            ".md" => "text/markdown; charset=utf-8",
            _ => null,
        };
        if (detected is not null)
        {
            return detected;
        }
        // 兜底：内容嗅探（与 Go http.DetectContentType 的保守分支一致）。
        return content.Length >= 4 && content[0] == 0x89 && content[1] == 0x50 ? "image/png"
            : content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 ? "image/jpeg"
            : "application/octet-stream";
    }

    // ------------------------------------------------------------ 内部

    /// <summary>单 Markdown 技能创建。对应 Go: <c>createSingleMarkdownSkill</c>。</summary>
    private async Task<(Skill Skill, SkillVersion Version, List<SkillFile> Files, UserSkillState State)> CreateSingleMarkdownSkillAsync(
        string userId,
        SkillMutationRequestDto normalized,
        string mediaJSON,
        CancellationToken cancellationToken)
    {
        (Dictionary<string, byte[]> files, string contentHash, long totalBytes) =
            ArchiveFromMarkdown(normalized.Instruction, normalized.SkillName, normalized.Description);
        string skillId = IdGenerator.NewId();
        string versionId = IdGenerator.NewId();
        DateTime now = DateTime.UtcNow;
        (string packageKey, SkillVersion version, List<SkillFile> skillFiles) = await PersistArchiveAsync(
            skillId, versionId, files, contentHash, totalBytes, cancellationToken).ConfigureAwait(false);
        Skill skill = new()
        {
            ID = skillId,
            OwnerID = userId,
            Name = normalized.SkillName,
            Description = normalized.Description,
            Instruction = Encoding.UTF8.GetString(files["SKILL.md"]),
            CurrentVersionID = versionId,
            VersionLabel = "1",
            ContentHash = contentHash,
            FileCount = files.Count,
            TotalBytes = totalBytes,
            SourceType = "markdown",
            SourceURL = normalized.MarkdownURL,
            SyncStatus = "synced",
            LastCheckedAt = now,
            LastSyncedAt = now,
            Status = SkillStatusEnabled,
            Source = 1,
            Tag = normalized.Tag,
            IsPrivate = normalized.IsPrivate,
            MarkdownURL = normalized.MarkdownURL,
            ShowcaseMediaJSON = mediaJSON,
            CreatedAt = now,
            UpdatedAt = now,
        };
        UserSkillState state = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            SkillID = skillId,
            Added = true,
            InstalledVersionID = versionId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // 持久化由调用方（CreateSkillAsync）统一执行，避免双写。
        return (skill, version, skillFiles, state);
    }

    /// <summary>单 Markdown 技能追加版本。对应 Go: <c>updateSingleMarkdownSkill</c> + <c>addSkillArchiveVersion</c>。</summary>
    private async Task<(SkillVersion Version, List<SkillFile> Files)> AddMarkdownArchiveVersionAsync(
        Skill skill, SkillMutationRequestDto normalized, CancellationToken cancellationToken)
    {
        (Dictionary<string, byte[]> files, string contentHash, long totalBytes) =
            ArchiveFromMarkdown(normalized.Instruction, normalized.SkillName, normalized.Description);
        string versionId = IdGenerator.NewId();
        (string packageKey, SkillVersion version, List<SkillFile> skillFiles) = await PersistArchiveAsync(
            skill.ID, versionId, files, contentHash, totalBytes, cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        version.VersionLabel = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        skill.CurrentVersionID = versionId;
        skill.VersionLabel = version.VersionLabel;
        skill.ContentHash = contentHash;
        skill.FileCount = files.Count;
        skill.TotalBytes = totalBytes;
        skill.SourceType = "markdown";
        skill.SourceURL = normalized.MarkdownURL;
        skill.SyncStatus = "synced";
        skill.SyncError = "";
        skill.LastCheckedAt = now;
        skill.LastSyncedAt = now;
        return (version, skillFiles);
    }

    /// <summary>
    /// 归档持久化：写 zip 到 <c>&lt;dataDir&gt;/skill-packages/&lt;skillId&gt;/&lt;versionId&gt;.zip</c>
    /// 并生成版本与文件清单。对应 Go: <c>persistSkillArchive</c>。
    /// </summary>
    private async Task<(string PackageKey, SkillVersion Version, List<SkillFile> Files)> PersistArchiveAsync(
        string skillId,
        string versionId,
        Dictionary<string, byte[]> files,
        string contentHash,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        string packageKey = skillId + "/" + versionId + ".zip";
        string absolute = Path.Combine(_dataDir, "skill-packages",
            packageKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await using (FileStream zipStream = new(absolute, FileMode.Create, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new(zipStream, ZipArchiveMode.Create))
        {
            foreach (string filePath in SortedPaths(files.Keys))
            {
                ZipArchiveEntry entry = archive.CreateEntry(filePath, CompressionLevel.Optimal);
                await using Stream entryStream = entry.Open();
                await entryStream.WriteAsync(files[filePath], cancellationToken).ConfigureAwait(false);
            }
        }
        SkillVersion version = new()
        {
            ID = versionId,
            SkillID = skillId,
            ContentHash = contentHash,
            EntryPath = "SKILL.md",
            PackageKey = packageKey,
            FileCount = files.Count,
            TotalBytes = totalBytes,
            CreatedAt = DateTime.UtcNow,
        };
        List<SkillFile> list = [];
        foreach (string filePath in SortedPaths(files.Keys))
        {
            byte[] content = files[filePath];
            list.Add(new SkillFile
            {
                ID = IdGenerator.NewId(),
                SkillVersionID = versionId,
                Path = filePath,
                Kind = SkillFileKind(filePath),
                MimeType = SkillFileMime(filePath, content),
                Size = content.Length,
                SHA256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            });
        }
        return (packageKey, version, list);
    }

    /// <summary>
    /// 单 Markdown 归档（SKILL.md = 指令正文，名称/简介回退到请求值）。
    /// 对应 Go: <c>archiveFromMarkdown</c>。
    /// </summary>
    private static (Dictionary<string, byte[]> Files, string ContentHash, long TotalBytes) ArchiveFromMarkdown(
        string instruction, string fallbackName, string fallbackDescription)
    {
        byte[] data = Encoding.UTF8.GetBytes(instruction);
        if (data.Length == 0 || data.Length > MaxSkillFileBytes
            || !Utf8Valid(data))
        {
            throw AppError.BadAuthRequest("Markdown 文件为空、过大或不是 UTF-8");
        }
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["SKILL.md"] = data,
        };
        (string hash, long total) = FinalizeArchiveHash(files);
        _ = fallbackName;
        _ = fallbackDescription;
        return (files, hash, total);
    }

    /// <summary>内容哈希：排序路径后 path+0+content+0 累积 SHA-256。对应 Go: <c>finalizeSkillArchive</c>。</summary>
    private static (string Hash, long TotalBytes) FinalizeArchiveHash(Dictionary<string, byte[]> files)
    {
        if (!files.ContainsKey("SKILL.md"))
        {
            throw AppError.BadAuthRequest("技能包入口必须是 SKILL.md");
        }
        using SHA256 sha = SHA256.Create();
        long total = 0;
        foreach (string filePath in SortedPaths(files.Keys))
        {
            byte[] content = files[filePath];
            total += content.Length;
            byte[] pathBytes = Encoding.UTF8.GetBytes(filePath);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), total);
    }

    private static string[] SortedPaths(IEnumerable<string> paths)
    {
        string[] sorted = [.. paths];
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    private static bool Utf8Valid(byte[] data)
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
}
