#nullable enable
using OpenAICanvas.Application.Prompts;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.Prompts;

/// <summary>
/// 提示词模板的渲染与结果校验。对应 Go:
/// <c>prompt_template_catalog_test.go</c> + <c>prompt_template_json_test.go</c>。
/// </summary>
/// <remarks>
/// 这两步决定了「模板」是否真的生效：渲染管模型<b>收到什么</b>，校验管模型<b>返回什么算合格</b>。
/// 只做其中一步的话，运营模板与用户定制仍然只是数据库里的死数据。
/// </remarks>
public sealed class PromptTemplateRenderTests
{
    // ------------------------------------------------------------ 渲染

    /// <summary>短剧大纲模板的变量替换。对应 Go: <c>TestRenderShortDramaTemplateSubstitutesVariables</c>。</summary>
    [Fact]
    public void 渲染短剧大纲模板会替换变量()
    {
        PromptOperationDefinition definition =
            Definition("short_drama_outline");

        string rendered = PromptTemplateService.RenderPromptTemplate(definition, definition.DefaultContent,
            new Dictionary<string, string>
            {
                ["章节数量"] = "5",
                ["叙事结构"] = "单线推进",
                ["每章字数"] = "800",
                ["叙事视角"] = "第三人称",
                ["整体基调"] = "平稳叙事",
                ["角色规模"] = "3-4 个",
                ["章节篇幅"] = "中",
            });

        Assert.Contains("5 个章节", rendered, StringComparison.Ordinal);
        Assert.Contains("单线推进", rendered, StringComparison.Ordinal);
        // 声明过的变量必须全部替换掉，不能把 {{...}} 原样发给模型。
        Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
    }

    /// <summary>受保护上下文里必须同时带上用户输入与 JSON 契约标识。</summary>
    [Fact]
    public void 短剧大纲受保护上下文包含故事与契约()
    {
        string protectedContext = PromptProtectedContext.For("short_drama_outline",
            new Dictionary<string, string> { ["用户故事"] = "租客发现房东不是人" });

        Assert.Contains("租客发现房东不是人", protectedContext, StringComparison.Ordinal);
        Assert.Contains("short-drama-outline/v1", protectedContext, StringComparison.Ordinal);
    }

    /// <summary>技能草稿的受保护上下文同理。</summary>
    [Fact]
    public void 技能草稿受保护上下文包含想法与契约()
    {
        string protectedContext = PromptProtectedContext.For("skill_draft",
            new Dictionary<string, string> { ["用户想法"] = "做一个电商主图技能" });

        Assert.Contains("做一个电商主图技能", protectedContext, StringComparison.Ordinal);
        Assert.Contains("skill-draft/v1", protectedContext, StringComparison.Ordinal);
    }

    /// <summary>
    /// 分镜的执行契约必须固定在受保护段里：它是分镜校验能过的前提，
    /// 一旦被运营模板或用户定制顶掉，模型就会产出超限镜头。
    /// </summary>
    [Fact]
    public void 分镜受保护上下文包含执行契约与JSON契约()
    {
        string protectedContext = PromptProtectedContext.For("storyboard_plan",
            new Dictionary<string, string>
            {
                ["剧情"] = "小猫在溪边抓鱼",
                ["用户要求"] = "共 6 秒",
                ["单镜头时长规则"] = "单镜头 6 秒",
                ["镜头数量规则"] = "总镜头数 1",
            });

        Assert.Contains("小猫在溪边抓鱼", protectedContext, StringComparison.Ordinal);
        Assert.Contains("【受保护执行契约】", protectedContext, StringComparison.Ordinal);
        Assert.Contains("dialogue 只写本镜头实际念出的台词", protectedContext, StringComparison.Ordinal);
        Assert.Contains("storyboard-plan/v3", protectedContext, StringComparison.Ordinal);
    }

