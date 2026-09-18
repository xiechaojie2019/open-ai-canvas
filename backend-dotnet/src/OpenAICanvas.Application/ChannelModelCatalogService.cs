#nullable enable
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Application;

/// <summary>上游模型目录条目。对应 Go: <c>app.ChannelModelCatalogItem</c>。</summary>
public sealed class ChannelModelCatalogItemDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonPropertyName("modelType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelType { get; init; }

    [JsonPropertyName("supportedEndpointTypes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SupportedEndpointTypes { get; init; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogOptionsDto? Options { get; init; }
}

public sealed class CatalogOptionsDto
{
    [JsonPropertyName("aspectRatio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CatalogOptionDto>? AspectRatio { get; set; }

    [JsonPropertyName("durationSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CatalogOptionDto>? DurationSeconds { get; set; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CatalogOptionDto>? Resolution { get; set; }
}

public sealed record CatalogOptionDto(string Value, string? Label);

/// <summary>
/// 上游模型目录拉取（/models）。对应 Go: <c>app/channel_model_catalog.go</c>
/// 的 <c>FetchChannelModelCatalog</c> / <c>apiURL</c> / <c>channelModelsUpstreamError</c>。
/// </summary>
/// <remarks>
/// 只代理固定的模型目录 GET；渠道密钥仅用于本次请求，不写入数据库或日志。
/// <c>extendChannelModelCatalog</c>（插件扩展目录）属阶段 10，未移植。
/// </remarks>
public sealed class ChannelModelCatalogService
{
    /// <summary>对应 Go: <c>maxProviderResponseBytes</c>。</summary>
    private const long MaxResponseBytes = 64L << 20;

    /// <summary>对应 Go: <c>channelAPIPrefixes</c>（匹配顺序即声明顺序）。</summary>
    private static readonly string[] ChannelApiPrefixes =
        ["/api/plan/v3", "/api/v3", "/api/v1", "/v1beta", "/v1", "/v2", "/v3"];

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>对应 Go: <c>ChannelAPIURL</c>。渠道地址可为 host、host/、host/v1 或 host/v1/。</summary>
    public static string ApiUrl(string baseURL, string path) =>
        ApiUrlWithDefaultPrefix(baseURL, path, "/v1");

    /// <summary>对应 Go: <c>apiURLWithDefaultPrefix</c>。请求路径显式版本优先于 baseURL 残留版本。</summary>
    public static string ApiUrlWithDefaultPrefix(string baseURL, string path, string defaultPrefix)
    {
        string baseTrimmed = baseURL.Trim().TrimEnd('/');
        string requestPath = path.Trim();
        if (requestPath.Length == 0)
        {
            return baseTrimmed;
        }
        if (!requestPath.StartsWith('/'))
        {
            requestPath = "/" + requestPath;
        }

        string requestPrefix = RequestApiPathPrefix(requestPath);
        string basePrefix = BaseApiPathPrefix(baseTrimmed);
        if (requestPrefix.Length > 0)
        {
            if (basePrefix == requestPrefix)
            {
                return baseTrimmed + requestPath[requestPrefix.Length..];
            }
            return baseTrimmed.EndsWith(basePrefix, StringComparison.OrdinalIgnoreCase)
                ? baseTrimmed[..^basePrefix.Length] + requestPath
                : baseTrimmed + requestPath;
        }
        if (basePrefix.Length > 0)
        {
            return baseTrimmed + requestPath;
        }
        return baseTrimmed + defaultPrefix + requestPath;
    }

    private static string RequestApiPathPrefix(string value)
    {
        string lower = value.ToLowerInvariant();
        foreach (string prefix in ChannelApiPrefixes)
        {
            if (lower == prefix || lower.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                lower.StartsWith(prefix + "?", StringComparison.Ordinal) ||
                lower.StartsWith(prefix + "#", StringComparison.Ordinal))
            {
                return prefix;
            }
        }
        return "";
    }

    private static string BaseApiPathPrefix(string value)
    {
        string lower = value.TrimEnd('/').ToLowerInvariant();
        foreach (string prefix in ChannelApiPrefixes)
        {
            if (lower == prefix || lower.EndsWith(prefix, StringComparison.Ordinal))
            {
                return prefix;
            }
        }
        return "";
    }

    /// <summary>目录拉取输入。对应 Go: <c>app.ChannelModelsRequest</c>。</summary>
    public sealed record CatalogRequest(
        string BaseURL,
        string APIKey,
        string APIFormat,
        IReadOnlyList<OutboundHeader>? Headers);

    /// <summary>拉取并归一化上游模型目录（按 ID 排序）。对应 Go: <c>FetchChannelModelCatalog</c>。</summary>
    public async Task<IReadOnlyList<ChannelModelCatalogItemDto>> FetchChannelModelCatalogAsync(
        User actor,
        CatalogRequest input,
        CancellationToken cancellationToken = default)
    {
        if (actor is null || actor.ID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        string baseURL = input.BaseURL.Trim().TrimEnd('/');
        string apiKey = input.APIKey.Trim();
        if (baseURL.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写 Base URL");
        }
        if (apiKey.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写 API Key");
        }
        string apiFormat = input.APIFormat.Trim().ToLowerInvariant();
        if (apiFormat.Length == 0)
        {
            apiFormat = "openai";
        }
        if (apiFormat is not ("openai" or "gemini"))
        {
            throw AppError.BadAuthRequest("接口协议不支持拉取模型");
        }
        List<OutboundHeader> headers = OutboundGuard.NormalizeOutboundHeaders(input.Headers ?? []);

        string target = ApiUrl(baseURL, "/models");
        if (apiFormat == "gemini")
        {
            if (!baseURL.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
            {
                baseURL += "/v1beta";
            }
            target = baseURL + "/models";
        }
        _ = await OutboundGuard.ValidateOutboundUrlAsync(target).ConfigureAwait(false);

        using HttpRequestMessage request = new(HttpMethod.Get, target);
        if (apiFormat == "gemini")
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        OutboundHttpClient.ApplyHeaders(request, headers);

        string body = await SendWithPolicyAsync(request, cancellationToken).ConfigureAwait(false);

        CatalogPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CatalogPayload>(body, PayloadOptions);
        }
        catch (JsonException error)
        {
            throw AppError.Wrap(502, "模型服务返回的不是有效 JSON", error);
        }
        if (payload is null)
        {
            throw AppError.New(502, "模型服务返回的不是有效 JSON");
        }
        if (!string.IsNullOrWhiteSpace(payload.Error?.Message))
        {
            throw AppError.New(502, "模型服务返回失败，请检查渠道配置");
        }
        if (payload.Code is not null && payload.Code != 0)
        {
            throw AppError.New(502, "模型服务返回失败，请检查渠道配置");
        }

        List<CatalogItem> items = apiFormat == "gemini" ? payload.Models ?? [] : payload.Data ?? [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ChannelModelCatalogItemDto> catalog = [];
        foreach (CatalogItem item in items)
        {
            string name = FirstNonEmpty(item.ID, item.Name).Trim();
            if (name.StartsWith("models/", StringComparison.Ordinal))
            {
                name = name["models/".Length..];
            }
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }
            catalog.Add(new ChannelModelCatalogItemDto
            {
                ID = name,
                DisplayName = item.DisplayName?.Trim(),
                ModelType = NormalizeModelType(item.ModelType),
                SupportedEndpointTypes = NormalizeEndpointTypes(item.SupportedEndpointTypes),
                Options = NormalizeOptions(item.Options),
            });
        }
        return catalog.OrderBy(item => item.ID, StringComparer.Ordinal).ToList();
    }

    /// <summary>拉取并去重的模型名列表（字典序）。对应 Go: <c>FetchChannelModels</c>。</summary>
    public async Task<IReadOnlyList<string>> FetchChannelModelsAsync(
        User actor,
        CatalogRequest input,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChannelModelCatalogItemDto> items = await FetchChannelModelCatalogAsync(
            actor, input, cancellationToken).ConfigureAwait(false);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> models = [];
        foreach (ChannelModelCatalogItemDto item in items)
        {
            string name = item.ID.Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }
            models.Add(name);
        }
        return models.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 发送并读取响应体：非 2xx 映射为上游错误文案，大小上限 64MB。
    /// 对应 Go: <c>doBinary</c> 在目录拉取场景的最小投影。
    /// </summary>
    private static async Task<string> SendWithPolicyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using HttpClient client = OutboundHttpClient.Create();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or SocketException or InvalidOperationException)
        {
            // 网络层失败：与 Go 的非 HTTP 错误分支一致（502 固定文案）。
            throw AppError.Wrap(502, "连接模型服务失败，请检查渠道地址和网络", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw UpstreamStatusError((int)response.StatusCode);
            }
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxResponseBytes)
                {
                    throw AppError.New(502, "模型服务响应超过大小上限");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    /// <summary>对应 Go: <c>channelModelsUpstreamError</c>。</summary>
    private static AppError UpstreamStatusError(int statusCode) => statusCode switch
    {
        401 or 403 => AppError.New(502, "模型服务鉴权失败，请检查 API Key"),
        404 => AppError.New(502, "模型服务未提供 /models 接口"),
        429 => AppError.New(502, "模型服务请求过于频繁或额度不足"),
        _ => AppError.Wrap(502, UpstreamStatusText(statusCode), null),
    };

    /// <summary>对应 Go: <c>providerHTTPError.Error()</c>。</summary>
    private static string UpstreamStatusText(int statusCode) => statusCode switch
    {
        524 => "上游网关超时（524）：模型请求可能仍在服务端执行并产生费用，请勿立即重试，请先到供应商后台核对任务或账单",
        400 or 422 => "模型服务拒绝了请求，请检查模型和参数",
        408 or 504 => "模型服务响应超时，请稍后重试",
        >= 500 => $"模型服务暂时不可用（HTTP {statusCode}）",
        _ => $"模型服务请求失败（HTTP {statusCode.ToString(CultureInfo.InvariantCulture)}）",
    };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return "";
    }

    private static string? NormalizeModelType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "text" or "image" or "video" or "audio" => value!.Trim().ToLowerInvariant(),
        _ => null,
    };

    private static List<string>? NormalizeEndpointTypes(List<string>? values)
    {
        if (values is null)
        {
            return null;
        }
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }
            result.Add(trimmed);
        }
        return result.Count == 0 ? null : result;
    }

    private static CatalogOptionsDto? NormalizeOptions(CatalogItem.CatalogOptions? options)
    {
        if (options is null)
        {
            return null;
        }
        CatalogOptionsDto result = new()
        {
            AspectRatio = NormalizeOptionList(options.AspectRatio),
            DurationSeconds = NormalizeOptionList(options.DurationSeconds),
            Resolution = NormalizeOptionList(options.Resolution),
        };
        return result.AspectRatio is null && result.DurationSeconds is null && result.Resolution is null
            ? null
            : result;
    }

    private static List<CatalogOptionDto>? NormalizeOptionList(List<CatalogItem.CatalogOption>? options)
    {
        if (options is null)
        {
            return null;
        }
        List<CatalogOptionDto> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CatalogItem.CatalogOption option in options)
        {
            string value = option.Value.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(new CatalogOptionDto(value, option.Label?.Trim()));
        }
        return result.Count == 0 ? null : result;
    }

    // ------------------------------------------------------------ 上游 payload

    private sealed class CatalogPayload
    {
        [JsonPropertyName("data")]
        public List<CatalogItem>? Data { get; set; }

        [JsonPropertyName("models")]
        public List<CatalogItem>? Models { get; set; }

        [JsonPropertyName("error")]
        public CatalogError? Error { get; set; }

        [JsonPropertyName("code")]
        public long? Code { get; set; }
    }

    private sealed class CatalogError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class CatalogItem
    {
        [JsonPropertyName("id")]
        public string? ID { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("model_type")]
        public string? ModelType { get; set; }

        [JsonPropertyName("supported_endpoint_types")]
        public List<string>? SupportedEndpointTypes { get; set; }

        [JsonPropertyName("options")]
        public CatalogOptions? Options { get; set; }

        internal sealed class CatalogOptions
        {
            [JsonPropertyName("aspectRatio")]
            public List<CatalogOption>? AspectRatio { get; set; }

            [JsonPropertyName("durationSeconds")]
            public List<CatalogOption>? DurationSeconds { get; set; }

            [JsonPropertyName("resolution")]
            public List<CatalogOption>? Resolution { get; set; }
        }

        internal sealed class CatalogOption
        {
            [JsonPropertyName("value")]
            public string Value { get; set; } = "";

            [JsonPropertyName("label")]
            public string? Label { get; set; }
        }
    }
}
