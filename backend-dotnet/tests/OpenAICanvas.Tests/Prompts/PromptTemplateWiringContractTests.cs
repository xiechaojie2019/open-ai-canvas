#nullable enable
using Xunit;

namespace OpenAICanvas.Tests.Prompts;

/// <summary>
/// 提示词模板在执行链路上的接线契约。
/// </summary>
/// <remarks>
/// <para>
/// 这套源码级断言存在的原因：模板能力（编译 + 结果校验）与执行链路是<b>两处独立代码</b>，
/// 任何一处漏掉都不会让编译失败，只会让功能静默失效。实测过两种失效形态：
/// </para>
/// <list type="bullet">
/// <item>前端从不发 <c>metadata.promptTemplateOperation</c> ⇒ 后端根本没有编译机会；</item>
/// <item>后端没接编译/校验 ⇒ 模型只收到节点提示词框里的原话，拿不到契约，返回散文，
/// 前端报「分镜任务没有返回镜头行」——排查方向完全被带偏。</item>
/// </list>
/// <para>
/// 因此这里用「顺序 + 存在性」把接线钉住：编译必须在工具 mention 展开<b>之前</b>（否则
/// <c>@[tool:...]</c> 展开结果会被模板覆盖），校验必须在文本任务<b>成功之后</b>。
/// </para>
/// </remarks>
public sealed class PromptTemplateWiringContractTests
{
    private const string WorkerRelativePath = "src/OpenAICanvas.Application/TaskWorkerService.cs";
    private const string CompileRelativePath = "src/OpenAICanvas.Application/Prompts/PromptCompile.cs";
    private const string ProtectedRelativePath =
        "src/OpenAICanvas.Application/Prompts/PromptProtectedContext.cs";

    /// <summary>编译与结果校验都必须挂在任务执行路径上，且相对文本任务一前一后。</summary>
    [Fact]
    public void 任务执行路径包含模板编译与结果校验()
    {
        string body = SourceSection(
            Source(WorkerRelativePath),
            "private async Task<ProviderExecutionResult> ExecuteProviderTaskAsync(",
            "private static async Task<Dictionary<string, object?>> RunTextTaskWithContractAsync(");

        // ① 触发开关：metadata.promptTemplateOperation。
        Assert.Contains("MetadataString(input.Metadata, \"promptTemplateOperation\")", body, StringComparison.Ordinal);

        // ② 编译：必须真的调用 CompilePromptAsync。
        Assert.Contains("CompilePromptAsync(", body, StringComparison.Ordinal);

        // ③ 编译必须在工具 mention 展开之前——否则展开出来的工具提示词会被模板内容整体覆盖。
        int compileIndex = body.IndexOf("CompilePromptAsync(", StringComparison.Ordinal);
        int mentionIndex = body.IndexOf("ResolveToolMentionTokensAsync(", StringComparison.Ordinal);
        Assert.True(compileIndex >= 0 && mentionIndex >= 0);
        Assert.True(compileIndex < mentionIndex,
            "编译提示词必须发生在工具 mention 展开之前（Go: compilePrompt 在 ResolveToolMentionTokens 之前）");

        // ④ 视频节点不允许被模板替换（Go: input.Mode != "video"）。
        Assert.Contains("input.Mode != \"video\"", body, StringComparison.Ordinal);

        // ⑤ 文本任务必须走带契约校验的包装，而不是裸跑 ProviderTextTask。
        Assert.Contains("RunTextTaskWithContractAsync(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProviderTextTask(context)", body, StringComparison.Ordinal);
    }

    /// <summary>包装方法内部：先跑文本任务，再用模板操作校验结果。</summary>
    [Fact]
    public void 文本任务包装先执行再校验结果()
    {
        string body = SourceSection(
            Source(WorkerRelativePath),
            "private static async Task<Dictionary<string, object?>> RunTextTaskWithContractAsync(",
            "/// Agent 工具任务执行：水合规划图片占位");

        int runIndex = body.IndexOf("RunTextTaskAsync(", StringComparison.Ordinal);
        int validateIndex = body.IndexOf("ValidatePromptTemplateResult(", StringComparison.Ordinal);
        Assert.True(runIndex >= 0, "包装方法必须真的执行文本任务");
        Assert.True(validateIndex > runIndex,
            "结果校验必须发生在文本任务成功之后（Go: runTextTask 之后才 validatePromptTemplateResult）");
        // 只在声明了模板操作时才校验（Go: promptTemplateOperation != ""）。
        Assert.Contains("promptTemplateOperation.Length > 0", body, StringComparison.Ordinal);
    }

    /// <summary>编译的拼装顺序：渲染 → 再追加受保护上下文。</summary>
    [Fact]
    public void 编译先渲染模板再追加受保护上下文()
    {
        string body = SourceSection(
            Source(CompileRelativePath),
            "public async Task<CompiledPrompt> CompilePromptAsync(",
            "public static string RenderPromptTemplate(");

        int renderIndex = body.IndexOf("RenderPromptTemplate(definition, creative, values)", StringComparison.Ordinal);
        int protectedIndex = body.IndexOf("PromptProtectedContext.For(operation, values)", StringComparison.Ordinal);
        Assert.True(renderIndex >= 0 && protectedIndex > renderIndex,
            "受保护上下文必须拼在模板渲染结果之后（用户 rewrite 不能顶掉 JSON 契约）");

        // 用户定制的 append / rewrite 两种模式都要有分支。
        Assert.Contains("CustomizationAppend:", body, StringComparison.Ordinal);
        Assert.Contains("CustomizationRewrite:", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON Schema 只有一份（<c>PromptDefaults</c>）：受保护上下文必须查表引用它，
    /// 不能内联第二份，否则运营改模板时两处会漂移。
    /// </summary>
    [Fact]
    public void 受保护上下文复用统一的_Schema_定义()
    {
        string source = Source(ProtectedRelativePath);

        Assert.Contains("PromptDefaults.Definitions()", source, StringComparison.Ordinal);
        // 没有第二份 Schema 正文。
        Assert.DoesNotContain("additionalProperties", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$defs\"", source, StringComparison.Ordinal);
    }

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
