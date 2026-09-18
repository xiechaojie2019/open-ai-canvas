using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Build;

namespace OpenAICanvas.Web.Endpoints;

/// <summary>
/// 数据库结构版本状态。对应 Go: <c>database.SchemaStatus</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class SchemaStatusDto
{
    [JsonPropertyName("current")]
    public long Current { get; init; }

    [JsonPropertyName("expected")]
    public long Expected { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }
}

/// <summary>
/// 依赖健康检查结果。对应 Go: <c>systemStatusChecks</c>（struct）。
/// </summary>
public sealed class SystemStatusChecksDto
{
    [JsonPropertyName("database")]
    public bool Database { get; init; }

    [JsonPropertyName("runtime")]
    public bool Runtime { get; init; }

    [JsonPropertyName("schema")]
    public bool Schema { get; init; }
}

/// <summary>
/// 系统状态快照。对应 Go: <c>systemStatusSnapshot</c>（struct，字段顺序即输出顺序）。
/// </summary>
public sealed class SystemStatusSnapshotDto
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("started")]
    public bool Started { get; init; }

    [JsonPropertyName("draining")]
    public bool Draining { get; init; }

    [JsonPropertyName("activeWorkerTasks")]
    public long ActiveWorkerTasks { get; init; }

    [JsonPropertyName("build")]
    public required BuildInfo Build { get; init; }

    [JsonPropertyName("schema")]
    public required SchemaStatusDto Schema { get; init; }

    [JsonPropertyName("checks")]
    public required SystemStatusChecksDto Checks { get; init; }
}

/// <summary>
/// <c>/api/health/live</c> 的 data。
/// </summary>
/// <remarks>
/// Go 用 <c>gin.H</c> 构造，map 键按字典序输出，因此属性必须按
/// <c>build</c> → <c>status</c> 的字母序声明。
/// </remarks>
public sealed class HealthLiveDataDto
{
    [JsonPropertyName("build")]
    public required BuildInfo Build { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";
}

/// <summary>
/// <c>/api/system/version</c> 的 data（gin.H，字母序）。
/// </summary>
public sealed class SystemVersionDataDto
{
    [JsonPropertyName("build")]
    public required BuildInfo Build { get; init; }

    [JsonPropertyName("schema")]
    public required SchemaStatusDto Schema { get; init; }
}
