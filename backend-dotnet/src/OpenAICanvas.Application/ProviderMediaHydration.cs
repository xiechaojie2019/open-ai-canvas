#nullable enable
using System.Globalization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Platform;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Providers;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 媒体任务执行前的参考素材水合：把 <c>storageKey: resource:&lt;id&gt;</c> 形式的
/// 参考素材解析成供应商可用的形式（URL 优先协议 → 签名下发地址；multipart
/// 协议 → 字节 data URL）。对应 Go: <c>app/provider.go</c> 的
/// <c>hydrateGenerationMedia</c> / <c>hydrateProviderMedia</c>。
/// </summary>
public static class ProviderMediaHydrator
{
    /// <summary>明确接受远程 URL 的协议。对应 Go: <c>providerPrefersMediaURLs</c> 的静态清单。</summary>
    private static readonly string[] PreferURLInterfaces =
    [
        "chat-completion", "openai-response", "claude-api", "grok-image",
        "volcengine-ark-image", "volcengine-ark-agent-plan-image",
        "xai-video", "novita-video", "minimax-video", "newapi",
        "newapi-channel-1", "newapi-channel-2",
        "volcengine-ark-video", "volcengine-ark-agent-plan-video",
    ];

    /// <summary>只接受 URL 的协议（不接受内嵌数据）。对应 Go 的 requireURL 清单。</summary>
    private static readonly string[] RequireURLInterfaces =
    [
        "newapi", "newapi-channel-1", "newapi-channel-2",
        "volcengine-ark-video", "volcengine-ark-agent-plan-video", "minimax-video",
    ];

    private sealed record HydrationPolicy(bool RequireURL, bool PreferURL, bool ImageOnly, long MaxBytes);

