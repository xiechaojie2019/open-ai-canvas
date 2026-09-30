#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>资源投递选项。对应 Go: <c>service.ResourceDeliveryOptions</c>。</summary>
public sealed record ResourceDeliveryOptions(
    bool ForceDirect = false,
    bool ForceProxy = false);

/// <summary>资源投递结果。对应 Go: <c>assets.ResourceDelivery</c>。</summary>
public sealed record ResourceDelivery(
    Resource Resource,
    string RedirectURL,
    Stream? Stream,
    string ContentRange,
    string AcceptRanges);

/// <summary>
/// 资源流（含状态码、长度与 Range 信息）。
/// 对应 Go: <c>internal/assets/types.go</c> 的 <c>ResourceStream</c>。
/// </summary>
public sealed record ResourceStream(
    Resource Resource,
    Stream Body,
    string ContentRange,
    string AcceptRanges)
{
    /// <summary>对应 Go 的 <c>StatusCode</c>，本地投递固定 200。</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>对应 Go 的 <c>ContentLength</c>。</summary>
    public long ContentLength { get; init; }
}

/// <summary>账号文件存储用量。对应 Go: <c>service.AccountFileStorageUsage</c>。</summary>
public sealed record AccountFileStorageUsage(
    [property: System.Text.Json.Serialization.JsonPropertyName("usedBytes")] long UsedBytes,
    [property: System.Text.Json.Serialization.JsonPropertyName("totalBytes")] long TotalBytes);

/// <summary>
/// 资源访问请求项。对应 Go: <c>assets.AccessRequest</c>（内嵌 AccessOptions）。
/// </summary>
public sealed class ResourceAccessRequest
{
    [JsonPropertyName("resourceId")] public string ResourceID { get; set; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    [JsonPropertyName("variant")] public string Variant { get; set; } = "";
    [JsonPropertyName("downloadName")] public string DownloadName { get; set; } = "";
}

/// <summary>
/// 资源访问意图。只承载用途，不含任何客户端可控的鉴权或传输覆盖。
/// 对应 Go: <c>assets.AccessOptions</c>。
/// </summary>
public sealed record ResourceAccessOptions(
    string Purpose = "",
    string Variant = "",
    string DownloadName = "")
{
    /// <summary>授权过期上限（公开签名下发路径使用）。对应 Go 的 <c>ExpiresAt</c> 内部字段。</summary>
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>资源访问描述。对应 Go: <c>assets.ResourceAccess</c>。</summary>
public sealed record ResourceAccess(
    [property: JsonPropertyName("resourceId")] string ResourceID,
    [property: JsonPropertyName("requestedVariant")] string RequestedVariant,
    [property: JsonPropertyName("actualVariant")] string ActualVariant,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("delivery")] string Delivery,
    [property: JsonPropertyName("issuedAt")] DateTime IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTime? ExpiresAt,
    [property: JsonPropertyName("refreshAt")] DateTime RefreshAt,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("fallbackReason"), GoOmitEmpty] string FallbackReason = "");

/// <summary>单项访问失败。对应 Go: <c>app.ResourceAccessFailure</c>。</summary>
public sealed record ResourceAccessFailure(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("msg")] string Message);

/// <summary>批量访问的逐项结果。对应 Go: <c>app.ResourceAccessResult</c>。</summary>
public sealed record ResourceAccessResult(
    [property: JsonPropertyName("resourceId")] string ResourceID,
    [property: JsonPropertyName("access"), GoOmitEmpty] ResourceAccess? Access,
    [property: JsonPropertyName("error"), GoOmitEmpty] ResourceAccessFailure? Error);

/// <summary>
/// 资源域服务。对应 Go: <c>app/resource.go</c> 的 CRUD 与投递部分。
/// </summary>
/// <remarks>
/// multipart 上传与远端下载依赖文件系统 + 出站 HTTP 客户端（阶段 5 资源上传节点）；
/// 本轮落地 7 条 CRUD/投递路由（列表/详情/存储用量/OSS 直链/文件下发/ARK 同步/导入），
/// multipart 上传三件套（uploads/chunks/complete）留待资源节点一并接线。
/// </remarks>
public sealed class ResourceDomainService
{
    private const long Gigabyte = 1L << 30;

