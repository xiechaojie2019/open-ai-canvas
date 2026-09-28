#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>素材库分页。对应 Go: <c>app.UserAssetPage</c>（字段顺序即输出顺序）。</summary>
public sealed class UserAssetPageDto
{
    [JsonPropertyName("assets")]
    public IReadOnlyList<JsonElement> Assets { get; init; } = [];

    [JsonPropertyName("kindCounts")]
    public IReadOnlyDictionary<string, long> KindCounts { get; init; } = new Dictionary<string, long>();

    [JsonPropertyName("categoryCounts")]
    public IReadOnlyDictionary<string, long> CategoryCounts { get; init; } = new Dictionary<string, long>();

    [JsonPropertyName("folderCounts")]
    public IReadOnlyDictionary<string, long> FolderCounts { get; init; } = new Dictionary<string, long>();

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>素材分类 DTO（Go 直接序列化 model.AssetFolder，字段顺序即输出顺序）。</summary>
public sealed class AssetFolderDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("position")]
    public long Position { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }
}

/// <summary>
/// 素材库域服务。对应 Go: <c>app/asset_library.go</c> 与 <c>internal/canvas</c> 的素材部分
/// （AssetFromJSON / validateUserAssetDocument / ClientAssetPayload / UpsertUserAsset）。
/// </summary>
/// <remarks>
/// DELETE /assets/:id 的资源级联清理由 <see cref="ResourceDeleteService"/> 负责，
/// 入口复用用户归属、引用快照、Outbox 与本地物理资源安全校验。
/// </remarks>
public static class AssetLibraryDomain
{
    /// <summary>对应 Go: <c>userAssetKinds</c>。</summary>
    private static readonly HashSet<string> UserAssetKinds =
        new(StringComparer.Ordinal) { "text", "image", "video", "audio", "model", "entity" };

