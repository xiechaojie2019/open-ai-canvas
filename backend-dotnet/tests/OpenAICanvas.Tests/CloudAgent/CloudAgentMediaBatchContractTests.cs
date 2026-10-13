#nullable enable
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 批量出图（<c>count&gt;1</c>）与「auto 模式生图不再逐张等审批」的契约。
/// </summary>
/// <remarks>
/// 背景（2026-10-12，线上 run <c>ag2b5f67e4b74c1ad998bea46af78e8c5e</c>）：
/// 用户已经选了「自动执行」，Agent 仍对 5 张图逐一发起
/// <c>approval_requested → approval_decided → generation_task_created</c>，
/// 07:45:16 → 07:50:12 五分钟只出了 5 张（1 张/分钟）；
/// 而前端「直接图片生成」选 3 张时会一次建 3 个节点并行跑。
/// <para>
/// 根因有两层：
/// ① 媒体策略 v2 原文写着「创建媒体草稿及连线不等于收费授权；<b>所有模式</b>都必须等待界面独立审批」，
///    代码在四处都照此实现（本文件对应的 <c>needsApproval</c>、Go 的
///    <c>PermissionMode == "request_approval" || name == "generate_media"</c>、工具描述、策略文档）。
/// ② <c>mediaTaskId</c> 只有一个槽位，且服务端有硬约束「每个获批执行项只能生成一个产物」
///    ⇒ 想并行就必须 N 个节点 + N 个 <c>count=1</c> 的任务，也就是前端那套做法。
/// </para>
/// 这里锁住的是这两层改动的<b>可达性</b>：光把 helper 写对没用，
/// 调用点不接线的话线上行为一点不变。
/// </remarks>
public sealed class CloudAgentMediaBatchContractTests
{
    // ------------------------------------------------------------------ 参数校验

    [Fact]
    public void 张数上限与服务端同源()
    {
        Assert.Equal(8, CloudAgentMediaService.MaxBatchCount);

        AppError error = Assert.Throws<AppError>(() =>
            CloudAgentMediaService.ValidateArgs(Image(count: CloudAgentMediaService.MaxBatchCount + 1), State()));
        Assert.Contains($"一次最多生成 {CloudAgentMediaService.MaxBatchCount} 个产物", error.Message);

        // 边界内不抛。
        CloudAgentMediaService.ValidateArgs(Image(count: CloudAgentMediaService.MaxBatchCount), State());
    }

    [Fact]
    public void 负张数被拒()
    {
        AppError error = Assert.Throws<AppError>(() =>
            CloudAgentMediaService.ValidateArgs(Image(count: -1), State()));
        Assert.Contains("生成数量不能为负数", error.Message);
    }

    [Fact]
    public void 视频与音频不支持一次出多个()
    {
        CloudAgentMediaArgs video = Image(count: 2);
        video.Mode = "video";

        AppError error = Assert.Throws<AppError>(() => CloudAgentMediaService.ValidateArgs(video, State()));
        Assert.Contains("只有图片生成支持一次出多张", error.Message);
    }

    // ------------------------------------------------------------------ 节点派生

    [Fact]
    public void 单张时节点ID原样保留()
    {
        Assert.Equal(new[] { "image-new" }, CloudAgentMediaService.BatchNodeIDs(Image(count: 1)));
    }

    [Fact]
    public void 多张时派生编号节点ID()
    {
        Assert.Equal(
            new[] { "image-new-1", "image-new-2", "image-new-3" },
            CloudAgentMediaService.BatchNodeIDs(Image(count: 3)));
    }

    [Fact]
    public void 多张时节点ID过长会被拒()
    {
        // 80 是硬上限；批量要追加「-N」，所以最多 77。超了必须建草稿之前就拒绝。
        AppError error = Assert.Throws<AppError>(() =>
            CloudAgentMediaService.BatchNodeIDs(Image(nodeID: new string('n', 78), count: 2)));
        Assert.Contains("77", error.Message);

        // 恰好 77 个字符可用（-1 后正好 79）。
        Assert.Equal(
            new[] { new string('n', 77) + "-1", new string('n', 77) + "-2" },
            CloudAgentMediaService.BatchNodeIDs(Image(nodeID: new string('n', 77), count: 2)));
    }

    // ------------------------------------------------------------------ 每项参数副本

