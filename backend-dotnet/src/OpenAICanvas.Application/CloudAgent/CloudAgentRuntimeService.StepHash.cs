#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 「同轮多写接续快照」：把同一轮（模型一次输出）里后续写操作的 snapshotHash 接到当前画布上。
/// 对应 Go: <c>internal/app/cloud_agent_step_hash.go</c>。
/// </summary>
/// <remarks>
/// 背景：模型读完画布后，一轮里可能连续下多个写调用（例如同一轮里先建草稿节点、
/// 再 generate_media，或连下多个 generate_media 各出一个节点）。第一个写成功后画布就变了，
/// 后面那些调用手里的 snapshotHash 立刻过期，于是逐个报「画布已变化，请重新读取画布并重新审批」，
/// 模型只能重读画布、退回「一次只写一个」。
/// <para>
/// 放宽范围被刻意限死在三重条件上，缺一不可（否则就变成绕过并发保护）：
/// ① 只放宽本运行自己产生的变更——<c>cloudAgentCanvasMutations</c> 里最近一行必须是本轮运行写的，
///    且其 afterSnapshotHash 等于当前画布哈希；用户手动画布没有 mutation 行，会被挡住；
/// ② 变更链必须与基线首尾严丝合缝：第一行的 beforeSnapshotHash == 基线，末行的 afterSnapshotHash == 当前哈希；
/// ③ 只对 snapshotHash 仍等于本轮 base 线的调用生效——模型自己换过哈希就不管。
/// 跨轮调用、别的运行写的、用户手工改的，全部照旧走原校验。
/// </para>
/// </remarks>
public sealed partial class CloudAgentRuntimeService
{
    /// <summary>调用参数里的 snapshotHash；解析不出来算空串。对应 Go: <c>cloudAgentCallSnapshotHash</c>。</summary>
    internal static string CallSnapshotHash(CloudAgentCallDto call)
    {
        if (!TryParseCallArguments(call.Function.Arguments, out JsonObject args))
        {
            return "";
        }
        return CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(args, "snapshotHash"));
    }

    /// <summary>本步第一个非空 snapshotHash 作为基线。对应 Go: <c>cloudAgentCaptureStepSnapshotHash</c>。</summary>
    internal static string CaptureStepSnapshotHash(IEnumerable<CloudAgentCallDto> calls)
    {
        foreach (CloudAgentCallDto call in calls)
        {
            string hash = CallSnapshotHash(call);
            if (hash.Length > 0)
            {
                return hash;
            }
        }
        return "";
    }

    /// <summary>重写调用参数里的 snapshotHash。对应 Go: <c>cloudAgentRewriteCallSnapshotHash</c>。</summary>
    internal static CloudAgentCallDto RewriteCallSnapshotHash(CloudAgentCallDto call, string hash)
    {
        if (!TryParseCallArguments(call.Function.Arguments, out JsonObject args))
        {
            return call;
        }
        args["snapshotHash"] = hash;
        call.Function.Arguments = args.ToJsonString(GoJson.WriteOptions);
        return call;
    }

    private static bool TryParseCallArguments(string raw, out JsonObject args)
    {
        args = new JsonObject();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        try
        {
            JsonNode? parsed = JsonNode.Parse(raw);
            if (parsed is not JsonObject obj)
            {
                return false;
            }
            args = obj;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把同一轮里后续写操作的 snapshotHash 接到当前画布上。
    /// 任一步出错都原样返回调用（对应 Go 的 <c>return call</c>），绝不因为补偿失败而中断工具执行。
    /// </summary>
    internal async Task<CloudAgentCallDto> RefreshStepSnapshotHashAsync(
        CloudAgentExecution run, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        CancellationToken cancellationToken)
    {
        // CallIndex==0 是本轮第一个调用：它手里的哈希就是本轮基线，无所谓接续。
        if (state.CallIndex <= 0 || state.Calls.Count == 0 || !CloudAgentTools.IsWrite(call.Function.Name))
        {
            return call;
        }
        if (!TryParseCallArguments(call.Function.Arguments, out JsonObject args))
        {
            return call;
        }
        try
        {
            CanvasProject? canvas = await _repository
                .CanvasProjectForUserAsync(run.UserID, state.Request.CanvasID, cancellationToken)
                .ConfigureAwait(false);
            if (canvas is null)
            {
                return call;
            }
            JsonObject doc = CloudAgentJsonHelpers.Document(canvas.PayloadJSON);
            string current = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(args, "snapshotHash"));
            if (current.Length == 0)
            {
                // 漏传快照时只给 generate_media 补当前媒体内容哈希：其余写工具必须显式读画布，
                // 否则模型可以完全不读就写。对应 Go 的同名分支。
                if (call.Function.Name != "generate_media")
                {
                    return call;
                }
                string filled = CloudAgentContracts.MediaContentHash(doc);
                if (filled.Length == 0)
                {
                    return call;
                }
                Console.Error.WriteLine(
                    $"agent step hash filled: run={run.ID} step={state.Step} index={state.CallIndex} tool={call.Function.Name}");
                return RewriteCallSnapshotHash(call, filled);
            }
            string baseline = state.StepSnapshotHash;
            if (baseline.Length == 0 || current != baseline)
            {
                return call;
            }
            string latest = CloudAgentContracts.CanvasHash(doc);
            if (latest.Length == 0 || latest == current)
            {
                return call;
            }
            CloudAgentCanvasMutation? latestMutation = await _repository
                .LatestCloudAgentCanvasMutationForCanvasAsync(run.UserID, state.Request.CanvasID, cancellationToken)
                .ConfigureAwait(false);
            if (latestMutation is null || latestMutation.RunID != run.ID
                || latestMutation.AfterSnapshotHash != latest)
            {
                return call;
            }
            List<CloudAgentCanvasMutation> chain = await _repository
                .CloudAgentCanvasMutationChainAsync(run.UserID, run.ID, state.Request.CanvasID, cancellationToken)
                .ConfigureAwait(false);
            HashSet<string> stepIDs = new(StringComparer.Ordinal);
            foreach (CloudAgentCallDto candidate in state.Calls)
            {
                if (candidate.ID.Length > 0)
                {
                    stepIDs.Add(candidate.ID);
                }
            }
            // 只认「本轮调用自己写出来的那条链」：从基线出发逐段相接，直到当前哈希。
            string expected = baseline;
            bool advanced = false;
            foreach (CloudAgentCanvasMutation mutation in chain)
            {
                if (!stepIDs.Contains(mutation.StepID))
                {
                    continue;
                }
                if (!advanced)
                {
                    if (mutation.BeforeSnapshotHash != expected)
                    {
                        continue;
                    }
                    advanced = true;
                }
                else if (mutation.BeforeSnapshotHash != expected)
                {
                    return call;
                }
                expected = mutation.AfterSnapshotHash;
                if (expected == latest)
                {
                    break;
                }
            }
            if (!advanced || expected != latest)
            {
                return call;
            }
            Console.Error.WriteLine(
                $"agent step hash refreshed: run={run.ID} step={state.Step} index={state.CallIndex} tool={call.Function.Name}");
            return RewriteCallSnapshotHash(call, latest);
        }
        catch (Exception cause) when (cause is AppError or InvalidOperationException
            or System.Data.Common.DbException or JsonException)
        {
            Console.Error.WriteLine(
                $"agent step hash refresh skipped: run={run.ID} index={state.CallIndex} cause={cause.GetType().Name}: {cause.Message}");
            return call;
        }
    }
}
