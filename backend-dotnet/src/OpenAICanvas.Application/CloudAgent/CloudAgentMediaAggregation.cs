using System.Text.Json.Nodes;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>
/// 媒体生成的「同一来源归并 + 产物历史」。
/// </summary>
/// <remarks>
/// <para>
/// 产品要求：同一个生成节点多次执行任务时，不要每次都新增图片节点和连线，而是在原节点上
/// 展示最新生成的图片，同时保留历史生成结果、支持切换查看。
/// </para>
/// <para>
/// 原实现每次生成都新建节点（<c>MediaDocumentAsync</c> 明确拒绝复用已完成节点：
/// 「目标不是可续用的未提交媒体草稿…不要循环创建替代节点」），于是同一来源执行 N 次
/// 就在画布上散出 N 个节点 + N 条 <c>agent-*</c> 连线。
/// </para>
/// <para>
/// 现在的规则：**同一来源 key 的多次生成（含一次出多张）都落到同一个结果节点上**。
/// 「当前展示的产物」仍放在节点顶层字段（<c>content</c>/<c>storageKey</c>/…），
/// 历次旧产物按最新在前压进 <c>metadata.mediaHistory</c>；切换即把选中项与顶层字段对调。
/// 前端 <c>web/src/lib/canvas/canvas-media-history.ts</c> 是同一套契约的另一半实现。
/// </para>
/// </remarks>
public static class CloudAgentMediaAggregation
{
    /// <summary>历史条数上限。画布文档是整体 PUT 的 JSON，必须防元数据无限膨胀。</summary>
    public const int HistoryLimit = 30;

    /// <summary>一个节点最多记多少个任务 ID（归并后一个节点会先后承接多个任务）。</summary>
    public const int TaskIDLimit = 20;

    /// <summary>当前展示的产物字段：切换历史版本时与历史条目对调的正是这些字段。</summary>
    private static readonly string[] MediaKeys =
    [
        "content", "storageKey", "previewContent", "videoPreview",
        "naturalWidth", "naturalHeight", "bytes", "mimeType", "durationMs",
    ];

    /// <summary>重写节点元数据时必须整段保留的字段（历史与展示中的产物）。</summary>
    private static readonly string[] PreservedKeys =
    [
        .. MediaKeys, "mediaHistory", "generationSourceKey",
    ];

