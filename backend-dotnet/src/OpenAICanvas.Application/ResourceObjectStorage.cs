#nullable enable
using System.Globalization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>
/// 资源对应的对象存储设置解析：历史资源按 StorageSettingID/存储位置恢复凭据，
/// CDN 是具体存储位置的出口，切换位置后不得沿用。对应 Go: <c>ossSettingForResource</c> 等。
/// </summary>
internal static class ResourceObjectStorage
{
    /// <summary>对应 Go: <c>activeResourceOSSSetting</c>。返回归一化设置、存储位置 ID 与是否走云存储。</summary>
    public static async Task<(StorageChannelSettings Setting, string StorageSettingID, bool UseOSS)> ActiveForUserAsync(
        Repository repository, StorageSettingsService settings, string userID, CancellationToken cancellationToken)
    {
        // Go 的 ossSettingValue 是值类型：无记录等价零值，不会是 nil。
        StorageChannelSettings user = await settings.LatestUserChannelSettingsAsync(userID, cancellationToken)
            .ConfigureAwait(false) ?? new StorageChannelSettings();
        StorageChannelSettings system = await settings.PlatformChannelSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        bool userAllowed = user.Provider != StorageObjectChannel.S3Provider || system.AllowUserS3;
        if (user.Enabled && userAllowed)
        {
            ValidateActive(user, "用户 OSS 尚未启用", "你的 OSS 配置不完整");
            return (user, user.StorageLocationId, true);
        }
        if (!system.Enabled)
        {
            return (new StorageChannelSettings(), "", false);
        }
        ValidateActive(system, "管理员尚未启用 OSS", "平台 OSS 配置不完整，请联系管理员");
        return (system, system.StorageLocationId, true);
    }

