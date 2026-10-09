#nullable enable
using System.Text;

namespace OpenAICanvas.Providers;

/// <summary>
/// 一次任务执行共享的调用审计元数据。
/// 对应 Go: <c>providerAnalyticsContext</c>（<c>provider.go:178</c>，随请求上下文传递）。
/// </summary>
/// <remarks>
/// 在 <see cref="OpenAICanvas.Application.TaskWorkerService.ExecuteProviderTaskAsync"/>
/// 装配（Go: <c>withProviderAnalytics</c>），随 <see cref="IProviderRequestContext"/> 下发到
/// Provider 出站收口。<c>null</c>（探测、渠道连通性测试）表示不记录。
/// </remarks>
public sealed record ProviderCallAudit(
    string UserID,
    string TaskID,
    string TraceID,
    string RequestID,
    string BillingOrderID,
    string ChannelID,
    string Capability,
    string Operation,
    string Model,
    int VideoSeconds,
    /// <summary>请求种类覆盖（Go: <c>withProviderRequestKind</c>；空串按 method/path 推断）。</summary>
    string RequestKindOverride = "")
{
    /// <summary>什么都不记录的空审计（显式优于 null 散布）。</summary>
    public static ProviderCallAudit Disabled { get; } = new("", "", "", "", "", "", "", "", "", 0);
}

/// <summary>
/// 一次 Provider 出站请求的观测结果（尚未脱敏/富化）。
/// 对应 Go: <c>recordProviderRequest</c> 在 <c>doBinaryWithConsumer</c> 各终态的入参。
/// </summary>
public sealed record ProviderCallObservation(
    string Method,
    /// <summary>scheme://host/path。对应 Go 的 <c>UpstreamURL</c>。</summary>
    string UpstreamURL,
    /// <summary>仅路径。对应 Go 的 <c>Path</c>。</summary>
    string Path,
    /// <summary>"gemini"（x-goog-api-key 头存在）或 "openai"。</summary>
    string APIFormat,
    string RequestContentType,
    byte[] RequestBody,
    /// <summary>0 表示请求未到达响应阶段（连接失败、超限、取消）。</summary>
    int StatusCode,
    byte[] ResponseBody,
    /// <summary>传输/HTTP 异常；HTTP 层成功但业务失败时为 null。</summary>
    Exception? Failure,
    long DurationMs,
    DateTime StartedAt);

/// <summary>审计记录的旁路保护：记录失败绝不影响上游调用本身。</summary>
public static class ProviderCallAuditLog
{
    /// <summary>把观测结果交给上下文记录；任何异常只留进程日志。</summary>
    public static async Task RecordAsync(
        IProviderRequestContext? context, ProviderCallObservation observation, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }
        try
        {
            await context.RecordProviderCallAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                $"provider call audit write failed: {error.GetType().Name} {error.Message}");
        }
    }

    /// <summary>按 Go 的 <c>apiFormat</c> 判定：带 x-goog-api-key 头即为 gemini 协议。</summary>
    public static string APIFormatOf(HttpRequestMessage request) =>
        request.Headers.Contains("x-goog-api-key") ? "gemini" : "openai";

    /// <summary>对应 Go: <c>providerRequestKind</c>。</summary>
    public static string RequestKindFor(string method, string path)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            string trimmed = path.TrimEnd('/');
            if (trimmed.EndsWith("/content", StringComparison.Ordinal) || path.Contains("/download", StringComparison.Ordinal))
            {
                return "download";
            }
            return "poll";
        }
        if (path.Contains("repair", StringComparison.Ordinal))
        {
            return "repair";
        }
        return "create";
    }
}