    /// <summary>对应 Go: <c>AssetFromJSON</c>。</summary>
    public static Asset AssetFromJSON(string userId, string rawText)
    {
        UserDataService.ValidateSyncedPayload(rawText, "素材");
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("素材数据格式错误");
        }
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("素材数据格式错误");
        }

        DateTime now = DateTime.UtcNow;
        DateTime createdAt = UserDataService.ParseClientTime(UserDataService.PayloadString(payload, "createdAt"), now);
        DateTime updatedAt = UserDataService.ParseClientTime(UserDataService.PayloadString(payload, "updatedAt"), createdAt);

        string id = UserDataService.PayloadString(payload, "id").Trim();
        if (id.Length == 0)
        {
            id = IdGenerator.NewId();
        }
        if (id.Length > AssetConstraints.IDMaxLength)
        {
            throw AppError.BadAuthRequest("素材 ID 不能超过 80 个字符");
        }
        string primaryVersionID = UserDataService.PayloadString(payload, "primaryVersionID").Trim();
        if (primaryVersionID.Length > 36)
        {
            throw AppError.BadAuthRequest("素材主版本 ID 不能超过 36 个字符");
        }
        ValidateUserAssetDocument(payload);
        string kind = UserDataService.PayloadString(payload, "kind").Trim();
        return new Asset
        {
            ID = id,
            UserID = userId,
            FolderID = UserDataService.PayloadString(payload, "folderId").Trim(),
            Kind = kind,
            Category = NormalizeAssetCategory(UserDataService.PayloadString(payload, "category"), kind),
            Status = ParseStatus(UserDataService.PayloadString(payload, "status")),
            PrimaryVersionID = primaryVersionID,
            Title = UserDataService.PayloadString(payload, "title").Trim(),
            PayloadJSON = rawText,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
    }

    /// <summary>对应 Go: <c>validateUserAssetDocument</c>。</summary>
    private static void ValidateUserAssetDocument(JsonElement payload)
    {
        string kind = RequiredStringField(payload, "kind").Trim();
        if (!UserAssetKinds.Contains(kind))
        {
            throw AppError.BadAuthRequest("不支持的素材类型");
        }
        _ = RequiredStringField(payload, "title");
        _ = RequiredStringField(payload, "coverUrl");
        ValidateTags(payload);
        JsonElement data = RequiredObjectField(payload, "data");
        ValidateUserAssetData(kind, data);
    }

    /// <summary>对应 Go: <c>validateUserAssetTags</c>。</summary>
    private static void ValidateTags(JsonElement payload)
    {
        if (!payload.TryGetProperty("tags", out JsonElement tags) ||
            tags.ValueKind != JsonValueKind.Array)
        {
            throw AppError.BadAuthRequest("素材 tags 必须是字符串数组");
        }
        foreach (JsonElement tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String)
            {
                throw AppError.BadAuthRequest("素材 tags 必须是字符串数组");
            }
        }
    }

    /// <summary>对应 Go: <c>validateUserAssetData</c>。</summary>
    private static void ValidateUserAssetData(string kind, JsonElement data)
    {
        switch (kind)
        {
            case "text":
                _ = RequiredStringField(data, "content");
                break;
            case "image":
                RequireMediaLocator(data, "dataUrl", "图片");
                RequireNumberField(data, "width");
                RequireNumberField(data, "height");
                RequireNumberField(data, "bytes");
                RequireNonEmptyString(data, "mimeType");
                break;
            case "video":
                RequireMediaLocator(data, "url", "视频");
                RequireNumberField(data, "width");
                RequireNumberField(data, "height");
                RequireNumberField(data, "bytes");
                RequireNonEmptyString(data, "mimeType");
                break;
            case "audio":
                RequireMediaLocator(data, "url", "音频");
                RequireNumberField(data, "bytes");
                RequireNonEmptyString(data, "mimeType");
                break;
            case "model":
                RequireMediaLocator(data, "url", "模型");
                RequireNumberField(data, "bytes");
                RequireNonEmptyString(data, "mimeType");
                RequireNonEmptyString(data, "fileName");
                break;
            case "entity":
                _ = RequiredObjectField(data, "definition");
                break;
            default:
                throw AppError.BadAuthRequest("不支持的素材类型");
        }
    }

    /// <summary>对应 Go: <c>requireMediaLocator</c>。</summary>
    private static void RequireMediaLocator(JsonElement data, string primaryKey, string label)
    {
        string primary = OptionalStringField(data, primaryKey);
        string storageKey = OptionalStringField(data, "storageKey");
        if (primary.Trim().Length == 0 && storageKey.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest($"{label}素材缺少 {primaryKey} 或 storageKey");
        }
    }

    private static string RequiredStringField(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw element.ValueKind == JsonValueKind.Undefined
                ? AppError.BadAuthRequest("素材缺少 " + key + " 字段")
                : AppError.BadAuthRequest("素材字段 " + key + " 必须是字符串");
        }
        return element.GetString() ?? "";
    }

    private static string OptionalStringField(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return "";
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            throw AppError.BadAuthRequest("素材字段 " + key + " 必须是字符串");
        }
        return element.GetString() ?? "";
    }

    private static void RequireNonEmptyString(JsonElement payload, string key)
    {
        string value = RequiredStringField(payload, key);
        if (value.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("素材字段 " + key + " 不能为空");
        }
    }

    private static void RequireNumberField(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out JsonElement element))
        {
            throw AppError.BadAuthRequest("素材缺少 " + key + " 字段");
        }
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw AppError.BadAuthRequest("素材字段 " + key + " 必须是数字");
        }
        if (element.ValueKind != JsonValueKind.Number ||
            double.IsNaN(element.GetDouble()) || double.IsInfinity(element.GetDouble()) ||
            element.GetDouble() < 0)
        {
            throw AppError.BadAuthRequest("素材字段 " + key + " 必须是非负数字");
        }
    }

    private static JsonElement RequiredObjectField(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out JsonElement element))
        {
            throw AppError.BadAuthRequest("素材缺少 " + key + " 字段");
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("素材字段 " + key + " 必须是对象");
        }
        return element;
    }

    /// <summary>对应 Go: <c>model.NormalizeAssetCategory</c>。</summary>
    private static string NormalizeAssetCategory(string value, string kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "character":
                return "character";
            case "environment":
                return "environment";
            case "prop":
            case "wardrobe":
            case "weapon":
            case "accessory":
                return "prop";
            case "material":
            case "style":
                return "material";
            case "other":
                return "other";
        }
        switch (kind.Trim().ToLowerInvariant())
        {
            case "entity":
                return "character";
            case "video":
            case "image":
            case "audio":
            case "model":
            case "text":
                break;
        }
        return "other";
    }

    private static string ParseStatus(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? AssetVersionStatus.AssetVersionStatusConfirmed : trimmed;
    }

    /// <summary>
    /// 把素材记录补齐为前端素材合同可消费的 JSON（coverUrl/tags/时间戳/正数尺寸）。
    /// 对应 Go: <c>canvas.ClientAssetPayload</c>。
    /// </summary>
    public static JsonElement? ClientAssetPayload(Asset asset)
    {
        string raw = asset.PayloadJSON.Trim();
        if (raw.Length == 0)
        {
            return null;
        }
        JsonElement element;
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }

        Dictionary<string, JsonElement> payload = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }
        if (!payload.ContainsKey("coverUrl"))
        {
            string? cover = DeriveAssetCoverURL(payload);
            if (cover is not null)
            {
                payload["coverUrl"] = JsonSerializer.SerializeToElement(cover);
            }
            else
            {
                payload["coverUrl"] = JsonSerializer.SerializeToElement("");
            }
        }
        if (!payload.ContainsKey("tags"))
        {
            payload["tags"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
        }
        if (!payload.ContainsKey("createdAt"))
        {
            payload["createdAt"] = JsonSerializer.SerializeToElement(FormatClientAssetTime(asset.CreatedAt));
        }
        if (!payload.ContainsKey("updatedAt"))
        {
            payload["updatedAt"] = JsonSerializer.SerializeToElement(FormatClientAssetTime(asset.UpdatedAt));
        }
        if (payload.TryGetValue("data", out JsonElement dataElement) &&
            dataElement.ValueKind == JsonValueKind.Object)
        {
            string? kind = payload.TryGetValue("kind", out JsonElement kindElement) &&
                           kindElement.ValueKind == JsonValueKind.String
                ? kindElement.GetString()
                : null;
            if (kind is "image" or "video")
            {
                Dictionary<string, JsonElement> data = new(StringComparer.Ordinal);
                foreach (JsonProperty property in dataElement.EnumerateObject())
                {
                    data[property.Name] = property.Value.Clone();
                }
                EnsurePositiveAssetDimension(data, "width");
                EnsurePositiveAssetDimension(data, "height");
                NormalizeResourceLocator(data);
                payload["data"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, JsonElement>(data, StringComparer.Ordinal));
            }
        }
        return JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>(payload.OrderBy(pair => pair.Key, StringComparer.Ordinal), StringComparer.Ordinal));
    }

    /// <summary>对应 Go: <c>deriveAssetCoverURL</c>。</summary>
    private static string? DeriveAssetCoverURL(Dictionary<string, JsonElement> payload)
    {
        if (payload.TryGetValue("coverUrl", out JsonElement cover) && cover.ValueKind == JsonValueKind.String)
        {
            string value = cover.GetString() ?? "";
            if (value.Trim().Length > 0)
            {
                return value;
            }
        }
        if (!payload.TryGetValue("data", out JsonElement dataElement) ||
            dataElement.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        foreach (string key in new[] { "dataUrl", "url" })
        {
            if (dataElement.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? "";
                if (text.Trim().Length > 0)
                {
                    return text;
                }
            }
        }
        if (dataElement.TryGetProperty("storageKey", out JsonElement storageKey) &&
            storageKey.ValueKind == JsonValueKind.String)
        {
            return ResourceURLFromStorageKey(storageKey.GetString() ?? "");
        }
        return "";
    }

    /// <summary>对应 Go: <c>resourceURLFromStorageKey</c>。</summary>
    private static string ResourceURLFromStorageKey(string storageKey)
    {
        storageKey = storageKey.Trim();
        if (!storageKey.StartsWith("resource:", StringComparison.Ordinal))
        {
            return "";
        }
        string resourceID = storageKey["resource:".Length..].Trim();
        if (resourceID.Length == 0)
        {
            return "";
        }
        return "/api/resources/" + resourceID + "/file";
    }

    /// <summary>让带 resource: 引用的历史素材统一使用稳定资源地址。</summary>
    private static void NormalizeResourceLocator(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("storageKey", out JsonElement storageKey) ||
            storageKey.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string url = ResourceURLFromStorageKey(storageKey.GetString() ?? "");
        if (url.Length == 0)
        {
            return;
        }

        foreach (string key in new[] { "dataUrl", "url" })
        {
            if (data.ContainsKey(key))
            {
                data[key] = JsonSerializer.SerializeToElement(url);
            }
        }
    }

    private static void EnsurePositiveAssetDimension(Dictionary<string, JsonElement> data, string key)
    {
        bool positive = data.TryGetValue(key, out JsonElement value) &&
                        value.ValueKind == JsonValueKind.Number &&
                        value.GetDouble() > 0;
        if (!positive)
        {
            data[key] = JsonSerializer.SerializeToElement(1);
        }
    }

    /// <summary>对应 Go: <c>formatClientAssetTime</c>（RFC3339Nano，UTC）。</summary>
    private static string FormatClientAssetTime(DateTime value)
    {
        if (value == default)
        {
            value = DateTime.UtcNow;
        }
        return FormatRfc3339Nano(value);
    }

    /// <summary>RFC3339Nano：小数位去尾零（Go 布局 2006-01-02T15:04:05.999999999Z07:00）。</summary>
    internal static string FormatRfc3339Nano(DateTime value)
    {
        DateTime utc = value.ToUniversalTime();
        string baseText = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        string fraction = (utc.Ticks % TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)
            .PadLeft(7, '0')
            .TrimEnd('0');
        return fraction.Length == 0
            ? baseText + "Z"
            : baseText + "." + fraction + "Z";
    }

    /// <summary>资产约束别名。对应 Go: <c>model.AssetIDMaxLength</c>。</summary>
    internal static class AssetConstraints
    {
        public const int IDMaxLength = 80;
    }
}
