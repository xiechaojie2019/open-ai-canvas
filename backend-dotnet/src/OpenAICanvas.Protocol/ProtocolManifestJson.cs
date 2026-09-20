#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 清单线格式的 JSON 选项。对应 Go 的 <c>json.Marshal</c> / <c>json.Unmarshal</c> 默认行为：
/// <c>omitempty</c> 由 <see cref="GoOmitEmptyAttribute"/> 标注驱动；
/// 声明为 <c>object</c> 的字段统一转换成 Go 风格的 <c>map[string]any</c> 形态。
/// </summary>
internal static class ProtocolManifestJson
{
    /// <summary>写方向：Go <c>omitempty</c> 语义 + 非 ASCII 原样输出。</summary>
    public static readonly JsonSerializerOptions GoWriteOptions = BuildOptions();

    /// <summary>读方向：<c>object</c> 字段转为 JSON 值形态，未知字段忽略（与 Go 一致）。</summary>
    public static readonly JsonSerializerOptions ReadOptions = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new GoValueJsonConverter());
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { GoOmitEmptyTypeInfoResolver.Apply },
        };
        return options;
    }
}

/// <summary>
/// 把声明为 <c>object</c> 的清单字段读成 Go 风格的 JSON 值（数字统一按 <c>double</c> 承载）。
/// 没有它，STJ 会交出 <see cref="JsonElement"/>，与表达式引擎期望的形态不一致。
/// </summary>
internal sealed class GoValueJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonFields.FromElement(JsonElement.ParseValue(ref reader));

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
