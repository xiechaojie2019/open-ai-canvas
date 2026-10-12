#nullable enable
using OpenAICanvas.Application.Prompts;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.Prompts;

/// <summary>
/// 分镜规划输出契约：内容、幂等性与执行链路接线。
/// </summary>
/// <remarks>
/// 这组断言对应线上实测故障：画布「分镜脚本」节点只发原始提示词，模型回了散文/ Markdown 表格，
/// 前端报「分镜任务没有返回镜头行」。契约段与结果校验缺任何一半都会退化成同一个现象。
/// </remarks>
public sealed class StoryboardOutputContractTests
{
    private const string WorkerRelativePath = "src/OpenAICanvas.Application/TaskWorkerService.cs";

    /// <summary>契约必须逐字给出前端解析所需的顶层结构，否则模型照旧回散文。</summary>
    [Fact]
    public void 契约声明顶层_rows_结构与全部字段()
    {
        string contract = StoryboardOutputContract.Rows(null);

        Assert.StartsWith(StoryboardOutputContract.ContractHeading, contract, StringComparison.Ordinal);
        // 顶层结构由前端 parseStoryboardRowsPayload 直接读取，必须出现该字面量。
        Assert.Contains("\"title\": string, \"rows\": object[]", contract, StringComparison.Ordinal);
        // 前端 storyboardRowsFromTask 会把这些字段按名取用。
        foreach (string field in new[]
                 {
                     "shotNumber", "durationSeconds", "plotDescription", "dialogue", "characters",
                     "narrativeIntent", "viewerPOV", "performanceBlocking", "shotSize", "emotion",
                     "lightingAndAtmosphere", "audioEffects", "camera", "motion", "timeBeats",
                     "imageGenerationPrompt", "videoMotionPrompt", "continuityOut", "negativePrompt",
                     "mustHave", "optionalDetails",
                 })
        {
            Assert.Contains(field, contract, StringComparison.Ordinal);
        }
    }

    /// <summary>已经自带契约的调用方（章节分镜/列表模式）不能被追加第二份。</summary>
    [Fact]
    public void 已有契约时追加是幂等的()
    {
        string once = StoryboardOutputContract.Append("把这段小说拆成镜头", null);
        string twice = StoryboardOutputContract.Append(once, "另一个要求");

        Assert.Equal(once, twice);
        Assert.True(StoryboardOutputContract.HasContract(once));
        // 原文不能被替换掉，只在末尾追加。
        Assert.StartsWith("把这段小说拆成镜头", once, StringComparison.Ordinal);
    }

    /// <summary>额外要求要进契约正文；为空时退回默认文案。</summary>
    [Fact]
    public void 额外要求进入契约正文()
    {
        Assert.Contains("每镜必须可独立生成", StoryboardOutputContract.Rows("每镜必须可独立生成"), StringComparison.Ordinal);
        Assert.Contains(StoryboardOutputContract.DefaultRequirement, StoryboardOutputContract.Rows("  "), StringComparison.Ordinal);
    }

    /// <summary>合法结果放行：允许模型夹带解释或 Markdown 代码块。</summary>
    [Fact]
    public void 校验接受被散文包裹的_rows_对象()
    {
        const string text = "好的，这是分镜表：\n```json\n{\"title\":\"小猫抓鱼\",\"rows\":[{\"shotNumber\":1,\"durationSeconds\":6,\"videoMotionPrompt\":\"推近\"}]}\n```";

        StoryboardOutputContract.ValidateResult(new Dictionary<string, object?> { ["text"] = text });
    }

    /// <summary>真实现象：模型回一段「我无法生成视频，但可以给你脚本」的散文 —— 必须显式失败。</summary>
    [Theory]
    [InlineData("我无法直接发送或生成视频文件，但可以给你一个 6 秒小猫抓鱼短视频脚本。\n**6秒分镜：**\n- 0–1s：鱼缸水面特写")]
    [InlineData("")]
    public void 校验拒绝抽不出_JSON_的正文(string text)
    {
        AppError error = Assert.Throws<AppError>(
            () => StoryboardOutputContract.ValidateResult(new Dictionary<string, object?> { ["text"] = text }));

        // 文案必须原样透出（UserFacing 只认 AppError），把排障方向钉在契约上。
        Assert.Contains("不符合受保护 JSON 契约", error.Message, StringComparison.Ordinal);
    }

    /// <summary>抽得出 JSON 但没有 rows 字段：模型答了别的结构。</summary>
    [Fact]
    public void 校验拒绝缺少_rows_的对象()
    {
        AppError error = Assert.Throws<AppError>(() => StoryboardOutputContract.ValidateResult(
            new Dictionary<string, object?> { ["text"] = "{\"title\":\"小猫抓鱼\"}" }));

        Assert.Contains("缺少 rows 数组", error.Message, StringComparison.Ordinal);
    }

    /// <summary>解析出对象但没有镜头行，或镜头行为空，都不能算成功。</summary>
    [Fact]
    public void 校验拒绝空_rows_数组()
    {
        AppError error = Assert.Throws<AppError>(() => StoryboardOutputContract.ValidateResult(
            new Dictionary<string, object?> { ["text"] = "{\"title\":\"小猫抓鱼\",\"rows\":[]}" }));

        Assert.Contains("没有规划出任何镜头", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 源码级接线断言：契约追加与结果校验必须真的挂在任务执行路径上。
    /// 只测 helper 会漏掉「调用点没接线」——这里正是上一次失效的形态。
    /// </summary>
    [Fact]
    public void 任务执行路径补契约并校验结果()
    {
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), WorkerRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        string body = SourceSection(
            source,
            "private async Task<ProviderExecutionResult> ExecuteProviderTaskAsync(",
            "private static async Task<Dictionary<string, object?>> RunTextTaskWithContractAsync(");

        // ① 按 operation 兜底：画布分镜节点不发 promptTemplateOperation，只能靠 operation 识别。
        Assert.Contains("StoryboardOutputContract.PlanTaskOperation", body, StringComparison.Ordinal);
        Assert.Contains("task.Operation", body, StringComparison.Ordinal);
        // ② 只在没走服务端分镜模板时补，避免与 storyboard-plan/v3（shots 结构）互相矛盾。
        Assert.Contains("promptTemplateOperation.Length == 0", body, StringComparison.Ordinal);
        // ③ 补契约必须发生在工具 mention 展开之后，契约始终是提示词最后一段。
        int mentionIndex = body.IndexOf("ResolveToolMentionTokensAsync(", StringComparison.Ordinal);
        int appendIndex = body.IndexOf("StoryboardOutputContract.Append(", StringComparison.Ordinal);
        Assert.True(mentionIndex >= 0 && appendIndex > mentionIndex,
            "分镜契约必须追加在工具 mention 展开之后，否则会被展开结果顶掉位置");

        string wrapper = SourceSection(
            source,
            "private static async Task<Dictionary<string, object?>> RunTextTaskWithContractAsync(",
            "/// Agent 工具任务执行：水合规划图片占位");

        // ④ 执行任务之后校验，且不能只依赖 promptTemplateOperation（分镜路径下它为空）。
        int runIndex = wrapper.IndexOf("RunTextTaskAsync(", StringComparison.Ordinal);
        int validateIndex = wrapper.IndexOf("StoryboardOutputContract.ValidateResult(", StringComparison.Ordinal);
        Assert.True(runIndex >= 0 && validateIndex > runIndex,
            "分镜结果校验必须发生在文本任务成功之后");
    }

    private static string SourceSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到起点标记：{startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"未找到终点标记：{endMarker}");
        return source[start..end];
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }
}
