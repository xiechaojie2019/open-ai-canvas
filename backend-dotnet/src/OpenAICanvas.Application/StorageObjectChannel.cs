#nullable enable
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Application;

/// <summary>
/// 对象存储通道的归一化运行时设置（对应 Go: <c>storage.Settings</c>）。
/// 由 <see cref="StorageSettingsService"/> 从平台/用户持久化设置投影。
/// </summary>
public sealed class StorageChannelSettings
{
    [JsonPropertyName("delivery")] public StorageDeliverySettings Delivery { get; set; } = new();
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("provider")] public string Provider { get; set; } = "aliyun";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("cdnBaseUrl")] public string CdnBaseUrl { get; set; } = "";
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "";
    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";
    [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";
    [JsonPropertyName("publicBaseUrl")] public string PublicBaseUrl { get; set; } = "";
    [JsonPropertyName("pathPrefix")] public string PathPrefix { get; set; } = "open-ai-canvas";
    [JsonPropertyName("s3Preset")] public string S3Preset { get; set; } = "custom";
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; }
    [JsonPropertyName("sessionToken")] public string SessionToken { get; set; } = "";
    [JsonPropertyName("storageLocationId")] public string StorageLocationId { get; set; } = "";
    [JsonPropertyName("allowUserS3")] public bool AllowUserS3 { get; set; }
    [JsonPropertyName("archivedCredentials")]
    public Dictionary<string, StorageArchivedCredentials>? ArchivedCredentials { get; set; }

    public StorageChannelSettings Clone() => new()
    {
        Delivery = new StorageDeliverySettings
        {
            CdnAuthMode = Delivery.CdnAuthMode,
            RequireCDN = Delivery.RequireCDN,
            AllowPrivateProxy = Delivery.AllowPrivateProxy,
        },
        Enabled = Enabled, Provider = Provider, Region = Region, Endpoint = Endpoint,
        CdnBaseUrl = CdnBaseUrl, Bucket = Bucket, AccessKeyId = AccessKeyId,
        AccessKeySecret = AccessKeySecret, PublicBaseUrl = PublicBaseUrl, PathPrefix = PathPrefix,
        S3Preset = S3Preset, PathStyle = PathStyle, SessionToken = SessionToken,
        StorageLocationId = StorageLocationId, AllowUserS3 = AllowUserS3,
        ArchivedCredentials = ArchivedCredentials is null
            ? null
            : new Dictionary<string, StorageArchivedCredentials>(ArchivedCredentials, StringComparer.Ordinal),
    };
}

public sealed class StorageDeliverySettings
{
    [JsonPropertyName("cdnAuthMode")] public string CdnAuthMode { get; set; } = "";
    [JsonPropertyName("requireCDN")] public bool RequireCDN { get; set; }
    [JsonPropertyName("allowPrivateProxy")] public bool AllowPrivateProxy { get; set; }
}

public sealed class StorageArchivedCredentials
{
    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";
    [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";
}

/// <summary>远程对象读取流。Owner 持有 HTTP 响应生命周期，随 Body 一起释放。</summary>
public sealed class StorageObjectStream : IDisposable
{
    public required Stream Body { get; init; }
    public required int StatusCode { get; init; }
    public long ContentLength { get; init; }
    public string ContentRange { get; init; } = "";
    public string AcceptRanges { get; init; } = "bytes";
    public IDisposable? Owner { get; init; }

    public void Dispose()
    {
        Body.Dispose();
        Owner?.Dispose();
    }
}

/// <summary>
/// 对象存储读写通道：阿里云 OSS（V1 签名）、腾讯云 COS、七牛云 Kodo 与通用 S3。
/// 写入只有在配置的源站接受后才算成功；读取按 CDN 鉴权/源站直连/服务端代理分流。
/// 对应 Go: <c>internal/storage</c>（objects.go / s3.go / delivery.go / settings.go）。
/// </summary>
public static class StorageObjectChannel
{
    public const string AliyunProvider = "aliyun";
    public const string TencentProvider = "tencent";
    public const string QiniuProvider = "qiniu";
    public const string S3Provider = "s3";
    private const string DefaultPathPrefix = "open-ai-canvas";
    private static readonly TimeSpan ObjectAccessTtl = TimeSpan.FromMinutes(5);

    public static StorageChannelSettings Normalize(StorageChannelSettings value)
    {
        // Go 的 Delivery/StorageLocationID 都是值类型零值永不为 nil；
        // 反序列化链路（Stored 的可空字段写出 null）必须等价保证非空。
        value.Delivery ??= new StorageDeliverySettings();
        value.StorageLocationId ??= "";
        value.Provider = value.Provider.Trim().ToLowerInvariant();
        if (value.Provider.Length == 0)
        {
            value.Provider = AliyunProvider;
        }
        value.Region = value.Region.Trim();
        value.Endpoint = value.Endpoint.Trim().TrimEnd('/');
        if (value.Provider == TencentProvider && value.Endpoint.Length == 0 && value.Region.Length > 0)
        {
            value.Endpoint = "https://cos." + value.Region + ".myqcloud.com";
        }
        value.CdnBaseUrl = value.CdnBaseUrl.Trim().TrimEnd('/');
        value.Delivery.CdnAuthMode = value.Delivery.CdnAuthMode.Trim().ToLowerInvariant();
        value.Bucket = value.Bucket.Trim();
        value.AccessKeyId = value.AccessKeyId.Trim();
        value.AccessKeySecret = value.AccessKeySecret.Trim();
        value.PublicBaseUrl = value.PublicBaseUrl.Trim().TrimEnd('/');
        value.PathPrefix = value.PathPrefix.Trim().Trim('/');
        if (value.PathPrefix.Length == 0)
        {
            value.PathPrefix = DefaultPathPrefix;
        }
        value.S3Preset = value.S3Preset.Trim().ToLowerInvariant();
        if (value.S3Preset.Length == 0)
        {
            value.S3Preset = "custom";
        }
        value.SessionToken = value.SessionToken.Trim();
        value.StorageLocationId = value.StorageLocationId.Trim();
        return value;
    }