    [Fact]
    public void 每项副本把张数归一为一并追加标题序号()
    {
        CloudAgentMediaArgs source = Image(count: 3, title: "分镜图");

        CloudAgentMediaArgs first = CloudAgentMediaService.CloneMediaArgs(source, "image-new-1", 0, 3);
        CloudAgentMediaArgs third = CloudAgentMediaService.CloneMediaArgs(source, "image-new-3", 2, 3);

        Assert.Equal("image-new-1", first.NodeID);
        Assert.Equal("分镜图 1", first.Title);
        Assert.Equal("分镜图 3", third.Title);
        // 每项只出一个产物：服务端硬约束「每个获批执行项只能生成一个产物」。
        Assert.Equal(1, first.Count);
        Assert.Equal(1, third.Count);
        // 读时快照先原样带过去（草稿准入要校验），由 BakeMediaDraftAsync 逐项重算覆盖。
        Assert.Equal(source.SnapshotHash, first.SnapshotHash);
        // 副本必须独立：改一项不能污染源参数或别的项。
        first.ReferenceNodeIDs.Add("image-ref");
        Assert.Empty(source.ReferenceNodeIDs);
        Assert.Empty(third.ReferenceNodeIDs);
    }

    [Fact]
    public void 单张时标题不加序号()
    {
        CloudAgentMediaArgs single = CloudAgentMediaService.CloneMediaArgs(Image(title: "分镜图"), "image-new", 0, 1);
        Assert.Equal("分镜图", single.Title);
    }

    // ------------------------------------------------------------------ 逐张提示词（拆词）

    /// <summary>
    /// 批量时必须逐张下发各自的提示词。
    /// </summary>
    /// <remarks>
    /// 线上现象（2026-10-13）：count=3 时三个节点的 prompt 完全相同，
    /// 而那段 prompt 本身写的是「本组共三张：第1张…第2张…第3张…」，
    /// 于是每张图都被画成三格拼贴。正解是让模型把多张拆成 N 段互不相同的提示词。
    /// </remarks>
    [Fact]
    public void 批量时逐项使用各自的提示词()
    {
        CloudAgentMediaArgs source = Image(count: 3);
        source.Prompt = "共同主体和风格";
        source.Prompts = ["第1张：扑蝶", "第2张：伸懒腰", "第3张：舔毛"];

        CloudAgentMediaArgs first = CloudAgentMediaService.CloneMediaArgs(source, "image-new-1", 0, 3);
        CloudAgentMediaArgs second = CloudAgentMediaService.CloneMediaArgs(source, "image-new-2", 1, 3);
        CloudAgentMediaArgs third = CloudAgentMediaService.CloneMediaArgs(source, "image-new-3", 2, 3);

        Assert.Equal("第1张：扑蝶", first.Prompt);
        Assert.Equal("第2张：伸懒腰", second.Prompt);
        Assert.Equal("第3张：舔毛", third.Prompt);
        Assert.NotEqual(first.Prompt, second.Prompt);
    }

    /// <summary>
    /// 没给 <c>prompts</c> 时回落共同 prompt —— 旧调用与在途旧 run 状态的行为必须一字不变。
    /// </summary>
    [Fact]
    public void 缺省时回落共同提示词()
    {
        CloudAgentMediaArgs source = Image(count: 3);
        source.Prompt = "画一只猫";

        Assert.Equal("画一只猫", CloudAgentMediaService.CloneMediaArgs(source, "image-new-1", 0, 3).Prompt);
        Assert.Equal("画一只猫", CloudAgentMediaService.CloneMediaArgs(source, "image-new-3", 2, 3).Prompt);
    }

    /// <summary>段数与张数不一致必须当场拒绝，不能靠猜。</summary>
    [Fact]
    public void 提示词段数必须与张数一致()
    {
        CloudAgentMediaArgs few = Image(count: 3);
        few.Prompts = ["甲", "乙"];

        AppError error = Assert.Throws<AppError>(() => CloudAgentMediaService.ValidateArgs(few, State()));
        Assert.Contains("段数（2）必须与 count（3）一致", error.Message);

        // 一致时放行。
        CloudAgentMediaArgs exact = Image(count: 2);
        exact.Prompts = ["甲", "乙"];
        CloudAgentMediaService.ValidateArgs(exact, State());
    }

    [Fact]
    public void 单张或非图片不允许传提示词数组()
    {
        CloudAgentMediaArgs single = Image(count: 1);
        single.Prompts = ["甲"];
        AppError one = Assert.Throws<AppError>(() => CloudAgentMediaService.ValidateArgs(single, State()));
        Assert.Contains("仅在 mode=image 且 count>1 时使用", one.Message);

        // 视频 count>1 会更早撞上「只有图片支持一次出多张」，那也是对的——这里只锁单张这条。
        CloudAgentMediaArgs video = Image(count: 1);
        video.Mode = "video";
        video.Duration = 3;
        video.Prompts = ["甲"];
        AppError two = Assert.Throws<AppError>(() => CloudAgentMediaService.ValidateArgs(video, State()));
        Assert.Contains("仅在 mode=image 且 count>1 时使用", two.Message);
    }

