#nullable enable
using System.Text;

namespace OpenAICanvas.Domain.Kernel;

/// <summary>kernel 包的通用工具。对应 Go: <c>internal/kernel/util.go</c>。</summary>
public static class KernelUtil
{
    /// <summary>
    /// 按 rune 截断并在截断时追加省略号。对应 Go: <c>kernel.TruncateRunes</c>。
    /// </summary>
    /// <remarks>
    /// 注意两点：一是按 rune（而非 UTF-16 码元）计数，避免把代理对切成两半；
    /// 二是<b>只有真正截断时才追加 <c>...</c></b>，未超限时原样返回。
    /// </remarks>
    public static string TruncateRunes(string value, int limit)
    {
        int runeCount = value.EnumerateRunes().Count();
        if (runeCount <= limit)
        {
            return value;
        }

        StringBuilder builder = new();
        int taken = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (taken == limit)
            {
                break;
            }
            builder.Append(rune);
            taken++;
        }
        return builder.Append("...").ToString();
    }
}
