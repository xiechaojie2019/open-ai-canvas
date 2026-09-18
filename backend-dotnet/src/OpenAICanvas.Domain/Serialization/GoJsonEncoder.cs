using System.Text.Encodings.Web;

namespace OpenAICanvas.Domain.Serialization;

/// <summary>
/// 复刻 Go <c>encoding/json</c> 的字符串转义规则，用于保证响应体逐字节一致。
/// </summary>
/// <remarks>
/// 与 System.Text.Json 默认编码器的差异（都会导致响应体字节不同）：
/// <list type="bullet">
/// <item>STJ 默认把 <c>+</c>、<c>'</c>、<c>`</c> 也转义，Go <b>不转义</b>；</item>
/// <item>STJ 默认转义全部非 ASCII 字符，Go <b>原样输出 UTF-8</b>；</item>
/// <item>STJ 的 <c>\u</c> 用大写十六进制，Go 用<b>小写</b>。</item>
/// </list>
/// Go 实际转义集合：<c>"</c> <c>\</c> <c>&lt;</c> <c>&gt;</c> <c>&amp;</c>、所有小于 0x20 的控制字符、
/// 以及 U+2028 / U+2029。其余（含全部非 ASCII 与代理对）原样输出。
/// </remarks>
public sealed unsafe class GoJsonEncoder : JavaScriptEncoder
{
    public static readonly GoJsonEncoder Instance = new();

    private GoJsonEncoder()
    {
    }

    /// <summary>最长转义形式是 <c>\u00xx</c>，共 6 个字符。</summary>
    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) => NeedsEscape(unicodeScalar);

    public override int FindFirstCharacterToEncode(char* text, int textLength)
    {
        if (text is null)
        {
            return -1;
        }

        for (int index = 0; index < textLength; index++)
        {
            if (NeedsEscape(text[index]))
            {
                return index;
            }
        }

        return -1;
    }

    public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
    {
        int index = 0;
        while (index < utf8Text.Length)
        {
            System.Buffers.OperationStatus status =
                System.Text.Rune.DecodeFromUtf8(utf8Text[index..], out System.Text.Rune rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                // 非法 UTF-8 交给基类按 U+FFFD 处理。
                return index;
            }

            if (NeedsEscape(rune.Value))
            {
                return index;
            }

            index += consumed;
        }

        return -1;
    }

    /// <summary>把需要转义的标量写成转义序列。返回 false 表示缓冲区不足。</summary>
    public override bool TryEncodeUnicodeScalar(
        int unicodeScalar,
        char* buffer,
        int bufferLength,
        out int numberOfCharactersWritten)
    {
        numberOfCharactersWritten = 0;

        string? literal = unicodeScalar switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\b' => "\\b",
            '\f' => "\\f",
            _ => null,
        };

        if (literal is not null)
        {
            if (bufferLength < literal.Length)
            {
                return false;
            }

            for (int index = 0; index < literal.Length; index++)
            {
                buffer[index] = literal[index];
            }

            numberOfCharactersWritten = literal.Length;
            return true;
        }

        if (!NeedsUnicodeEscape(unicodeScalar))
        {
            // 不应到达：WillEncode 与 NeedsUnicodeEscape 共用同一判定集合。
            return false;
        }

        const int escapeLength = 6;
        if (bufferLength < escapeLength)
        {
            return false;
        }

        buffer[0] = '\\';
        buffer[1] = 'u';
        buffer[2] = HexDigit((unicodeScalar >> 12) & 0xF);
        buffer[3] = HexDigit((unicodeScalar >> 8) & 0xF);
        buffer[4] = HexDigit((unicodeScalar >> 4) & 0xF);
        buffer[5] = HexDigit(unicodeScalar & 0xF);
        numberOfCharactersWritten = escapeLength;
        return true;
    }

    /// <summary>Go 使用小写十六进制。</summary>
    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));

    /// <summary>
    /// 需要写成 <c>\uXXXX</c> 的标量。
    /// 注意：<c>&lt;</c> <c>&gt;</c> <c>&amp;</c> 在 Go 中也走十六进制转义
    /// （<c>\u003c</c> / <c>\u003e</c> / <c>\u0026</c>），不能按字面量原样输出。
    /// </summary>
    private static bool NeedsUnicodeEscape(int scalar) =>
        scalar < 0x20
        || scalar is 0x2028 or 0x2029
        || scalar is '<' or '>' or '&';

    private static bool NeedsEscape(int scalar) =>
        scalar is '"' or '\\' or '\n' or '\r' or '\t' or '\b' or '\f'
        || NeedsUnicodeEscape(scalar);
}
