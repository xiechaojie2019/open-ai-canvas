#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 把「画布已删除、任务仍在执行」的生成结果补登进素材库。
/// </summary>
/// <remarks>
/// 删除画布时任务可能还在排队或生成中。产物原本只挂在画布节点上，节点随画布消失后
/// 结果就无处可取，而积分已经扣掉了。删除路径因此给这些任务的 inputJson.metadata 打上
/// 归档标记，这里在任务成功入库之后按标记补登素材。补登失败只写任务日志，
/// 不影响任务成功与计费结算。
/// </remarks>
public sealed class GeneratedAssetArchiveService
{
    private readonly CanvasService _canvas;

    public GeneratedAssetArchiveService(CanvasService canvas)
    {
        _canvas = canvas;
    }

    /// <summary>按任务标记补登素材；未标记或无产物时直接返回。</summary>
    public async Task SaveAsync(TaskEntity task, CancellationToken cancellationToken = default)
    {
        if (task.ResultJSON.Length == 0)
        {
            return;
        }
        // 删除可能发生在任务被重读之后，必须取数据库里的最新 inputJson 再判断标记。
        TaskEntity? latest = await _canvas.Repository.TaskAsync(task.ID, cancellationToken).ConfigureAwait(false);
        string inputJSON = latest?.InputJSON ?? task.InputJSON;
        if (!TaskArchiveMarker.TryRead(inputJSON, out string canvasID, out string canvasTitle))
        {
            return;
        }

        List<JsonObject> assets = BuildAssets(task, canvasID, canvasTitle);
        if (assets.Count == 0)
        {
            return;
        }
        int saved = 0;
        foreach (JsonObject asset in assets)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(asset.ToJsonString());
                await _canvas.UserData
                    .UpsertUserAssetAsync(task.UserID, document.RootElement, CancellationToken.None)
                    .ConfigureAwait(false);
                saved++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await _canvas.Repository
                    .CreateTaskLogAsync(task.UserID, task.ID, "warn", "画布已删除，素材补登失败", error.Message,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        if (saved > 0)
        {
            await _canvas.Repository
                .CreateTaskLogAsync(task.UserID, task.ID, "info", "画布已删除，产物已保存到素材库",
                    canvasTitle, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>把生成结果展开成素材载荷。</summary>
    private static List<JsonObject> BuildAssets(TaskEntity task, string canvasID, string canvasTitle)
    {
        List<JsonObject> assets = [];
        JsonNode? result;
        try
        {
            result = JsonNode.Parse(task.ResultJSON);
        }
        catch (JsonException)
        {
            return assets;
        }
        if (result is not JsonObject payload)
        {
            return assets;
        }

        string prompt = task.Prompt.Trim();
        int index = 0;
        foreach (JsonObject media in MediaItems(payload, "images", "image"))
        {
            if (BuildAsset(task, media, "image", index++, prompt, canvasID, canvasTitle) is { } asset)
            {
                assets.Add(asset);
            }
        }
        foreach (JsonObject media in MediaItems(payload, "videos", "video"))
        {
            if (BuildAsset(task, media, "video", index++, prompt, canvasID, canvasTitle) is { } asset)
            {
                assets.Add(asset);
            }
        }
        foreach (JsonObject media in MediaItems(payload, "audios", "audio"))
        {
            if (BuildAsset(task, media, "audio", index++, prompt, canvasID, canvasTitle) is { } asset)
            {
                assets.Add(asset);
            }
        }
        if (TextContent(payload) is { Length: > 0 } text)
        {
            assets.Add(BuildTextAsset(task, text, prompt, canvasID, canvasTitle));
        }
        return assets;
    }

    /// <summary>取结果里的媒体条目：既接受数组，也接受单个对象。</summary>
    private static IEnumerable<JsonObject> MediaItems(JsonObject payload, string arrayKey, string objectKey)
    {
        JsonNode? node = payload[arrayKey] ?? payload[objectKey];
        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is JsonObject media)
                    {
                        yield return media;
                    }
                }
                break;
            case JsonObject media:
                yield return media;
                break;
        }
    }

    private static string TextContent(JsonObject payload)
    {
        foreach (string key in (string[])["text", "content", "result"])
        {
            if (payload[key] is JsonValue value && value.TryGetValue(out string? text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return "";
    }

    private static JsonObject? BuildAsset(
        TaskEntity task,
        JsonObject media,
        string kind,
        int index,
        string prompt,
        string canvasID,
        string canvasTitle)
    {
        string url = StringValue(media, "url");
        string dataUrl = StringValue(media, "dataUrl");
        string storageKey = StringValue(media, "storageKey");
        if (url.Length == 0 && dataUrl.Length == 0 && storageKey.Length == 0)
        {
            return null;
        }
        // 内联 data: 媒体不能进素材文档（素材契约禁止内嵌媒体），只能放弃。
        if (IsInlineDataURL(url) || IsInlineDataURL(dataUrl))
        {
            return null;
        }
        string locator = kind == "image"
            ? (dataUrl.Length > 0 ? dataUrl : url.Length > 0 ? url : "")
            : (url.Length > 0 ? url : dataUrl.Length > 0 ? dataUrl : "");
        string mimeType = StringValue(media, "mimeType");
        if (mimeType.Length == 0)
        {
            mimeType = kind switch
            {
                "video" => "video/mp4",
                "audio" => "audio/mpeg",
                _ => "image/png",
            };
        }

        JsonObject data = new()
        {
            ["storageKey"] = storageKey,
            ["bytes"] = NumberValue(media, "bytes"),
            ["mimeType"] = mimeType,
        };
        if (kind == "image")
        {
            data["dataUrl"] = locator;
            data["width"] = NumberValue(media, "width");
            data["height"] = NumberValue(media, "height");
        }
        else
        {
            data["url"] = locator;
            if (kind == "video")
            {
                data["width"] = NumberValue(media, "width");
                data["height"] = NumberValue(media, "height");
                data["durationMs"] = NumberValue(media, "durationMs");
            }
            else
            {
                data["durationMs"] = NumberValue(media, "durationMs");
            }
        }

        string poster = StringValue(media, "posterUrl");
        string coverUrl = kind switch
        {
            "image" => locator,
            "video" => poster.Length > 0 ? poster : StringValue(media, "thumbnailUrl"),
            _ => "",
        };

        return AssetPayload(task, kind, index, prompt, canvasID, canvasTitle, coverUrl, data);
    }

    private static JsonObject BuildTextAsset(
        TaskEntity task,
        string text,
        string prompt,
        string canvasID,
        string canvasTitle)
    {
        JsonObject data = new() { ["content"] = text };
        return AssetPayload(task, "text", 0, prompt, canvasID, canvasTitle, "", data);
    }

    private static JsonObject AssetPayload(
        TaskEntity task,
        string kind,
        int index,
        string prompt,
        string canvasID,
        string canvasTitle,
        string coverUrl,
        JsonObject data)
    {
        string fallback = kind switch
        {
            "image" => "画布图片",
            "video" => "画布视频",
            "audio" => "画布音频",
            _ => "画布文本",
        };
        string title = prompt.Length > 0
            ? KernelUtil.TruncateRunes(prompt, 40)
            : fallback;
        if (index > 0)
        {
            title = $"{title} ({index + 1})";
        }

        DateTime now = DateTime.UtcNow;
        return new JsonObject
        {
            ["id"] = $"asset-task-{task.ID}-{index}".Length <= AssetLibraryDomain.AssetConstraints.IDMaxLength
                ? $"asset-task-{task.ID}-{index}"
                : $"asset-task-{task.ID[..Math.Max(8, 60 - index.ToString().Length)]}-{index}",
            ["kind"] = kind,
            ["title"] = title,
            ["coverUrl"] = coverUrl,
            ["tags"] = new JsonArray("生成"),
            ["category"] = kind == "text" ? "other" : "material",
            ["status"] = AssetVersionStatus.AssetVersionStatusConfirmed,
            ["source"] = canvasTitle.Length > 0 ? $"已删除画布：{canvasTitle}" : "已删除画布",
            ["data"] = data,
            ["metadata"] = new JsonObject
            {
                ["source"] = "generation-task",
                ["taskId"] = task.ID,
                ["nodeId"] = MetadataValue(task.InputJSON, "nodeId"),
                ["sourceNodeId"] = MetadataValue(task.InputJSON, "sourceNodeId"),
                ["deletedCanvasId"] = canvasID,
                ["deletedCanvasTitle"] = canvasTitle,
                ["prompt"] = prompt,
                ["resourceKey"] = data["storageKey"]?.GetValue<string>() ?? "",
            },
            ["createdAt"] = now.ToString("O"),
            ["updatedAt"] = now.ToString("O"),
        };
    }

    private static string MetadataValue(string inputJSON, string key)
    {
        if (inputJSON.Length == 0)
        {
            return "";
        }
        try
        {
            return JsonNode.Parse(inputJSON) is JsonObject input &&
                   input["metadata"] is JsonObject metadata &&
                   metadata[key] is JsonValue value &&
                   value.TryGetValue(out string? text)
                ? text ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string StringValue(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    private static double NumberValue(JsonObject item, string key) =>
        item[key] is JsonValue value && value.TryGetValue(out double number) ? Math.Max(0, number) : 0;

    private static bool IsInlineDataURL(string value) =>
        value.StartsWith("data:image/", StringComparison.Ordinal)
        || value.StartsWith("data:video/", StringComparison.Ordinal)
        || value.StartsWith("data:audio/", StringComparison.Ordinal);
}
