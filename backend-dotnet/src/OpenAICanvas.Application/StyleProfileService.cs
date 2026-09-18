#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>风格档案请求。对应 Go: <c>prompts.StyleProfileRequest</c>。</summary>
public sealed class StyleProfileRequest
{
    [JsonPropertyName("profileJson")]
    public string ProfileJSON { get; set; } = "";
}

/// <summary>收藏请求。对应 Go: <c>prompts.StyleProfileFavoriteRequest</c>。</summary>
public sealed class StyleProfileFavoriteRequest
{
    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }
}

/// <summary>
/// 风格档案服务。对应 Go: <c>internal/prompts/style_profile.go</c> 的用户风格部分。
/// </summary>
/// <remarks>
/// 内置预设目录与技能库（<c>internal/skills</c>）属阶段 10.3/10.5；用户风格 CRUD 已完整移植。
/// </remarks>
public sealed class StyleProfileService
{
    /// <summary>用户风格上限。对应 Go: <c>maxUserStyleProfiles</c>。</summary>
    private const int MaxUserStyleProfiles = 200;

    private static readonly HashSet<string> ValidSources = new(StringComparer.Ordinal)
    {
        "builtin", "user", "external",
    };

    private readonly Repository _repository;

    public StyleProfileService(Repository repository)
    {
        _repository = repository;
    }

    /// <summary>风格列表。对应 Go: <c>ListStyleProfiles</c>。</summary>
    public Task<IReadOnlyList<StyleProfile>> ListStyleProfilesAsync(
        string userId, CancellationToken cancellationToken = default) =>
        _repository.StyleProfilesAsync(userId, cancellationToken);

