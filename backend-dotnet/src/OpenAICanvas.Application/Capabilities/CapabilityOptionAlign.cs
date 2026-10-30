#nullable enable
using System.Text.Json;

namespace OpenAICanvas.Application.Capabilities;

/// <summary>
/// 把请求里的质量档位对齐到当前模型声明的合法值。
/// </summary>
/// <remarks>
/// <para>
/// 背景（2026-10-30）：画布、前端、上游模型对 picture quality 有三套写法——
/// 价格档是 <c>1k/2k/4k</c>、上游 OpenAI 兼容协议是 <c>low/medium/high</c>、
/// 部分中转站是 <c>standard/hd</c>。而渠道能力配置里 <c>quality.values</c> 是管理端手填的，
/// 不参与任何归一（<c>ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig</c>）。
/// 于是模型（或旧前端）填了 <c>high</c> 而 values 是 <c>1k/2k/4k</c> 时，
/// 准入判定直接判「参数 质量超出支持范围」，任务根本不提交 ⇒ Agent 每改一次图都要
/// 白搭一轮模型调用重试。
/// </para>
/// <para>
/// 这里做的是「值对齐」而不是「放宽判定」：判定侧另由
/// <c>CapabilitySpecOps.CapabilityOptionValuesEqual</c> 用同一张别名表比较，
/// 两侧共用 <see cref="ModelSku.NormalizeImageQualityAlias"/>，避免出现
/// 「准入认 A、计费算 B」。
/// </para>
/// <para>
/// ⚠️ 三条规则有严格顺序，不能简化成「非法就删」：
/// ① 原值就是声明值 → 不动（最保真）；
/// ② 别名折到同一档 → 写回**声明里实际存在的那个词**（不是归一后的 1k/4k），
///    因为 <c>ProviderImageOptions.NormalizeImageQuality</c> 发上游前只认
///    <c>1k→low / 2k→medium / 4k→high</c>，写回声明词才不会让上游收到陌生值；
/// ③ 折不到才删除。删除后**必须由调用方在补默认值之前执行**，否则默认值已经补过，
///    删掉就成了空 ⇒ 上游收空、计费按 size 猜档，出现漏收。
/// </para>
/// </remarks>
internal static class CapabilityOptionAlign
{
    /// <summary>
    /// 就地校正 <paramref name="options"/> 里的质量档位。仅处理字符串值，
    /// 且只在模型确实声明了该参数（<c>values</c> 非空）时生效。
    /// </summary>
    /// <param name="spec">当前模型/线路的能力规格；null（如音频模型）时原样返回。</param>
    /// <param name="options">待校正的选项字典，就地修改。</param>
    /// <param name="name">参数名，图片为 <c>quality</c>。</param>
    /// <param name="report">可选的观测回调：<c>(请求值, 落定值)</c>，落定值为空串表示已删除。</param>
    public static void AlignQuality(
        CapabilitySpec? spec,
        Dictionary<string, JsonElement>? options,
        string name = "quality",
        Action<string, string>? report = null)
    {
        if (options is null
            || spec?.Options is null
            || !spec.Options.TryGetValue(name, out OptionConstraint? constraint)
            || constraint.Values is not { Count: > 0 })
        {
            return;
        }
        if (!options.TryGetValue(name, out JsonElement raw) || raw.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string requested = (raw.GetString() ?? "").Trim();
        if (requested.Length == 0
            || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "any", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        List<(string Text, string Key)> declared = [];
        foreach (JsonElement value in constraint.Values)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string text = (value.GetString() ?? "").Trim();
            if (text.Length == 0 || text == "*")
            {
                continue;
            }
            declared.Add((text, AliasKey(text)));
        }

        // ① 原值就是声明值。
        foreach ((string text, _) in declared)
        {
            if (string.Equals(text, requested, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(text, requested, StringComparison.Ordinal))
                {
                    options[name] = JsonSerializer.SerializeToElement(text);
                    report?.Invoke(requested, text);
                }
                return;
            }
        }

        // ② 别名折到同一档，写回声明里的原词。
        string wanted = AliasKey(requested);
        if (wanted.Length > 0)
        {
            foreach ((string text, string key) in declared)
            {
                if (key.Length > 0 && string.Equals(key, wanted, StringComparison.Ordinal))
                {
                    options[name] = JsonSerializer.SerializeToElement(text);
                    report?.Invoke(requested, text);
                    return;
                }
            }
        }

        // ③ 折不到：删除，交给默认值补全（调用方必须在补默认值之前调用本方法）。
        options.Remove(name);
        report?.Invoke(requested, "");
    }

    /// <summary>别名归一：<c>low/standard→1k</c>、<c>medium/hd→2k</c>、<c>high→4k</c>。</summary>
    internal static string AliasKey(string raw) => ModelSku.NormalizeImageQualityAlias(raw);
}