    /// <summary>未声明受保护上下文的操作返回空串（调用方据此跳过拼接）。</summary>
    [Fact]
    public void 无受保护上下文的操作返回空串()
    {
        Assert.Equal("", PromptProtectedContext.For("storyboard_first_frame", new Dictionary<string, string>()));
        Assert.Equal("", PromptProtectedContext.For("storyboard_video", new Dictionary<string, string>()));
    }

    // ------------------------------------------------------------ JSON 抽取

    /// <summary>散文与 Markdown 代码块都要能跳过。对应 Go: <c>TestExtractJSONTextSkipsProseAndMarkdownFence</c>。</summary>
    [Fact]
    public void 抽取_JSON_跳过散文与代码块()
    {
        const string raw = "先说明一下，{这不是有效 JSON}。\n```json\n{\"characters\":[{\"name\":\"林夏\",\"aliases\":[]}]}\n```\n";

        Assert.True(PromptJsonExtract.TryExtractJsonText(raw, out string jsonText));
        Assert.Equal("{\"characters\":[{\"name\":\"林夏\",\"aliases\":[]}]}", jsonText);
    }

    /// <summary>字符串里的花括号不算结构字符。对应 Go: <c>TestExtractJSONTextHandlesBracesInJSONString</c>。</summary>
    [Fact]
    public void 抽取_JSON_能处理字符串内的花括号()
    {
        const string raw = "```json\n{\"characters\":[{\"name\":\"林夏\",\"role\":\"拿着{小夜灯}的租客\"}]}\n```";

        Assert.True(PromptJsonExtract.TryExtractJsonText(raw, out string jsonText));
        Assert.Equal("{\"characters\":[{\"name\":\"林夏\",\"role\":\"拿着{小夜灯}的租客\"}]}", jsonText);
    }

    /// <summary>正文里既没有对象也没有数组时判失败。</summary>
    [Fact]
    public void 抽取_JSON_无内容时失败()
    {
        Assert.False(PromptJsonExtract.TryExtractJsonText("完全没有任何 JSON", out _));
        Assert.False(PromptJsonExtract.TryExtractJsonText("", out _));
    }