    /// <summary>创建风格（上限 200）。对应 Go: <c>CreateStyleProfile</c>。</summary>
    public async Task<StyleProfile> CreateStyleProfileAsync(
        string userId, StyleProfileRequest request, CancellationToken cancellationToken = default)
    {
        long count = await _repository.StyleProfileCountAsync(userId, cancellationToken).ConfigureAwait(false);
        if (count >= MaxUserStyleProfiles)
        {
            throw AppError.BadAuthRequest("我的风格最多保存 200 个");
        }
        (string normalized, StyleProfileDocument document) = NormalizeUserStyleProfileJSON(
            request.ProfileJSON, "", 1);

        DateTime now = DateTime.UtcNow;
        StyleProfile profile = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            Name = document.Title.Trim(),
            Description = document.Description.Trim(),
            CoverURL = document.CoverURL.Trim(),
            TagsJSON = JsonSerializer.Serialize(document.Tags),
            Favorite = false,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        (normalized, document) = NormalizeUserStyleProfileJSON(normalized, profile.ID, profile.Revision);
        profile.ProfileJSON = normalized;
        profile.Name = document.Title;
        await _repository.CreateStyleProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    /// <summary>更新风格（版本 +1）。对应 Go: <c>UpdateStyleProfile</c>。</summary>
    public async Task<StyleProfile> UpdateStyleProfileAsync(
        string userId, string id, StyleProfileRequest request, CancellationToken cancellationToken = default)
    {
        StyleProfile? profile = await _repository.StyleProfileForUserAsync(userId, id, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            throw new InvalidOperationException("record not found");
        }
        (string normalized, StyleProfileDocument document) = NormalizeUserStyleProfileJSON(
            request.ProfileJSON, profile.ID, profile.Revision + 1);
        profile.Name = document.Title;
        profile.Description = document.Description;
        profile.CoverURL = document.CoverURL;
        profile.TagsJSON = JsonSerializer.Serialize(document.Tags);
        profile.ProfileJSON = normalized;
        profile.Revision++;
        profile.UpdatedAt = DateTime.UtcNow;
        await _repository.UpdateStyleProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    /// <summary>设置收藏。对应 Go: <c>SetStyleProfileFavorite</c>。</summary>
    public async Task SetStyleProfileFavoriteAsync(
        string userId, string id, bool favorite, CancellationToken cancellationToken = default)
    {
        if (!await _repository.SetStyleProfileFavoriteAsync(userId, id, favorite, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>记录最近使用。对应 Go: <c>TouchStyleProfile</c>。</summary>
    public async Task TouchStyleProfileAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        if (!await _repository.TouchStyleProfileAsync(userId, id, DateTime.UtcNow, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("record not found");
        }
    }

    /// <summary>删除风格。对应 Go: <c>DeleteStyleProfile</c>。</summary>
    public async Task DeleteStyleProfileAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        if (!await _repository.DeleteStyleProfileAsync(userId, id, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("record not found");
        }
    }

    // ------------------------------------------------------------ 校验（对应 Go prompts 包纯函数）

    /// <summary>对应 Go: <c>ValidateStyleProfileJSON</c>。返回归一化后的 JSON 文本。</summary>
    internal static string ValidateStyleProfileJSON(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }
        if (Encoding.UTF8.GetByteCount(trimmed) > 256 * 1024)
        {
            throw AppError.BadAuthRequest("项目画风资产配置过大");
        }
        JsonElement profile;
        try
        {
            profile = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("项目画风资产配置不是合法 JSON");
        }
        if (profile.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少必要字段");
        }
        long schemaVersion = NumberField(profile, "schemaVersion");
        string presetID = StringField(profile, "presetId");
        string title = StringField(profile, "title");
        string prompt = StringField(profile, "prompt");
        long revision = NumberField(profile, "revision");
        if (schemaVersion != 1 || presetID.Length == 0 || title.Length == 0 || prompt.Length == 0 || revision < 1)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少必要字段");
        }
        if (!profile.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw AppError.BadAuthRequest("项目画风资产配置缺少 assets 数组");
        }
        if (assets.GetArrayLength() > 20)
        {
            throw AppError.BadAuthRequest("项目画风执行资产最多绑定 20 个");
        }
        string executionPolicy = StringField(profile, "executionPolicy");
        if (executionPolicy.Length > 0 && executionPolicy is not ("compatible-fallback" or "strict-assets"))
        {
            throw AppError.BadAuthRequest("项目画风执行策略不支持");
        }
        string source = StringField(profile, "source");
        if (!ValidSources.Contains(source))
        {
            throw AppError.BadAuthRequest("项目画风来源不支持");
        }
        return trimmed;
    }

    /// <summary>对应 Go: <c>normalizeUserStyleProfileJSON</c>。</summary>
    internal static (string JSON, StyleProfileDocument Document) NormalizeUserStyleProfileJSON(
        string value, string presetId, long revision)
    {
        string validated = ValidateStyleProfileJSON(value);
        StyleProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StyleProfileDocument>(validated);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("用户风格配置解析失败");
        }
        if (document is null)
        {
            throw AppError.BadAuthRequest("用户风格配置解析失败");
        }
        if (document.Title.Trim().Length > 80)
        {
            throw AppError.BadAuthRequest("风格名称最多 80 个字");
        }
        if (document.Description.Trim().Length > 500)
        {
            throw AppError.BadAuthRequest("风格简介最多 500 个字");
        }
        if (document.Tags.Count > 20)
        {
            throw AppError.BadAuthRequest("风格标签最多 20 个");
        }
        if (Encoding.UTF8.GetByteCount(document.CoverURL) > 4096 ||
            Encoding.UTF8.GetByteCount(document.NegativePrompt) > 64 * 1024)
        {
            throw AppError.BadAuthRequest("风格封面或负面 Prompt 过大");
        }
        string coverURL = document.CoverURL.Trim().ToLowerInvariant();
        if (coverURL.Length > 0 &&
            !coverURL.StartsWith("http://", StringComparison.Ordinal) &&
            !coverURL.StartsWith("https://", StringComparison.Ordinal) &&
            !coverURL.StartsWith('/') &&
            !coverURL.StartsWith("data:image/", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("风格封面只支持 http(s)、站内路径或图片 Data URL");
        }
        if (presetId.Trim().Length > 0)
        {
            document.PresetID = presetId.Trim();
        }
        document.SourceProfileID = document.PresetID;
        document.Source = "user";
        document.Revision = revision;
        document.Title = document.Title.Trim();
        document.Description = document.Description.Trim();
        document.CoverURL = document.CoverURL.Trim();
        document.NegativePrompt = document.NegativePrompt.Trim();
        document.Tags = NonEmptyStyleProfileStrings(document.Tags);

        string encoded = JsonSerializer.Serialize(document);
        if (Encoding.UTF8.GetByteCount(encoded) > 256 * 1024)
        {
            throw AppError.BadAuthRequest("用户风格配置过大");
        }
        return (encoded, document);
    }

    /// <summary>对应 Go: <c>NonEmptyStyleProfileStrings</c>。</summary>
    internal static List<string> NonEmptyStyleProfileStrings(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string raw in values)
        {
            string value = raw.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        return result;
    }

    private static string StringField(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long NumberField(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
}

/// <summary>
/// 风格档案文档。对应 Go: <c>prompts.StyleProfileDocument</c>。
/// </summary>
/// <remarks>
/// 字段顺序即 Go 结构体顺序；序列化时必须保持，否则写库的 profile_json 与 Go 不逐字节一致。
/// </remarks>
public sealed class StyleProfileDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("presetId")]
    public string PresetID { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("negativePrompt")]
    public string NegativePrompt { get; set; } = "";

    [JsonPropertyName("coverUrl")]
    public string CoverURL { get; set; } = "";

    [JsonPropertyName("sourceProfileId")]
    public string SourceProfileID { get; set; } = "";

    [JsonPropertyName("selection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Selection { get; set; }

    [JsonPropertyName("assets")]
    public List<JsonElement> Assets { get; set; } = [];

    [JsonPropertyName("executionPolicy")]
    public string ExecutionPolicy { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }
}