    // 资源访问合同常量。对应 Go: assets.AccessPurpose / ResourceVariant / DeliveryMode。
    private const string PurposeDisplay = "display";
    private const string PurposeCopy = "copy";
    private const string PurposeDownload = "download";
    private const string PurposeProcess = "browser-process";
    private const string PurposeProvider = "provider-input";
    private const string VariantOriginal = "original";
    private const string VariantPlayback = "playback";
    private const string DeliveryLocal = "platform-local";
    private const string DeliveryCdn = "cdn";
    private const string DeliveryOrigin = "origin";
    private const string DeliveryProxy = "platform-proxy";
    private const string PlaybackStatusReady = "ready";

    /// <summary>本地投递描述符版本。对应 Go: <c>storage.DeliveryRevision</c>（零值 Settings）——
    /// net8 后端本地路径没有可配置的分发项，固化为常量摘要，仅供前端访问描述失效比较。</summary>
    private static readonly string LocalDeliveryRevision = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData("platform-local"u8))[..16].ToLowerInvariant();

    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _policyProvider;
    private readonly string _dataDir;
    private readonly VideoPlaybackService? _playback;
    private readonly StorageSettingsService? _storageSettings;

    public ResourceDomainService(
        Repository repository, IRuntimePolicyProvider policyProvider, string? dataDir = null,
        Func<byte[]?>? settingsEncryptionKey = null, VideoPlaybackService? playback = null,
        StorageSettingsService? storageSettings = null)
    {
        _repository = repository;
        _policyProvider = policyProvider;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
        _playback = playback;
        _storageSettings = storageSettings;
        if (settingsEncryptionKey is not null)
        {
            SettingsEncryptionKey = settingsEncryptionKey;
        }
    }

    /// <summary>用户资源列表（按更新时间倒序，剥离 publicURL）。对应 Go: <c>Resources</c>。</summary>
    public async Task<IReadOnlyList<Resource>> ListResourcesAsync(
        string userId, long limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Resource> resources = await _repository.ResourcesAsync(userId, limit, cancellationToken)
            .ConfigureAwait(false);
        foreach (Resource resource in resources)
        {
            resource.PublicURL = "";
        }
        return resources;
    }

    /// <summary>账号存储用量（按包策略上限）。对应 Go: <c>AccountFileStorageUsage</c>。</summary>
    public async Task<AccountFileStorageUsage> AccountFileStorageUsageAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        RuntimeResourcePolicy resource = _policyProvider.Current().Resource;
        long usedBytes = await _repository.UserStoredFileBytesAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return new AccountFileStorageUsage(usedBytes, Gigabyte * resource.StoredFileGB);
    }

    /// <summary>单个资源（剥离 publicURL）。对应 Go: <c>Resource</c>。</summary>
    public async Task<Resource?> GetResourceAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(userId, id, cancellationToken)
            .ConfigureAwait(false);
        if (resource is not null)
        {
            resource.PublicURL = "";
        }
        return resource;
    }

    /// <summary>同步到方舟私有资产。对应 Go: <c>SyncResourceToArkPrivateAsset</c>（依赖 #25 云 SDK，留待）。</summary>
    public Task SyncResourceToArkPrivateAssetAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Ark 私有资产同步依赖云 SDK（待确认 #25）");



    /// <summary>短时签名直链（云 provider）。对应 Go: <c>DirectResourceURL</c>。</summary>
    public async Task<string> DirectResourceUrlAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(
            userId, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        return await DirectLocalResourceUrlAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本地资源投递（含 Range 与 ETag）。对应 Go: <c>PrepareResourceDelivery</c>。</summary>
    public async Task<ResourceDelivery> PrepareResourceDeliveryAsync(
        string userId, string resourceId, ResourceDeliveryOptions options,
        CancellationToken cancellationToken = default,
        string? rangeHeader = null)
    {
        Resource? resource = await _repository.ResourceForUserAsync(
            userId, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("资源尚未上传完成");
        }
        if (!IsLocalProvider(resource.Provider))
        {
            // 云 provider：CDN/公网源站签发 307 直链；私有源站仅在代理投递时走进程内流。
            ResourceAccess access = await ResolveAccessAsync(
                resource,
                new ResourceAccessOptions(PurposeDisplay, VariantOriginal, DownloadName: ""),
                DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            if (access.Delivery is DeliveryCdn or DeliveryOrigin)
            {
                return new ResourceDelivery(
                    resource, RedirectURL: access.Url, Stream: null, ContentRange: "", AcceptRanges: "bytes");
            }
        }
        ResourceStream stream = await OpenResourceRangeAsync(resource, rangeHeader, cancellationToken)
            .ConfigureAwait(false);
        return new ResourceDelivery(
            resource, RedirectURL: "", Stream: stream.Body, ContentRange: stream.ContentRange,
            AcceptRanges: stream.AcceptRanges);
    }

    // ------------------------------------------------------------ 资源访问合同（批量签发）

    /// <summary>
    /// 批量签发资源访问描述。对应 Go: <c>app.ResourceAccessBatch</c>。
    /// 逐项独立成功/失败：单项错误写入 <see cref="ResourceAccessResult.Error"/>，
    /// 只有未登录或请求项数超出 1–100 才抛出顶层错误。
    /// </summary>
    public async Task<IReadOnlyList<ResourceAccessResult>> ResourceAccessBatchAsync(
        string userId, IReadOnlyList<ResourceAccessRequest> requests, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userId))
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (requests.Count == 0 || requests.Count > 100)
        {
            throw AppError.BadAuthRequest("每批资源访问请求须为 1–100 项");
        }
        List<ResourceAccessResult> results = new(requests.Count);
        foreach (ResourceAccessRequest request in requests)
        {
            results.Add(await ResolveAccessItemAsync(userId, request, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<ResourceAccessResult> ResolveAccessItemAsync(
        string userId, ResourceAccessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // 与 Go 相同：调用方已通过登录鉴权，逐项再经 ResourceForUser 归属校验，
            // 因此 provider-input 可以在这里安全签发，浏览器侧模型适配器与其他
            // 资源消费方共用同一访问合同。
            ResourceAccessOptions options = NormalizeAccessOptions(
                new ResourceAccessOptions(request.Purpose, request.Variant, request.DownloadName),
                allowProvider: true);
            Resource? resource = await _repository.ResourceForUserAsync(
                userId, request.ResourceID, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                throw AppError.NotFound("资源不存在或不可访问");
            }
            return new ResourceAccessResult(
                request.ResourceID,
                await ResolveAccessAsync(resource, options, DateTime.UtcNow, cancellationToken).ConfigureAwait(false),
                Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AppError appError)
        {
            return new ResourceAccessResult(
                request.ResourceID, Access: null,
                new ResourceAccessFailure(appError.Code, appError.Reason, appError.Message));
        }
        catch (Exception)
        {
            return new ResourceAccessResult(
                request.ResourceID, Access: null,
                new ResourceAccessFailure(500, "resource_access_failed", "资源访问失败"));
        }
    }

    /// <summary>访问意图归一化。对应 Go: <c>assets.NormalizeAccessOptions</c>。</summary>
    private static ResourceAccessOptions NormalizeAccessOptions(ResourceAccessOptions options, bool allowProvider)
    {
        string purpose = options.Purpose.Length == 0 ? PurposeDisplay : options.Purpose;
        switch (purpose)
        {
            case PurposeDisplay or PurposeCopy or PurposeDownload or PurposeProcess:
                break;
            case PurposeProvider:
                if (!allowProvider)
                {
                    throw AppError.Forbidden("不允许请求模型输入凭据");
                }
                break;
            default:
                throw AppError.BadAuthRequest("资源访问用途无效");
        }
        string variant = options.Variant.Length == 0 ? VariantOriginal : options.Variant;
        if (variant is not (VariantOriginal or VariantPlayback))
        {
            throw AppError.BadAuthRequest("资源变体无效");
        }
        if (purpose is PurposeCopy or PurposeDownload or PurposeProvider)
        {
            variant = VariantOriginal;
        }
        if (purpose != PurposeDownload)
        {
            options = options with { DownloadName = "" };
        }
        return options with { Purpose = purpose, Variant = variant };
    }

    /// <summary>
    /// 解析单项访问描述。对应 Go: <c>resolveResourceAccess</c> + <c>assets.ResolveAccess</c>
    /// 的本地存储路径；云 provider 投递走 <see cref="ResolveRemoteAccessAsync"/> 的 CDN/源站/代理策略。
    /// </summary>
    private async Task<ResourceAccess> ResolveAccessAsync(
        Resource resource, ResourceAccessOptions options, DateTime now, CancellationToken cancellationToken)
    {
        options = NormalizeAccessOptions(options, allowProvider: true);
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw new AppError(409, "资源尚未上传完成", reason: "resource_not_ready");
        }
        if (!IsLocalProvider(resource.Provider))
        {
            return await ResolveRemoteAccessAsync(resource, options, now, cancellationToken).ConfigureAwait(false);
        }
        TimeSpan ttl = options.Purpose == PurposeProvider ? TimeSpan.FromHours(4) : TimeSpan.FromMinutes(5);
        DateTime expires = now + ttl;
        if (options.ExpiresAt is DateTime capped && capped < expires)
        {
            expires = capped;
        }
        if (expires <= now)
        {
            throw AppError.Forbidden("资源授权已过期");
        }
        string actualVariant = VariantOriginal;
        string fallbackReason = "";
        if (options.Variant == VariantPlayback
            && resource.PlaybackStatus == PlaybackStatusReady
            && !string.IsNullOrEmpty(resource.PlaybackObjectKey))
        {
            actualVariant = VariantPlayback;
        }
        else if (options.Variant == VariantPlayback)
        {
            fallbackReason = "playback_not_ready";
        }
        string url = await SignedResourceAccessUrlAsync(
            resource, actualVariant, expires, publicBaseUrl: options.Purpose == PurposeProvider,
            cancellationToken).ConfigureAwait(false);
        TimeSpan remaining = expires - now;
        return new ResourceAccess(
            resource.ID, options.Variant, actualVariant, url, DeliveryLocal, now, expires,
            now + TimeSpan.FromTicks(remaining.Ticks * 4 / 5),
            resource.ETag + ":" + resource.PlaybackStatus + ":" + LocalDeliveryRevision,
            fallbackReason);
    }

    /// <summary>
    /// 云 provider 的投递策略。对应 Go: <c>assets.ResolveAccess</c> 的远程分支——
    /// CDN 优先、RequireCDN 显式失败、公网源站直链、provider-input 允许私有源站代理。
    /// </summary>
    private async Task<ResourceAccess> ResolveRemoteAccessAsync(
        Resource resource, ResourceAccessOptions options, DateTime now, CancellationToken cancellationToken)
    {
        if (_storageSettings is null)
        {
            throw new InvalidOperationException("对象存储通道未注入存储设置服务");
        }
        StorageChannelSettings setting;
        try
        {
            setting = await ResourceObjectStorage
                .ForResourceAsync(_repository, _storageSettings, resource.UserID, resource, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new AppError(503, "无法解析资源实际存储位置：" + error.Message + " @ "
                + string.Join(" | ", (error.StackTrace ?? "").Split(new[] { (char)10 }).Take(4).Select(f => f.Trim())),
                reason: "resource_location_unresolved", cause: error);
        }
        TimeSpan ttl = options.Purpose == PurposeProvider ? TimeSpan.FromHours(4) : TimeSpan.FromMinutes(5);
        DateTime expires = now + ttl;
        if (options.ExpiresAt is DateTime capped && capped < expires)
        {
            expires = capped;
        }
        if (expires <= now)
        {
            throw AppError.Forbidden("资源授权已过期");
        }
        string revision = resource.ETag + ":" + resource.PlaybackStatus + ":"
            + StorageObjectChannel.DeliveryRevision(setting);
        if (options.Purpose == PurposeDownload)
        {
            if (!StorageObjectChannel.PublicOrigin(setting))
            {
                throw new AppError(503, "对象存储源站不可由浏览器直连，无法在不占用服务器带宽的前提下下载",
                    reason: "resource_origin_private");
            }
            string downloadUrl = await StorageObjectChannel.SignedOriginObjectDownloadUrlAsync(
                setting, resource.ObjectKey, expires, options.DownloadName).ConfigureAwait(false);
            return new ResourceAccess(resource.ID, options.Variant, VariantOriginal, downloadUrl,
                DeliveryOrigin, now, expires, now + TimeSpan.FromTicks((expires - now).Ticks * 4 / 5),
                revision);
        }
        if (StorageObjectChannel.CdnEnabled(setting))
        {
            string cdnUrl = StorageObjectChannel.SignCdnUrl(setting, resource.ObjectKey, expires);
            return new ResourceAccess(resource.ID, options.Variant, VariantOriginal, cdnUrl,
                DeliveryCdn, now,
                setting.Delivery.CdnAuthMode == "public" ? null : expires,
                now + TimeSpan.FromTicks((expires - now).Ticks * 4 / 5), revision);
        }
        if (setting.Delivery.RequireCDN)
        {
            throw new AppError(503, "CDN 访问鉴权未配置，请检查存储分发设置", reason: "resource_cdn_unconfigured");
        }
        if (StorageObjectChannel.PublicOrigin(setting))
        {
            string originUrl = await StorageObjectChannel.SignedOriginObjectUrlAsync(
                setting, resource.ObjectKey, expires).ConfigureAwait(false);
            return new ResourceAccess(resource.ID, options.Variant, VariantOriginal, originUrl,
                DeliveryOrigin, now, expires, now + TimeSpan.FromTicks((expires - now).Ticks * 4 / 5),
                revision, setting.CdnBaseUrl.Length > 0 ? "cdn_auth_unconfigured" : "");
        }
        if (options.Purpose == PurposeProvider && setting.Delivery.AllowPrivateProxy)
        {
            // 只有服务端 provider-input 允许私有源站代理；浏览器侧流量绝不中转媒体字节。
            string proxyUrl = await SignedResourceAccessUrlAsync(
                resource, VariantOriginal, expires, publicBaseUrl: true, cancellationToken).ConfigureAwait(false);
            return new ResourceAccess(resource.ID, options.Variant, VariantOriginal, proxyUrl,
                DeliveryProxy, now, expires, now + TimeSpan.FromTicks((expires - now).Ticks * 4 / 5),
                revision, "private_origin");
        }
        throw new AppError(503, "对象存储源站不可由浏览器直连，请配置公网 OSS 源站或 CDN",
            reason: "resource_origin_private");
    }

    /// <summary>
    /// 签发平台公开下发地址。对应 Go: <c>signedResourceAccessURL</c>；
    /// provider-input 需要模型上游可读的绝对 HTTPS 地址，其余用途返回受控相对路径。
    /// </summary>
    /// <summary>
    /// 供模型上游读取的绝对资源地址（provider 参考素材 URL 路径）。
    /// 对应 Go: <c>providerResourceURL</c>——本地资源走平台签名 URL（要求 HTTPS 公网地址）；
    /// 云资源走完整访问策略（公网源站签 OSS 直链、CDN 优先、私有源站代理回退）。
    /// </summary>
    public async Task<string> ProviderResourceUrlAsync(
        Resource resource, DateTime expires, CancellationToken cancellationToken = default)
    {
        if (IsLocalProvider(resource.Provider))
        {
            return await SignedResourceAccessUrlAsync(
                resource, "original", expires, publicBaseUrl: true, cancellationToken).ConfigureAwait(false);
        }
        ResourceAccess access = await ResolveRemoteAccessAsync(
            resource,
            new ResourceAccessOptions(PurposeProvider, VariantOriginal, DownloadName: ""),
            DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return access.Url;
    }

    private async Task<string> SignedResourceAccessUrlAsync(
        Resource resource, string variant, DateTime expires, bool publicBaseUrl, CancellationToken cancellationToken)
    {
        string expiry = new DateTimeOffset(expires).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string signature = ComputePublicResourceSignature(resource.ID, variant, expiry);
        string path = "/api/public/resources/" + Uri.EscapeDataString(resource.ID) + "/file";
        string query = "expires=" + Uri.EscapeDataString(expiry)
            + "&signature=" + Uri.EscapeDataString(signature)
            + "&variant=" + Uri.EscapeDataString(variant);
        if (!publicBaseUrl)
        {
            return path + "?" + query;
        }
        Uri baseUri = await PublicResourceBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(baseUri.Scheme, "https", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("模型读取平台资源需要配置 HTTPS 公网访问地址");
        }
        return new Uri(baseUri, path + "?" + query).ToString();
    }

    /// <summary>
    /// 平台公网访问基地址。对应 Go: <c>publicResourceBaseURL</c> +
    /// <c>validatePublicResourceBaseURL</c>（平台存储设置优先，其次 CANVAS_PUBLIC_BASE_URL）。
    /// </summary>
    private async Task<Uri> PublicResourceBaseUrlAsync(CancellationToken cancellationToken)
    {
        string platformBaseUrl = _storageSettings is null
            ? ""
            : await _storageSettings.PlatformPublicBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        string envBaseUrl = Environment.GetEnvironmentVariable("CANVAS_PUBLIC_BASE_URL") ?? "";
        string raw = platformBaseUrl.Length > 0 ? platformBaseUrl : envBaseUrl;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw AppError.BadAuthRequest("服务器本地存储尚未配置服务器访问地址，请设置 CANVAS_PUBLIC_BASE_URL 或在存储设置中配置公网访问地址（或改用 OSS 存储）");
        }
        Uri parsed = await OpenAICanvas.Outbound.OutboundGuard.ValidateOutboundUrlAsync(raw).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw AppError.BadAuthRequest("服务器访问地址不能包含查询参数或片段");
        }
        if (parsed.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("服务器访问地址请填写根地址，不要包含 /api");
        }
        return parsed;
    }

    
    /// <summary>
    /// 匿名签名下发：校验 expires/signature 后打开资源流。
    /// 对应 Go: <c>OpenPublicResourceRange</c>。签名覆盖 <c>id\nvariant\nexpires</c>，
    /// 变体归一化非法时 400；playback 请求在副本就绪时下发副本，否则回退原件。
    /// </summary>
    public async Task<ResourceStream> OpenPublicResourceRangeAsync(
        string id, string? expires, string? signature, string? variant, string? rangeHeader,
        CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceAsync(id, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (!IsLocalProvider(resource.Provider))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        string normalizedVariant = (variant ?? "").Trim();
        if (normalizedVariant.Length == 0)
        {
            normalizedVariant = VariantOriginal;
        }
        if (normalizedVariant is not (VariantOriginal or VariantPlayback))
        {
            throw AppError.BadAuthRequest("资源变体无效");
        }
        VerifyPublicResourceSignature(resource.ID, normalizedVariant, expires, signature);
        if (normalizedVariant == VariantPlayback
            && resource.PlaybackStatus == PlaybackStatusReady
            && !string.IsNullOrEmpty(resource.PlaybackObjectKey))
        {
            ResourceStream? playback = await OpenPlaybackAsync(resource, cancellationToken).ConfigureAwait(false);
            if (playback is not null)
            {
                return playback;
            }
        }
        return await OpenResourceRangeAsync(resource, rangeHeader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按用户 + 资源 ID 打开资源流（含状态校验）。
    /// 对应 Go: <c>OpenResourceRange</c>。
    /// </summary>
    public async Task<ResourceStream> OpenResourceRangeAsync(
        string userId, string resourceId, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        Resource? resource = await _repository.ResourceForUserAsync(userId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("资源不存在");
        }
        return await OpenResourceRangeAsync(resource, rangeHeader, cancellationToken).ConfigureAwait(false);
    }

    public Task<ResourceStream?> OpenPlaybackAsync(Resource resource, CancellationToken cancellationToken = default) =>
        _playback is null ? Task.FromResult<ResourceStream?>(null) : _playback.OpenPlaybackAsync(resource, cancellationToken);

    /// <summary>
    /// 按资源实体直接打开流（管理员下发用，跳过归属校验）。
    /// 对应 Go: <c>Service.openResourceRange(resource.UserID, resource, rangeHeader)</c>。
    /// </summary>
    public Task<ResourceStream> OpenResourceForOwnerAsync(
        Resource resource, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        resource.Provider = string.IsNullOrWhiteSpace(resource.Provider) ? "local" : resource.Provider.Trim();
        return OpenResourceRangeAsync(resource, rangeHeader, cancellationToken);
    }

    /// <summary>
    /// 资源响应 ETag。无 ETag 时回落到 <c>ID-Size-UpdatedAtTicks</c>，与 Go 的 UnixNano 语义一致。
    /// 对应 Go: <c>resourceResponseETag</c>。
    /// </summary>
    public static string ResourceResponseETag(Resource resource)
    {
        string value = (resource.ETag ?? string.Empty).Trim().Trim('"');
        if (value.Length == 0)
        {
            value = $"{resource.ID}-{resource.Size}-{resource.UpdatedAt.Ticks}";
        }
        return '"' + value + '"';
    }

    /// <summary>If-None-Match 匹配（支持 <c>*</c> 与 <c>W/</c> 弱比较）。对应 Go: <c>ifNoneMatch</c>。</summary>
    public static bool IfNoneMatch(string? header, string etag)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }
        foreach (string raw in header.Split(','))
        {
            string candidate = raw.Trim();
            if (candidate.StartsWith("W/", StringComparison.Ordinal))
            {
                candidate = candidate[2..].Trim();
            }
            if (candidate == "*" || candidate == etag)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 单区间 Range 头归一化：仅接受 <c>bytes=start-end</c> 形式（≤128 字符、无逗号多区间）。
    /// 与 Go 一致，只做合法性过滤，真正的裁剪交给 ServeContent 等价逻辑。
    /// 对应 Go: <c>normalizeSingleByteRange</c>。
    /// </summary>
    public static string NormalizeSingleByteRange(string? value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length > 128 || !value.StartsWith("bytes=", StringComparison.Ordinal)
            || value.Contains(',', StringComparison.Ordinal))
        {
            return string.Empty;
        }
        string payload = value["bytes=".Length..];
        int dash = payload.IndexOf('-');
        if (dash < 0)
        {
            return string.Empty;
        }
        string start = payload[..dash];
        string end = payload[(dash + 1)..];
        if ((start.Length == 0 && end.Length == 0) || !IsDecimalDigits(start) || !IsDecimalDigits(end))
        {
            return string.Empty;
        }
        return "bytes=" + start + "-" + end;
    }

    /// <summary>
    /// 解析单区间 Range 为绝对偏移。<c>ContentRange</c> 为空表示整文件 200；
    /// 返回 <c>null</c> 表示 Range 不可满足（调用方应回 416）。
    /// 语义等价于 Go 的 <c>http.ServeContent</c>（bytes 类型、单区间）。
    /// </summary>
    public static (long Start, long Length, string ContentRange)? ResolveRange(long size, string? rangeHeader)
    {
        string normalized = NormalizeSingleByteRange(rangeHeader);
        if (normalized.Length == 0)
        {
            return (0, size, string.Empty);
        }
        string payload = normalized["bytes=".Length..];
        int dash = payload.IndexOf('-');
        string startText = payload[..dash];
        string endText = payload[(dash + 1)..];

        if (startText.Length == 0)
        {
            // "-N" 后缀：取最后 N 字节。
            if (!long.TryParse(endText, out long suffix) || suffix <= 0)
            {
                return null;
            }
            long start = Math.Max(0, size - suffix);
            long length = size - start;
            return (start, length, $"bytes {start}-{size - 1}/{size}");
        }

        if (!long.TryParse(startText, out long from) || from >= size)
        {
            return null;
        }
        long to = size - 1;
        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, out long parsedTo) || parsedTo < from)
            {
                return null;
            }
            to = Math.Min(parsedTo, size - 1);
        }
        return (from, to - from + 1, $"bytes {from}-{to}/{size}");
    }

    /// <summary>
    /// 校验匿名下发的 expires/signature。
    /// 对应 Go: <c>verifyPublicResourceSignature</c>；签名覆盖 <c>id\nvariant\nexpires</c>。
    /// </summary>
    public void VerifyPublicResourceSignature(string resourceId, string variant, string? expires, string? signature)
    {
        if (string.IsNullOrWhiteSpace(expires) || string.IsNullOrWhiteSpace(signature))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (!long.TryParse(expires, out long expiresAt))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAt)
        {
            throw AppError.Forbidden("匿名下载链接已过期");
        }
        // 与 Go 的 hmac.Equal 一致：定长比较，避免时序侧信道。
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(ComputePublicResourceSignature(resourceId, variant, expires));
        byte[] actual = System.Text.Encoding.UTF8.GetBytes(signature);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw AppError.Forbidden("匿名下载链接无效");
        }
    }

    /// <summary>
    /// 生成匿名下发签名：<c>HMAC-SHA256(settingsKey, id + "\n" + variant + "\n" + expires)</c>
    /// 的 base64 RawURL。对应 Go: <c>signPublicResource</c>（payload 含资源变体）。
    /// </summary>
    /// <remarks>密钥为设置加密密钥（<c>data/.settings-key</c>，32 字节）；未配置时与 Go 的 nopHost 一致。</remarks>
    public string ComputePublicResourceSignature(string resourceId, string variant, string expires)
    {
        byte[] key = SettingsEncryptionKey() ?? [];
        byte[] data = System.Text.Encoding.UTF8.GetBytes(resourceId + "\n" + variant + "\n" + expires);
        byte[] digest = System.Security.Cryptography.HMACSHA256.HashData(key, data);
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// 设置加密密钥来源，对应 Go 的 <c>settingsEncryptionKey</c>（读取 <c>data/.settings-key</c>）。
    /// 宿主未接线时返回 null，与 Go 的 nopHost 等价。
    /// </summary>
    private Func<byte[]?> SettingsEncryptionKey { get; set; } = static () => null;

    // ------------------------------------------------------------ 内部（本地投递）

    private async Task<ResourceStream> OpenResourceRangeAsync(
        Resource resource, string? rangeHeader, CancellationToken cancellationToken)
    {
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("资源尚未上传完成");
        }
        if (!IsLocalProvider(resource.Provider))
        {
            // 远程源站按 Range 读取（服务端代理路径：私有源站代理与水合等进程内消费）。
            if (_storageSettings is null)
            {
                throw new InvalidOperationException("对象存储通道未注入存储设置服务");
            }
            StorageChannelSettings setting = await ResourceObjectStorage
                .ForResourceAsync(_repository, _storageSettings, resource.UserID, resource, cancellationToken)
                .ConfigureAwait(false);
            StorageObjectStream remote = await StorageObjectChannel.GetOriginObjectRangeAsync(
                setting, resource.ObjectKey, StorageObjectChannel.NormalizeSingleByteRange(rangeHeader),
                cancellationToken).ConfigureAwait(false);
            return new ResourceStream(resource, remote.Body, remote.ContentRange, remote.AcceptRanges)
            {
                StatusCode = remote.StatusCode,
                ContentLength = remote.ContentLength > 0 ? remote.ContentLength : resource.Size,
            };
        }
        string root = Path.GetFullPath(Path.Combine(_dataDir, "resources"));
        string path = Path.GetFullPath(Path.Combine(root,
            resource.ObjectKey.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
        {
            throw AppError.NotFound("资源文件不存在");
        }
        FileStream fs = File.OpenRead(path);
        return new ResourceStream(resource, fs, ContentRange: "", AcceptRanges: "bytes")
        {
            StatusCode = 200,
            ContentLength = resource.Size,
        };
    }

    private static bool IsLocalProvider(string? provider) =>
        string.IsNullOrEmpty(provider) || string.Equals(provider, "local", StringComparison.OrdinalIgnoreCase);

    private static bool IsDecimalDigits(string value)
    {
        foreach (char ch in value)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>对应 Go: <c>resourceStorageURL</c>（本地直链相对路径）。</summary>
    private static Task<string> DirectLocalResourceUrlAsync(
        Resource resource, CancellationToken cancellationToken)
    {
        string objectKey = resource.ObjectKey.TrimStart('/');
        return Task.FromResult("/api/resources/" + resource.ID + "/file");
    }
}
