#nullable enable
using System.Text.Json;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Prompts;

/// <summary>
/// 分镜规划任务（<c>operation == "storyboard"</c>）的受保护输出契约与结果校验。
/// </summary>
/// <remarks>
/// <para>
/// 画布「分镜脚本」节点只把用户在输入框里写的原始提示词发给后端（<c>inputJson</c> 仅
/// <c>{"mode":"text","metadata":{"nodeId":...}}</c>），既不声明
/// <c>metadata.promptTemplateOperation</c>，也不自带输出契约。模型因此按对话习惯回答
/// 「我无法直接生成视频，但可以给你一个脚本…」这类散文或 Markdown 表格，
/// 前端 <c>storyboardRowsFromTask</c> 解析不到 <c>rows</c>，报出「分镜任务没有返回镜头行」。
/// </para>
/// <para>
/// 章节分镜路径（<c>project-chapter-ai.ts</c>）在自己的提示词里显式拼了同一段契约并因此可用，
/// 这正说明<b>契约必须随请求一起送达</b>。与其依赖每个调用方记得拼，服务端在
/// <c>TaskWorkerService</c> 里按 operation 兜底补上：契约只描述输出结构，不改变创作意图，
/// 已在提示词内出现过时不会重复追加。
/// </para>
/// <para>
/// 结构与前端 <c>storyboardRowsOutputContract</c> / <c>storyboardRowsFromTask</c> 一一对应，
/// 字段名必须逐字对齐，改动这里等于改前端的解析契约。
/// </para>
/// </remarks>
public static class StoryboardOutputContract
{
    /// <summary>分镜规划任务的 operation 值（前端 <c>use-canvas-storyboard.ts</c> / <c>project-chapter-ai.ts</c>）。</summary>
    public const string PlanTaskOperation = "storyboard";

    /// <summary>契约段的固定首行，同时作为「提示词里已有契约」的检测标记。</summary>
    public const string ContractHeading = "【受保护输出契约】";

    /// <summary>未从任务输入取到额外要求时的兜底要求。与章节分镜路径的文案保持一致。</summary>
    public const string DefaultRequirement = "每个镜头必须能独立用于生成首帧图片和镜头视频。";

    /// <summary>提示词是否已经包含分镜契约（章节分镜/列表模式自带，避免重复追加）。</summary>
    public static bool HasContract(string prompt) =>
        !string.IsNullOrEmpty(prompt)
        && prompt.Contains(ContractHeading, StringComparison.Ordinal);

    /// <summary>
    /// 分镜镜头行的受保护输出契约正文。
    /// </summary>
    /// <param name="requirement">本次拆镜的额外要求；为空时用 <see cref="DefaultRequirement"/>。</param>
    public static string Rows(string? requirement)
    {
        string extra = string.IsNullOrWhiteSpace(requirement) ? DefaultRequirement : requirement!.Trim();
        return string.Join('\n',
        [
            ContractHeading,
            "- 只返回一个 JSON 对象，不要 Markdown、表格、代码块、注释或任何解释文字。",
            "- 顶层固定为 {\"title\": string, \"rows\": object[]}，rows 至少 1 项。" + extra,
            "- rows 每项对应一个镜头，按剧情顺序排列；字段缺失时填空字符串或空数组，不要省略字段：",
            "  shotNumber（整数，从 1 递增）、durationSeconds（整数，1-60）、plotDescription（本镜可见的剧情与关键动作）、",
            "  dialogue（本镜实际念出的台词或简短旁白，超长必须拆镜）、characters（本镜出现的角色名数组）、",
            "  narrativeIntent、viewerPOV、performanceBlocking、shotSize（景别）、emotion、lightingAndAtmosphere、",
            "  audioEffects、camera、motion、timeBeats、imageGenerationPrompt（首帧画面提示词）、",
            "  videoMotionPrompt（运镜与结尾状态）、continuityOut、negativePrompt、",
            "  mustHave（数组，最多 3 项）、optionalDetails（数组）。",
            "- 镜头数量以完整覆盖剧情因果、人物关系和关键台词为准，不要为压缩数量删减关键情节。",
        ]);
    }

    /// <summary>把契约追加到提示词末尾；已有契约时原样返回。</summary>
    public static string Append(string prompt, string? requirement)
    {
        string trimmed = (prompt ?? "").Trim();
        if (trimmed.Length == 0 || HasContract(trimmed))
        {
            return prompt ?? "";
        }
        return trimmed + "\n\n" + Rows(requirement);
    }

    /// <summary>
    /// 校验分镜结果是否包含可用的镜头行。
    /// </summary>
    /// <remarks>
    /// 失败必须显式抛 <see cref="AppError"/>：任务级文案走
    /// <c>ProviderErrorMessages.UserFacing</c>，只有 <c>AppError.Message</c> 会原样透出；
    /// 兜底分支会把它显示成「连接模型服务失败」，把排障方向带偏。
    /// 同时这也让失败发生在<b>任务</b>上，而不是让前端拿一段散文当分镜结果、
    /// 报出更难定位的「分镜任务没有返回镜头行」。
    /// </remarks>
    public static void ValidateResult(IReadOnlyDictionary<string, object?> result)
    {
        // 与 Go 的带 ok 类型断言一致：非字符串一律当空正文（从而落到契约错误）。
        string text = result.TryGetValue("text", out object? value) ? value as string ?? "" : "";
        if (!PromptJsonExtract.TryExtractPreferredJsonText(text, "rows", out string jsonText))
        {
            throw AppError.BadAuthRequest(
                $"分镜规划返回内容不符合受保护 JSON 契约：{PromptJsonExtract.NotJsonMessage}");
        }
        if (!TryReadRowCount(jsonText, out int rowCount))
        {
            throw AppError.BadAuthRequest("分镜规划结果缺少 rows 数组");
        }
        if (rowCount == 0)
        {
            throw AppError.BadAuthRequest("分镜规划没有规划出任何镜头，请补充剧情或重新拆镜");
        }
    }

    /// <summary>读取 <c>rows</c> 的条目数；缺少该字段或不是数组时返回 false。</summary>
    private static bool TryReadRowCount(string jsonText, out int rowCount)
    {
        rowCount = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!document.RootElement.TryGetProperty("rows", out JsonElement rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            rowCount = rows.GetArrayLength();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
