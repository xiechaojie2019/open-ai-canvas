#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.Appearance;

/// <summary>
/// 外观配置：品牌标识、登录页素材、皮肤主题与 SEO 文案。
/// 对应 Go: <c>app/appearance.go</c> + <c>app/appearance_skins.go</c>。
/// </summary>
/// <remarks>
/// 外观资源（Logo / 视频 / 封面）一律落在服务端本地资源目录，避免其可用性依赖管理员
/// 当前选择的对象存储 —— 与 Go 的注释意图一致。
/// </remarks>
public sealed class AppearanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Repository _repository;
    private readonly ResourceUploadService _uploads;
    private readonly ResourceDomainService _resources;
    private readonly string _dataDir;
    private readonly SemaphoreSlim _storageMutex = new(1, 1);

    public AppearanceService(
        Repository repository,
        ResourceUploadService uploads,
        ResourceDomainService resources,
        string? dataDir = null)
    {
        _repository = repository;
        _uploads = uploads;
        _resources = resources;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir!;
    }

    /// <summary>对外外观设置。对应 Go: <c>Service.Appearance</c>。</summary>
    public async Task<PublicAppearanceSetting> GetPublicAsync(CancellationToken cancellationToken = default)
    {
        (SystemSetting? setting, AppearanceSetting value) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        value = await ResolveAvailableAssetsAsync(value, cancellationToken).ConfigureAwait(false);
        return ToPublic(setting, value);
    }

    /// <summary>管理员视角外观设置。对应 Go: <c>Service.AdminAppearance</c>。</summary>
    public async Task<AdminAppearanceSetting> GetAdminAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (SystemSetting? setting, AppearanceSetting value) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        value = await ResolveAvailableAssetsAsync(value, cancellationToken).ConfigureAwait(false);
        return BuildAdmin(setting, value);
    }

    /// <summary>更新外观设置。对应 Go: <c>Service.UpdateAppearance</c>。</summary>
    public async Task<AdminAppearanceSetting> UpdateAsync(
        User? actor, AppearanceSetting input, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        AppearanceSetting value = Normalize(input, outstring: false);
        Validate(value);

        await _storageMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (SystemSetting? current, AppearanceSetting before) = await ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            (string Slot, string ResourceID, string CurrentID)[] candidates =
            [
                (AppearanceText.Logo, value.LogoResourceID, before.LogoResourceID),
                (AppearanceText.DarkLogo, value.DarkLogoResourceID, before.DarkLogoResourceID),
                (AppearanceText.Video, value.AuthVideoResourceID, before.AuthVideoResourceID),
                (AppearanceText.Poster, value.AuthVideoPosterResourceID, before.AuthVideoPosterResourceID),
            ];
            foreach ((string slot, string resourceId, string currentId) in candidates)
            {
                await ValidateAppearanceResourceAsync(actor!, slot, resourceId, currentId, cancellationToken)
                    .ConfigureAwait(false);
            }

            DateTime now = DateTime.UtcNow;
            SystemSetting setting = new()
            {
                Key = AppearanceText.SettingKey,
                ValueJSON = JsonSerializer.Serialize(value, JsonOptions),
                UpdatedBy = actor!.ID,
                CreatedAt = current?.CreatedAt ?? default,
                UpdatedAt = now,
            };
            await _repository.SaveSystemSettingAsync(setting, cancellationToken).ConfigureAwait(false);
            await RecordAuditAsync(actor!, "appearance.update", "更新外观配置",
                new { before, after = value }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storageMutex.Release();
        }
        return await GetAdminAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>恢复默认外观。对应 Go: <c>Service.ResetAppearance</c>。</summary>
    public async Task<AdminAppearanceSetting> ResetAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        await _storageMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (_, AppearanceSetting before) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            await _repository.DeleteSystemSettingAsync(AppearanceText.SettingKey, cancellationToken)
                .ConfigureAwait(false);
            AppearanceSetting after = AppearanceText.Default();
            await RecordAuditAsync(actor!, "appearance.reset", "恢复影策默认品牌标识",
                new { before, after }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _storageMutex.Release();
        }
        return await GetAdminAsync(actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 上传外观资源。Logo 强制走本地 provider（安装方自有资产，需与对象存储解耦）。
    /// 对应 Go: <c>Service.UploadAppearanceAsset</c>。
    /// </summary>
    /// <remarks>调用方需已完成 MIME 嗅探并返回 <paramref name="mimeType"/>（与 Go 覆写 Content-Type 等价）。</remarks>
    public async Task<Resource> UploadAssetAsync(
        User? actor,
        string slot,
        string fileName,
        long size,
        string mimeType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        string kind = slot == AppearanceText.Video ? "video" : "image";
        bool forceLocal = slot is AppearanceText.Logo or AppearanceText.DarkLogo;

        Resource resource = await _uploads.UploadResourceFromStreamAsync(
            actor!.ID,
            fileName,
            size,
            kind,
            0,
            0,
            0,
            content,
            mimeType,
            uploadIdentity: null,
            forceLocal: forceLocal,
            cancellationToken).ConfigureAwait(false);
        resource.PublicURL = "";
        return resource;
    }

    /// <summary>打开外观资源流（匿名可访问）。对应 Go: <c>Service.OpenAppearanceAsset</c>。</summary>
    public async Task<ResourceStream> OpenAssetAsync(
        string slot, string? rangeHeader, CancellationToken cancellationToken = default)
    {
        (_, AppearanceSetting value) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        string resourceId = AppearanceText.ResourceID(value, slot);
        if (resourceId.Length == 0)
        {
            throw AppError.NotFound("未配置该外观资源");
        }
        Resource? resource = await _repository.ResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("外观资源不存在");
        }
        ValidateResourceType(slot, resource);
        return await _resources.OpenResourceForOwnerAsync(resource, rangeHeader, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 外观对资源的引用（用于管理端删除阻塞）。
    /// 对应 Go: <c>Service.appearanceResourceReferences</c>；配置不可读时 fail closed。
    /// </summary>
    public async Task<Dictionary<string, List<AdminResourceReferenceView>>> ResourceReferencesAsync(
        IReadOnlyList<string> resourceIds, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<AdminResourceReferenceView>> result = new(StringComparer.Ordinal);
        AppearanceSetting value;
        try
        {
            (_, value) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 无效外观 JSON 必须在删除时 fail closed：返成可见的阻塞引用而不是放行删除。
            foreach (string resourceId in resourceIds)
            {
                result[resourceId] =
                [
                    new AdminResourceReferenceView
                    {
                        Kind = "外观",
                        ID = AppearanceText.SettingKey,
                        Title = "外观配置无法读取",
                    },
                ];
            }
            return result;
        }

        (string ResourceID, string Title)[] candidates =
        [
            (value.LogoResourceID, "浅色模式品牌 Logo"),
            (value.DarkLogoResourceID, "深色模式品牌 Logo"),
            (value.AuthVideoResourceID, "登录页品牌视频"),
            (value.AuthVideoPosterResourceID, "登录页视频封面"),
        ];
        HashSet<string> wanted = new(resourceIds, StringComparer.Ordinal);
        foreach ((string resourceId, string title) in candidates)
        {
            if (resourceId.Length == 0 || !wanted.Contains(resourceId))
            {
                continue;
            }
            if (!result.TryGetValue(resourceId, out List<AdminResourceReferenceView>? list))
            {
                list = [];
                result[resourceId] = list;
            }
            if (!list.Any(reference => reference.Kind == "外观" && reference.ID == AppearanceText.SettingKey))
            {
                list.Add(new AdminResourceReferenceView
                {
                    Kind = "外观",
                    ID = AppearanceText.SettingKey,
                    Title = title,
                });
            }
        }
        return result;
    }

    /// <summary>品牌名（其他模块引用）。对应 Go: <c>appearanceBrandName</c>。</summary>
    public async Task<string> BrandNameAsync(CancellationToken cancellationToken = default)
    {
        (string brandName, _) = await IdentityAsync(cancellationToken).ConfigureAwait(false);
        return brandName;
    }

    /// <summary>品牌名 + slug。对应 Go: <c>appearanceIdentity</c>。</summary>
    public async Task<(string BrandName, string BrandSlug)> IdentityAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            (_, AppearanceSetting value) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (value.BrandName.Trim().Length == 0)
            {
                return (AppearanceText.DefaultBrandName, AppearanceText.DefaultBrandSlug);
            }
            return (value.BrandName, value.BrandSlug);
        }
        catch (Exception)
        {
            return (AppearanceText.DefaultBrandName, AppearanceText.DefaultBrandSlug);
        }
    }

    // ------------------------------------------------------------ 校验

    /// <summary>上传前的槽位与大小/MIME 校验。对应 Go: <c>validateAppearanceUpload</c>。</summary>
    public static void ValidateUpload(string slot, long size, ReadOnlySpan<byte> head, string sniffedMimeType)
    {
        long maxBytes = AppearanceText.AssetMaxBytes(slot);
        if (size <= 0 || size > maxBytes)
        {
            throw AppError.BadAuthRequest(
                $"{AppearanceText.AssetLabel(slot)}大小必须在 {maxBytes >> 20}MB 以内");
        }
        if (head.Length == 0)
        {
            throw AppError.BadAuthRequest("外观资源内容无法读取");
        }
        string mimeType = DetectAppearanceMime(slot, head, size, sniffedMimeType);
        if (!AppearanceText.AllowedMimeTypes(slot).Contains(mimeType))
        {
            throw AppError.BadAuthRequest(AppearanceText.AssetLabel(slot) + "文件类型不受支持");
        }
    }

    /// <summary>
    /// 外观专用 MIME 判定：视频槽位额外接受结构合法的 ISO BMFF <c>ftyp</c> 头。
    /// 对应 Go: <c>detectAppearanceMIME</c>。
    /// </summary>
    public static string DetectAppearanceMime(
        string slot, ReadOnlySpan<byte> data, long fileSize, string sniffedMimeType)
    {
        string mimeType = sniffedMimeType.Split(';')[0].Trim().ToLowerInvariant();
        if (slot != AppearanceText.Video || mimeType == "video/mp4" || data.Length < 12)
        {
            return mimeType;
        }
        // Go 的嗅探器只认部分 MP4 兼容 brand；这里额外接受合法的 ftyp 首盒，
        // 覆盖 isom / iso2 / avc1 / M4V 等常见 MP4。
        long boxSize = (long)data[0] << 24 | (long)data[1] << 16 | (long)data[2] << 8 | data[3];
        bool isFtyp = data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p';
        if (isFtyp && boxSize >= 12 && boxSize % 4 == 0 && boxSize <= fileSize)
        {
            return "video/mp4";
        }
        return mimeType;
    }

    /// <summary>对应 Go: <c>validateAppearanceSetting</c>。</summary>
    public static void Validate(AppearanceSetting value)
    {
        if (value.BrandName.Length == 0 || AppearanceSkins.RuneCount(value.BrandName) > 40)
        {
            throw AppError.BadAuthRequest("品牌名称必须为 1 到 40 个字符");
        }
        if (AppearanceText.HasControlChar(value.BrandName))
        {
            throw AppError.BadAuthRequest("品牌名称不能包含控制字符");
        }
        if (!AppearanceText.IsValidBrandSlug(value.BrandSlug))
        {
            throw AppError.BadAuthRequest("英文品牌标识须为 1 到 48 位小写字母、数字或连字符，且不能以连字符开头或结尾");
        }
        AppearanceText.ValidateCopy(value.AuthHeroTitle, "登录页主标题", 80, required: true);
        AppearanceText.ValidateCopy(value.AuthHeroDescription, "登录页说明文案", 160, required: false);
        AppearanceSkins.ValidateThemes(value.SkinThemes, value.SkinID);
        foreach ((string text, string label, int max) in new (string, string, int)[]
                 {
                     (value.SEOTitle, "SEO 标题", 70),
                     (value.SEODescription, "SEO 描述", 200),
                     (value.SEOKeywords, "SEO 关键词", 300),
                     (value.FooterCopyright, "版权信息", 160),
                     (value.ICPFilingNumber, "备案号", 64),
                 })
        {
            AppearanceText.ValidateCopy(text, label, max, required: false);
        }
        if (value.ICPFilingEnabled && value.ICPFilingNumber.Length == 0)
        {
            throw AppError.BadAuthRequest("显示备案号前请先填写备案号");
        }
        foreach (string resourceId in new[]
                 {
                     value.LogoResourceID, value.DarkLogoResourceID,
                     value.AuthVideoResourceID, value.AuthVideoPosterResourceID,
                 })
        {
            if (resourceId.Length > 80)
            {
                throw AppError.BadAuthRequest("外观资源 ID 无效");
            }
        }
    }

    /// <summary>对应 Go: <c>normalizeAppearanceSetting</c> 的归一化部分。</summary>
    private static AppearanceSetting Normalize(AppearanceSetting input, bool outstring)
    {
        AppearanceSetting value = new()
        {
            SchemaVersion = AppearanceText.SchemaVersion,
            BrandName = input.BrandName.Trim(),
            BrandSlug = input.BrandSlug.Trim().ToLowerInvariant(),
            AuthHeroTitle = AppearanceText.NormalizeCopy(input.AuthHeroTitle),
            AuthHeroDescription = AppearanceText.NormalizeCopy(input.AuthHeroDescription),
            LogoResourceID = input.LogoResourceID.Trim(),
            DarkLogoResourceID = input.DarkLogoResourceID.Trim(),
            LogoFrameEnabled = input.LogoFrameEnabled,
            AuthVideoResourceID = input.AuthVideoResourceID.Trim(),
            AuthVideoPosterResourceID = input.AuthVideoPosterResourceID.Trim(),
            AuthVideoAutoplay = input.AuthVideoAutoplay,
            SkinID = input.SkinID.Trim(),
            SkinThemes = input.SkinThemes.Count == 0
                ? AppearanceSkins.DefaultThemes()
                : AppearanceSkins.NormalizeThemes(input.SkinThemes),
            SEOTitle = AppearanceText.NormalizeSingleLine(input.SEOTitle),
            SEODescription = AppearanceText.NormalizeCopy(input.SEODescription),
            SEOKeywords = AppearanceText.NormalizeSingleLine(input.SEOKeywords),
            FooterCopyright = AppearanceText.NormalizeSingleLine(input.FooterCopyright),
            ICPFilingEnabled = input.ICPFilingEnabled,
            ICPFilingNumber = AppearanceText.NormalizeSingleLine(input.ICPFilingNumber),
        };
        return value;
    }

    /// <summary>对应 Go: <c>validateAppearanceResource</c>。</summary>
    private async Task ValidateAppearanceResourceAsync(
        User actor, string slot, string resourceId, string currentId, CancellationToken cancellationToken)
    {
        if (resourceId.Length == 0)
        {
            return;
        }
        Resource? resource = await _repository.ResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.BadAuthRequest("选择的外观资源不存在");
        }
        if (resourceId != currentId && resource.UserID != actor.ID)
        {
            throw AppError.Forbidden("只能使用当前管理员上传的外观资源");
        }
        ValidateResourceType(slot, resource);
    }

    /// <summary>对应 Go: <c>validateAppearanceResourceType</c>。</summary>
    internal static void ValidateResourceType(string slot, Resource resource)
    {
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("外观资源尚未上传完成");
        }
        string mimeType = resource.MimeType.Split(';')[0].Trim().ToLowerInvariant();
        if (!AppearanceText.AllowedMimeTypes(slot).Contains(mimeType))
        {
            throw AppError.BadAuthRequest("外观资源文件类型不受支持");
        }
        if (slot == AppearanceText.Video && resource.Kind != "video")
        {
            throw AppError.BadAuthRequest("登录页品牌视频必须是视频资源");
        }
        if (slot != AppearanceText.Video && resource.Kind != "image")
        {
            throw AppError.BadAuthRequest("Logo 和视频封面必须是图片资源");
        }
    }

    // ------------------------------------------------------------ 读取与投影

    /// <summary>对应 Go: <c>readAppearance</c>。</summary>
    private async Task<(SystemSetting? Setting, AppearanceSetting Value)> ReadAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(
            AppearanceText.SettingKey, cancellationToken).ConfigureAwait(false);
        AppearanceSetting value = AppearanceText.Default();
        if (setting is null)
        {
            return (null, value);
        }
        if (string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            throw new InvalidOperationException("外观配置格式无效");
        }
        try
        {
            AppearanceSetting? parsed = JsonSerializer.Deserialize<AppearanceSetting>(
                setting.ValueJSON, JsonOptions);
            if (parsed is null)
            {
                throw new InvalidOperationException("外观配置格式无效");
            }
            value = parsed;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("外观配置格式无效");
        }

        value.SchemaVersion = AppearanceText.SchemaVersion;
        value.BrandName = value.BrandName.Trim();
        if (value.BrandName.Length == 0)
        {
            value.BrandName = AppearanceText.DefaultBrandName;
        }
        value.BrandSlug = value.BrandSlug.Trim().ToLowerInvariant();
        if (value.BrandSlug.Length == 0)
        {
            value.BrandSlug = AppearanceText.DefaultBrandSlug;
        }
        value.AuthHeroTitle = AppearanceText.NormalizeCopy(value.AuthHeroTitle);
        if (value.AuthHeroTitle.Length == 0)
        {
            value.AuthHeroTitle = AppearanceText.DefaultHeroTitle;
        }
        value.AuthHeroDescription = AppearanceText.NormalizeCopy(value.AuthHeroDescription);
        value.SkinID = value.SkinID.Trim();
        if (value.SkinID.Length == 0)
        {
            value.SkinID = AppearanceSkins.DefaultSkinID;
        }
        if (value.SkinThemes.Count == 0)
        {
            value.SkinThemes = AppearanceSkins.DefaultThemes();
        }
        value.SkinThemes = AppearanceSkins.NormalizeThemes(value.SkinThemes);
        value.SEOTitle = AppearanceText.NormalizeSingleLine(value.SEOTitle);
        value.SEODescription = AppearanceText.NormalizeCopy(value.SEODescription);
        value.SEOKeywords = AppearanceText.NormalizeSingleLine(value.SEOKeywords);
        value.FooterCopyright = AppearanceText.NormalizeSingleLine(value.FooterCopyright);
        value.ICPFilingNumber = AppearanceText.NormalizeSingleLine(value.ICPFilingNumber);
        return (setting, value);
    }

    /// <summary>
    /// 清理已失效的资源引用，避免把陈旧 ID 暴露给匿名端或回显给管理端编辑器。
    /// 对应 Go: <c>resolveAvailableAppearanceAssets</c>。
    /// </summary>
    private async Task<AppearanceSetting> ResolveAvailableAssetsAsync(
        AppearanceSetting value, CancellationToken cancellationToken)
    {
        foreach (string slot in new[]
                 {
                     AppearanceText.Logo, AppearanceText.DarkLogo,
                     AppearanceText.Video, AppearanceText.Poster,
                 })
        {
            string resourceId = AppearanceText.ResourceID(value, slot);
            if (resourceId.Length == 0
                || await AssetAvailableAsync(slot, resourceId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }
            AppearanceText.SetResourceID(value, slot, "");
        }
        return value;
    }

    /// <summary>对应 Go: <c>appearanceAssetAvailable</c>。</summary>
    private async Task<bool> AssetAvailableAsync(string slot, string resourceId, CancellationToken cancellationToken)
    {
        Resource? resource = await _repository.ResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            return false;
        }
        try
        {
            ValidateResourceType(slot, resource);
        }
        catch (AppError)
        {
            return false;
        }
        if (!string.Equals(resource.Provider, "local", StringComparison.Ordinal))
        {
            // 远端对象不逐次探测：前端有自身的加载失败兜底。
            return true;
        }
        string path = Path.Combine(_dataDir, "resources",
            resource.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path);
    }

    /// <summary>对应 Go: <c>publicAppearanceSetting</c>。</summary>
    internal static PublicAppearanceSetting ToPublic(SystemSetting? setting, AppearanceSetting value)
    {
        string revision = setting is null
            ? "builtin"
            : ToBase36(setting.UpdatedAt.Ticks);
        PublicAppearanceSetting result = new()
        {
            SchemaVersion = AppearanceText.SchemaVersion,
            BrandName = value.BrandName,
            BrandSlug = value.BrandSlug,
            AuthHeroTitle = value.AuthHeroTitle,
            AuthHeroDescription = value.AuthHeroDescription,
            LogoURL = AppearanceText.DefaultLogoURL,
            DarkLogoURL = AppearanceText.DefaultLogoURL,
            LogoFrameEnabled = value.LogoFrameEnabled,
            AuthVideoURL = AppearanceText.DefaultVideoURL,
            AuthVideoPosterURL = AppearanceText.DefaultPosterURL,
            AuthVideoAutoplay = value.AuthVideoAutoplay,
            SkinID = value.SkinID,
            ActiveSkin = AppearanceSkins.ActiveSkin(value.SkinThemes, value.SkinID),
            SEOTitle = EffectiveSEOTitle(value),
            SEODescription = EffectiveSEODescription(value),
            SEOKeywords = value.SEOKeywords,
            FooterCopyright = EffectiveCopyright(value),
            ICPFilingEnabled = value.ICPFilingEnabled && value.ICPFilingNumber.Length > 0,
            ICPFilingNumber = value.ICPFilingNumber,
            Configured = setting is not null,
            Revision = revision,
            UpdatedAt = setting?.UpdatedAt ?? default,
        };
        if (value.LogoResourceID.Length > 0 || value.DarkLogoResourceID.Length > 0)
        {
            result.LogoConfigured = true;
            result.LogoURL = value.LogoResourceID.Length > 0
                ? AppearanceText.AssetURL(AppearanceText.Logo, revision)
                : AppearanceText.AssetURL(AppearanceText.DarkLogo, revision);
            if (value.DarkLogoResourceID.Length > 0)
            {
                result.DarkLogoConfigured = true;
                result.DarkLogoURL = AppearanceText.AssetURL(AppearanceText.DarkLogo, revision);
            }
            else
            {
                result.DarkLogoURL = result.LogoURL;
            }
            if (value.LogoResourceID.Length == 0)
            {
                result.LogoURL = result.DarkLogoURL;
            }
        }
        if (value.AuthVideoResourceID.Length > 0)
        {
            result.AuthVideoConfigured = true;
            result.AuthVideoURL = AppearanceText.AssetURL(AppearanceText.Video, revision);
            if (value.AuthVideoPosterResourceID.Length == 0)
            {
                // 自定义视频不得在首帧加载时短暂露出内置封面。
                result.AuthVideoPosterURL = "";
            }
        }
        if (value.AuthVideoPosterResourceID.Length > 0)
        {
            result.AuthVideoPosterConfigured = true;
            result.AuthVideoPosterURL = AppearanceText.AssetURL(AppearanceText.Poster, revision);
        }
        return result;
    }

    private AdminAppearanceSetting BuildAdmin(SystemSetting? setting, AppearanceSetting value)
    {
        AdminAppearanceSetting result = new()
        {
            SchemaVersion = value.SchemaVersion,
            BrandName = value.BrandName,
            BrandSlug = value.BrandSlug,
            AuthHeroTitle = value.AuthHeroTitle,
            AuthHeroDescription = value.AuthHeroDescription,
            LogoResourceID = value.LogoResourceID,
            DarkLogoResourceID = value.DarkLogoResourceID,
            LogoFrameEnabled = value.LogoFrameEnabled,
            AuthVideoResourceID = value.AuthVideoResourceID,
            AuthVideoPosterResourceID = value.AuthVideoPosterResourceID,
            AuthVideoAutoplay = value.AuthVideoAutoplay,
            SkinID = value.SkinID,
            SkinThemes = value.SkinThemes,
            SEOTitle = value.SEOTitle,
            SEODescription = value.SEODescription,
            SEOKeywords = value.SEOKeywords,
            FooterCopyright = value.FooterCopyright,
            ICPFilingEnabled = value.ICPFilingEnabled,
            ICPFilingNumber = value.ICPFilingNumber,
            Public = ToPublic(setting, value),
            Configured = setting is not null,
            UpdatedBy = setting?.UpdatedBy ?? "",
            CreatedAt = setting?.CreatedAt ?? default,
            UpdatedAt = setting?.UpdatedAt ?? default,
        };
        return result;
    }

    private static string EffectiveSEOTitle(AppearanceSetting value) =>
        value.SEOTitle.Length > 0 ? value.SEOTitle : value.BrandName;

    private static string EffectiveSEODescription(AppearanceSetting value) =>
        value.SEODescription.Length > 0
            ? value.SEODescription
            : value.BrandName + "，面向 AI 影视与短剧创作的工作台。";

    private static string EffectiveCopyright(AppearanceSetting value) =>
        value.FooterCopyright.Length > 0
            ? value.FooterCopyright
            : $"© {DateTime.Now.Year} {value.BrandName}. All rights reserved.";

    /// <summary>对应 Go 的 <c>strconv.FormatInt(nanos, 36)</c>（这里以 Ticks 为基数保持同构）。</summary>
    private static string ToBase36(long value)
    {
        if (value <= 0)
        {
            return "0";
        }
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        System.Text.StringBuilder builder = new();
        long remaining = value;
        while (remaining > 0)
        {
            builder.Insert(0, digits[(int)(remaining % 36)]);
            remaining /= 36;
        }
        return builder.ToString();
    }

    /// <summary>对应 Go: <c>appendAdminAudit(actor, action, "system_setting", key, summary, metadata)</c>。</summary>
    private async Task RecordAuditAsync(
        User actor, string action, string summary, object metadata, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(metadata, JsonOptions);
        if (json.Length > 4000)
        {
            json = json[..4000];
        }
        await _repository.AppendAdminAuditAsync(new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = action,
            TargetType = "system_setting",
            TargetID = AppearanceText.SettingKey,
            Summary = summary,
            MetadataJSON = json,
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }
}