    /// <summary>对应 Go: <c>ossSettingForResource</c>。</summary>
    public static async Task<StorageChannelSettings> ForResourceAsync(
        Repository repository, StorageSettingsService settings, string userID, Resource resource,
        CancellationToken cancellationToken)
    {
        StorageChannelSettings setting;
        if (resource.StorageSettingID.Length > 0)
        {
            // 密钥固定在资源绑定的历史存储位置；只有存储位置完全一致时才沿用当前 CDN 分发配置。
            setting = await LocationSettingsAsync(repository, resource.StorageSettingID, cancellationToken)
                .ConfigureAwait(false)
                ?? await settings.LatestUserChannelSettingsAsync(userID, cancellationToken).ConfigureAwait(false)
                ?? await settings.PlatformChannelSettingsAsync(cancellationToken).ConfigureAwait(false);
            StorageChannelSettings? current =
                await settings.LatestUserChannelSettingsAsync(userID, cancellationToken).ConfigureAwait(false)
                ?? await settings.PlatformChannelSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (StorageMatches(current, resource))
            {
                setting.CdnBaseUrl = current.CdnBaseUrl;
                setting.Delivery = new StorageDeliverySettings
                {
                    CdnAuthMode = current.Delivery.CdnAuthMode,
                    RequireCDN = current.Delivery.RequireCDN,
                    AllowPrivateProxy = current.Delivery.AllowPrivateProxy,
                };
            }
        }
        else
        {
            // 早期资源没有 StorageSettingID，按用户历史存储位置反查，不能把当前厂商配置猜给历史对象。
            setting = await HistoricalUserSettingAsync(repository, settings, userID, resource, cancellationToken)
                .ConfigureAwait(false)
                ?? await settings.PlatformChannelSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        string resourceProvider = resource.Provider.Trim().ToLowerInvariant();
        bool matches = StorageMatches(setting, resource);
        setting = ForProvider(setting, FirstNonEmpty(resource.Provider, setting.Provider));
        setting.Endpoint = FirstNonEmpty(resource.Endpoint, setting.Endpoint);
        setting.Bucket = FirstNonEmpty(resource.Bucket, setting.Bucket);
        if (!matches || (resourceProvider.Length > 0 && resourceProvider != setting.Provider))
        {
            setting.CdnBaseUrl = "";
        }
        if (setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("对象存储访问密钥不可用");
        }
        return setting;
    }

    /// <summary>按资源记录的 provider/endpoint/bucket 匹配用户历史设置。对应 Go: <c>userOSSSettingForResource</c>。</summary>
    private static async Task<StorageChannelSettings?> HistoricalUserSettingAsync(
        Repository repository, StorageSettingsService settings, string userID, Resource resource,
        CancellationToken cancellationToken)
    {
        foreach (StorageChannelSettings value in await settings
            .UserChannelSettingsHistoryAsync(userID, cancellationToken).ConfigureAwait(false))
        {
            if (StorageMatches(value, resource))
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>从存储位置登记表恢复归一化设置。对应 Go: <c>storageLocationValue</c>。</summary>
    public static async Task<StorageChannelSettings?> LocationSettingsAsync(
        Repository repository, string storageSettingID, CancellationToken cancellationToken)
    {
        StorageLocation? location = await repository.StorageLocationByIDAsync(
            storageSettingID, cancellationToken).ConfigureAwait(false);
        return location is null ? null : settingsFromJson(location.ValueJSON);
    }

    /// <summary>存储位置 JSON 兼容两种历史形状：嵌套 delivery（Go storage.Settings）与扁平（.NET Stored）。</summary>
    private static StorageChannelSettings? settingsFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        StorageChannelSettings? channel;
        FlatDeliveryView? flat;
        try
        {
            channel = System.Text.Json.JsonSerializer.Deserialize<StorageChannelSettings>(json);
            flat = System.Text.Json.JsonSerializer.Deserialize<FlatDeliveryView>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (channel is null)
        {
            return null;
        }
        if (flat is not null
            && (channel.Delivery.CdnAuthMode.Length == 0 && !channel.Delivery.RequireCDN && !channel.Delivery.AllowPrivateProxy)
            && (flat.CdnAuthMode.Length > 0 || flat.RequireCDN || flat.AllowPrivateProxy))
        {
            channel.Delivery = new StorageDeliverySettings
            {
                CdnAuthMode = flat.CdnAuthMode,
                RequireCDN = flat.RequireCDN,
                AllowPrivateProxy = flat.AllowPrivateProxy,
            };
        }
        return channel;
    }

    private sealed class FlatDeliveryView
    {
        [System.Text.Json.Serialization.JsonPropertyName("cdnAuthMode")]
        public string CdnAuthMode { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("requireCDN")]
        public bool RequireCDN { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("allowPrivateProxy")]
        public bool AllowPrivateProxy { get; set; }
    }

    /// <summary>provider/endpoint/bucket 三元组一致。对应 Go: <c>resourceStorageMatches</c>。</summary>
    public static bool StorageMatches(StorageChannelSettings setting, Resource resource)
    {
        setting = StorageObjectChannel.Normalize(setting.Clone());
        return setting.Provider == resource.Provider.Trim().ToLowerInvariant()
            && setting.Endpoint == resource.Endpoint.Trim().TrimEnd('/')
            && setting.Bucket == resource.Bucket.Trim();
    }

    /// <summary>历史厂商归档密钥切换。对应 Go: <c>ossSettingForProvider</c>。</summary>
    public static StorageChannelSettings ForProvider(StorageChannelSettings setting, string provider)
    {
        setting = StorageObjectChannel.Normalize(setting.Clone());
        provider = provider.Trim().ToLowerInvariant();
        if (provider.Length == 0 || provider == setting.Provider)
        {
            return setting;
        }
        if (setting.ArchivedCredentials?.TryGetValue(provider, out StorageArchivedCredentials? credentials) != true
            || credentials.AccessKeyId.Length == 0 || credentials.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("历史对象存储访问密钥不可用");
        }
        setting.Provider = provider;
        setting.AccessKeyId = credentials.AccessKeyId;
        setting.AccessKeySecret = credentials.AccessKeySecret;
        return setting;
    }

    /// <summary>启用与完整性校验。对应 Go: <c>validateActiveOSSSetting</c>。</summary>
    public static void ValidateActive(StorageChannelSettings setting, string disabledMessage, string incompleteMessage)
    {
        setting = StorageObjectChannel.Normalize(setting.Clone());
        if (!setting.Enabled)
        {
            throw AppError.BadAuthRequest(disabledMessage);
        }
        if (setting.Provider is not (StorageObjectChannel.AliyunProvider
            or StorageObjectChannel.TencentProvider or StorageObjectChannel.QiniuProvider
            or StorageObjectChannel.S3Provider))
        {
            throw AppError.BadAuthRequest("仅支持阿里云 OSS、腾讯云 COS、七牛云 Kodo 和通用 S3");
        }
        if (setting.Bucket.Length == 0 || setting.Endpoint.Length == 0
            || setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw AppError.BadAuthRequest(incompleteMessage);
        }
    }

    /// <summary>云对象键。对应 Go: <c>ossObjectKey</c>。</summary>
    public static string ObjectKey(StorageChannelSettings setting, string userID, string kind, string fileName, string mimeType, DateTime now)
    {
        setting = StorageObjectChannel.Normalize(setting.Clone());
        string extension = FileExtension(fileName, mimeType, kind);
        return string.Join('/',
            new[] { setting.PathPrefix, "users", SafeObjectSegment(userID), kind, now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), IdGenerator.NewId() + extension }
                .Where(part => part.Length > 0));
    }

    /// <summary>文件扩展名推断。对应 Go: <c>resourceFileExtension</c>。</summary>
    public static string FileExtension(string fileName, string mimeType, string kind)
    {
        string raw = System.IO.Path.GetExtension((fileName ?? "").Trim());
        if (raw.Length > 1)
        {
            return raw.ToLowerInvariant();
        }
        string clean = (mimeType ?? "").Split(';')[0].Trim();
        if (MimeTypes.TryGetValue(clean, out string? byMime))
        {
            return byMime;
        }
        return kind switch
        {
            "image" => ".png",
            "video" => ".mp4",
            "audio" => ".mp3",
            _ => ".bin",
        };
    }

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.Ordinal)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["video/mp4"] = ".mp4",
        ["video/quicktime"] = ".mov",
        ["video/webm"] = ".webm",
        ["audio/mpeg"] = ".mp3",
        ["audio/wav"] = ".wav",
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
    };

    private static string SafeObjectSegment(string value)
    {
        string cleaned = new(value.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return cleaned.Length == 0 ? "anonymous" : cleaned;
    }

    private static string FirstNonEmpty(string first, string second) => first.Trim().Length > 0 ? first : second;
}
