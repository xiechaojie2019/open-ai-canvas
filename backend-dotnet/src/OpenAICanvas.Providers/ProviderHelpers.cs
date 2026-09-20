#nullable enable
using System.Globalization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Providers;

/// <summary>
/// Provider 层的小工具函数（媒体 URL 校验、data URL 编码、配置字段取用）。
/// 对应 Go: <c>internal/app/provider.go</c> 的
/// <c>firstNonEmptyString</c> / <c>dataURL</c> / <c>stringField</c> /
/// <c>withSystemPrompt</c> / <c>metadataString</c> /
/// <c>openAIImageInputURL</c> / <c>openAIVideoInputURL</c> / <c>isPublicMediaURL</c> /
/// <c>providerChannelModelKey</c> / <c>systemChannelIDFromBaseURL</c>。
/// </summary>
public static class ProviderHelpers
{
    /// <summary>对应 Go: <c>isPublicMediaURL</c>（仅前缀判断，不做 DNS 解析）。</summary>
    public static bool IsPublicMediaURL(string value)
    {
        string lower = (value ?? "").ToLowerInvariant();
        return lower.StartsWith("http://", StringComparison.Ordinal)
            || lower.StartsWith("https://", StringComparison.Ordinal);
    }

    /// <summary>
    /// OpenAI 文本多模态的参考图片来源：仅接受 <c>data:image/</c> 或公网 URL。
    /// 对应 Go: <c>openAIImageInputURL</c>。
    /// </summary>
    public static string OpenAIImageInputURL(ProviderMedia media)
    {
        string value = media.DataURL.Trim();
        if (value.StartsWith("data:image/", StringComparison.Ordinal))
        {
            return value;
        }
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("参考图片 MIME 类型无效，请重新读取或上传图片");
        }
        value = media.URL.Trim();
        if (value.StartsWith("data:image/", StringComparison.Ordinal) || IsPublicMediaURL(value))
        {
            return value;
        }
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("参考图片 MIME 类型无效，请重新读取或上传图片");
        }
        throw new InvalidOperationException("OpenAI 文本多模态参考图片需要公网 URL 或 base64 data URL");
    }

    /// <summary>
    /// 文本多模态的参考视频来源：仅接受 <c>data:video/</c> 或公网 URL。
    /// 对应 Go: <c>openAIVideoInputURL</c>。
    /// </summary>
    public static string OpenAIVideoInputURL(ProviderMedia media)
    {
        string value = media.DataURL.Trim();
        if (value.StartsWith("data:video/", StringComparison.Ordinal))
        {
            return value;
        }
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("参考视频 MIME 类型无效，请重新读取或上传视频");
        }
        value = media.URL.Trim();
        if (value.StartsWith("data:video/", StringComparison.Ordinal) || IsPublicMediaURL(value))
        {
            return value;
        }
        if (value.StartsWith("data:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("参考视频 MIME 类型无效，请重新读取或上传视频");
        }
        throw new InvalidOperationException("文本多模态参考视频需要公网 URL 或 base64 data URL");
    }

    /// <summary>
    /// 构造 data URL：空 MIME 归一为 <c>application/octet-stream</c>，且只取主类型。
    /// 对应 Go: <c>dataURL</c>。
    /// </summary>
    public static string DataUrl(string mimeType, byte[] data)
    {
        string mainType = mimeType.Length == 0 ? "application/octet-stream" : mimeType.Split(';')[0];
        return "data:" + mainType + ";base64," + Convert.ToBase64String(data);
    }

    /// <summary>
    /// 系统提示词与用户提示词拼接（系统提示词为空时原样返回）。
    /// 对应 Go: <c>withSystemPrompt</c>。
    /// </summary>
    public static string WithSystemPrompt(string? systemPrompt, string prompt)
    {
        string trimmed = (systemPrompt ?? "").Trim();
        return trimmed.Length == 0 ? prompt : trimmed + "\n\n" + prompt;
    }

    /// <summary>
    /// 渠道模型键：取 <c>channelModelKey</c> 或 <c>model</c>，并剥离 <c>models/</c> 前缀。
    /// 对应 Go: <c>providerChannelModelKey</c>。
    /// </summary>
    public static string ChannelModelKey(string? channelModelKey, string? model)
    {
        string value = FirstNonEmpty(channelModelKey, model).Trim();
        return value.StartsWith("models/", StringComparison.Ordinal) ? value["models/".Length..] : value;
    }

    /// <summary>
    /// 从系统渠道 Base URL 反推渠道 ID（形如 <c>/api/ai/system/{id}</c> 或 <c>/api/{id}</c>）。
    /// 对应 Go: <c>systemChannelIDFromBaseURL</c>。
    /// </summary>
    /// <remarks>
    /// 命中 <c>v1</c>/<c>v1beta</c>/<c>v2</c>/<c>v3</c>/<c>plan</c>/<c>ai</c> 等保留段时跳过；
    /// 剩余段中仍含 <c>/</c> 的（说明不是末段）同样跳过。marker 取**最后一次**出现的位置。
    /// </remarks>
    public static string SystemChannelIdFromBaseUrl(string? baseUrl)
    {
        string value = (baseUrl ?? "").Trim();
        string lower = value.ToLowerInvariant();
        foreach (string marker in new[] { "/api/ai/system/", "/api/" })
        {
            int index = lower.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }
            string id = value[(index + marker.Length)..].Trim('/');
            int queryIndex = id.IndexOfAny(['?', '#']);
            if (queryIndex >= 0)
            {
                id = id[..queryIndex];
            }
            if (id.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }
            id = id.Trim();
            if (id.Length == 0)
            {
                continue;
            }
            switch (id.ToLowerInvariant())
            {
                case "v1" or "v1beta" or "v2" or "v3" or "plan" or "ai":
                    continue;
                default:
                    return id;
            }
        }
        return "";
    }

    /// <summary>对应 Go: <c>firstNonEmptyString</c>（返回首个 trim 后非空的值）。</summary>
    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }
        return "";
    }

    /// <summary>
    /// 只读取异构上游 JSON 中的可选字符串：缺失或 null 返回空串，
    /// 存在但类型错误<b>也不强制转换</b>（返回空串而非抛错，对应 Go 的 <c>stringField</c>）。
    /// </summary>
    /// <remarks>task ID 等必填标识必须用 <c>JsonFields.RequireString</c>，让类型错误显式失败。</remarks>
    public static string StringField(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        try
        {
            return OpenAICanvas.Outbound.JsonFields.OptionalString(payload, key);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>对应 Go: <c>metadataString</c>。</summary>
    public static string MetadataString(IReadOnlyDictionary<string, object?>? metadata, string key) =>
        StringField(metadata, key).Trim();

    /// <summary>
    /// 判断两个地址是否同源（scheme + host 都相同）。
    /// 对应 Go: <c>sameProviderOrigin</c>。
    /// </summary>
    /// <remarks>
    /// 用于决定是否把渠道鉴权带给下载地址：<b>跨源绝不带鉴权</b>，
    /// 否则等于把渠道密钥送给第三方 CDN。任一侧无法解析出 scheme+host 时都返回 false。
    /// </remarks>
    public static bool IsSameProviderOrigin(string baseUrl, string rawUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out Uri? baseUri)
            || !Uri.TryCreate((rawUrl ?? "").Trim(), UriKind.Absolute, out Uri? targetUri))
        {
            return false;
        }
        if (baseUri.Scheme.Length == 0 || baseUri.Host.Length == 0
            || targetUri.Scheme.Length == 0 || targetUri.Host.Length == 0)
        {
            return false;
        }
        return string.Equals(baseUri.Scheme, targetUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(baseUri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把字符串值解析为整数（失败为 0），对应 Go 中忽略错误的 <c>strconv.Atoi</c> 用法。
    /// </summary>
    public static int AtoiOrZero(string? value) =>
        int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

    /// <summary>
    /// 参考素材是否走对象存储（决定是否需要签名 URL 而非内联 data URL）。
    /// 对应 Go: <c>resourceUsesObjectStorage</c>（provider 非 local 即视为对象存储）。
    /// </summary>
    public static bool ResourceUsesObjectStorage(Resource? resource) =>
        resource is not null
        && !string.Equals(resource.Provider, "local", StringComparison.Ordinal);
}