    /// <summary>
    /// 水合生成任务的全部参考素材与蒙版。对应 Go: <c>hydrateGenerationMedia</c>
    /// （Agent 看图的 imageOnly/字节上限特例未含，Agent 路径另行接入）。
    /// </summary>
    public static async Task HydrateGenerationMediaAsync(
        string userID, TextTaskInput input, Repository repository, ResourceDomainService domain,
        RuntimePolicySetting policy, TextReferenceConfig? textReferences = null,
        CancellationToken cancellationToken = default)
    {
        string interfaceType = input.Config.InterfaceType.Trim();
        HydrationPolicy hydration = new(
            RequireURL: RequireURLInterfaces.Contains(interfaceType),
            PreferURL: PreferURLInterfaces.Contains(interfaceType),
            ImageOnly: false,
            MaxBytes: 0);
        if (input.Mask is not null)
        {
            hydration = hydration with { RequireURL = false, PreferURL = false };
        }

        // 仅 Agent 看图走内存字节；视频、音频及普通生成任务保留协议策略。
        // 图片按模型声明的数量与字节上限强制校验（对应 Go: hydrateGenerationMedia 的 imageOnly 分支）。
        HydrationPolicy agentImagePolicy = hydration;
        if (input.Mode == "text" && input.AgentRequests?.Canonical is not null)
        {
            agentImagePolicy = new HydrationPolicy(RequireURL: false, PreferURL: false, ImageOnly: true, MaxBytes: 0);
            if (textReferences is not null)
            {
                if (input.ReferenceImages.Count > textReferences.MaxImages)
                {
                    throw new InvalidOperationException("参考图片数量超过当前模型限制");
                }
                agentImagePolicy = agentImagePolicy with { MaxBytes = textReferences.MaxImageBytes };
            }
        }

        List<List<ProviderMedia>> groups =
            [input.ReferenceImages, input.ReferenceVideos, input.ReferenceAudios];
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            HydrationPolicy groupPolicy = groupIndex == 0 ? agentImagePolicy : hydration;
            foreach (ProviderMedia media in groups[groupIndex])
            {
                await HydrateAsync(userID, media, groupPolicy, repository, domain, policy, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        if (input.Mask is not null)
        {
            await HydrateAsync(userID, input.Mask, hydration, repository, domain, policy, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>任一参考素材仍以 resource: 引用表示（判断是否需要水合）。</summary>
    public static bool HasResourceReferences(TextTaskInput input) =>
        input.ReferenceImages.Concat(input.ReferenceVideos).Concat(input.ReferenceAudios)
            .Any(media => media.StorageKey.StartsWith("resource:", StringComparison.Ordinal))
        || (input.Mask?.StorageKey.StartsWith("resource:", StringComparison.Ordinal) ?? false);

    private static async Task HydrateAsync(
        string userID, ProviderMedia media, HydrationPolicy hydration,
        Repository repository, ResourceDomainService domain, RuntimePolicySetting policy,
        CancellationToken cancellationToken)
    {
        if (!media.StorageKey.StartsWith("resource:", StringComparison.Ordinal))
        {
            if (hydration.RequireURL && media.DataURL.StartsWith("data:", StringComparison.Ordinal))
            {
                throw AppError.BadAuthRequest("当前 JSON 视频协议的参考素材不能使用内嵌数据，请先上传到对象存储或提供公网素材地址");
            }
            return;
        }
        string resourceID = media.StorageKey["resource:".Length..];
        Resource? resource = await repository.ResourceForUserAsync(userID, resourceID, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("读取任务参考资源失败：record not found");
        if (resource.Status != "ready")
        {
            throw AppError.BadAuthRequest("任务参考资源尚未上传完成");
        }
        // Agent 看图强制图片类型与模型字节上限（对应 Go: hydrateProviderMedia 的 imageOnly 分支）。
        if (hydration.ImageOnly
            && !resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw AppError.BadAuthRequest("看图资源不是图片");
        }
        if (hydration.MaxBytes > 0 && resource.Size > hydration.MaxBytes)
        {
            throw AppError.BadAuthRequest("参考图片文件超过当前模型大小限制");
        }

        bool objectStorage = ResourceUsesObjectStorage(resource);
        bool useObjectURL = hydration.RequireURL || (hydration.PreferURL && objectStorage);
        if (useObjectURL)
        {
            media.URL = await domain.ProviderResourceUrlAsync(
                resource, DateTime.UtcNow.AddHours(4), cancellationToken).ConfigureAwait(false);
            media.DataURL = "";
            media.MIMEType = FirstNonEmpty(media.MIMEType, resource.MimeType);
            media.Bytes = resource.Size;
            media.Width = (int)resource.Width;
            media.Height = (int)resource.Height;
            media.DurationMs = resource.DurationMs;
            return;
        }
        // Agent 看图以归属校验后的资源文件为准，不能让附带的内嵌内容替换真实图片。
        if (!hydration.ImageOnly && media.DataURL.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        long resourceLimit = policy.Resource.ResourceUploadMB * 1024 * 1024;
        if (hydration.MaxBytes > 0)
        {
            resourceLimit = Math.Min(resourceLimit, hydration.MaxBytes);
        }
        ResourceStream stream = await domain.OpenResourceRangeAsync(
            userID, resource.ID, null, cancellationToken).ConfigureAwait(false);
        await using (stream.Body.ConfigureAwait(false))
        {
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > resourceLimit)
                {
                    throw AppError.BadAuthRequest(
                        $"任务参考资源超过读取上限 {resourceLimit} 字节");
                }
                buffer.Write(chunk, 0, read);
            }
            byte[] data = buffer.ToArray();
            if (data.Length == 0)
            {
                throw AppError.BadAuthRequest("任务参考资源内容为空");
            }
            string mimeType = NormalizedMediaMimeType(
                FirstNonEmpty(media.MIMEType, resource.MimeType), data);
            media.DataURL = "data:" + mimeType + ";base64," + Convert.ToBase64String(data);
            media.MIMEType = mimeType;
            media.Bytes = data.Length;
            media.Width = (int)resource.Width;
            media.Height = (int)resource.Height;
            media.DurationMs = resource.DurationMs;
        }
    }

    private static bool ResourceUsesObjectStorage(Resource resource)
    {
        string provider = resource.Provider.Trim().ToLowerInvariant();
        return provider.Length > 0 && provider != "local";
    }

    private static string NormalizedMediaMimeType(string declared, byte[] data)
    {
        declared = declared.Trim().Split(';')[0];
        if (declared.Length > 0 && declared != "application/octet-stream")
        {
            return declared;
        }
        string detected = data.Length >= 4 ? DetectContentType(data) : "";
        return detected.Length > 0 ? detected : "application/octet-stream";
    }

    /// <summary>与 Go <c>http.DetectContentType</c> 对齐的常见媒体签名探测（子集足够）。</summary>
    private static string DetectContentType(byte[] data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return "image/gif";
        }
        if (data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
            if (brand is "M4A " or "M4B ")
            {
                return "audio/mp4";
            }
            return "video/mp4";
        }
        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            return "video/webm";
        }
        if (data.Length >= 3 && data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33)
        {
            return "audio/mpeg";
        }
        if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
        {
            return "audio/mpeg";
        }
        if (data.Length >= 4 && data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
        {
            return "application/ogg";
        }
        if (data.Length >= 4 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46)
        {
            return "audio/wave";
        }
        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string value in values)
        {
            if (value is not null && value.Trim().Length > 0)
            {
                return value.Trim();
            }
        }
        return "";
    }
}
