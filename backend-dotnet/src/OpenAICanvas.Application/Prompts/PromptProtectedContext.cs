#nullable enable

namespace OpenAICanvas.Application.Prompts;

/// <summary>
/// 「受保护上下文」：不经过运营模板与用户定制的固定补充段。
/// 对应 Go: <c>internal/prompts/prompt_template_defaults.go</c> 的
/// <c>protectedPromptContext</c> / <c>promptOutputContract</c> / <c>StoryboardExecutionContract</c>。
/// </summary>
/// <remarks>
/// <para>
/// 模板正文（以及用户的 append/rewrite 定制）都可能被改写，但<b>剧情、用户本次要求、画布资产、
/// 角色版本、以及服务端固定的 JSON Schema 契约</b>必须原样送达模型——否则结果校验必然失败，
/// 而失败原因是运营/用户根本无法从模板里看出来的。
/// </para>
/// <para>
/// 所以 <see cref="PromptTemplateService.CompilePromptAsync"/> 一律把本类产出拼在模板渲染结果
/// <b>之后</b>（<c>"\n\n"</c> 分隔），并且<b>不受定制模式影响</b>（rewrite 只换创意部分）。
/// </para>
/// </remarks>
public static class PromptProtectedContext
{
    /// <summary>
    /// 按操作拼装受保护上下文。对应 Go: <c>protectedPromptContext</c>。
    /// </summary>
    /// <remarks>
    /// 未声明受保护上下文的操作（分镜首帧、分镜视频）返回空串，
    /// 调用方据此跳过拼接，不会留下多余的空行。
    /// </remarks>
    public static string For(string operation, IReadOnlyDictionary<string, string> values) =>
        operation switch
        {
            "storyboard_plan" => StoryboardContext(values),
            "storyboard_repair" => StoryboardRepairContext(values),
            "character_extract" => CharacterExtractContext(values),
            "chapter_assets_extract" => ChapterAssetsContext(values),
            "character_turnaround" => string.Join("\n\n",
                "【角色名称】\n" + Value(values, "角色名称"),
                "【项目画风】\n" + Value(values, "项目画风"),
                "【角色设定】\n" + Value(values, "角色设定")),
            "short_drama_outline" => string.Join("\n\n",
                "【用户故事】\n" + Value(values, "用户故事"),
                "【受保护输出契约】\n" + OutputContract(operation)),
            "skill_draft" => string.Join("\n\n",
                "【用户想法】\n" + Value(values, "用户想法"),
                "【受保护输出契约】\n" + OutputContract(operation)),
            _ => "",
        };

    /// <summary>
    /// 服务端固定的输出契约说明。对应 Go: <c>promptOutputContract</c>。
    /// </summary>
    /// <remarks>
    /// 正文与 <see cref="PromptDefaults"/> 的 <c>OutputContract</c> 同源（后者由 Go 导出），
    /// 因此这里直接查表，避免两份 JSON Schema 各自漂移。
    /// </remarks>
    public static string OutputContract(string operation)
    {
        foreach (PromptOperationDefinition definition in PromptDefaults.Definitions())
        {
            if (string.Equals(definition.Operation, operation, StringComparison.Ordinal))
            {
                return definition.OutputContract;
            }
        }
        // 与 Go 的 default 分支一致：非 JSON 操作说明「没有 Schema」。
        return "当前操作输出普通文本提示词，没有 JSON Schema。";
    }