    [Fact]
    public void 提示词数组里不能有空段()
    {
        CloudAgentMediaArgs args = Image(count: 2);
        args.Prompts = ["甲", "   "];

        AppError error = Assert.Throws<AppError>(() => CloudAgentMediaService.ValidateArgs(args, State()));
        Assert.Contains("prompts 里不能有空段", error.Message);
    }

    /// <summary>
    /// 逐张提示词必须真接线到服务端。<c>CloneMediaArgs</c> 写对了但没人调它照样不生效。
    /// </summary>
    [Fact]
    public void 拆词必须接线到工具schema与参数副本()
    {
        string tools = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentTools.cs"));
        // schema 要暴露 prompts，且描述必须写清「逐张」「段数等于 count」「否则拼贴图」。
        Assert.Contains("[\"prompts\"]", tools);
        Assert.Contains("段数必须等于 count", tools);
        Assert.Contains("拼贴图", tools);

        string media = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentMediaService.cs"));
        // 参数副本必须按序号取各自提示词，而不是一律沿用父 prompt。
        Assert.Contains("string prompt = source.Prompts.Count == total ? source.Prompts[index] : source.Prompt;", media);
        Assert.Contains("Prompt = prompt,", media);
    }

    // ------------------------------------------------------------------ 在途媒体任务不得被收尾掐断

    /// <summary>
    /// 批内已有任务成功提交时，一次失败的（多半是重复的）<c>generate_media</c> 调用
    /// 不得把整批的清掉 —— 否则已交出去、正在等结果的那批任务回写通道被掐断，
    /// 表现为「任务 succeeded、成品已落存储，画布节点永远 loading」。
    /// </summary>
    [Fact]
    public void 已提交批次不得被失败的重试调用清掉()
    {
        string execution = RuntimeExecutionSource();

        // 必须先算出「批内是否已有提交」，再按它决定要不要清空。
        int guard = execution.IndexOf("bool batchAlreadySubmitted = state.MediaBatch is { Count: > 0 } submittedBatch", StringComparison.Ordinal);
        Assert.True(guard >= 0, "MediaErrorAsync 必须先判断批内是否已有提交");

        int clear = execution.IndexOf("state.MediaBatch = null;", guard, StringComparison.Ordinal);
        Assert.True(clear > guard, "清空 MediaBatch 必须晚于（且受制于）已提交判定");
        // 清空与终态化都必须落在 !batchAlreadySubmitted 分支里。
        Assert.Contains("if (!batchAlreadySubmitted)", execution);
        // 工具失败回执的 taskSubmitted 要如实反映"这批已经交出去了"。
        Assert.Contains("[\"taskSubmitted\"] = submitted || batchAlreadySubmitted,", execution);
    }

    /// <summary>
    /// run 收尾时若还有已提交未回写的媒体任务，必须保持 running 且**保留原调用**，
    /// 否则下一拍调度器直接跳过（completed），或丢掉调用导致 ValidateRuntime 判状态损坏。
    /// </summary>
    [Fact]
    public void 在途媒体任务必须保持运行并保留调用()
    {
        string runtime = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeService.cs"));

        Assert.Contains("bool outstandingMedia = calls.Count == 0 && HasOutstandingMediaWork(state);", runtime);
        Assert.Contains("current.Status = outstandingMedia ? \"running\" : \"completed\";", runtime);
        // 覆盖 Calls/CallIndex 必须被 outstandingMedia 挡住（否则批次失去挂载点）。
        Assert.Contains("if (!outstandingMedia)", runtime);

        // 判定式本身：单槽 + 批次里任一已提交，都算在途。
        Assert.Contains("private static bool HasOutstandingMediaWork(CloudAgentRuntimeDto state)", runtime);
        Assert.Contains("state.MediaTaskID.Length > 0", runtime);
    }

    // ------------------------------------------------------------------ 预算

    [Fact]
    public void 批量超出剩余生成次数会被拒()
    {
        AppError error = Assert.Throws<AppError>(() =>
            CloudAgentMediaService.EnsureBatchBudget(
                Image(count: 2), State(generations: 1, maxGenerationTasks: 2)));
        Assert.Contains("本轮剩余生成次数不足", error.Message);
        Assert.Contains("还可生成 1 个", error.Message);

        // 预算为 0 表示不限，照旧放行。
        CloudAgentMediaService.EnsureBatchBudget(Image(count: 2), State(generations: 99));
        // 恰好用满放行。
        CloudAgentMediaService.EnsureBatchBudget(
            Image(count: 2), State(generations: 0, maxGenerationTasks: 2));
    }

