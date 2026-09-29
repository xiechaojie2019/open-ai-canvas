#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// Agent 模型上下文预算：工具 schema、供应商封装与最终补救轮次都是请求的一部分，
/// 但 canonical 消息体本身不体现它们，因此按窗口比例保留有界预留。
/// 对应 Go: <c>app/cloud_agent_context_budget.go</c>。
/// </summary>
public sealed record CloudAgentContextBudget(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int OverheadTokens,
    int InputBudgetTokens,
    int CompactAtTokens,
    string Source);

public static class CloudAgentContextBudgets
{
    public const int DefaultContextWindowTokens = 128_000;
    public const int DefaultMaxOutputTokens = 16_384;
    public const int MinContextWindowTokens = 4_096;
    public const int MaxContextWindowTokens = 10_000_000;

    public static CloudAgentContextBudget Default() =>
        For(DefaultContextWindowTokens, DefaultMaxOutputTokens, "default");

    /// <summary>对应 Go: <c>cloudAgentContextBudgetFor</c>。</summary>
    public static CloudAgentContextBudget For(int contextWindow, int maxOutput, string source)
    {
        if (contextWindow < MinContextWindowTokens || contextWindow > MaxContextWindowTokens)
        {
            contextWindow = DefaultContextWindowTokens;
        }
        if (maxOutput < 256 || maxOutput >= contextWindow)
        {
            maxOutput = Math.Min(DefaultMaxOutputTokens, contextWindow / 4);
        }
        int overhead = Math.Max(4_096, Math.Min(32_768, contextWindow / 25));
        int inputBudget = contextWindow - maxOutput - overhead;
        if (inputBudget < 1_024)
        {
            inputBudget = Math.Max(1_024, contextWindow / 2);
        }
        return new CloudAgentContextBudget(
            contextWindow,
            maxOutput,
            overhead,
            inputBudget,
            Math.Max(1_024, inputBudget * 85 / 100),
            source);
    }

    /// <summary>
    /// 刻意保守的规划估算（非供应商分词器）：ASCII 1/4、非 ASCII 1.5。
    /// 低估会让请求抵达上游后才失败。对应 Go: <c>cloudAgentEstimatedTokens</c>。
    /// </summary>
    public static int EstimatedTokens(string value)
    {
        if (value.Length == 0)
        {
            return 1;
        }
        long ascii = 0, nonAscii = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.IsAscii)
            {
                ascii++;
            }
            else
            {
                // 无效代理对等价 Go 的 RuneError 单字节，也按非 ASCII 计。
                nonAscii++;
            }
        }
        return Math.Max(1, (int)Math.Ceiling(ascii / 4.0 + nonAscii * 1.5));
    }

    /// <summary>对应 Go: <c>cloudAgentRequestEstimatedTokens</c>。</summary>
    public static int RequestEstimatedTokens(CloudAgentCanonicalRequestDto request) =>
        EstimatedTokens(JsonSerializer.Serialize(request, GoJson.WriteOptions));

    /// <summary>对应 Go: <c>cloudAgentContextBudgetMessage</c>。</summary>
    public static string BudgetMessage(CloudAgentContextBudget budget) =>
        "用户指令与当前执行事实超过模型输入预算（"
        + FormatTokenCount(budget.InputBudgetTokens)
        + " Token，能力来源：" + budget.Source.Trim() + "），请缩小本轮范围";

    /// <summary>对应 Go: <c>formatTokenCount</c>。</summary>
    public static string FormatTokenCount(int value)
    {
        if (value < 1_000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        return (value / 1_000) + "K";
    }
}