    /// <summary>
    /// 优先抽取策略必须跳过旁枝数组：本节先把角色名列一遍，只取第一个可解析值会命中那个数组。
    /// </summary>
    [Fact]
    public void 优先抽取会跳过旁枝数组()
    {
        const string raw = "本章角色：[\"林夏\",\"陈默\"]\n\n{\"chapters\":[{\"title\":\"入住\",\"content\":\"林夏搬进旧公寓。\"}]}";

        Assert.True(PromptJsonExtract.TryExtractPreferredJsonText(raw, "chapters", out string jsonText));
        Assert.StartsWith("{\"chapters\"", jsonText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 结果校验

    private const string CharacterCard =
        "{\"name\":\"林夏\",\"aliases\":[\"夏夏\"],\"role\":\"女主角\",\"appearance\":\"短发\"," +
        "\"clothing\":\"白衬衫\",\"physique\":\"纤细\",\"personality\":\"冷静\",\"props\":\"小夜灯\"," +
        "\"consistencyPrompt\":\"c\",\"multiViewPrompt\":\"m\",\"voiceLanguage\":\"中文\"," +
        "\"voiceAge\":\"青年\",\"voiceTimbre\":\"清亮\"}";

    /// <summary>
    /// 角色卡顶层形态的宽容度。对应 Go: <c>TestValidatePromptTemplateResultCharacterRootShapes</c>。
    /// 这些形态都违反 character-breakdown/v1 的顶层 object 契约，但内容本身可用，应当被接受；
    /// 真正无内容或缺字段的输入必须报出<b>可读的业务错误</b>，而不是 JSON 类型错误。
    /// </summary>
    [Theory]
    [InlineData("标准对象形态", "{\"characters\":[" + CharacterCard + "]}", "")]
    [InlineData("顶层角色卡数组", "[" + CharacterCard + "]", "")]
    [InlineData("单元素包 characters", "[{\"characters\":[" + CharacterCard + "]}]", "")]
    [InlineData("多元素各包 characters", "[{\"characters\":[" + CharacterCard + "]},{\"characters\":[" + CharacterCard + "]}]", "")]
    [InlineData("数组外再包一层", "[[" + CharacterCard + "]]", "")]
    [InlineData("数组混入非角色对象", "[{\"index\":1}," + CharacterCard + "]", "")]
    [InlineData("markdown 代码块包裹数组", "```json\n[" + CharacterCard + "]\n```", "")]
    [InlineData("正文先列角色名数组", "本章角色：[\"林夏\",\"陈默\"]\n\n{\"characters\":[" + CharacterCard + "]}", "")]
    [InlineData("characters 写成以角色名为键的对象", "{\"characters\":{\"林夏\":" + CharacterCard + "}}", "")]
    [InlineData("空数组", "[]", "没有识别到任何角色")]
    [InlineData("不相关数组", "[\"林夏\",\"陈默\"]", "没有识别到任何角色")]
    [InlineData("角色缺少必填字段", "[{\"name\":\"林夏\"}]", "第 1 个角色缺少字段 aliases")]
    // characters 位于契约位置时不做元素过滤，缺 name 也要报出具体字段而不是被当成空结果。
    [InlineData("契约位置内角色缺少 name", "{\"characters\":[{\"role\":\"女主角\"}]}", "第 1 个角色缺少字段 name")]
    public void 角色卡顶层形态的宽容度(string caseName, string text, string wantError)
    {
        Assert.False(string.IsNullOrEmpty(caseName));
        if (wantError.Length == 0)
        {
            PromptTemplateService.ValidatePromptTemplateResult(
                "character_extract", Text(text));
            return;
        }

        AppError error = Assert.Throws<AppError>(() =>
            PromptTemplateService.ValidatePromptTemplateResult("character_extract", Text(text)));

        Assert.Contains(wantError, error.Message, StringComparison.Ordinal);
        // 形态问题必须收敛成业务语言，不能把 JSON 解析器的内部错误抛给用户。
        Assert.DoesNotContain("JsonException", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON 无法解析", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 形态放宽只作用于角色卡提取。对应 Go: <c>TestValidatePromptTemplateResultScopesCharacterRelaxation</c>。
    /// </summary>
    [Fact]
    public void 形态放宽只作用于角色卡提取()
    {
        // 非角色卡操作只要求「能取出一个合法 JSON 值」，不套用 characters 契约。
        PromptTemplateService.ValidatePromptTemplateResult(
            "storyboard_plan", Text("[{\"name\":\"林夏\"}]"));

        AppError error = Assert.Throws<AppError>(() =>
            PromptTemplateService.ValidatePromptTemplateResult("storyboard_plan", Text("完全没有 JSON 内容")));
        Assert.Contains("受保护 JSON 契约", error.Message, StringComparison.Ordinal);

        AppError characterError = Assert.Throws<AppError>(() =>
            PromptTemplateService.ValidatePromptTemplateResult("character_extract", Text("完全没有 JSON 内容")));
        Assert.Contains("受保护 JSON 契约", characterError.Message, StringComparison.Ordinal);
    }

    /// <summary>短剧大纲校验。对应 Go: <c>TestValidatePromptTemplateResultShortDramaOutline</c>。</summary>
    [Fact]
    public void 短剧大纲校验()
    {
        const string ok = "{\"title\":\"夜灯\",\"synopsis\":\"租客发现房东不是人\",\"chapters\":[{\"title\":\"入住\",\"content\":\"林夏搬进旧公寓。\"}]}";
        PromptTemplateService.ValidatePromptTemplateResult(
            "short_drama_outline", Text("```json\n" + ok + "\n```"));

        AppError empty = Assert.Throws<AppError>(() => PromptTemplateService.ValidatePromptTemplateResult(
            "short_drama_outline", Text("{\"title\":\"夜灯\",\"synopsis\":\"简介\",\"chapters\":[]}")));
        Assert.Contains("没有生成任何章节", empty.Message, StringComparison.Ordinal);
    }

    /// <summary>技能草稿校验。对应 Go: <c>TestValidatePromptTemplateResultSkillDraft</c>。</summary>
    [Fact]
    public void 技能草稿校验()
    {
        const string ok = "{\"skillName\":\"分镜节奏\",\"tag\":\"drama\",\"description\":\"把章节拆成可执行镜头\",\"instruction\":\"角色设定：分镜导演。\"}";
        PromptTemplateService.ValidatePromptTemplateResult("skill_draft", Text(ok));

        AppError error = Assert.Throws<AppError>(() => PromptTemplateService.ValidatePromptTemplateResult(
            "skill_draft",
            Text("{\"skillName\":\"分镜节奏\",\"tag\":\"drama\",\"description\":\"简介\"}")));
        Assert.Contains("instruction", error.Message, StringComparison.Ordinal);
    }

    /// <summary>章节资产：三段数组必须齐全，场景/道具的 name、description、prompt 必须非空。</summary>
    [Fact]
    public void 章节资产校验()
    {
        const string ok = "{\"characters\":[" + CharacterCard + "],"
            + "\"scenes\":[{\"name\":\"旧公寓\",\"description\":\"走廊很窄\",\"prompt\":\"窄走廊\"}],"
            + "\"props\":[{\"name\":\"小夜灯\",\"description\":\"暖黄\",\"prompt\":\"暖黄小夜灯\"}]}";
        PromptTemplateService.ValidatePromptTemplateResult("chapter_assets_extract", Text(ok));

        AppError missingGroup = Assert.Throws<AppError>(() => PromptTemplateService.ValidatePromptTemplateResult(
            "chapter_assets_extract", Text("{\"characters\":[],\"scenes\":[]}")));
        Assert.Contains("必须包含 characters、scenes 和 props 数组", missingGroup.Message, StringComparison.Ordinal);

        AppError blankField = Assert.Throws<AppError>(() => PromptTemplateService.ValidatePromptTemplateResult(
            "chapter_assets_extract",
            Text("{\"characters\":[],\"scenes\":[{\"name\":\"旧公寓\",\"description\":\"  \",\"prompt\":\"窄走廊\"}],\"props\":[]}")));
        Assert.Contains("章节场景或道具缺少有效的 description", blankField.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 非 JSON 操作（outputType != json）直接放行：它们是纯文本提示词，没有契约可校验。
    /// </summary>
    [Fact]
    public void 非_JSON_操作不校验结果()
    {
        PromptTemplateService.ValidatePromptTemplateResult(
            "storyboard_first_frame", Text("随便一段首帧提示词，完全不是 JSON"));
        PromptTemplateService.ValidatePromptTemplateResult("未知操作", Text(""));
    }

    /// <summary>任务 input 里的模板变量摊平（对应 Go 的 <c>metadataStringValues</c>）。</summary>
    [Fact]
    public void 模板变量按字符串摊平()
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            "{\"项目名称\":\"夜灯\",\"用户要求\":\" 共 6 秒 \",\"章节数量\":5,\"启用\":true,\"空\":null}");

        Dictionary<string, string> values = PromptTemplateService.TemplateValues(document.RootElement);

        Assert.Equal("夜灯", values["项目名称"]);
        // 值统一 TrimSpace（Go 的 fmt.Sprint + TrimSpace）。
        Assert.Equal("共 6 秒", values["用户要求"]);
        Assert.Equal("5", values["章节数量"]);
        Assert.Equal("true", values["启用"]);
        Assert.Equal("<nil>", values["空"]);
        // 非对象（或缺失）时返回空表，不抛异常。
        Assert.Empty(PromptTemplateService.TemplateValues(null));
        Assert.Empty(PromptTemplateService.TemplateValues("不是对象"));
    }

    private static PromptOperationDefinition Definition(string operation) =>
        PromptDefaults.Definitions().Single(definition =>
            string.Equals(definition.Operation, operation, StringComparison.Ordinal));

    private static Dictionary<string, object?> Text(string text) =>
        new(StringComparer.Ordinal) { ["text"] = text };
}
