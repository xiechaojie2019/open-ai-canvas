#nullable enable

using System.Net;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Application;

/// <summary>GitHub 技能归档安装与同步。只访问 github.com / codeload.github.com 的 HTTPS 地址。</summary>
public sealed partial class SkillsService
{
    private const string GitHubHost = "github.com";
    private const string GitHubCodeLoadHost = "codeload.github.com";
    private const long GitHubMaxResponseBytes = 20L << 20;
    private static readonly TimeSpan GitHubTimeout = TimeSpan.FromSeconds(30);

    public async Task<SkillItemDto> InstallGitHubAsync(
        string userId,
        SkillGitHubInstallRequestDto request,
        CancellationToken cancellationToken = default)
    {
        GitHubSkillSpec spec = ParseGitHubSkillURL(request.URL, request.Ref, request.Subdir);
        try
        {
            GitHubArchiveResult archive = await FetchGitHubArchiveAsync(spec, cancellationToken)
                .ConfigureAwait(false);
            return await CreateImportedSkillAsync(
                userId,
                archive.Archive,
                requestedName: "",
                requestedDescription: "",
                requestedTag: request.Tag,
                isPrivate: request.IsPrivate,
                sourceType: "github",
                sourceURL: archive.CanonicalURL,
                sourceRef: archive.ResolvedRef,
                sourceSubdir: spec.Subdir,
                sourceCommit: archive.SourceCommit,
                autoUpdate: request.AutoUpdate,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            throw;
        }
        catch (Exception error)
        {
            throw AppError.Wrap(502, "GitHub 技能读取失败，请检查仓库地址和网络", error);
        }
    }

    public async Task<SkillItemDto> SyncGitHubAsync(
        string userId, string skillID, CancellationToken cancellationToken = default)
    {
        Skill skill = await OwnedSkillAsync(userId, skillID, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(skill.SourceType, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw AppError.BadAuthRequest("只有 GitHub 技能可以同步");
        }

        DateTime checkedAt = DateTime.UtcNow;
        try
        {
            GitHubSkillSpec spec = ParseGitHubSkillURL(skill.SourceURL, skill.SourceRef, skill.SourceSubdir);
            GitHubArchiveResult archive = await FetchGitHubArchiveAsync(spec, cancellationToken)
                .ConfigureAwait(false);
            skill.LastCheckedAt = checkedAt;

            if (string.Equals(archive.SourceCommit, skill.SourceCommit, StringComparison.Ordinal)
                && string.Equals(archive.Archive.ContentHash, skill.ContentHash, StringComparison.Ordinal))
            {
                skill.SyncStatus = "synced";
                skill.SyncError = "";
                await _repository.SaveSkillAsync(skill, cancellationToken).ConfigureAwait(false);
                return await SkillDetailAsync(userId, skill.ID, cancellationToken).ConfigureAwait(false);
            }

            await AddImportedArchiveVersionAsync(
                skill,
                archive.Archive,
                archive.CanonicalURL,
                archive.ResolvedRef,
                spec.Subdir,
                archive.SourceCommit,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AppError error)
        {
            await SaveGitHubSyncFailureAsync(skill, checkedAt, error.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            await SaveGitHubSyncFailureAsync(skill, checkedAt, error.Message, cancellationToken).ConfigureAwait(false);
            throw AppError.Wrap(502, "GitHub 技能同步失败，请稍后重试", error);
        }

        return await SkillDetailAsync(userId, skill.ID, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddImportedArchiveVersionAsync(
        Skill skill,
        ImportedSkillArchive archive,
        string sourceURL,
        string sourceRef,
        string sourceSubdir,
        string sourceCommit,
        CancellationToken cancellationToken)
    {
        string versionID = IdGenerator.NewId();
        (string packageKey, SkillVersion version, List<SkillFile> files) = await PersistArchiveAsync(
            skill.ID, versionID, archive.Files, archive.ContentHash, archive.TotalBytes, cancellationToken)
            .ConfigureAwait(false);

        string versionLabel = archive.Version.Length == 0 ? ShortCommit(sourceCommit) : archive.Version;
        DateTime now = DateTime.UtcNow;
        version.VersionLabel = versionLabel;
        version.SourceCommit = sourceCommit;
        skill.Name = archive.Name;
        skill.Description = archive.Description;
        skill.Instruction = System.Text.Encoding.UTF8.GetString(archive.Files["SKILL.md"]);
        skill.CurrentVersionID = versionID;
        skill.VersionLabel = versionLabel;
        skill.ContentHash = archive.ContentHash;
        skill.FileCount = archive.Files.Count;
        skill.TotalBytes = archive.TotalBytes;
        skill.SourceType = "github";
        skill.SourceURL = sourceURL;
        skill.SourceRef = sourceRef;
        skill.SourceSubdir = sourceSubdir;
        skill.SourceCommit = sourceCommit;
        skill.SyncStatus = "synced";
        skill.SyncError = "";
        skill.AutoUpdate = skill.AutoUpdate;
        skill.LastCheckedAt = now;
        skill.LastSyncedAt = now;
        skill.UpdatedAt = now;

        try
        {
            await _repository.AddSkillVersionAsync(skill, version, files, cancellationToken)
                .ConfigureAwait(false);
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
                // 保留数据库错误，物理包由后续清理处理。
            }
            throw;
        }
    }

    private async Task SaveGitHubSyncFailureAsync(
        Skill skill, DateTime checkedAt, string error, CancellationToken cancellationToken)
    {
        skill.LastCheckedAt = checkedAt;
        skill.SyncStatus = "failed";
        skill.SyncError = KernelUtil.TruncateRunes(error, 1000);
        try
        {
            await _repository.SaveSkillAsync(skill, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 同步原始错误优先返回；失败状态写入尽力而为。
        }
    }

    internal static GitHubSkillSpec ParseGitHubSkillURL(
        string rawURL, string requestedRef, string requestedSubdir)
    {
        if (!Uri.TryCreate((rawURL ?? "").Trim(), UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, GitHubHost, StringComparison.OrdinalIgnoreCase)
            || parsed.Port is not (-1 or 443)
            || parsed.UserInfo.Length != 0
            || parsed.Query.Length != 0
            || parsed.Fragment.Length != 0)
        {
            throw AppError.BadAuthRequest("请输入公开的 https://github.com 仓库地址");
        }

        string path = parsed.AbsolutePath.Trim('/');
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw AppError.BadAuthRequest("GitHub 地址必须包含 owner/repository");
        }

        string owner = Uri.UnescapeDataString(parts[0]);
        string repo = Uri.UnescapeDataString(parts[1]);
        ValidateGitHubPathPart(owner, "GitHub owner");
        ValidateGitHubPathPart(repo, "GitHub repository");
        repo = repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;
        if (repo.Length == 0)
        {
            throw AppError.BadAuthRequest("GitHub 仓库地址无效");
        }

        string sourceRef = (requestedRef ?? "").Trim();
        string subdir = (requestedSubdir ?? "").Trim().Replace('\\', '/').Trim('/');
        if (parts.Length > 2)
        {
            if (!string.Equals(parts[2], "tree", StringComparison.OrdinalIgnoreCase) || parts.Length < 4)
            {
                throw AppError.BadAuthRequest("GitHub 地址必须指向仓库根目录或 tree 子目录");
            }
            string urlRef = Uri.UnescapeDataString(parts[3]);
            if (sourceRef.Length == 0)
            {
                sourceRef = urlRef;
            }
            if (subdir.Length == 0 && parts.Length > 4)
            {
                subdir = string.Join('/', parts.Skip(4).Select(Uri.UnescapeDataString));
            }
        }

        if (sourceRef.EnumerateRunes().Count() > 255 || sourceRef.Contains('\0'))
        {
            throw AppError.BadAuthRequest("GitHub 分支或标签无效");
        }
        if (subdir.Length > 0)
        {
            subdir = NormalizeSkillPath(subdir);
        }
        return new GitHubSkillSpec(owner, repo, sourceRef, subdir);
    }

    private async Task<GitHubArchiveResult> FetchGitHubArchiveAsync(
        GitHubSkillSpec spec, CancellationToken cancellationToken)
    {
        string resolvedRef = spec.Ref.Length == 0 ? "HEAD" : spec.Ref;
        string escapedOwner = Uri.EscapeDataString(spec.Owner);
        string escapedRepo = Uri.EscapeDataString(spec.Repo);
        string escapedRef = Uri.EscapeDataString(resolvedRef);
        Uri downloadURL = new($"https://{GitHubCodeLoadHost}/{escapedOwner}/{escapedRepo}/zip/{escapedRef}");
        if (!string.Equals(downloadURL.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(downloadURL.Host, GitHubCodeLoadHost, StringComparison.OrdinalIgnoreCase)
            || downloadURL.Port is not (-1 or 443))
        {
            throw AppError.BadAuthRequest("GitHub 下载地址无效");
        }

        // 先做标准公网 SSRF 校验，再用禁代理、禁重定向客户端发出精确主机请求。
        await OutboundGuard.ValidateOutboundUrlAsync(downloadURL.ToString()).ConfigureAwait(false);
        using HttpClient client = OutboundHttpClient.Create(
            GitHubTimeout, allowAutoRedirect: false, useProxy: false);
        using HttpRequestMessage request = new(HttpMethod.Get, downloadURL);
        request.Headers.UserAgent.ParseAdd("open-ai-canvas-skill-fetch/1.0");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AppError.New(502, "GitHub 技能读取超时，请稍后重试");
        }
        catch (HttpRequestException error)
        {
            throw AppError.Wrap(502, "GitHub 技能读取失败，请检查仓库地址和网络", error);
        }

        using (response)
        {
            if (!string.Equals(response.RequestMessage?.RequestUri?.Host, GitHubCodeLoadHost,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw AppError.BadAuthRequest("GitHub 下载地址无效");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw AppError.Wrap(502, $"GitHub 技能下载失败（HTTP {(int)response.StatusCode}）", null);
            }
            if (response.Content.Headers.ContentLength is > GitHubMaxResponseBytes)
            {
                throw AppError.BadAuthRequest("GitHub 技能包超过 20MB");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int read = await body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > GitHubMaxResponseBytes)
                {
                    throw AppError.BadAuthRequest("GitHub 技能包超过 20MB");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            ImportedSkillArchive archive = ArchiveFromZipPackage(buffer.ToArray(), spec.Subdir);
            string sourceCommit = response.Headers.ETag?.Tag?.Trim() ?? "";
            if (sourceCommit.Length == 0)
            {
                sourceCommit = archive.ContentHash;
            }
            return new GitHubArchiveResult(
                archive,
                sourceCommit,
                $"https://{GitHubHost}/{spec.Owner}/{spec.Repo}",
                resolvedRef);
        }
    }

    private static void ValidateGitHubPathPart(string value, string field)
    {
        if (value.Length == 0 || value is "." or ".." || value.Any(char.IsWhiteSpace)
            || value.Contains('/') || value.Contains('\\') || value.Contains('\0'))
        {
            throw AppError.BadAuthRequest(field + " 无效");
        }
    }

    internal readonly record struct GitHubSkillSpec(
        string Owner, string Repo, string Ref, string Subdir);

    private readonly record struct GitHubArchiveResult(
        ImportedSkillArchive Archive,
        string SourceCommit,
        string CanonicalURL,
        string ResolvedRef);
}