    /// <summary>
    /// 分镜的固定执行契约。对应 Go: <c>StoryboardExecutionContract</c>（<c>storyboard_default.go</c> 同族）。
    /// </summary>
    /// <remarks>
    /// 逐条约束镜头复杂度与台词长度；这是分镜校验能通过的前提，绝不能被模板或用户定制覆盖。
    /// </remarks>
    public static string StoryboardExecutionContract(string durationRule, string countRule) =>
        string.Join('\n',
        [
            "【受保护执行契约】",
            "- " + durationRule,
            "- " + countRule,
            "- 单镜头最多 2 名主要角色、1 个主运镜、1 条主要动作链、3 个 timeBeats 和 3 个 mustHave；超限必须拆镜或在固定镜头数内重新分配。",
            "- dialogue 只写本镜头实际念出的台词或简短旁白，字数上限按 1 秒最多约 5 个中文字符计算（至少 24 字）；超长台词/旁白必须拆镜或精简，不得用 dialogue 承载长段叙述。",
            "- characterIds 优先填写当前角色版本中的 assetId；角色只有名称、尚未确认资产时填写角色名称，服务端会保留名称引用。不要编造 ID；没有角色时返回空数组。",
            "- assetRefs 只能引用当前画布资产中的 nodeId；不要根据相似名称编造 ID。每镜最多 6 个，priority 越大表示越重要。",
            "- styleGuide 最多 120 个中文字符；visualPrompt 只描述首帧，videoPrompt 只描述运动和结尾状态。",
            "- 画幅比例由视频节点参数控制，提示词不得写入具体比例，也不要讨论画幅配置。",
            "- 只返回完整 JSON，不要 Markdown 或解释。",
            "- " + OutputContract("storyboard_plan"),
        ]);

    private static string StoryboardContext(IReadOnlyDictionary<string, string> values) =>
        string.Join("\n\n",
            "【剧情】\n" + Value(values, "剧情"),
            "【用户本次要求】\n" + Value(values, "用户要求"),
            "【当前画布资产】\n" + Value(values, "画布资产"),
            "【当前项目画风】\n" + Value(values, "项目画风"),
            "【当前角色版本】\n" + Value(values, "角色版本"),
            StoryboardExecutionContract(Value(values, "单镜头时长规则"), Value(values, "镜头数量规则")));

    private static string StoryboardRepairContext(IReadOnlyDictionary<string, string> values) =>
        string.Join("\n\n",
            "【剧情】\n" + Value(values, "剧情"),
            "【用户本次要求】\n" + Value(values, "用户要求"),
            "【当前画布资产】\n" + Value(values, "画布资产"),
            "【原始校验错误】\n" + Value(values, "校验错误"),
            "【当前项目画风】\n" + Value(values, "项目画风"),
            "【当前角色版本】\n" + Value(values, "角色版本"),
            StoryboardExecutionContract(Value(values, "单镜头时长规则"), Value(values, "镜头数量规则")),
            "【需要修复的原始输出】\n" + Value(values, "原始输出"));

    private static string ChapterAssetsContext(IReadOnlyDictionary<string, string> values) =>
        string.Join("\n\n",
            $"【任务】\n从短剧项目《{Value(values, "项目名称")}》的章节“{Value(values, "章节名称")}”提取角色、场景和道具。",
            "【项目画风】\n" + Value(values, "项目画风"),
            "【章节正文】\n" + Value(values, "章节正文"),
            "【受保护输出契约】\n" + OutputContract("chapter_assets_extract"));

    private static string CharacterExtractContext(IReadOnlyDictionary<string, string> values) =>
        string.Join("\n\n",
            $"【任务】\n从短剧项目《{Value(values, "项目名称")}》的章节“{Value(values, "章节名称")}”提取角色。",
            "【项目画风】\n" + Value(values, "项目画风"),
            "【章节正文】\n" + Value(values, "章节正文"),
            "【受保护输出契约】\n" + OutputContract("character_extract")
                + "\n严格 JSON 示例：{\"characters\":[{\"name\":\"角色名\",\"aliases\":[],\"role\":\"剧情定位与人物关系\",\"appearance\":\"稳定外貌\",\"clothing\":\"固定服装\",\"physique\":\"体型体态\",\"personality\":\"表演基线\",\"props\":\"\",\"consistencyPrompt\":\"跨镜头一致性约束\",\"multiViewPrompt\":\"三视图结构重点\",\"voiceLanguage\":\"语言口音\",\"voiceAge\":\"声音年龄感\",\"voiceTimbre\":\"音色语速力度\"}]}");

    /// <summary>取变量值；缺键等价于 Go 的零值（空串）。</summary>
    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : "";
}
