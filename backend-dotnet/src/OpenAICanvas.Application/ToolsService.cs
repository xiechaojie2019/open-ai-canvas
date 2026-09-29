#nullable enable
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>工具列表查询。对应 Go: <c>tools.ToolListRequest</c>。</summary>
public sealed class ToolListRequest
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    /// <summary>public | favorites | custom。</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    /// <summary>style | motion | nine_grid | effect。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("search")]
    public string Search { get; set; } = "";
}

/// <summary>工具列表项。对应 Go: <c>tools.ToolItem</c>（struct 字段序）。</summary>
public sealed class ToolItemDto
{
    [JsonPropertyName("id")]
    public long ID { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("labelEn")]
    public string LabelEn { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("cover")]
    public string Cover { get; set; } = "";

    [JsonPropertyName("extraInfo")]
    public List<string> ExtraInfo { get; set; } = [];

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("mediaUrl")]
    public string MediaURL { get; set; } = "";

    [JsonPropertyName("ownerId")]
    public string OwnerID { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "";

    [JsonPropertyName("sortWeight")]
    public long SortWeight { get; set; }

    [JsonPropertyName("favorited")]
    public bool Favorited { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>列表摘要项（不含 prompt/extraInfo 大字段）。对应 Go: <c>tools.ToolSummary</c>。</summary>
public sealed class ToolSummaryDto
{
    [JsonPropertyName("id")]
    public long ID { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("labelEn")]
    public string LabelEn { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("cover")]
    public string Cover { get; set; } = "";

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("mediaUrl")]
    public string MediaURL { get; set; } = "";

    [JsonPropertyName("ownerId")]
    public string OwnerID { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "";

    [JsonPropertyName("sortWeight")]
    public long SortWeight { get; set; }

    [JsonPropertyName("favorited")]
    public bool Favorited { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>分页结果。对应 Go: <c>tools.ToolList</c>（struct 字段序）。</summary>
public sealed class ToolListDto
{
    [JsonPropertyName("tools")]
    public List<ToolSummaryDto> Tools { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public long TotalCount { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}

/// <summary>新增自定义工具请求。对应 Go: <c>tools.ToolMutationRequest</c>。</summary>
public sealed class ToolMutationRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "";

    [JsonPropertyName("cover")]
    public string Cover { get; set; } = "";

    [JsonPropertyName("extraInfo")]
    public List<string>? ExtraInfo { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("ratio")]
    public string Ratio { get; set; } = "";

    [JsonPropertyName("mediaUrl")]
    public string MediaURL { get; set; } = "";

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "";
}

/// <summary>
/// 画布工具域服务。对应 Go: <c>internal/tools</c>（tools.go / tool_mention.go / tools_seed.go）
/// 与 <c>app/tools_bridge.go</c> 的资源归属校验。
/// </summary>
public sealed class ToolsService
{
    private const string SourceBuiltin = "builtin";
    private const string SourceUser = "user";
    private const string VisibilityPublic = "public";
    private const string VisibilityPrivate = "private";
    private const int MaxPageSize = 80;
    private const int MaxPromptLength = 8000;

    private static readonly string[] ValidTypes = ["style", "motion", "nine_grid", "effect"];
    private static readonly string[] ValidScopes = ["public", "favorites", "custom"];

    private static readonly Regex MentionPattern =
        new(@"@\[tool:(style|motion|nine_grid|effect):(\d+):[^:\]]+:[^\]]+\]", RegexOptions.Compiled);

    private static readonly Regex LabelEnPattern = new(@"[^a-zA-Z0-9_]+", RegexOptions.Compiled);

    private readonly Repository _repository;

    public ToolsService(Repository repository) => _repository = repository;

    /// <summary>按范围分页查询。对应 Go: <c>tools.Service.List</c>。</summary>
    public async Task<ToolListDto> ListAsync(
        string userID, ToolListRequest request, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        NormalizeListRequest(request);
        (List<ToolListRow> items, long total) = await _repository.ListToolsAsync(
            userID, request.Scope, request.Type, request.Tag, request.Search,
            request.Page, request.PageSize, cancellationToken).ConfigureAwait(false);
        List<ToolSummaryDto> tools = items
            .Select(row => BuildSummary(ToTool(row), row.FavoriteRowID is not null))
            .ToList();
        return new ToolListDto
        {
            Tools = tools,
            TotalCount = total,
            Page = request.Page,
            PageSize = request.PageSize,
            HasMore = (long)request.Page * request.PageSize < total,
        };
    }

    /// <summary>工具详情（含提示词）。对应 Go: <c>tools.Service.Detail</c>。</summary>
    public async Task<ToolItemDto> DetailAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (toolID <= 0)
        {
            throw AppError.BadAuthRequest("工具 ID 无效");
        }
        Tool tool = (await _repository.ToolForUserAsync(userID, toolID, cancellationToken).ConfigureAwait(false))!;
        return BuildItem(tool, favorited: false, favoritedAt: null);
    }

    /// <summary>添加/取消收藏。对应 Go: <c>tools.Service.SetFavorite</c>。</summary>
    public async Task<ToolItemDto> SetFavoriteAsync(
        string userID, long toolID, bool favorite, CancellationToken cancellationToken = default)
    {
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (toolID <= 0)
        {
            throw AppError.BadAuthRequest("工具 ID 无效");
        }
        _ = await _repository.ToolForUserAsync(userID, toolID, cancellationToken).ConfigureAwait(false);
        if (favorite)
        {
            await _repository.AddToolFavoriteAsync(userID, toolID, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _repository.RemoveToolFavoriteAsync(userID, toolID, cancellationToken).ConfigureAwait(false);
        }
        (Tool updated, DateTime? favoritedAt) = await _repository.ToolFavoritedAsync(
            userID, toolID, cancellationToken).ConfigureAwait(false);
        return BuildItem(updated, favorited: true, favoritedAt);
    }

    /// <summary>
    /// 创建自定义工具，先校验预览资源的本人归属。对应 Go: <c>app.Service.CreateTool</c>。
    /// </summary>
    public async Task<ToolItemDto> CreateAsync(
        string userID, ToolMutationRequest request, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        foreach (string value in new[] { request.Cover, request.MediaURL }.Concat(request.ExtraInfo ?? []))
        {
            if (value.Trim().Length == 0)
            {
                continue;
            }
            string id = UserDataService.IDFromFileURL(value);
            if (id.Length == 0)
            {
                throw AppError.BadAuthRequest("工具预览仅支持本人上传的资源");
            }
            Resource? resource = await _repository.ResourceForUserAsync(userID, id, cancellationToken)
                .ConfigureAwait(false);
            if (resource is null)
            {
                throw new InvalidOperationException("record not found");
            }
            if (resource.Status != "ready")
            {
                throw AppError.BadAuthRequest("预览资源尚未上传完成");
            }
            if (request.Visibility == VisibilityPublic)
            {
                throw AppError.BadAuthRequest("公开工具暂不支持私人预览资源，请移除预览或改为私有");
            }
        }
        Tool normalized = NormalizeMutation(request);
        DateTime now = DateTime.UtcNow;
        Tool tool = new()
        {
            Type = normalized.Type,
            LabelEn = normalized.LabelEn,
            Label = normalized.Label,
            Desc = normalized.Desc,
            Tag = normalized.Tag,
            Cover = normalized.Cover,
            ExtraInfoJSON = normalized.ExtraInfoJSON,
            Prompt = normalized.Prompt,
            Ratio = normalized.Ratio,
            MediaURL = normalized.MediaURL,
            OwnerID = userID,
            Source = SourceUser,
            Enabled = true,
            Visibility = normalized.Visibility,
            SortWeight = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Tool created = await _repository.CreateToolAsync(tool, cancellationToken).ConfigureAwait(false);
        return BuildItem(created, favorited: false, favoritedAt: null);
    }

    /// <summary>删除自己的自定义工具。对应 Go: <c>tools.Service.Delete</c>。</summary>
    public async Task DeleteAsync(
        string userID, long toolID, CancellationToken cancellationToken = default)
    {
        if (userID.Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (toolID <= 0)
        {
            throw AppError.BadAuthRequest("工具 ID 无效");
        }
        await _repository.DeleteUserToolAsync(userID, toolID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 将 prompt 中的 @[tool:type:ID:label:icon] 令牌替换为对应工具的提示词文本。
    /// 对应 Go: <c>tools.ResolveToolMentionTokens</c>；每个工具先校验再展开。
    /// </summary>
    public async Task<string> ResolveToolMentionTokensAsync(
        string userID, string mode, string prompt, CancellationToken cancellationToken = default)
    {
        if (!prompt.Contains("@[tool:", StringComparison.Ordinal))
        {
            return prompt;
        }
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (MentionPattern.Replace(prompt, "").Contains("@[tool:", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("工具标签无效，请重新选择工具");
        }
        Dictionary<string, string> prompts = new(StringComparer.Ordinal);
        foreach (Match match in MentionPattern.Matches(prompt))
        {
            if (prompts.ContainsKey(match.Value))
            {
                continue;
            }
            if (!long.TryParse(match.Groups[2].Value, out long id) || id <= 0)
            {
                throw AppError.BadAuthRequest("工具 ID 无效");
            }
            Tool tool = (await _repository.ToolForUserAsync(userID, id, cancellationToken).ConfigureAwait(false))!;
            if (!tool.Enabled || tool.Type != match.Groups[1].Value)
            {
                throw AppError.BadAuthRequest("工具已停用或类型不匹配");
            }
            bool imageTool = tool.Type is "style" or "nine_grid";
            if ((imageTool && mode != "image") || (!imageTool && mode != "video"))
            {
                throw AppError.BadAuthRequest("工具不适用于当前生成类型");
            }
            if (tool.Prompt.Trim().Length == 0 || tool.Prompt.Contains("@[tool:", StringComparison.Ordinal))
            {
                throw AppError.BadAuthRequest("工具提示词为空或包含嵌套工具标签");
            }
            prompts[match.Value] = tool.Prompt;
        }
        return MentionPattern.Replace(prompt, match => prompts.GetValueOrDefault(match.Value, match.Value));
    }

    /// <summary>内置工具幂等落库。对应 Go: <c>tools.EnsureBuiltinTools</c>。</summary>
    public async Task EnsureBuiltinToolsAsync(CancellationToken cancellationToken = default)
    {
        string raw = ReadSeedJson();
        ToolSeedFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ToolSeedFile>(raw, GoJson.ReadOptions);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"解析内置工具失败: {error.Message}", error);
        }
        if (file is null)
        {
            throw new InvalidOperationException("内置工具列表为空");
        }

        List<Tool> tools = [];
        int sortWeight = 0;
        Dictionary<long, bool> seen = new();
        foreach ((string type, List<ToolSeedItem> list) in new (string, List<ToolSeedItem>)[]
                 {
                     ("style", file.Style.List),
                     ("motion", file.Motion.List),
                     ("nine_grid", file.NineGrid.List),
                 })
        {
            foreach (ToolSeedItem item in list)
            {
                string uniqueKey = $"{type}:{item.ID}";
                if (item.ID <= 0)
                {
                    throw new InvalidOperationException($"内置工具 ID 无效: type={type} label_en=\"{item.LabelEn}\"");
                }
                if (!seen.TryAdd(item.ID, true))
                {
                    throw new InvalidOperationException($"内置工具 ID 重复: {item.ID}");
                }
                string labelEn = item.LabelEn.Trim();
                if (labelEn.Length == 0)
                {
                    throw new InvalidOperationException($"内置工具英文标识为空: {uniqueKey}");
                }
                string label = item.Label.Trim();
                if (label.Length == 0)
                {
                    throw new InvalidOperationException($"内置工具名称为空: {uniqueKey}");
                }
                string prompt = item.Prompt.Trim();
                if (prompt.Length == 0)
                {
                    throw new InvalidOperationException($"内置工具提示词为空: {uniqueKey}");
                }
                string visibility = item.Visibility.Trim();
                if (visibility is not ("public" or "private"))
                {
                    throw new InvalidOperationException($"内置工具可见性无效: {uniqueKey} visibility=\"{item.Visibility}\"");
                }
                DateTime createdAt = ParseSeedTime(item.CreateAt, uniqueKey);
                DateTime updatedAt = ParseSeedTime(item.UpdateAt, uniqueKey);
                sortWeight++;
                tools.Add(new Tool
                {
                    ID = item.ID,
                    Type = type,
                    LabelEn = labelEn,
                    Label = label,
                    Desc = item.Desc.Trim(),
                    Tag = item.Tag.Trim(),
                    Cover = item.Cover.Trim(),
                    ExtraInfoJSON = item.ExtraInfo is { Count: > 0 }
                        ? JsonSerializer.Serialize(item.ExtraInfo, GoJson.WriteOptions)
                        : "",
                    Prompt = prompt,
                    Ratio = item.Ratio.Trim(),
                    MediaURL = item.MediaURL.Trim(),
                    OwnerID = item.OwnerID,
                    Source = SourceBuiltin,
                    Enabled = item.Enabled,
                    Visibility = visibility,
                    SortWeight = sortWeight,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                });
            }
        }
        if (tools.Count == 0)
        {
            throw new InvalidOperationException("内置工具列表为空");
        }
        await _repository.UpsertBuiltinToolsAsync(tools, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadSeedJson()
    {
        Assembly assembly = typeof(ToolsService).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream("OpenAICanvas.Application.Seed.tools.json")
            ?? throw new InvalidOperationException("找不到内置工具种子资源");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static DateTime ParseSeedTime(string value, string uniqueKey)
    {
        if (DateTime.TryParseExact(
                value.Trim(), "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateTime parsed))
        {
            return parsed;
        }
        throw new InvalidOperationException($"内置工具时间无效: {uniqueKey}: \"{value}\"");
    }

    // ------------------------------------------------------------- 归一化

    private static void NormalizeListRequest(ToolListRequest request)
    {
        if (request.Scope.Length == 0)
        {
            request.Scope = "public";
        }
        if (request.Page <= 0)
        {
            request.Page = 1;
        }
        if (request.PageSize <= 0)
        {
            request.PageSize = 20;
        }
        if (request.PageSize > MaxPageSize)
        {
            request.PageSize = MaxPageSize;
        }
        if (!ValidScopes.Contains(request.Scope))
        {
            throw AppError.BadAuthRequest($"不支持的范围: {request.Scope}");
        }
        if (request.Type.Length > 0 && !ValidTypes.Contains(request.Type))
        {
            throw AppError.BadAuthRequest($"不支持的工具类型: {request.Type}");
        }
        request.Search = request.Search.Trim();
        request.Tag = request.Tag.Trim();
    }

    private static Tool NormalizeMutation(ToolMutationRequest request)
    {
        string toolType = request.Type.Trim();
        if (!ValidTypes.Contains(toolType))
        {
            throw AppError.BadAuthRequest("工具类型无效，仅支持 style、motion、nine_grid、effect");
        }
        string label = request.Label.Trim();
        if (label.Length == 0)
        {
            throw AppError.BadAuthRequest("工具名称不能为空");
        }
        if (label.Length > 120)
        {
            throw AppError.BadAuthRequest("工具名称过长");
        }
        string prompt = request.Prompt.Trim();
        if (prompt.Length == 0)
        {
            throw AppError.BadAuthRequest("工具提示词不能为空");
        }
        if (prompt.Contains("@[tool:", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("不支持嵌套工具标签");
        }
        if (prompt.Length > MaxPromptLength)
        {
            throw AppError.BadAuthRequest("工具提示词过长");
        }
        string desc = request.Desc.Trim();
        if (desc.Length > 500)
        {
            throw AppError.BadAuthRequest("工具描述过长");
        }
        string tag = request.Tag.Trim();
        if (tag.Length > 64)
        {
            throw AppError.BadAuthRequest("标签过长");
        }
        string visibility = request.Visibility.Trim();
        if (visibility.Length == 0)
        {
            visibility = VisibilityPrivate;
        }
        if (visibility is not (VisibilityPublic or VisibilityPrivate))
        {
            throw AppError.BadAuthRequest("可见性仅支持 public 或 private");
        }
        string cover = request.Cover.Trim();
        if (cover.Length > 500)
        {
            throw AppError.BadAuthRequest("封面路径过长");
        }
        string mediaURL = request.MediaURL.Trim();
        if (mediaURL.Length > 500)
        {
            throw AppError.BadAuthRequest("媒体路径过长");
        }
        string ratio = request.Ratio.Trim();
        if (ratio.Length > 32)
        {
            throw AppError.BadAuthRequest("比例参数过长");
        }
        List<string> extraInfo = [];
        foreach (string item in request.ExtraInfo ?? [])
        {
            string trimmed = item.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (trimmed.Length > 500)
            {
                throw AppError.BadAuthRequest("扩展信息路径过长");
            }
            extraInfo.Add(trimmed);
        }
        string extraInfoJSON = extraInfo.Count > 0
            ? JsonSerializer.Serialize(extraInfo, GoJson.WriteOptions)
            : "";
        string labelEn = SanitizeLabelEn(label);
        return new Tool
        {
            Type = toolType,
            LabelEn = labelEn,
            Label = label,
            Desc = desc,
            Tag = tag,
            Cover = cover,
            ExtraInfoJSON = extraInfoJSON,
            Prompt = prompt,
            Ratio = ratio,
            MediaURL = mediaURL,
            Visibility = visibility,
        };
    }

    /// <summary>中文名称转英文标识；无可用字符时回落 custom_tool，唯一性由自增 ID 兜底。</summary>
    private static string SanitizeLabelEn(string label)
    {
        string normalized = LabelEnPattern.Replace(label.ToLowerInvariant(), "_").Trim('_');
        return normalized.Length == 0 ? "custom_tool" : normalized;
    }

    // ------------------------------------------------------------- 投影

    private static Tool ToTool(ToolListRow row) => new()
    {
        ID = row.ID,
        Type = row.Type,
        LabelEn = row.LabelEn,
        Label = row.Label,
        Desc = row.Desc,
        Tag = row.Tag,
        Cover = row.Cover,
        Prompt = "",
        Ratio = row.Ratio,
        MediaURL = row.MediaURL,
        OwnerID = row.OwnerID,
        Source = row.Source,
        Enabled = row.Enabled,
        Visibility = row.Visibility,
        SortWeight = row.SortWeight,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static ToolSummaryDto BuildSummary(Tool tool, bool favorited) => new()
    {
        ID = tool.ID,
        Type = tool.Type,
        LabelEn = tool.LabelEn,
        Label = tool.Label,
        Desc = tool.Desc,
        Tag = tool.Tag,
        Cover = tool.Cover,
        Ratio = tool.Ratio,
        MediaURL = tool.MediaURL,
        OwnerID = tool.OwnerID,
        Source = tool.Source,
        Enabled = tool.Enabled,
        Visibility = tool.Visibility,
        SortWeight = tool.SortWeight,
        Favorited = favorited,
        CreatedAt = tool.CreatedAt,
        UpdatedAt = tool.UpdatedAt,
    };

    private static ToolItemDto BuildItem(Tool tool, bool favorited, DateTime? favoritedAt)
    {
        List<string> extraInfo = [];
        if (tool.ExtraInfoJSON.Trim().Length > 0)
        {
            try
            {
                extraInfo = JsonSerializer.Deserialize<List<string>>(tool.ExtraInfoJSON, GoJson.ReadOptions) ?? [];
            }
            catch (JsonException)
            {
                extraInfo = [];
            }
        }
        return new ToolItemDto
        {
            ID = tool.ID,
            Type = tool.Type,
            LabelEn = tool.LabelEn,
            Label = tool.Label,
            Desc = tool.Desc,
            Tag = tool.Tag,
            Cover = tool.Cover,
            ExtraInfo = extraInfo,
            Prompt = tool.Prompt,
            Ratio = tool.Ratio,
            MediaURL = tool.MediaURL,
            OwnerID = tool.OwnerID,
            Source = tool.Source,
            Enabled = tool.Enabled,
            Visibility = tool.Visibility,
            SortWeight = tool.SortWeight,
            Favorited = favorited,
            CreatedAt = tool.CreatedAt,
            UpdatedAt = tool.UpdatedAt,
        };
    }

    // ------------------------------------------------------------- 种子结构

    private sealed class ToolSeedFile
    {
        [JsonPropertyName("style")]
        public ToolSeedGroup Style { get; set; } = new();

        [JsonPropertyName("motion")]
        public ToolSeedGroup Motion { get; set; } = new();

        [JsonPropertyName("nine_grid")]
        public ToolSeedGroup NineGrid { get; set; } = new();
    }

    private sealed class ToolSeedGroup
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }

        [JsonPropertyName("list")]
        public List<ToolSeedItem> List { get; set; } = [];
    }

    private sealed class ToolSeedItem
    {
        [JsonPropertyName("id")]
        public long ID { get; set; }

        [JsonPropertyName("label_en")]
        public string LabelEn { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("desc")]
        public string Desc { get; set; } = "";

        [JsonPropertyName("tag")]
        public string Tag { get; set; } = "";

        [JsonPropertyName("cover")]
        public string Cover { get; set; } = "";

        [JsonPropertyName("extra_info")]
        public List<string>? ExtraInfo { get; set; }

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";

        [JsonPropertyName("ratio")]
        public string Ratio { get; set; } = "";

        [JsonPropertyName("media_url")]
        public string MediaURL { get; set; } = "";

        [JsonPropertyName("owner_id")]
        public string OwnerID { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("visibility")]
        public string Visibility { get; set; } = "";

        [JsonPropertyName("create_at")]
        public string CreateAt { get; set; } = "";

        [JsonPropertyName("update_at")]
        public string UpdateAt { get; set; } = "";
    }
}
