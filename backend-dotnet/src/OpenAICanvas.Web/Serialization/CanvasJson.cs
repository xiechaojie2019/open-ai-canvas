using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Web.Serialization;

/// <summary>
/// 全局 JSON 契约。所有出参都必须经由此配置，才能与 Go 版逐字节一致。
/// </summary>
public static class CanvasJson
{
    /// <summary>请求体反序列化用的配置。</summary>
    public static JsonSerializerOptions ReadOptions { get; } = Create();

    /// <summary>响应体序列化用的配置。</summary>
    public static JsonSerializerOptions WriteOptions { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new()
        {
            // Go 的 struct tag 统一为 camelCase。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DictionaryKeyPolicy = null,
            WriteIndented = false,

            // 关键：Go 的 gin.H{"data": nil} 一定输出 data:null，
            // 所以这里绝不能全局忽略 null，否则失败响应会丢掉 data 字段。
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,

            // Go 原样输出非 ASCII；转义集合见 GoJsonEncoder。
            Encoder = GoJsonEncoder.Instance,

            // 未匹配字段不报错，与 Go 的 json.Unmarshal 行为一致。
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            // 注意：不开启 AllowReadingFromString——Go 的 json.Unmarshal 不接受 "123" 绑定到 int。
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };

        options.Converters.Add(new GoTimeConverter());

        // 叠加 Go omitempty 语义；保留默认解析器作为基底。
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { GoOmitEmptyTypeInfoResolver.Apply },
        };

        return options;
    }
}