    [Fact]
    public void 视频超出剩余秒数会被拒()
    {
        CloudAgentMediaArgs video = Image(count: 1);
        video.Mode = "video";
        video.Duration = 3;

        AppError error = Assert.Throws<AppError>(() =>
            CloudAgentMediaService.EnsureBatchBudget(
                video, State(videoSeconds: 4, maxVideoSeconds: 6)));
        Assert.Contains("已超过本轮视频时长预算", error.Message);

        // 预算为 0 表示不限。
        CloudAgentMediaService.EnsureBatchBudget(video, State(videoSeconds: 999));
    }

    // ------------------------------------------------------------------ 接线（源码级）

    /// <summary>
    /// auto 模式下 <c>generate_media</c> 不再无条件等待审批。
    /// 断言的是调用点的判定式本身：只要有人把 <c>|| name == "generate_media"</c> 加回来，
    /// 「选了自动执行还是要确认」就会原样复发，而所有 helper 测试都不会红。
    /// </summary>
    [Fact]
    public void 自动执行模式不再对生图强制审批()
    {
        string source = RuntimeExecutionSource();

        Assert.Contains(
            "bool needsApproval = state.Request.PermissionMode == \"request_approval\";", source);
        Assert.DoesNotContain("|| name == \"generate_media\"", source);

        // auto 分支必须先建草稿 + 固化哈希，再进提交；返回 false 表示已记成工具失败，必须直接退出。
        int prepare = source.IndexOf("await PrepareAutoMediaDraftAsync(", StringComparison.Ordinal);
        int advance = source.IndexOf("await AdvanceMediaAsync(", StringComparison.Ordinal);
        Assert.True(prepare >= 0, "auto 模式必须先固化草稿再提交");
        Assert.True(advance > prepare, "固化草稿必须早于提交，否则提交时快照哈希陈旧");
    }

    /// <summary>
    /// 批量产物必须先被分派走：<c>MediaBatch</c> 非空时不能落进单产物槽位，
    /// 否则第一批的第 2 张起会被当成「媒体任务不存在」。
    /// </summary>
    [Fact]
    public void 批次分派早于单产物分支()
    {
        string source = RuntimeExecutionSource();

        int batch = source.IndexOf("state.MediaBatch is { Count: > 0 } batch", StringComparison.Ordinal);
        int single = source.IndexOf("if (state.MediaTaskID.Length > 0)", StringComparison.Ordinal);
        Assert.True(batch >= 0, "AdvanceMediaAsync 必须先检查 MediaBatch");
        Assert.True(single > batch, "批次分支必须在单产物分支之前");
        Assert.Contains("await AdvanceMediaBatchAsync(run, state, call, batch, cancellationToken)", source);
    }

    /// <summary>
    /// 同轮多写接续快照必须真的接线：捕获基线（模型输出成批调用时）+ 执行前刷新（审批判定之前）。
    /// 少任何一处，基线恒为空、刷新恒早退，行为与没移植完全一样。
    /// </summary>
    [Fact]
    public void 接续快照在两端都必须接线()
    {
        string runtime = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeService.cs"));
        int capture = runtime.IndexOf("state.StepSnapshotHash = CaptureStepSnapshotHash(calls);", StringComparison.Ordinal);
        Assert.True(capture >= 0, "模型输出成批调用时必须捕获本步基线快照哈希");

        string execution = RuntimeExecutionSource();
        int refresh = execution.IndexOf("await RefreshStepSnapshotHashAsync(", StringComparison.Ordinal);
        int approvalWait = execution.IndexOf("return; // 等待审批。", StringComparison.Ordinal);
        Assert.True(refresh >= 0, "执行写工具前必须尝试接续快照");
        Assert.True(approvalWait > refresh, "刷新必须早于审批等待分支，否则第一个写之后就再也不刷");
    }

    private static string RuntimeExecutionSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "OpenAICanvas.Application", "CloudAgent", "CloudAgentRuntimeExecution.cs"));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenAICanvas.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }

    // ------------------------------------------------------------------ 夹具

    private static CloudAgentMediaArgs Image(string nodeID = "image-new", int count = 1, string title = "新图") => new()
    {
        Mode = "image",
        Prompt = "画一只猫",
        Title = title,
        NodeID = nodeID,
        Size = "1:1",
        SnapshotHash = new string('a', 64),
        Count = count,
    };

    private static CloudAgentRuntimeDto State(
        int generations = 0, int videoSeconds = 0, int maxGenerationTasks = 0, int maxVideoSeconds = 0) => new()
        {
            Request = new CloudAgentRequestDto
            {
                Budget = new CloudAgentBudgetDto
                {
                    MaxGenerationTasks = maxGenerationTasks,
                    MaxVideoSeconds = maxVideoSeconds,
                },
            },
            Generations = generations,
            VideoSeconds = videoSeconds,
        };
}
