#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>文件夹创建请求。对应 Go: <c>app.CreateProjectAssetFolderRequest</c>。</summary>
public sealed class CreateProjectAssetFolderRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("parentId")]
    public string ParentID { get; set; } = "";

    [JsonPropertyName("style")]
    public string Style { get; set; } = "";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "";
}

/// <summary>文件夹更新请求（指针区分未提交）。对应 Go: <c>app.UpdateProjectAssetFolderRequest</c>。</summary>
public sealed class UpdateProjectAssetFolderRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("parentId")]
    public string? ParentID { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("theme")]
    public string? Theme { get; set; }
}

/// <summary>
/// 项目素材文件夹服务。对应 Go: <c>app/project_asset_folder.go</c>。
/// </summary>
/// <remarks>
/// 项目素材列表/关联/版本（<c>project_asset.go</c>，含资产领域字段与首版本事务）
/// 属后续节点；文件夹树已完整移植。
/// </remarks>
public sealed class ProjectAssetFolderService
{
    /// <summary>文件夹最大层级。对应 Go: <c>projectAssetFolderMaxDepth</c>。</summary>
    private const int MaxDepth = 8;

    private static readonly HashSet<string> ValidStyles = new(StringComparer.Ordinal)
    {
        "glass", "stacked", "midnight", "paper", "cinema", "compact",
    };

    private static readonly HashSet<string> ValidThemes = new(StringComparer.Ordinal)
    {
        "aurora", "obsidian", "ember", "pearl",
    };

    private readonly Repository _repository;