    // ------------------------------------------------------------ 写入

    /// <summary>上传对象，返回 ETag（七牛返回文件 Hash）。对应 Go: <c>PutOSSObject</c>。</summary>
    public static async Task<string> PutObjectAsync(
        StorageChannelSettings setting, string objectKey, string mimeType, long size,
        Stream body, CancellationToken cancellationToken)
    {
        setting = Normalize(setting);
        return setting.Provider switch
        {
            TencentProvider => await PutCOSObjectAsync(setting, objectKey, mimeType, size, body, cancellationToken).ConfigureAwait(false),
            QiniuProvider => await PutQiniuObjectAsync(setting, objectKey, mimeType, size, body, cancellationToken).ConfigureAwait(false),
            S3Provider => await PutS3ObjectAsync(setting, objectKey, mimeType, size, body, cancellationToken).ConfigureAwait(false),
            _ => await PutAliyunObjectAsync(setting, objectKey, mimeType, size, body, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken, string failurePrefix)
    {
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromMinutes(2));
        HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.MultipleChoices)
            && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            string detail = "";
            try
            {
                detail = Truncate(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception)
            {
                // 读失败详情是辅助诊断，不能覆盖原始状态码错误。
            }
            response.Dispose();
            throw new InvalidOperationException($"{failurePrefix}失败：{(int)response.StatusCode} {response.StatusCode} {detail}".TrimEnd());
        }
        return response;
    }

    private static string Truncate(string value) => value.Length <= 512 ? value.Trim() : value[..512].Trim();

    // ---- 阿里云 OSS（V1 签名，与 Go NewOSSRequest 一致） ----

    private static async Task<string> PutAliyunObjectAsync(
        StorageChannelSettings setting, string objectKey, string mimeType, long size,
        Stream body, CancellationToken cancellationToken)
    {
        mimeType = string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType;
        using HttpRequestMessage request = AliyunRequest(
            HttpMethod.Put, setting, objectKey, mimeType, new NonDisposingStream(body));
        if (size > 0)
        {
            request.Content!.Headers.ContentLength = size;
        }
        using HttpResponseMessage response = await SendAsync(request, cancellationToken, "OSS 上传").ConfigureAwait(false);
        return response.Headers.ETag?.Tag?.Trim('"') ?? "";
    }

    private static HttpRequestMessage AliyunRequest(
        HttpMethod method, StorageChannelSettings setting, string objectKey,
        string contentType, Stream? body)
    {
        Uri baseUri = OssBucketBaseUri(setting);
        string path = baseUri.AbsolutePath.TrimEnd('/') + "/" + objectKey.TrimStart('/');
        Uri requestUri = new(baseUri, path);
        HttpRequestMessage request = new(method, requestUri);
        if (body is not null)
        {
            request.Content = new StreamContent(body);
        }
        string date = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("Date", date);
        if (contentType.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        string stringToSign = string.Join("\n", method.Method, "", contentType, date, "/" + setting.Bucket + "/" + objectKey.TrimStart('/'));
        string signature = Convert.ToBase64String(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret)).ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        request.Headers.TryAddWithoutValidation("Authorization", $"OSS {setting.AccessKeyId}:{signature}");
        return request;
    }

    /// <summary>签名直链（阿里云 V1 查询签名）。对应 Go: <c>signedAliyunOSSObjectURL</c>。</summary>
    private static string SignedAliyunObjectUrl(StorageChannelSettings setting, string objectKey, DateTime expiresAt, string disposition)
    {
        Uri baseUri = OssBucketBaseUri(setting);
        string key = objectKey.TrimStart('/');
        string canonicalResource = "/" + setting.Bucket + "/" + key;
        string expires = ((DateTimeOffset)expiresAt.ToUniversalTime()).ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        List<KeyValuePair<string, string>> query = [];
        if (disposition.Length > 0)
        {
            query.Add(new("response-content-disposition", disposition));
            canonicalResource += "?response-content-disposition=" + disposition;
        }
        string stringToSign = string.Join("\n", "GET", "", "", expires, canonicalResource);
        string signature = Convert.ToBase64String(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret)).ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        query.Add(new("OSSAccessKeyId", setting.AccessKeyId));
        query.Add(new("Expires", expires));
        query.Add(new("Signature", signature));
        string queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        UriBuilder signed = new(baseUri) { Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + key, Query = queryString };
        return signed.Uri.ToString();
    }

    /// <summary>Bucket 域名：非本地/非 IP 端点改写为桶子域。对应 Go: <c>OssBucketBaseURL</c>。</summary>
    private static Uri OssBucketBaseUri(StorageChannelSettings setting)
    {
        string endpoint = setting.Endpoint.TrimEnd('/');
        if (endpoint.Length == 0)
        {
            throw new InvalidOperationException("OSS Endpoint 为空");
        }
        if (!endpoint.Contains("://", StringComparison.Ordinal))
        {
            endpoint = "https://" + endpoint;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            throw new InvalidOperationException("OSS Endpoint 格式不正确");
        }
        string host = parsed.Host;
        bool local = host is "localhost" || host.StartsWith("localhost:", StringComparison.Ordinal) || IPAddress.TryParse(host, out _);
        UriBuilder builder = new(parsed);
        if (!local && !builder.Host.StartsWith(setting.Bucket + ".", StringComparison.OrdinalIgnoreCase))
        {
            builder.Host = setting.Bucket + "." + builder.Host;
        }
        return builder.Uri;
    }

    // ---- 腾讯云 COS（q-sign-algorithm=sha1，与 cos-go-sdk-v5 授权传输一致） ----

