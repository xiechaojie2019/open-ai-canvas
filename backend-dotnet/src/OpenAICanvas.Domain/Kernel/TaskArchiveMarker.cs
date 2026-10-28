#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAICanvas.Domain.Kernel;

/// <summary>
/// 任务输入里的「删除画布后把产物收进素材库」标记。
/// </summary>
/// <remarks>
/// 画布被删时任务可能还在排队或生成中，其产物既没有节点可挂靠、也不该随画布消失，
/// 否则已经扣掉的积分换不回任何东西。标记写在 <c>tasks.inputJson.metadata</c>，
/// 由任务完成侧读取后补登素材；不新增列，因此不需要迁移。
/// </remarks>
public static class TaskArchiveMarker
{
    public const string ArchiveToAssetsKey = "archiveToAssets";
    public const string DeletedCanvasIDKey = "deletedCanvasId";
    public const string DeletedCanvasTitleKey = "deletedCanvasTitle";

    /// <summary>写入归档标记，返回新的 inputJson；输入非法时返回空串。</summary>
    public static string Mark(string inputJSON, string canvasID, string canvasTitle)
    {
        if (string.IsNullOrWhiteSpace(inputJSON))
        {
            return "";
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(inputJSON);
        }
        catch (JsonException)
        {
            return "";
        }
        if (root is not JsonObject input)
        {
            return "";
        }

        JsonObject metadata = input["metadata"] as JsonObject ?? new JsonObject();
        metadata[ArchiveToAssetsKey] = true;
        metadata[DeletedCanvasIDKey] = canvasID;
        metadata[DeletedCanvasTitleKey] = canvasTitle;
        input["metadata"] = metadata;
        return input.ToJsonString();
    }

    /// <summary>读取归档标记；未标记时返回 false。</summary>
    public static bool TryRead(string inputJSON, out string canvasID, out string canvasTitle)
    {
        canvasID = "";
        canvasTitle = "";
        if (string.IsNullOrWhiteSpace(inputJSON))
        {
            return false;
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(inputJSON);
        }
        catch (JsonException)
        {
            return false;
        }
        if (root is not JsonObject input || input["metadata"] is not JsonObject metadata)
        {
            return false;
        }
        if (metadata[ArchiveToAssetsKey] is not JsonValue flag ||
            !flag.TryGetValue(out bool archive) || !archive)
        {
            return false;
        }
        canvasID = metadata[DeletedCanvasIDKey] is JsonValue id && id.TryGetValue(out string? value) ? value ?? "" : "";
        canvasTitle = metadata[DeletedCanvasTitleKey] is JsonValue title && title.TryGetValue(out string? text) ? text ?? "" : "";
        return true;
    }
}