/// <summary>
/// 持久任务事实的新鲜投影，绝不是执行授权。会话记录可以被压缩掉读取正文，
/// 但任务状态不依赖记忆。对应 Go: <c>app/cloudAgentContextFrame</c>（字段序一致）。
/// </summary>
public sealed class CloudAgentContextFrameDto
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("observedAt")]
    public DateTime ObservedAt { get; set; }

    [JsonPropertyName("runId")]
    public string RunID { get; set; } = "";

    [JsonPropertyName("userGoal")]
    public string UserGoal { get; set; } = "";

    [JsonPropertyName("plan")]
    [GoOmitEmpty]
    public List<Dictionary<string, JsonElement>>? Plan { get; set; }

    [JsonPropertyName("tasks")]
    public List<Dictionary<string, JsonElement>> Tasks { get; set; } = [];

    [JsonPropertyName("olderTaskCount")]
    [GoOmitEmpty]
    public int OlderTaskCount { get; set; }

    [JsonPropertyName("authority")]
    public string Authority { get; set; } = "";
}

/// <summary>
/// 模型上下文装配：预算解析、fresh context frame 与预算收敛。
/// 对应 Go: <c>app/cloud_agent_context_frame.go</c> 的 <c>cloudAgentModelContext</c> /
/// <c>fitCloudAgentModelContext</c> 与 <c>cloudAgentContextBudgetForRequest</c>。
/// </summary>
public sealed partial class CloudAgentRuntimeService
{
    /// <summary>对应 Go: <c>cloudAgentRuntimeContextMarker</c>。</summary>
    internal const string RuntimeContextMarker = "【运行状态】";

    /// <summary>对应 Go: <c>cloudAgentContextSourceKey</c>。</summary>
    internal const string ContextSourceKey = "agentContextSource";

    /// <summary>
    /// 解析即将执行下一轮文本步的能力合同。逻辑模型取所有合格文本路由的安全交集，
    /// 使路由选择无法在上下文装配后选到更小窗口。
    /// </summary>
    private async Task<CloudAgentContextBudget> ContextBudgetForRequestAsync(
        CloudAgentRequestDto request, CancellationToken cancellationToken)
    {
        if (_logicalModels is not null && request.LogicalModelID.Length > 0)
        {
            try
            {
                RouteCatalogSnapshot snapshot = await _logicalModels
                    .RouteCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot.Models.TryGetValue(request.LogicalModelID, out CachedLogicalModel? cached))
                {
                    int contextWindow = 0, maxOutput = 0;
                    foreach (CachedLogicalRoute route in cached.Routes)
                    {
                        if (CapabilitySpecOps.NormalizeCapability(route.CapabilitySpec.Capability) != "text")
                        {
                            continue;
                        }
                        ModelCapabilityConfig? capability;
                        try
                        {
                            capability = ChannelModelCapability
                                .NormalizedChannelModelCapability(route.ChannelModel);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (capability?.Text is null)
                        {
                            continue;
                        }
                        contextWindow = contextWindow == 0 || capability.Text.ContextWindowTokens < contextWindow
                            ? capability.Text.ContextWindowTokens
                            : contextWindow;
                        maxOutput = maxOutput == 0 || capability.Text.MaxOutputTokens < maxOutput
                            ? capability.Text.MaxOutputTokens
                            : maxOutput;
                    }
                    if (contextWindow > 0 && maxOutput > 0)
                    {
                        return CloudAgentContextBudgets.For(
                            contextWindow, maxOutput, "logical-route-intersection");
                    }
                }
            }
            catch (Exception error) when (error is AppError or InvalidOperationException)
            {
                // 目录不可用回退默认窗口（与 Go 的 err 忽略一致）。
            }
            return CloudAgentContextBudgets.Default();
        }
        if (request.ChannelID.Length > 0 && request.ChannelModelKey.Length > 0)
        {
            ChannelModel? channelModel = await _repository.ChannelModelByKeyAsync(
                request.ChannelID, request.ChannelModelKey, cancellationToken).ConfigureAwait(false);
            if (channelModel is not null)
            {
                ModelCapabilityConfig? capability;
                try
                {
                    capability = ChannelModelCapability.NormalizedChannelModelCapability(channelModel);
                }
                catch (Exception error) when (error is AppError or InvalidOperationException)
                {
                    capability = null;
                }
                if (capability?.Text is not null)
                {
                    return CloudAgentContextBudgets.For(
                        capability.Text.ContextWindowTokens,
                        capability.Text.MaxOutputTokens,
                        "channel-model");
                }
            }
        }
        return CloudAgentContextBudgets.Default();
    }

