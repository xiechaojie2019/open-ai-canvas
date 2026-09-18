using System.Text;
using System.Text.RegularExpressions;

namespace OpenAICanvas.Domain.Model;

/// <summary>
/// 领域级文本算法。对应 Go <c>internal/model</c> 里的包级函数。
/// </summary>
public static partial class ModelText
{
    /// <summary>素材 ID 的最大长度。对应 Go: <c>model.AssetIDMaxLength</c>。</summary>
    public const int AssetIdMaxLength = 80;

    /// <summary>
    /// 素材候选名称归一化键：去掉首尾空白后，只保留字母与数字并转小写。
    /// </summary>
    /// <remarks>对应 Go: <c>model.AssetCandidateNameKey</c>。用 Rune 迭代以正确处理代理对。</remarks>
    public static string AssetCandidateNameKey(string value)
    {
        StringBuilder builder = new();
        foreach (Rune rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 章节字数：剥离 HTML 标签、反转义实体、去首尾空白后按字符（rune）计数。
    /// </summary>
    /// <remarks>对应 Go: <c>model.ProjectUnitWordCount</c>。</remarks>
    public static int ProjectUnitWordCount(string sourceText)
    {
        string plainText = HtmlTagPattern().Replace(sourceText, string.Empty);
        return System.Net.WebUtility.HtmlDecode(plainText).Trim().EnumerateRunes().Count();
    }

    /// <summary>对应 Go: <c>projectUnitHTMLTagPattern = regexp.MustCompile(`&lt;[^&gt;]+&gt;`)</c>。</summary>
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagPattern();
}
