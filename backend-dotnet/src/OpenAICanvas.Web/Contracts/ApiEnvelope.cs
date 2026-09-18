using System.Text.Json.Serialization;
using OpenAICanvas.Web.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Web.Contracts;

/// <summary>
/// 统一业务信封：<c>{ code, data, msg, reason? }</c>。
/// </summary>
/// <remarks>
/// <para>
/// 属性声明顺序刻意保持 <c>code → data → msg → reason</c>，因为 Go 版用
/// <c>gin.H</c>（即 <c>map[string]any</c>）构造响应，而 Go 的 <c>encoding/json</c>
/// 对 map 键<b>按字典序输出</b>，结果正好是这个顺序。
/// </para>
/// <para>
/// <c>data</c> 永远输出（失败时为 <c>null</c>）；<c>reason</c> 仅在有值时出现。
/// </para>
/// <para>对应 Go: internal/handler/response.go 的 ok / writeFailure</para>
/// </remarks>
public sealed class ApiEnvelope
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    /// <summary>成功时的业务数据；失败时固定为 null，但字段本身必须输出。</summary>
    [JsonPropertyName("data")]
    public object? Data { get; init; }

    [JsonPropertyName("msg")]
    public required string Msg { get; init; }

    /// <summary>稳定机器可读原因。为空时整个字段从响应体中消失。</summary>
    [JsonPropertyName("reason")]
    [GoOmitEmpty]
    public string Reason { get; init; } = string.Empty;
}