    /// <summary>
    /// 构建本轮模型请求：克隆持久会话，追加重读任务事实的新鲜帧，再按预算收敛。
    /// 这是状态观察，不是执行或重复提交的授权。
    /// </summary>
    private async Task<CloudAgentCanonicalRequestDto> ModelContextAsync(
        CloudAgentExecution run,
        CloudAgentRuntimeDto state,
        CloudAgentContextBudget budget,
        CancellationToken cancellationToken)
    {
        CloudAgentCanonicalRequestDto canonical = JsonSerializer.Deserialize<CloudAgentCanonicalRequestDto>(
            JsonSerializer.Serialize(state.Canonical, GoJson.WriteOptions), GoJson.ReadOptions)
            ?? new CloudAgentCanonicalRequestDto();
        CloudAgentContextFrameDto frame = new()
        {
            Source = "task_repository",
            ObservedAt = DateTime.UtcNow,
            RunID = run.ID,
            UserGoal = state.Request.Prompt,
            Plan = null,
            Tasks = [],
            Authority = "状态观察，不是执行或重复提交的授权",
        };
        // 视窗不是历史丢失：全部任务身份都持久在案，可经 task_get 与会话日志回取。
        int start = Math.Max(0, state.TaskIDs.Count - 32);
        frame.OlderTaskCount = start;
        for (int index = start; index < state.TaskIDs.Count; index++)
        {
            string id = state.TaskIDs[index];
            TaskEntity? task = await _repository.TaskForUserAsync(run.UserID, id, cancellationToken)
                .ConfigureAwait(false);
            if (task is null)
            {
                frame.Tasks.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["taskId"] = JsonSerializer.SerializeToElement(id),
                    ["taskStatus"] = JsonSerializer.SerializeToElement("unavailable"),
                });
                continue;
            }
            if (task.ProjectID != state.Request.CanvasID)
            {
                throw AppError.BadAuthRequest("运行关联任务不属于当前画布");
            }
            if (task.Operation is "cloud_agent" or "cloud_agent_step")
            {
                continue;
            }
            frame.Tasks.Add(CloudAgentTaskFacts.Diagnostic(_repository, task, cancellationToken));
        }
        string body = JsonSerializer.Serialize(frame, GoJson.WriteOptions);
        canonical.Messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("user"),
            ["content"] = JsonSerializer.SerializeToElement(RuntimeContextMarker + body),
            [ContextSourceKey] = JsonSerializer.SerializeToElement("runtime"),
        });
        FitCloudAgentModelContext(canonical, budget.InputBudgetTokens);
        return canonical;
    }

    /// <summary>
    /// 只淘汰完整的历史 assistant/tool 轮次；用户指令与新鲜事实帧必须保留，
    /// 残缺工具对绝不出站。对应 Go: <c>fitCloudAgentModelContext</c>。
    /// </summary>
    internal static void FitCloudAgentModelContext(CloudAgentCanonicalRequestDto request, int maxTokens)
    {
        while (true)
        {
            if (CloudAgentContextBudgets.RequestEstimatedTokens(request) <= maxTokens)
            {
                return;
            }
            bool removed = false;
            for (int index = 0; index < request.Messages.Count - 3; index++)
            {
                if (MessageRole(request.Messages[index]) != "assistant")
                {
                    continue;
                }
                int end = index + 1;
                while (end < request.Messages.Count && MessageRole(request.Messages[end]) == "tool")
                {
                    end++;
                }
                if (end >= request.Messages.Count - 2)
                {
                    continue;
                }
                request.Messages.RemoveRange(index, end - index);
                removed = true;
                break;
            }
            if (!removed)
            {
                throw AppError.BadAuthRequest(
                    $"用户指令与当前执行事实超过模型输入预算（{maxTokens} Token），请缩小本轮范围");
            }
        }
    }
}
