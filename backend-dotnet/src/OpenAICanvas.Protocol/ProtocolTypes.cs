#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 声明式协议清单的出站请求相关类型。
/// 对应 Go: <c>internal/protocol/types.go</c>（RequestSpec / RequestFilePart / ManifestAuth 等）。
/// </summary>
/// <remarks>
/// 字段名与 <c>omitempty</c> 必须与 Go 的 json tag 逐字对齐 —— 插件清单是外部输入，
/// 反序列化行为直接决定宿主能否正确理解 manifest。
/// </remarks>
public sealed class RequestSpec
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>是否忽略 BaseURL 的既有路径，直接以本 spec 的 path 作为根路径。</summary>
    [JsonPropertyName("originPath")]
    public bool OriginPath { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("query")]
    public Dictionary<string, List<string>>? Query { get; set; }

    [JsonPropertyName("body")]
    public object? Body { get; set; }

    [JsonPropertyName("files")]
    public List<RequestFilePart>? Files { get; set; }

    [JsonPropertyName("auth")]
    public ManifestAuth Auth { get; set; } = new();
}

/// <summary>对应 Go: <c>RequestFilePart</c>。</summary>
public sealed class RequestFilePart
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MIMEType { get; set; } = "";

    [JsonPropertyName("reference")]
    public MediaReference Reference { get; set; } = new();
}

/// <summary>对应 Go: <c>ManifestAuth</c>。</summary>
public sealed class ManifestAuth
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("secretField")]
    [GoOmitEmpty]
    public string SecretField { get; set; } = "";

    [JsonPropertyName("header")]
    [GoOmitEmpty]
    public string Header { get; set; } = "";

    [JsonPropertyName("prefix")]
    [GoOmitEmpty]
    public string Prefix { get; set; } = "";

    [JsonPropertyName("query")]
    [GoOmitEmpty]
    public string Query { get; set; } = "";

    [JsonPropertyName("username")]
    [GoOmitEmpty]
    public string Username { get; set; } = "";

    [JsonPropertyName("service")]
    [GoOmitEmpty]
    public string Service { get; set; } = "";

    [JsonPropertyName("region")]
    [GoOmitEmpty]
    public string Region { get; set; } = "";
}

/// <summary>状态机取值。对应 Go: <c>Status</c>。</summary>
public static class ProtocolStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// 参考素材引用。对应 Go: <c>MediaReference</c>。
/// 与 <c>Providers.ProviderMedia</c> 形态相同，属于跨层契约。
/// </summary>
public sealed class MediaReference
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("dataUrl")]
    public string DataURL { get; set; } = "";

    /// <summary>媒体种类（<c>image</c> / <c>video</c> / <c>audio</c> / <c>file</c>）。对应 Go: <c>MediaReference.Kind</c>。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("url")]
    public string URL { get; set; } = "";

    [JsonPropertyName("storageKey")]
    public string StorageKey { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MIMEType { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// 素材在请求中的角色（<c>reference_image</c> / <c>edit_source</c> / <c>mask</c> /
    /// <c>start_frame</c> / <c>end_frame</c> / <c>reference_video</c> / <c>reference_audio</c>）。
    /// 对应 Go: <c>MediaReference.Role</c>（由宿主按模式赋值，不由插件声明）。
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>素材在同类中的序号。对应 Go: <c>MediaReference.Order</c>。</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>权重（部分 provider 的参考图优先级）。对应 Go: <c>MediaReference.Weight</c>。</summary>
    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    /// <summary>宿主补充的素材元信息（字节数/宽高/时长/存储键）。对应 Go: <c>MediaReference.Metadata</c>。</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// 临时地址：结果只在下游可访问期间有效，宿主必须立即下载而不是长期保存 URL。
    /// 对应 Go: <c>MediaReference.Ephemeral</c>。
    /// </summary>
    [JsonPropertyName("ephemeral")]
    public bool Ephemeral { get; set; }
}

/// <summary>Provider 凭证（对应 Go: <c>providerConfig</c> 中参与鉴权与签名的字段子集）。</summary>
public sealed record ProviderCredentials(string APIKey, string SecretKey);
