#nullable enable
using System.Text.Json.Serialization;

namespace OpenAICanvas.Application;

public sealed class LibTVImportRequestDto
{
    [JsonPropertyName("uuid")]
    public string UUID { get; set; } = "";
}

public sealed class TapNowImportRequestDto
{
    [JsonPropertyName("shareId")]
    public string ShareID { get; set; } = "";
}

public sealed class LibTVImportResultDto
{
    [JsonPropertyName("batchId")] public string BatchID { get; init; } = "";
    [JsonPropertyName("batchCreatedAt")] public DateTime BatchCreatedAt { get; init; }
    [JsonPropertyName("projectUuid")] public string ProjectUUID { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("nodes")] public IReadOnlyList<LibTVCanvasNodeDto> Nodes { get; init; } = [];
    [JsonPropertyName("connections")] public IReadOnlyList<LibTVCanvasConnectionDto> Connections { get; init; } = [];
    [JsonPropertyName("importedNodeCount")] public int ImportedNodeCount { get; init; }
    [JsonPropertyName("importedConnectionCount")] public int ImportedConnectionCount { get; init; }
    [JsonPropertyName("skippedNodes")] public IReadOnlyList<CanvasImportIssueDto> SkippedNodes { get; init; } = [];
    [JsonPropertyName("skippedConnections")] public IReadOnlyList<CanvasImportIssueDto> SkippedConnections { get; init; } = [];
    [JsonPropertyName("warnings")] public IReadOnlyList<CanvasImportWarningDto> Warnings { get; init; } = [];
    [JsonPropertyName("multiResultNodeCount")] public int MultiResultNodeCount { get; init; }
    [JsonPropertyName("staleNodeCount")] public int StaleNodeCount { get; init; }
    [JsonPropertyName("reusedFailedNodeCount")] public int ReusedFailedNodeCount { get; init; }
    [JsonPropertyName("placeholderNodeCount")] public int PlaceholderNodeCount { get; init; }
    [JsonPropertyName("convertedSpecialCount")] public int ConvertedSpecialCount { get; init; }
}

public sealed class LibTVCanvasNodeDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("width")] public double Width { get; init; }
    [JsonPropertyName("height")] public double Height { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("prompt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Prompt { get; init; }
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; init; }
    [JsonPropertyName("naturalWidth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NaturalWidth { get; init; }
    [JsonPropertyName("naturalHeight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NaturalHeight { get; init; }
    [JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? DurationMs { get; init; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MimeType { get; init; }
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; init; }
    [JsonPropertyName("errorDetails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ErrorDetails { get; init; }
    [JsonPropertyName("metadata")] public LibTVImportMetadataDto Metadata { get; init; } = new();
}

public sealed class LibTVImportMetadataDto
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "libtv";
    [JsonPropertyName("projectUuid")] public string ProjectUUID { get; init; } = "";
    [JsonPropertyName("nodeKey")] public string NodeKey { get; init; } = "";
    [JsonPropertyName("batchId")] public string BatchID { get; init; } = "";
    [JsonPropertyName("sourceType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceType { get; init; }
    [JsonPropertyName("styleAssetUuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StyleAssetUUID { get; init; }
    [JsonPropertyName("styleVersionUuid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StyleVersionUUID { get; init; }
    [JsonPropertyName("styleName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StyleName { get; init; }
}

public sealed class LibTVCanvasConnectionDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("fromNodeId")] public string FromNodeID { get; init; } = "";
    [JsonPropertyName("toNodeId")] public string ToNodeID { get; init; } = "";
}

public sealed class TapNowImportResultDto
{
    [JsonPropertyName("batchId")] public string BatchID { get; init; } = "";
    [JsonPropertyName("batchCreatedAt")] public DateTime BatchCreatedAt { get; init; }
    [JsonPropertyName("shareId")] public string ShareID { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("nodes")] public IReadOnlyList<TapNowCanvasNodeDto> Nodes { get; init; } = [];
    [JsonPropertyName("connections")] public IReadOnlyList<TapNowCanvasConnectionDto> Connections { get; init; } = [];
    [JsonPropertyName("importedNodeCount")] public int ImportedNodeCount { get; init; }
    [JsonPropertyName("importedConnectionCount")] public int ImportedConnectionCount { get; init; }
    [JsonPropertyName("skippedNodes")] public IReadOnlyList<CanvasImportIssueDto> SkippedNodes { get; init; } = [];
    [JsonPropertyName("skippedConnections")] public IReadOnlyList<CanvasImportIssueDto> SkippedConnections { get; init; } = [];
    [JsonPropertyName("warnings")] public IReadOnlyList<CanvasImportWarningDto> Warnings { get; init; } = [];
    [JsonPropertyName("multiResultNodeCount")] public int MultiResultNodeCount { get; init; }
    [JsonPropertyName("reusedFailedNodeCount")] public int ReusedFailedNodeCount { get; init; }
    [JsonPropertyName("placeholderNodeCount")] public int PlaceholderNodeCount { get; init; }
}

public sealed class TapNowCanvasNodeDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("width")] public double Width { get; init; }
    [JsonPropertyName("height")] public double Height { get; init; }
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("prompt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Prompt { get; init; }
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Model { get; init; }
    [JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Size { get; init; }
    [JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Quality { get; init; }
    [JsonPropertyName("seconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Seconds { get; init; }
    [JsonPropertyName("vquality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? VQuality { get; init; }
    [JsonPropertyName("generateAudio"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GenerateAudio { get; init; }
    [JsonPropertyName("naturalWidth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NaturalWidth { get; init; }
    [JsonPropertyName("naturalHeight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? NaturalHeight { get; init; }
    [JsonPropertyName("durationMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? DurationMs { get; init; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MimeType { get; init; }
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; init; }
    [JsonPropertyName("errorDetails"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ErrorDetails { get; init; }
    [JsonPropertyName("metadata")] public TapNowImportMetadataDto Metadata { get; init; } = new();
}

public sealed class TapNowImportMetadataDto
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "tapnow";
    [JsonPropertyName("shareId")] public string ShareID { get; init; } = "";
    [JsonPropertyName("nodeId")] public string NodeID { get; init; } = "";
    [JsonPropertyName("batchId")] public string BatchID { get; init; } = "";
    [JsonPropertyName("sourceType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SourceType { get; init; }
}

public sealed class TapNowCanvasConnectionDto
{
    [JsonPropertyName("id")] public string ID { get; init; } = "";
    [JsonPropertyName("fromNodeId")] public string FromNodeID { get; init; } = "";
    [JsonPropertyName("toNodeId")] public string ToNodeID { get; init; } = "";
    [JsonPropertyName("fromHandleId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FromHandleID { get; init; }
    [JsonPropertyName("toHandleId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToHandleID { get; init; }
}

public sealed class CanvasImportIssueDto
{
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ID { get; init; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

public sealed class CanvasImportWarningDto
{
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ID { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}
