using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAICanvas.Domain.Serialization;

/// <summary>
/// 复刻 Go <c>time.Time</c> 的 JSON 编码：RFC3339Nano。
/// </summary>
/// <remarks>
/// Go 使用布局 <c>2006-01-02T15:04:05.999999999Z07:00</c>，行为要点：
/// <list type="bullet">
/// <item>小数秒最多 9 位（纳秒），且<b>裁剪末尾零</b>；</item>
/// <item>小数部分整体为零时，连小数点一起省略；</item>
/// <item>UTC 偏移为零时输出 <c>Z</c>，否则输出 <c>±HH:MM</c>；</item>
/// <item>零值时间输出 <c>0001-01-01T00:00:00Z</c>。</item>
/// </list>
/// System.Text.Json 的 <c>"O"</c> 格式固定 7 位小数，与 Go 不一致，因此必须自定义。
/// </remarks>
public sealed class GoTimeConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(DateTime) ||
        typeToConvert == typeof(DateTimeOffset) ||
        typeToConvert == typeof(DateTime?) ||
        typeToConvert == typeof(DateTimeOffset?);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(DateTime))
        {
            return new DateTimeConverter();
        }

        if (typeToConvert == typeof(DateTime?))
        {
            return new NullableDateTimeConverter();
        }

        if (typeToConvert == typeof(DateTimeOffset))
        {
            return new DateTimeOffsetConverter();
        }

        return new NullableDateTimeOffsetConverter();
    }

    /// <summary>按 Go RFC3339Nano 输出。</summary>
    internal static string Format(DateTimeOffset value)
    {
        string date = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string time = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        long ticksOfSecond = value.Ticks % TimeSpan.TicksPerSecond;
        string fraction = string.Empty;
        if (ticksOfSecond != 0)
        {
            // 100 纳秒为一个 tick；9 位纳秒 = 100ns * 10，即补到 7 位 tick 再乘 10。
            string digits = (ticksOfSecond * 100).ToString("D9", CultureInfo.InvariantCulture);
            fraction = "." + digits.TrimEnd('0');
        }

        string offset = value.Offset == TimeSpan.Zero
            ? "Z"
            : value.ToString("zzz", CultureInfo.InvariantCulture);

        return $"{date}T{time}{fraction}{offset}";
    }

    /// <summary>按 Go time.Parse(RFC3339) 可接受的输入解析。</summary>
    internal static DateTimeOffset Parse(string text)
    {
        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new JsonException($"无法解析为 RFC3339 时间：{text}");
    }

    private sealed class DateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string raw = reader.GetString() ?? throw new JsonException("时间不能为 null");
            return Parse(raw).UtcDateTime;
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            // DateTime 不携带偏移，按 UTC 解释，与 Go 读取数据库后的 UTC 归一一致。
            DateTimeOffset offsetValue = new(value, TimeSpan.Zero);
            writer.WriteStringValue(Format(offsetValue));
        }
    }

    private sealed class NullableDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            return new DateTimeConverter().Read(ref reader, typeof(DateTime), options);
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            new DateTimeConverter().Write(writer, value.Value, options);
        }
    }

    private sealed class DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string raw = reader.GetString() ?? throw new JsonException("时间不能为 null");
            return Parse(raw);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(Format(value));
        }
    }

    private sealed class NullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            string raw = reader.GetString() ?? throw new JsonException("时间不能为 null");
            return Parse(raw);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(Format(value.Value));
        }
    }
}