    private static async Task<string> PutCOSObjectAsync(
        StorageChannelSettings setting, string objectKey, string mimeType, long size,
        Stream body, CancellationToken cancellationToken)
    {
        Uri bucketUri = CosBucketBaseUri(setting);
        string key = objectKey.TrimStart('/');
        Uri requestUri = new(bucketUri, "/" + key);
        HttpRequestMessage request = new(HttpMethod.Put, requestUri)
        {
            Content = new StreamContent(new NonDisposingStream(body)),
        };
        if (size > 0)
        {
            request.Content.Headers.ContentLength = size;
        }
        mimeType = string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType;
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        request.Headers.TryAddWithoutValidation(
            "Authorization", CosAuthorization(setting, "put", "/" + key, request, ""));
        using HttpResponseMessage response = await SendAsync(request, cancellationToken, "COS 上传").ConfigureAwait(false);
        return response.Headers.ETag?.Tag?.Trim('"') ?? "";
    }

    private static async Task<StorageObjectStream> GetCOSObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader)
    {
        Uri bucketUri = CosBucketBaseUri(setting);
        string key = objectKey.TrimStart('/');
        Uri requestUri = new(bucketUri, "/" + key);
        HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        if (rangeHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }
        request.Headers.TryAddWithoutValidation(
            "Authorization", CosAuthorization(setting, "get", "/" + key, request, rangeHeader));
        HttpResponseMessage response = await SendAsync(request, CancellationToken.None, "COS 读取").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var unsatisfiable = new StorageObjectStream
            {
                Body = Stream.Null, StatusCode = (int)response.StatusCode,
                ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            };
            response.Dispose();
            return unsatisfiable;
        }
        return new StorageObjectStream
        {
            Body = response.Content.ReadAsStream(CancellationToken.None),
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength ?? 0,
            ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            AcceptRanges = "bytes",
            Owner = response,
        };
    }

    /// <summary>COS 请求授权头。对应 cos-go-sdk-v5 的 AuthorizationTransport 签名。</summary>
    private static string CosAuthorization(
        StorageChannelSettings setting, string method, string uriPath, HttpRequestMessage request, string rangeHeader)
    {
        if (setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("COS 访问密钥不可用");
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string keyTime = $"{now - 60};{now + 600}";
        // 参与签名的请求头：host 固定；带 Range 时一并签入（与 SDK 的 AddAuthHeader 一致）。
        List<KeyValuePair<string, string>> headers = new() { new("host", request.RequestUri!.Host) };
        if (rangeHeader.Length > 0)
        {
            headers.Add(new("range", rangeHeader));
        }
        string headerList = string.Join(";", headers.Select(pair => pair.Key));
        string headerValues = string.Join("&", headers.Select(pair =>
            pair.Key.ToLowerInvariant() + "=" + Uri.EscapeDataString(pair.Value).ToLowerInvariant()));
        string formatString = string.Join("\n", method.ToLowerInvariant(), uriPath, "", headerValues);
        string stringToSign = string.Join("\n", "sha1", keyTime,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(formatString))).ToLowerInvariant());
        string signKey = Convert.ToHexString(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret))
                .ComputeHash(Encoding.UTF8.GetBytes(keyTime))).ToLowerInvariant();
        string signature = Convert.ToHexString(
            new HMACSHA1(Encoding.UTF8.GetBytes(signKey)).ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        return "q-sign-algorithm=sha1&q-ak=" + setting.AccessKeyId + "&q-sign-time=" + keyTime
            + "&q-key-time=" + keyTime + "&q-header-list=" + headerList
            + "&q-url-param-list=&q-signature=" + signature;
    }

    /// <summary>COS 预签名直链（query 参与签名）。对应 Go: <c>signedCOSObjectURL</c>。</summary>
    private static string SignedCOSObjectUrl(StorageChannelSettings setting, string objectKey, DateTime expiresAt, string disposition)
    {
        if (setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("COS 访问密钥不可用");
        }
        string key = objectKey.TrimStart('/');
        if (key.Length == 0)
        {
            throw new InvalidOperationException("COS 对象路径为空");
        }
        long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long end = ((DateTimeOffset)expiresAt.ToUniversalTime()).ToUnixTimeSeconds();
        if (end <= start)
        {
            throw new InvalidOperationException("COS 签名有效期必须晚于当前时间");
        }
        string keyTime = $"{start};{end}";
        List<KeyValuePair<string, string>> query = [];
        string paramList = "";
        string paramValues = "";
        if (disposition.Length > 0)
        {
            query.Add(new("response-content-disposition", disposition));
            paramList = "response-content-disposition";
            paramValues = "response-content-disposition=" + Uri.EscapeDataString(disposition);
        }
        string formatString = string.Join("\n", "get", "/" + key, paramValues, "");
        string stringToSign = string.Join("\n", "sha1", keyTime,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(formatString))).ToLowerInvariant());
        string signKey = Convert.ToHexString(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret))
                .ComputeHash(Encoding.UTF8.GetBytes(keyTime))).ToLowerInvariant();
        string signature = Convert.ToHexString(
            new HMACSHA1(Encoding.UTF8.GetBytes(signKey)).ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        query.Add(new("q-sign-algorithm", "sha1"));
        query.Add(new("q-ak", setting.AccessKeyId));
        query.Add(new("q-sign-time", keyTime));
        query.Add(new("q-key-time", keyTime));
        query.Add(new("q-header-list", ""));
        query.Add(new("q-url-param-list", paramList));
        query.Add(new("q-signature", signature));
        Uri bucketUri = CosBucketBaseUri(setting);
        string queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new Uri(bucketUri, "/" + key + "?" + queryString).ToString();
    }

    /// <summary>COS 桶域名（无桶前缀端点自动补齐并校验一致性）。对应 Go: <c>CosBucketBaseURL</c>。</summary>
    private static Uri CosBucketBaseUri(StorageChannelSettings setting)
    {
        string endpoint = setting.Endpoint.TrimEnd('/');
        if (endpoint.Length == 0)
        {
            throw new InvalidOperationException("COS Endpoint 为空");
        }
        if (!endpoint.Contains("://", StringComparison.Ordinal))
        {
            endpoint = "https://" + endpoint;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed)
            || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length > 0
            || parsed.Query.Length > 0 || parsed.Fragment.Length > 0
            || parsed.AbsolutePath.Trim('/').Length > 0)
        {
            throw new InvalidOperationException("COS Endpoint 格式不正确");
        }
        if (setting.Bucket.Length == 0)
        {
            throw new InvalidOperationException("COS Bucket 为空");
        }
        string host = parsed.Host.ToLowerInvariant();
        UriBuilder builder = new(parsed);
        if (host.EndsWith(".myqcloud.com", StringComparison.Ordinal)
            || host.EndsWith(".tencentcos.cn", StringComparison.Ordinal))
        {
            if (host.StartsWith("cos.", StringComparison.Ordinal)
                || host.StartsWith("cos-internal.", StringComparison.Ordinal)
                || host.StartsWith("cos-website.", StringComparison.Ordinal))
            {
                builder.Host = setting.Bucket + "." + builder.Host;
            }
            else if (!host.StartsWith(setting.Bucket + ".", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("COS Endpoint 中的 Bucket 与配置不一致");
            }
        }
        return builder.Uri;
    }

    // ---- 七牛云 Kodo（表单上传 + S3 兼容读取 + Kodo 管理删除） ----

    private static async Task<string> PutQiniuObjectAsync(
        StorageChannelSettings setting, string objectKey, string mimeType, long size,
        Stream body, CancellationToken cancellationToken)
    {
        if (setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("七牛云 Kodo 访问密钥不可用");
        }
        string key = objectKey.TrimStart('/');
        if (setting.Bucket.Length == 0 || key.Length == 0)
        {
            throw new InvalidOperationException("七牛云 Kodo Bucket 或对象路径为空");
        }
        string policy = QiniuUrlSafeBase64(JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scope"] = setting.Bucket + ":" + key,
                ["deadline"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
            }, JsonOptions));
        string encodedSign = QiniuUrlSafeBase64(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret))
                .ComputeHash(Encoding.UTF8.GetBytes(policy)));
        string token = setting.AccessKeyId + ":" + encodedSign + ":" + policy;
        string upHost = QiniuUpHost(setting);
        using HttpRequestMessage request = new(HttpMethod.Post, upHost);
        using MultipartFormDataContent form = new();
        form.Add(new StringContent(key), "key");
        form.Add(new StringContent(token), "token");
        StreamContent fileContent = new(new NonDisposingStream(body));
        if (size > 0)
        {
            fileContent.Headers.ContentLength = size;
        }
        if (mimeType.Length > 0)
        {
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        }
        form.Add(fileContent, "file", key);
        request.Content = form;
        using HttpResponseMessage response = await SendAsync(request, cancellationToken, "七牛云 Kodo 上传").ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("hash", out JsonElement hash)
                && hash.ValueKind == JsonValueKind.String)
            {
                return hash.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // 成功响应缺 hash 时按空 ETag 处理（与 Go 的 ret.Hash 零值一致）。
        }
        return "";
    }

    private static string QiniuUpHost(StorageChannelSettings setting)
    {
        string region = QiniuS3Region(setting);
        return region switch
        {
            "cn-north-1" => "https://up-z1.qiniup.com",
            "cn-south-1" => "https://up-z2.qiniup.com",
            "us-north-1" => "https://up-na0.qiniup.com",
            "ap-southeast-1" => "https://up-as0.qiniup.com",
            "cn-east-2" => "https://up-cn-east-2.qiniup.com",
            _ => "https://up.qiniup.com",
        };
    }

    /// <summary>七牛 Kodo 的 S3 兼容读取走 AWS V4 预签名。</summary>
    private static string SignedQiniuS3ObjectUrl(StorageChannelSettings setting, string objectKey, DateTime expiresAt, string disposition)
    {
        string region = QiniuS3Region(setting);
        if (region.Length == 0)
        {
            throw new InvalidOperationException("七牛云 Kodo S3 Region 不可用");
        }
        string key = objectKey.TrimStart('/');
        string host = setting.Bucket + ".s3." + region + ".qiniucs.com";
        string query = disposition.Length > 0
            ? "?response-content-disposition=" + Uri.EscapeDataString(disposition)
            : "";
        // 资源键保持原始路径参与签名，只做一次 RFC 3986 转义。
        string canonicalUri = "/" + key;
        string requestUrl = "https://" + host + canonicalUri + query;
        return AwsV4Presign(setting, requestUrl, region, canonicalUri, query, expiresAt);
    }

    /// <summary>手工 AWS Signature V4 查询预签名（qiniucs 等 S3 兼容源站）。</summary>
    private static string AwsV4Presign(
        StorageChannelSettings setting, string requestUrl, string region,
        string canonicalUri, string query, DateTime expiresAt)
    {
        long expires = Math.Max(1, (long)(expiresAt.ToUniversalTime() - DateTime.UtcNow).TotalSeconds);
        string amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string dateStamp = amzDate[..8];
        string scope = $"{dateStamp}/{region}/s3/aws4_request";
        string canonicalQuery = query.Length == 0
            ? ""
            : query.TrimStart('&');
        List<KeyValuePair<string, string>> parts =
        [
            new("X-Amz-Algorithm", "AWS4-HMAC-SHA256"),
            new("X-Amz-Credential", $"{setting.AccessKeyId}/{scope}"),
            new("X-Amz-Date", amzDate),
            new("X-Amz-Expires", expires.ToString(CultureInfo.InvariantCulture)),
            new("X-Amz-SignedHeaders", "host"),
        ];
        if (query.StartsWith("?response-content-disposition=", StringComparison.Ordinal))
        {
            parts.Add(new("response-content-disposition", Uri.UnescapeDataString(query["?response-content-disposition=".Length..])));
        }
        parts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        string canonicalQueryString = string.Join("&", parts.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        string host = new Uri(requestUrl).Host;
        string canonicalRequest = string.Join("\n", "GET", canonicalUri, canonicalQueryString, host + "\n", "host", "UNSIGNED-PAYLOAD");
        string stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());
        byte[] signingKey = QiniuHmac("AWS4" + setting.AccessKeySecret, dateStamp, region, "s3");
        string signature = Convert.ToHexString(
            new HMACSHA256(signingKey).ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        return requestUrl + (query.Length == 0 ? "?" : query + "&")
            + "X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential="
            + Uri.EscapeDataString($"{setting.AccessKeyId}/{scope}")
            + "&X-Amz-Date=" + amzDate + "&X-Amz-Expires=" + expires.ToString(CultureInfo.InvariantCulture)
            + "&X-Amz-SignedHeaders=host&X-Amz-Signature=" + signature;
    }

    private static byte[] QiniuHmac(string key, string dateStamp, string region, string service)
    {
        byte[] kDate = new HMACSHA256(Encoding.UTF8.GetBytes(key)).ComputeHash(Encoding.UTF8.GetBytes(dateStamp));
        byte[] kRegion = new HMACSHA256(kDate).ComputeHash(Encoding.UTF8.GetBytes(region));
        byte[] kService = new HMACSHA256(kRegion).ComputeHash(Encoding.UTF8.GetBytes(service));
        return new HMACSHA256(kService).ComputeHash("aws4_request"u8.ToArray());
    }

    /// <summary>七牛私有 CDN V2 直链。对应 Go: <c>SignedQiniuObjectURL</c>（MakePrivateURLv2）。</summary>
    private static string SignedQiniuCdnObjectUrl(StorageChannelSettings setting, string objectKey, DateTime expiresAt)
    {
        string key = objectKey.TrimStart('/');
        long deadline = ((DateTimeOffset)expiresAt.ToUniversalTime()).ToUnixTimeSeconds();
        if (setting.CdnBaseUrl.Length == 0)
        {
            return SignedQiniuS3ObjectUrl(setting, objectKey, expiresAt, "");
        }
        string url = setting.CdnBaseUrl.TrimEnd('/') + "/" + key;
        Uri parsed = new(url);
        string pathAndQuery = parsed.PathAndQuery;
        string sign = QiniuUrlSafeBase64(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret))
                .ComputeHash(Encoding.UTF8.GetBytes(pathAndQuery)));
        return url + (parsed.Query.Length > 0 ? "&" : "?") + "sign=" + sign + "&t=" + deadline
            .ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Kodo 管理接口删除对象（rs 域名 + QBox 管理签名）。对应 Go: <c>DeleteQiniuObject</c>。</summary>
    private static async Task DeleteQiniuObjectAsync(StorageChannelSettings setting, string objectKey)
    {
        if (setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("七牛云 Kodo 访问密钥不可用");
        }
        string key = objectKey.TrimStart('/');
        string entry = QiniuUrlSafeBase64(Encoding.UTF8.GetBytes(setting.Bucket + ":" + key));
        string path = "/delete/" + entry;
        string data = path + "\n";
        string sign = QiniuUrlSafeBase64(
            new HMACSHA1(Encoding.UTF8.GetBytes(setting.AccessKeySecret))
                .ComputeHash(Encoding.UTF8.GetBytes(data)));
        using HttpRequestMessage request = new(HttpMethod.Post, "https://rs.qiniup.com" + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"QBox {setting.AccessKeyId}:{sign}");
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromMinutes(2));
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        if ((int)response.StatusCode >= 300 && response.StatusCode != HttpStatusCode.NotFound)
        {
            string detail = Truncate(await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false));
            throw new InvalidOperationException($"删除七牛云 Kodo 对象失败：{(int)response.StatusCode} {detail}".TrimEnd());
        }
    }

    private static string QiniuUrlSafeBase64(byte[] data) => Convert.ToBase64String(data)
        .Replace('+', '-').Replace('/', '_');

    private static string QiniuUrlSafeBase64(string value) => QiniuUrlSafeBase64(Encoding.UTF8.GetBytes(value));

    private static string QiniuS3Region(StorageChannelSettings setting)
    {
        string region = setting.Region.Trim().ToLowerInvariant();
        if (region.Length == 0)
        {
            string endpoint = setting.Endpoint.ToLowerInvariant();
            foreach (string candidate in new[]
                     {
                         "z0", "z1", "z2", "na0", "as0", "cn-east-1", "cn-north-1", "cn-south-1",
                         "us-north-1", "ap-southeast-1", "cn-east-2",
                     })
            {
                if (endpoint.Contains(candidate, StringComparison.Ordinal))
                {
                    region = candidate;
                    break;
                }
            }
        }
        return region switch
        {
            "" or "z0" or "cn-east-1" => "cn-east-1",
            "z1" or "cn-north-1" => "cn-north-1",
            "z2" or "cn-south-1" => "cn-south-1",
            "na0" or "us-north-1" => "us-north-1",
            "as0" or "ap-southeast-1" => "ap-southeast-1",
            "cn-east-2" or "zhejiang2" => "cn-east-2",
            _ => "",
        };
    }

    // ---- 通用 S3（AWSSDK.S3，与连接测试共用配置语义） ----

    private static AmazonS3Client CreateS3Client(StorageChannelSettings setting)
    {
        if (setting.Region.Length == 0 || setting.Bucket.Length == 0
            || setting.AccessKeyId.Length == 0 || setting.AccessKeySecret.Length == 0)
        {
            throw new InvalidOperationException("S3 Region、Bucket 或访问密钥不完整");
        }
        AWSCredentials credentials = string.IsNullOrEmpty(setting.SessionToken)
            ? new BasicAWSCredentials(setting.AccessKeyId, setting.AccessKeySecret)
            : new SessionAWSCredentials(setting.AccessKeyId, setting.AccessKeySecret, setting.SessionToken);
        AmazonS3Config config = new()
        {
            ServiceURL = setting.Endpoint,
            ForcePathStyle = setting.PathStyle || !StandardAwsS3Endpoint(setting.Endpoint),
            UseHttp = setting.Endpoint.StartsWith("http://", StringComparison.Ordinal),
            AuthenticationRegion = setting.Region.Length == 0 ? "us-east-1" : setting.Region,
        };
        return new AmazonS3Client(credentials, config);
    }

    private static async Task<string> PutS3ObjectAsync(
        StorageChannelSettings setting, string objectKey, string mimeType, long size,
        Stream body, CancellationToken cancellationToken)
    {
        using AmazonS3Client client = CreateS3Client(setting);
        PutObjectRequest request = new()
        {
            BucketName = setting.Bucket,
            Key = objectKey.TrimStart('/'),
            InputStream = new NonDisposingStream(body),
            ContentType = string.IsNullOrEmpty(mimeType) ? null : mimeType,
        };
        if (size > 0)
        {
            request.Headers.ContentLength = size;
        }
        try
        {
            PutObjectResponse response = await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ETag.Trim('"');
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"S3 上传失败：{error.Message}", error);
        }
    }

    /// <summary>通用 S3 对象删除（404 视为已删除）。对应 Go: <c>DeleteS3Object</c>。</summary>
    private static async Task DeleteS3ObjectAsync(StorageChannelSettings setting, string objectKey)
    {
        using AmazonS3Client client = CreateS3Client(setting);
        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = setting.Bucket, Key = objectKey.TrimStart('/'),
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // 已删除按成功处理（与 Go 一致）。
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"删除 S3 对象失败：{error.Message}", error);
        }
    }

    private static bool StandardAwsS3Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }
        string host = parsed.Host.ToLowerInvariant();
        return host is "s3.amazonaws.com"
            || (host.StartsWith("s3.", StringComparison.Ordinal)
                && (host.EndsWith(".amazonaws.com", StringComparison.Ordinal)
                    || host.EndsWith(".amazonaws.com.cn", StringComparison.Ordinal)))
            || (host.StartsWith("s3-", StringComparison.Ordinal)
                && host.EndsWith(".amazonaws.com", StringComparison.Ordinal));
    }

    // ---- 读取与投递 ----

    /// <summary>
    /// 从源站（或 CDN 兼容入口）按 Range 读取对象。对应 Go: <c>GetOSSObjectRange</c>。
    /// 兼容历史调用：配置了 CDN 且鉴权显式启用（或历史空鉴权）时优先走 CDN。
    /// </summary>
    public static Task<StorageObjectStream> GetObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader,
        CancellationToken cancellationToken)
    {
        setting = Normalize(setting);
        if (setting.CdnBaseUrl.Length > 0
            && (setting.Delivery.CdnAuthMode.Length == 0 || CdnEnabled(setting)))
        {
            return GetCdnObjectRangeAsync(setting, objectKey, rangeHeader);
        }
        return GetOriginObjectRangeAsync(setting, objectKey, rangeHeader, cancellationToken);
    }

    /// <summary>源站按 Range 读取。对应 Go: <c>GetOriginObjectRange</c>。</summary>
    public static async Task<StorageObjectStream> GetOriginObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader,
        CancellationToken cancellationToken)
    {
        setting = Normalize(setting);
        switch (setting.Provider)
        {
            case TencentProvider:
                return await GetCOSObjectRangeAsync(setting, objectKey, rangeHeader).ConfigureAwait(false);
            case QiniuProvider:
                return await GetQiniuObjectRangeAsync(setting, objectKey, rangeHeader).ConfigureAwait(false);
            case S3Provider:
                return await GetS3ObjectRangeAsync(setting, objectKey, rangeHeader).ConfigureAwait(false);
            default:
                return await GetAliyunObjectRangeAsync(setting, objectKey, rangeHeader).ConfigureAwait(false);
        }
    }

    private static async Task<StorageObjectStream> GetAliyunObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader)
    {
        using HttpRequestMessage request = AliyunRequest(HttpMethod.Get, setting, objectKey, "", null);
        if (rangeHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }
        HttpResponseMessage response = await SendAsync(request, CancellationToken.None, "OSS 读取").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var unsatisfiable = new StorageObjectStream
            {
                Body = Stream.Null, StatusCode = (int)response.StatusCode,
                ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            };
            response.Dispose();
            return unsatisfiable;
        }
        return new StorageObjectStream
        {
            Body = response.Content.ReadAsStream(CancellationToken.None),
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength ?? 0,
            ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            AcceptRanges = "bytes",
            Owner = response,
        };
    }

    private static async Task<StorageObjectStream> GetQiniuObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader)
    {
        string signedUrl = SignedQiniuS3ObjectUrl(
            setting, objectKey, DateTime.UtcNow.Add(ObjectAccessTtl), "");
        using HttpRequestMessage request = new(HttpMethod.Get, signedUrl);
        if (rangeHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }
        HttpResponseMessage response = await SendAsync(request, CancellationToken.None, "七牛云 Kodo 读取").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var unsatisfiable = new StorageObjectStream
            {
                Body = Stream.Null, StatusCode = (int)response.StatusCode,
                ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            };
            response.Dispose();
            return unsatisfiable;
        }
        return new StorageObjectStream
        {
            Body = response.Content.ReadAsStream(CancellationToken.None),
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength ?? 0,
            ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            AcceptRanges = "bytes",
            Owner = response,
        };
    }

    private static async Task<StorageObjectStream> GetS3ObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader)
    {
        using AmazonS3Client client = CreateS3Client(setting);
        GetObjectRequest request = new()
        {
            BucketName = setting.Bucket, Key = objectKey.TrimStart('/'),
        };
        (long start, long? end)? byteRange = ParseByteRange(rangeHeader);
        if (byteRange is not null)
        {
            request.ByteRange = new ByteRange(byteRange.Value.start, byteRange.Value.end ?? long.MaxValue - 1);
        }
        try
        {
            GetObjectResponse response = await client
                .GetObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
            return new StorageObjectStream
            {
                Body = response.ResponseStream,
                StatusCode = rangeHeader.Length > 0 && !string.IsNullOrEmpty(response.ContentRange)
                    ? (int)HttpStatusCode.PartialContent
                    : (int)HttpStatusCode.OK,
                ContentLength = response.ContentLength,
                ContentRange = response.ContentRange ?? "",
                AcceptRanges = string.IsNullOrEmpty(response.AcceptRanges) ? "bytes" : response.AcceptRanges,
                Owner = response,
            };
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new StorageObjectStream
            {
                Body = Stream.Null, StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable,
            };
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"S3 读取失败：{error.Message}", error);
        }
    }

    /// <summary>单段 bytes 范围解析；非法/多段范围按无 Range 处理。对应 Go: <c>normalizeSingleByteRange</c>。</summary>
    public static string NormalizeSingleByteRange(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Length > 128 || !value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(','))
        {
            return "";
        }
        string spec = value["bytes=".Length..];
        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return "";
        }
        string start = spec[..dash];
        string end = spec[(dash + 1)..];
        if ((start.Length == 0 && end.Length == 0) || !DecimalDigits(start) || !DecimalDigits(end))
        {
            return "";
        }
        return "bytes=" + start + "-" + end;
    }

    private static (long Start, long? End)? ParseByteRange(string rangeHeader)
    {
        string spec = NormalizeSingleByteRange(rangeHeader);
        if (spec.Length == 0)
        {
            return null;
        }
        string body = spec["bytes=".Length..];
        int dash = body.IndexOf('-', StringComparison.Ordinal);
        string start = body[..dash];
        string end = body[(dash + 1)..];
        if (start.Length == 0)
        {
            return null;
        }
        long startValue = long.Parse(start, CultureInfo.InvariantCulture);
        return end.Length == 0
            ? (startValue, null)
            : (startValue, long.Parse(end, CultureInfo.InvariantCulture));
    }

    private static bool DecimalDigits(string value)
    {
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<StorageObjectStream> GetCdnObjectRangeAsync(
        StorageChannelSettings setting, string objectKey, string rangeHeader)
    {
        string url = setting.Delivery.CdnAuthMode.Length == 0
            ? OssCdnObjectUrl(setting.CdnBaseUrl, objectKey)
            : SignCdnUrl(setting, objectKey, DateTime.UtcNow.Add(ObjectAccessTtl));
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (rangeHeader.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }
        HttpResponseMessage response = await SendAsync(request, CancellationToken.None, "CDN 读取").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var unsatisfiable = new StorageObjectStream
            {
                Body = Stream.Null, StatusCode = (int)response.StatusCode,
                ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            };
            response.Dispose();
            return unsatisfiable;
        }
        return new StorageObjectStream
        {
            Body = response.Content.ReadAsStream(CancellationToken.None),
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content.Headers.ContentLength ?? 0,
            ContentRange = response.Content.Headers.ContentRange?.ToString() ?? "",
            AcceptRanges = "bytes",
            Owner = response,
        };
    }

    // ---- 投递策略原语 ----

    /// <summary>CDN 公网/七牛鉴权访问是否就绪。对应 Go: <c>CDNEnabled</c>。</summary>
    public static bool CdnEnabled(StorageChannelSettings setting) =>
        setting.CdnBaseUrl.Length > 0
        && setting.Delivery.CdnAuthMode is "public" or "qiniu";

    /// <summary>源站是否可由浏览器直连。对应 Go: <c>PublicOrigin</c>；
    /// 实际出站由 OutboundHttpClient 的 SSRF 守卫在连接层兜底。</summary>
    public static bool PublicOrigin(StorageChannelSettings setting)
    {
        if (setting.Provider == QiniuProvider)
        {
            // Kodo 的 S3 源站由 SDK 签名，浏览器不需要直连上传端点。
            return true;
        }
        if (!Uri.TryCreate(setting.Endpoint.Trim(), UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }
        return !OutboundGuard.AllowedPrivateUpstreamHost(parsed.Host);
    }

    /// <summary>按分发策略签名访问 URL。对应 Go: <c>SignCDNURL</c>。</summary>
    public static string SignCdnUrl(StorageChannelSettings setting, string objectKey, DateTime expiresAt)
    {
        switch (setting.Delivery.CdnAuthMode)
        {
            case "public":
                return OssCdnObjectUrl(setting.CdnBaseUrl, objectKey);
            case "qiniu" when setting.Provider == QiniuProvider:
                return SignedQiniuCdnObjectUrl(setting, objectKey, expiresAt);
            default:
                throw new InvalidOperationException("CDN 用户访问鉴权方式未配置或不支持");
        }
    }

    /// <summary>源站直链（按 provider 分派）。对应 Go: <c>SignedOriginObjectURL</c>。</summary>
    public static async Task<string> SignedOriginObjectUrlAsync(
        StorageChannelSettings setting, string objectKey, DateTime expiresAt)
    {
        setting = Normalize(setting);
        return setting.Provider switch
        {
            TencentProvider => SignedCOSObjectUrl(setting, objectKey, expiresAt, ""),
            QiniuProvider => SignedQiniuS3ObjectUrl(setting, objectKey, expiresAt, ""),
            S3Provider => await SignedS3ObjectUrlAsync(setting, objectKey, expiresAt, "").ConfigureAwait(false),
            _ => SignedAliyunObjectUrl(setting, objectKey, expiresAt, ""),
        };
    }

    /// <summary>强制下载直链（带 Content-Disposition）。对应 Go: <c>SignedOriginObjectDownloadURL</c>。</summary>
    public static Task<string> SignedOriginObjectDownloadUrlAsync(
        StorageChannelSettings setting, string objectKey, DateTime expiresAt, string fileName)
    {
        string disposition = ObjectDownloadContentDisposition(fileName, objectKey);
        setting = Normalize(setting);
        return setting.Provider switch
        {
            TencentProvider => Task.FromResult(SignedCOSObjectUrl(setting, objectKey, expiresAt, disposition)),
            QiniuProvider => Task.FromResult(SignedQiniuS3ObjectUrl(setting, objectKey, expiresAt, disposition)),
            S3Provider => SignedS3ObjectUrlAsync(setting, objectKey, expiresAt, disposition),
            _ => Task.FromResult(SignedAliyunObjectUrl(setting, objectKey, expiresAt, disposition)),
        };
    }

    /// <summary>通用 S3 预签名（AWSSDK GetPreSignedURL，路径/虚拟桶式由配置决定）。</summary>
    private static async Task<string> SignedS3ObjectUrlAsync(
        StorageChannelSettings setting, string objectKey, DateTime expiresAt, string disposition)
    {
        TimeSpan duration = expiresAt.ToUniversalTime() - DateTime.UtcNow;
        if (duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("S3 签名有效期必须晚于当前时间");
        }
        using AmazonS3Client client = CreateS3Client(setting);
        GetPreSignedUrlRequest request = new()
        {
            BucketName = setting.Bucket,
            Key = objectKey.TrimStart('/'),
            Expires = expiresAt.ToUniversalTime(),
            Verb = HttpVerb.GET,
        };
        if (disposition.Length > 0)
        {
            request.ResponseHeaderOverrides.ContentDisposition = disposition;
        }
        try
        {
            return await client.GetPreSignedURLAsync(request).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"S3 下载地址签名失败：{error.Message}", error);
        }
    }

    /// <summary>删除远程对象（按 provider 分派，404 容忍）。对应 Go: <c>DeleteAliyunOSSObject</c> 等。</summary>
    public static async Task DeleteObjectAsync(StorageChannelSettings setting, string objectKey)
    {
        setting = Normalize(setting);
        switch (setting.Provider)
        {
            case TencentProvider:
                await DeleteQiniuStyleOrCosAsync(setting, objectKey).ConfigureAwait(false);
                break;
            case QiniuProvider:
                await DeleteQiniuObjectAsync(setting, objectKey).ConfigureAwait(false);
                break;
            case S3Provider:
                await DeleteS3ObjectAsync(setting, objectKey).ConfigureAwait(false);
                break;
            default:
                await DeleteAliyunObjectAsync(setting, objectKey).ConfigureAwait(false);
                break;
        }
    }

    private static async Task DeleteAliyunObjectAsync(StorageChannelSettings setting, string objectKey)
    {
        using HttpRequestMessage request = AliyunRequest(HttpMethod.Delete, setting, objectKey, "", null);
        using HttpResponseMessage response = await SendAsync(request, CancellationToken.None, "删除阿里云 OSS 对象").ConfigureAwait(false);
    }

    private static async Task DeleteQiniuStyleOrCosAsync(StorageChannelSettings setting, string objectKey)
    {
        // COS 删除：DELETE + 管理授权头（404 容忍）。
        Uri bucketUri = CosBucketBaseUri(setting);
        string key = objectKey.TrimStart('/');
        using HttpRequestMessage request = new(HttpMethod.Delete, new Uri(bucketUri, "/" + key));
        request.Headers.TryAddWithoutValidation(
            "Authorization", CosAuthorization(setting, "delete", "/" + key, request, ""));
        using HttpClient client = OutboundHttpClient.Create(TimeSpan.FromMinutes(2));
        using HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        if ((int)response.StatusCode >= 300 && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"删除腾讯云 COS 对象失败：{(int)response.StatusCode}");
        }
    }

    /// <summary>公开 CDN 直链（无签名）。对应 Go: <c>OssCDNObjectURL</c>。</summary>
    public static string OssCdnObjectUrl(string raw, string objectKey)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            throw new InvalidOperationException("对象存储 CDN 加速域名格式不正确");
        }
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException("对象存储 CDN 加速域名只支持 http/https");
        }
        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0
            || parsed.AbsolutePath.Trim('/').Length > 0)
        {
            throw new InvalidOperationException("对象存储 CDN 加速域名不能包含认证信息、路径、查询参数或片段");
        }
        string key = objectKey.TrimStart('/');
        if (key.Length == 0)
        {
            throw new InvalidOperationException("对象存储对象路径为空");
        }
        UriBuilder builder = new(parsed) { Path = "/" + key };
        return builder.Uri.ToString();
    }

    /// <summary>分发配置摘要（凭据变化即失效）。对应 Go: <c>DeliveryRevision</c>。</summary>
    public static string DeliveryRevision(StorageChannelSettings setting)
    {
        string json = JsonSerializer.Serialize(setting, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }

    /// <summary>浏览器强制下载的 Content-Disposition。对应 Go: <c>objectDownloadContentDisposition</c>。</summary>
    public static string ObjectDownloadContentDisposition(string fileName, string objectKey)
    {
        string name = (fileName ?? "").Trim().Replace('\\', '_').Replace('/', '_');
        name = new string(name.Where(c => c >= 0x20 && c != 0x7f).ToArray());
        if (name.Length == 0 || name == ".")
        {
            int slash = objectKey.TrimEnd('/').LastIndexOf('/');
            name = slash >= 0 ? objectKey[(slash + 1)..] : objectKey;
        }
        if (name.Length == 0 || name == ".")
        {
            name = "download";
        }
        string contentDisposition = "attachment";
        string encoded = Uri.EscapeDataString(name);
        return contentDisposition + "; filename*=UTF-8''" + encoded;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 请求体必须用非关闭包装：源站提前拒绝（如签名 403）时 HTTP 传输会关闭未发完的
    /// Request.Body，若直接包装调用方持有的文件/内存流，“降级本地存储”的重读将因
    /// Cannot access a closed Stream 失败。Dispose 是空操作，底层流保持可用。
    /// 对应 Go: <c>NewOSSRequest</c> 的 io.NopCloser 包装及其注释。
    /// </summary>
    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Close()
        {
            // 空操作：不关闭底层流。
        }
    }
}