    public ProjectAssetFolderService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>文件夹列表。对应 Go: <c>ProjectAssetFolders</c>。</summary>
    public async Task<IReadOnlyList<ProjectAssetFolder>> ProjectAssetFoldersAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        return await _repository.ProjectAssetFoldersAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建文件夹。对应 Go: <c>CreateProjectAssetFolder</c>。</summary>
    public async Task<ProjectAssetFolder> CreateProjectAssetFolderAsync(
        string userId, string projectId, CreateProjectAssetFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetFolder> folders = await _repository.ProjectAssetFoldersAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        string name = ValidateName(request.Name);
        string parentId = request.ParentID.Trim();
        ValidateParent(folders, "", parentId);
        if (NameExists(folders, parentId, name, ""))
        {
            throw AppError.BadAuthRequest("同级目录下已存在同名文件夹");
        }
        long position = NextPosition(folders, parentId);
        DateTime now = DateTime.UtcNow;
        ProjectAssetFolder folder = new()
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            ParentID = parentId,
            Name = name,
            NameKey = name.ToLowerInvariant(),
            Style = ValidateStyle(request.Style),
            Theme = ValidateTheme(request.Theme),
            Position = position,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _repository.CreateProjectAssetFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsNameConflict(error))
        {
            throw AppError.BadAuthRequest("同级目录下已存在同名文件夹");
        }
        return folder;
    }

    /// <summary>更新文件夹（局部提交）。对应 Go: <c>UpdateProjectAssetFolder</c>。</summary>
    public async Task<ProjectAssetFolder> UpdateProjectAssetFolderAsync(
        string userId, string projectId, string folderId, UpdateProjectAssetFolderRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectAssetFolder? folder = await _repository.ProjectAssetFolderAsync(
            projectId, folderId.Trim(), cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            throw new InvalidOperationException("record not found");
        }
        IReadOnlyList<ProjectAssetFolder> folders = await _repository.ProjectAssetFoldersAsync(
            projectId, cancellationToken).ConfigureAwait(false);

        if (request.Name is not null)
        {
            string name = ValidateName(request.Name);
            folder.Name = name;
            folder.NameKey = name.ToLowerInvariant();
        }
        if (request.ParentID is not null)
        {
            string parentId = request.ParentID.Trim();
            ValidateParent(folders, folder.ID, parentId);
            if (parentId != folder.ParentID)
            {
                folder.ParentID = parentId;
                folder.Position = NextPosition(folders, parentId);
            }
        }
        if (request.Style is not null)
        {
            folder.Style = ValidateStyle(request.Style);
        }
        if (request.Theme is not null)
        {
            folder.Theme = ValidateTheme(request.Theme);
        }
        if (NameExists(folders, folder.ParentID, folder.Name, folder.ID))
        {
            throw AppError.BadAuthRequest("同级目录下已存在同名文件夹");
        }
        folder.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _repository.UpdateProjectAssetFolderAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsNameConflict(error))
        {
            throw AppError.BadAuthRequest("同级目录下已存在同名文件夹");
        }
        return folder;
    }

    /// <summary>删除空文件夹。对应 Go: <c>DeleteProjectAssetFolder</c>。</summary>
    public async Task DeleteProjectAssetFolderAsync(
        string userId, string projectId, string folderId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        Repository.DeleteFolderOutcome outcome = await _repository.DeleteProjectAssetFolderAsync(
            projectId, folderId.Trim(), DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case Repository.DeleteFolderOutcome.NotEmpty:
                throw AppError.BadAuthRequest("文件夹非空，请先移动其中的素材和子文件夹");
            case Repository.DeleteFolderOutcome.NotFound:
                throw new InvalidOperationException("record not found");
        }
    }

    // ------------------------------------------------------------ 校验（对应 Go 同名函数）

    private async Task RequireProjectAsync(string userId, string projectId, CancellationToken cancellationToken)
    {
        if (await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>对应 Go: <c>validateProjectAssetFolderName</c>（≤60 字符）。</summary>
    private static string ValidateName(string value)
    {
        string name = value.Trim();
        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("文件夹名称不能为空");
        }
        if (name.Length > 60)
        {
            throw AppError.BadAuthRequest("文件夹名称不能超过 60 个字符");
        }
        return name;
    }

    /// <summary>对应 Go: <c>validateProjectAssetFolderStyle</c>。</summary>
    private static string ValidateStyle(string value)
    {
        string style = value.Trim();
        if (style.Length == 0)
        {
            return "glass";
        }
        if (!ValidStyles.Contains(style))
        {
            throw AppError.BadAuthRequest("不支持的文件夹样式");
        }
        return style;
    }

    /// <summary>对应 Go: <c>validateProjectAssetFolderTheme</c>。</summary>
    private static string ValidateTheme(string value)
    {
        string theme = value.Trim();
        if (theme.Length == 0)
        {
            return "aurora";
        }
        if (!ValidThemes.Contains(theme))
        {
            throw AppError.BadAuthRequest("不支持的文件夹主题");
        }
        return theme;
    }

    /// <summary>对应 Go: <c>validateProjectAssetFolderParent</c>（自引用/环/深度 8 层）。</summary>
    private static void ValidateParent(
        IReadOnlyList<ProjectAssetFolder> folders, string folderId, string parentId)
    {
        Dictionary<string, ProjectAssetFolder> byID = new(StringComparer.Ordinal);
        foreach (ProjectAssetFolder folder in folders)
        {
            byID[folder.ID] = folder;
        }
        int ancestorDepth = 0;
        string currentId = parentId;
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (currentId.Length > 0)
        {
            if (currentId == folderId && folderId.Length > 0)
            {
                throw AppError.BadAuthRequest("文件夹不能移动到自身或其子目录");
            }
            if (!seen.Add(currentId))
            {
                throw AppError.BadAuthRequest("文件夹目录关系存在循环");
            }
            if (!byID.TryGetValue(currentId, out ProjectAssetFolder? current))
            {
                throw AppError.BadAuthRequest("父文件夹不存在或不属于当前项目");
            }
            ancestorDepth++;
            currentId = current.ParentID;
        }
        int subtreeHeight = SubtreeHeight(folders, folderId);
        if (ancestorDepth + subtreeHeight > MaxDepth)
        {
            throw AppError.BadAuthRequest("文件夹层级不能超过 8 层");
        }
    }

    /// <summary>对应 Go: <c>projectAssetFolderSubtreeHeight</c>（BFS）。</summary>
    private static int SubtreeHeight(IReadOnlyList<ProjectAssetFolder> folders, string folderId)
    {
        if (folderId.Length == 0)
        {
            return 1;
        }
        int maxDepth = 1;
        Queue<(string ID, int Depth)> queue = new();
        queue.Enqueue((folderId, 1));
        HashSet<string> seen = new(StringComparer.Ordinal) { folderId };
        while (queue.Count > 0)
        {
            (string currentId, int depth) = queue.Dequeue();
            maxDepth = Math.Max(maxDepth, depth);
            foreach (ProjectAssetFolder folder in folders)
            {
                if (folder.ParentID != currentId || !seen.Add(folder.ID))
                {
                    continue;
                }
                queue.Enqueue((folder.ID, depth + 1));
            }
        }
        return maxDepth;
    }

    /// <summary>对应 Go: <c>projectAssetFolderNameExists</c>（同级 + 忽略大小写）。</summary>
    private static bool NameExists(
        IReadOnlyList<ProjectAssetFolder> folders, string parentId, string name, string excludeId)
    {
        foreach (ProjectAssetFolder folder in folders)
        {
            if (folder.ID != excludeId && folder.ParentID == parentId &&
                string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>对应 Go: <c>nextProjectAssetFolderPosition</c>。</summary>
    private static long NextPosition(IReadOnlyList<ProjectAssetFolder> folders, string parentId)
    {
        long max = -1;
        foreach (ProjectAssetFolder folder in folders)
        {
            if (folder.ParentID == parentId)
            {
                max = Math.Max(max, folder.Position);
            }
        }
        return max + 1;
    }

    /// <summary>对应 Go: <c>isProjectAssetFolderNameConflict</c>（唯一约束冲突）。</summary>
    private static bool IsNameConflict(Exception error)
    {
        string message = error.Message.ToLowerInvariant();
        return message.Contains("idx_project_asset_folders_sibling_name", StringComparison.Ordinal) ||
               message.Contains("unique constraint", StringComparison.Ordinal) ||
               message.Contains("duplicate key", StringComparison.Ordinal);
    }
}