    /// <summary>
    /// 生成来源 key：同一画布内 key 相同的多次生成都归并到同一个结果节点。
    /// </summary>
    /// <remarks>
    /// 归并只认「基于哪张图」：<b>有参考图就以参考图集合为准</b>——同一张图不管走 Agent
    /// 生成还是手动生成，都落到同一个结果节点；没有参考图（文生图）才回落到来源节点。
    /// 生成方式与来源节点本身不再参与 key（此前 Agent 生成与手动生成因为来源节点段不同，
    /// 被拆成了两个节点）。规则必须与前端 <c>canvasMediaSourceKey</c> <b>逐字一致</b>。
    /// 参考图排序去重后再拼，保证顺序无关。
    /// </remarks>
    public static string SourceKey(CloudAgentMediaArgs args)
    {
        List<string> references = [.. args.ReferenceNodeIDs
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)];
        string anchor = references.Count > 0
            ? string.Join(',', references)
            : args.SourceNodeID.Length > 0 ? args.SourceNodeID : "-";
        return string.Join('|', "v1", args.Mode, anchor);
    }

    /// <summary>
    /// 从 key 里取出「媒体大类 + 归并锚点」，忽略来源节点段（兼容旧四段格式）。
    /// </summary>
    /// <remarks>
    /// 老画布上的节点写的是 <c>v1|mode|sourceNodeId|refs</c> 四段旧 key，只比整串
    /// 会永远匹配不上，同一张图于是又长出一个新节点。
    /// </remarks>
    public static string SourceIdentity(string key)
    {
        string[] parts = key.Split('|');
        if (parts.Length == 4 && parts[0] == "v1")
        {
            string references = parts[3];
            return string.Join('|', parts[1],
                references.Length > 0 && references != "-" ? references : parts[2].Length > 0 ? parts[2] : "-");
        }
        if (parts.Length == 3 && parts[0] == "v1")
        {
            return string.Join('|', parts[1], parts[2].Length > 0 ? parts[2] : "-");
        }
        return key;
    }

    /// <summary>在画布上找同一来源已经建好的结果节点（没有则返回空串）。</summary>
    /// <remarks>
    /// 只认带 <c>generationSourceKey</c> 标记的节点：那是本服务自己建的归并目标。
    /// 取画布数组里最后一个命中的（最近追加），并跳过被锁定的节点。
    /// </remarks>
    public static string ResolveTargetNodeID(JsonObject doc, CloudAgentMediaArgs args)
    {
        // 只比「媒体大类 + 归并锚点」：同一张参考图的不同生成路径（手动/Agent）必须命中同一个节点。
        string identity = SourceIdentity(SourceKey(args));
        if (identity.Length == 0)
        {
            return "";
        }
        string found = "";
        foreach (JsonObject node in CloudAgentJsonHelpers.Maps(CloudAgentJsonHelpers.Get(doc, "nodes")))
        {
            if (node["metadata"] is not JsonObject metadata)
            {
                continue;
            }
            if (!string.Equals(SourceIdentity(Field(metadata, "generationSourceKey")), identity, StringComparison.Ordinal))
            {
                continue;
            }
            if (IsTruthy(metadata, "locked"))
            {
                continue;
            }
            string id = CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, "id"));
            if (id.Length > 0)
            {
                found = id;
            }
        }
        return found;
    }

    /// <summary>
    /// 把展示中的产物压进历史（最新在前）并从顶层字段移除。返回是否有产物入栈。
    /// </summary>
    /// <remarks>
    /// 幂等：顶层没有产物时原样返回，因此草稿与提交两次写入只会入栈一次；
    /// 同一 <c>storageKey</c> 重复入栈（回写重试）会被识别并跳过。
    /// </remarks>
    public static bool PushCurrentMediaToHistory(JsonObject metadata, string title)
    {
        if (!HasCurrentMedia(metadata))
        {
            return false;
        }
        JsonObject entry = new()
        {
            ["id"] = "hist-" + Guid.NewGuid().ToString("N")[..16],
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        foreach (string key in MediaKeys)
        {
            if (metadata.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                entry[key] = value.DeepClone();
            }
        }
        foreach (string key in new[] { "prompt", "taskId" })
        {
            if (metadata.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                entry[key] = value.DeepClone();
            }
        }
        if (title.Length > 0)
        {
            entry["title"] = title;
        }
        string storageKey = Field(entry, "storageKey");
        JsonArray history = metadata["mediaHistory"] as JsonArray ?? [];
        bool duplicated = history.Count > 0
            && storageKey.Length > 0
            && string.Equals(Field(history[0] as JsonObject, "storageKey"), storageKey, StringComparison.Ordinal);
        if (!duplicated)
        {
            history.Insert(0, entry);
        }
        while (history.Count > HistoryLimit)
        {
            history.RemoveAt(history.Count - 1);
        }
        metadata["mediaHistory"] = history;
        foreach (string key in MediaKeys)
        {
            metadata.Remove(key);
        }
        return true;
    }

    /// <summary>节点当前是否有可展示的产物。</summary>
    public static bool HasCurrentMedia(JsonObject? metadata) =>
        metadata is not null
        && (Field(metadata, "storageKey").Length > 0
            || Field(metadata, "content").Length > 0
            || Field(metadata["videoPreview"] as JsonObject, "storageKey").Length > 0);

    /// <summary>
    /// 重写节点元数据时保留历史与展示中的产物。
    /// </summary>
    /// <remarks>
    /// <c>CreateMediaNodeAsync</c> 原本整段替换 <c>metadata</c>，复用一个已完成节点会把
    /// 展示中的图和历史一起抹掉。归并之后一个节点会先后承接草稿与提交，
    /// 这里把该留的字段搬过去，并维护 <c>mediaTaskIds</c> 任务账本
    /// （完成回写按它匹配节点，不能只看单个 <c>taskId</c>）。
    /// </remarks>
    public static void PreserveAcrossRewrite(JsonObject previous, JsonObject next, string taskID)
    {
        foreach (string key in PreservedKeys)
        {
            if (previous.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                next[key] = value.DeepClone();
            }
        }
        List<string> taskIDs = [];
        if (previous["mediaTaskIds"] is JsonArray prior)
        {
            foreach (JsonNode? item in prior)
            {
                string id = TaskIDOf(item);
                if (id.Length > 0 && !taskIDs.Contains(id, StringComparer.Ordinal))
                {
                    taskIDs.Add(id);
                }
            }
        }
        if (taskID.Length > 0 && !taskIDs.Contains(taskID, StringComparer.Ordinal))
        {
            taskIDs.Add(taskID);
        }
        if (taskIDs.Count > TaskIDLimit)
        {
            taskIDs.RemoveRange(0, taskIDs.Count - TaskIDLimit);
        }
        if (taskIDs.Count > 0)
        {
            next["mediaTaskIds"] = new JsonArray([.. taskIDs.Select(id => (JsonNode?)JsonValue.Create(id))]);
        }
        else
        {
            next.Remove("mediaTaskIds");
        }
    }

    /// <summary>节点的 <c>mediaTaskIds</c> 是否包含该任务（完成回写匹配用）。</summary>
    public static bool OwnsTask(JsonObject metadata, string taskID)
    {
        if (taskID.Length == 0)
        {
            return false;
        }
        if (string.Equals(Field(metadata, "taskId"), taskID, StringComparison.Ordinal))
        {
            return true;
        }
        if (metadata["mediaTaskIds"] is not JsonArray ids)
        {
            return false;
        }
        foreach (JsonNode? item in ids)
        {
            if (string.Equals(TaskIDOf(item), taskID, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string TaskIDOf(JsonNode? node) =>
        node is null ? "" : node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString().Trim('"');

    private static string Field(JsonObject? node, string key) =>
        node is null ? "" : CloudAgentJsonHelpers.StringValue(CloudAgentJsonHelpers.Get(node, key));

    private static bool IsTruthy(JsonObject node, string key)
    {
        JsonNode? value = CloudAgentJsonHelpers.Get(node, key);
        if (value is null)
        {
            return false;
        }
        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => value.GetValue<string>() is "true" or "1",
            _ => false,
        };
    }
}
