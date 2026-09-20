#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 逐字节对齐 Go <c>encoding/json</c> 默认输出的 JSON 编码器。
/// 对应 Go: <c>json.Marshal</c>（在 <c>internal/protocol/expression.go</c> 中被用于
/// <c>$eq</c>/<c>$ne</c>/<c>$in</c> 的比较基准与 <c>$json</c> 的输出）。
/// </summary>
/// <remarks>
/// 为什么不用 <c>System.Text.Json</c> 直接序列化：<c>$eq</c> 的语义是「两侧序列化后的字节相等」，
/// 而 STJ 与 Go 有三处默认行为差异会直接翻转比较结果 ——
/// 字典键序（Go 排序、STJ 保持插入序）、HTML 转义（Go 转义 <c>&lt;</c>/<c>&gt;</c>/<c>&amp;</c>、
/// 宽松编码器不转义）、浮点写法（Go 在 <c>|v| &lt; 1e-6</c> 或 <c>|v| ≥ 1e21</c> 时用指数形式）。
/// 这里逐条复刻 Go 的行为，保证表达式结果与 Go 侧一致。
/// </remarks>
public static class ProtocolJsonValue
{
    /// <summary>把值编码为 Go 风格的 JSON 文本。无法编码的类型抛 <see cref="InvalidOperationException"/>。</summary>
    public static string Encode(object? value)
    {
        StringBuilder builder = new();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                return;
            case string text:
                WriteString(builder, text);
                return;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case double number:
                builder.Append(EncodeFloat(number));
                return;
            case float number:
                builder.Append(EncodeFloat(number));
                return;
            case Dictionary<string, object?> map:
            {
                // Go 的 encoding/json 对 map 键按字符串排序后输出。
                List<string> keys = [.. map.Keys];
                keys.Sort(StringComparer.Ordinal);
                builder.Append('{');
                for (int index = 0; index < keys.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }
                    WriteString(builder, keys[index]);
                    builder.Append(':');
                    Write(builder, map[keys[index]]);
                }
                builder.Append('}');
                return;
            }
            case List<object?> items:
            {
                builder.Append('[');
                for (int index = 0; index < items.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }
                    Write(builder, items[index]);
                }
                builder.Append(']');
                return;
            }
            case JsonElement element:
                Write(builder, JsonFields.FromElement(element));
                return;
            default:
                throw new InvalidOperationException(
                    $"unsupported manifest value {value.GetType().Name}");
        }
    }

    /// <summary>Go 的字符串转义规则：控制字符 + <c>"</c>/<c>\</c> + HTML 敏感字符 + U+2028/U+2029。</summary>
    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (Rune rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '<':
                    builder.Append("\\u003c");
                    break;
                case '>':
                    builder.Append("\\u003e");
                    break;
                case '&':
                    builder.Append("\\u0026");
                    break;
                case 0x2028:
                    builder.Append("\\u2028");
                    break;
                case 0x2029:
                    builder.Append("\\u2029");
                    break;
                default:
                    if (rune.Value < 0x20)
                    {
                        builder.Append("\\u")
                            .Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>
    /// 浮点编码：Go 在 <c>|v| &lt; 1e-6</c> 或 <c>|v| ≥ 1e21</c> 时使用指数形式，其余用定点形式；
    /// 两者都是「最短可往返」写法。
    /// </summary>
    private static string EncodeFloat(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            // Go 的 json.Marshal 对 NaN/Inf 返回错误；调用方在比较场景下忽略该错误（得到空串）。
            throw new InvalidOperationException("unsupported manifest number");
        }
        double absolute = Math.Abs(value);
        if (absolute != 0 && (absolute < 1e-6 || absolute >= 1e21))
        {
            return NormalizeExponent(value.ToString("R", CultureInfo.InvariantCulture));
        }
        return ProtocolRequestBuilder.FormatFloatGo(value);
    }

    /// <summary>指数写法归一：小写 <c>e</c>、去掉指数前导零（<c>1E-07</c> → <c>1e-7</c>）。</summary>
    private static string NormalizeExponent(string text)
    {
        string lowered = text.ToLowerInvariant();
        int marker = lowered.IndexOf('e');
        if (marker < 0)
        {
            return lowered;
        }
        string mantissa = lowered[..(marker + 1)];
        string exponent = lowered[(marker + 1)..];
        string sign = "";
        if (exponent.StartsWith('+') || exponent.StartsWith('-'))
        {
            sign = exponent[..1];
            exponent = exponent[1..];
        }
        string digits = exponent.TrimStart('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
        return mantissa + sign + digits;
    }
}
