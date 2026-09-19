#nullable enable
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

/// <summary>
/// 创作画布提交。对应 Go: <c>internal/app/creation_canvas.go</c> 的
/// CreateRunCanvas / CreationCanvasSnapshot / CommitCreationCanvas。
/// </summary>
/// <remarks>
/// 逐值 diff（Go reflect.DeepEqual）用规范化 JSON 文本比较（Ordinal 键序），
/// 与 Go 的语义等价（Go map 键序同样规范化）；数字保留原文（PENDING #47 家族取舍）。
/// </remarks>
public sealed partial class CreationRunService
{
    /// <summary>新增节点默认尺寸/标题。对应 Go: <c>creationAddedNode</c> + capability 注册表。</summary>
    private static readonly Dictionary<string, (double Width, double Height, string Label)> NodeDefaults =
        new(StringComparer.Ordinal)
        {
            ["text"] = (340, 240, "文本"),
            ["markdown"] = (420, 320, "Markdown"),
            ["frame"] = (760, 520, "背板"),
            ["batch-table"] = (900, 520, "批量创作表"),
            ["script"] = (920, 360, "分镜脚本"),
            ["image"] = (720, 405, "图片"),
            ["video"] = (720, 405, "视频"),
            ["audio"] = (340, 120, "音频"),
        };

    /// <summary>创建/关联画布。对应 Go: <c>CreateRunCanvas</c>。</summary>
    public async Task<Dictionary<string, JsonElement?>> CreateCanvasAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        return await _repository.MutateCreationRunAsync(
            userId, runId,
            async (run, tx) =>
            {
                ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
                if (run.Status is "paused" or "cancelled")
                {
                    throw CreationConflict("请先恢复创作任务");
                }
                if (run.ApprovedAt is null)
                {
                    throw CreationConflict("请先确认方案");
                }
                if (run.CanvasID.Length > 0)
                {
                    if (await tx.CanvasProjectForUserAsync(userId, run.CanvasID, cancellationToken)
                            .ConfigureAwait(false) is null)
                    {
                        throw CreationConflict("已关联画布失效，请核对后继续");
                    }
                    return CanvasOut(run, run.CanvasID);
                }
                DateTime now = DateTime.UtcNow;
                string cid = IdGenerator.NewId();
                Dictionary<string, JsonElement> doc = new(StringComparer.Ordinal)
                {
                    ["id"] = JsonSerializer.SerializeToElement(cid),
                    ["title"] = JsonSerializer.SerializeToElement("智能创作"),
                    ["createdAt"] = JsonSerializer.SerializeToElement(
                        ProjectService.FormatRfc3339Nano(now)),
                    ["updatedAt"] = JsonSerializer.SerializeToElement(
                        ProjectService.FormatRfc3339Nano(now)),
                    ["nodes"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    ["connections"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    ["chatSessions"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    ["activeChatId"] = JsonSerializer.SerializeToElement<JsonElement?>(null),
                    ["backgroundMode"] = JsonSerializer.SerializeToElement("dots"),
                    ["showImageInfo"] = JsonSerializer.SerializeToElement(true),
                    ["viewport"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                    {
                        ["x"] = JsonSerializer.SerializeToElement(0L),
                        ["y"] = JsonSerializer.SerializeToElement(0L),
                        ["k"] = JsonSerializer.SerializeToElement(1L),
                    }),
                    ["directorScenes"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                };
                string raw = JsonSerializer.Serialize(
                    ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(doc)),
                    ProjectCharacterService.GoPayloadOptions);
                CanvasProject canvas = new()
                {
                    ID = cid,
                    UserID = userId,
                    Title = "智能创作",
                    PayloadJSON = raw,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                Platform.RuntimePolicySetting policy = _runtimePolicy.Current();
                UserStorageUsage usage = await tx
                    .UserStorageUsageAsync(userId, cancellationToken).ConfigureAwait(false);
                ValidateStructuredCanvasQuota(usage, creating: true, delta: raw.Length, policy);
                await tx.CreateCanvasProjectAsync(canvas, cancellationToken).ConfigureAwait(false);
                run.CanvasID = cid;
                run.Revision++;
                run.Status = "waiting_canvas";
                return CanvasOut(run, cid);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画布快照。对应 Go: <c>CreationCanvasSnapshot</c>。</summary>
    public async Task<Dictionary<string, JsonElement?>> CanvasSnapshotAsync(
        string userId, string runId, CancellationToken cancellationToken = default)
    {
        CreationRun run = await LoadRunAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        CanvasProject? canvas = await _repository
            .CanvasProjectForUserAsync(userId, run.CanvasID, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();
        JsonElement doc = ParseJson(canvas.PayloadJSON);
        return new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["document"] = doc,
            ["snapshotHash"] = JsonSerializer.SerializeToElement(
                CreationHash(ParseJson(canvas.PayloadJSON))),
        };
    }

    /// <summary>提交画布（获批 ops 范围校验）。对应 Go: <c>CommitCreationCanvas</c>。</summary>
    public async Task<Dictionary<string, JsonElement?>> CommitCanvasAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.Document is null || request.Document.Value.ValueKind != JsonValueKind.Object)
        {
            throw AppError.BadAuthRequest("画布文档格式无效");
        }
        UserDataService.ValidateSyncedPayload(request.Document.Value.GetRawText(), "画布");
        ValidateCreationJson(request.Document.Value.GetRawText());
        await (UserData ?? throw new InvalidOperationException("creation user data gate")).ValidateCanvasMediaAssetsPublicAsync(
            userId, request.Document.Value.GetRawText(), cancellationToken).ConfigureAwait(false);
        Dictionary<string, JsonElement> after = CanvasMap(request.Document.Value);

        return await _repository.MutateCreationRunAsync(
            userId, runId,
            async (run, tx) =>
            {
                ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
                if (run.Status is "paused" or "cancelled")
                {
                    throw CreationConflict("请先恢复创作任务");
                }
                if (run.ApprovedAt is null)
                {
                    throw CreationConflict("当前方案尚未批准");
                }
                CanvasProject? canvas = await _repository
                    .CanvasProjectForUserAsync(userId, run.CanvasID, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("record not found");
                Dictionary<string, JsonElement> before = CanvasMap(ParseJson(canvas.PayloadJSON));
                if (CreationHash(ParseJson(JsonSerializer.Serialize(before))) != request.ExpectedSnapshotHash)
                {
                    throw CreationConflict("画布已变化，请重新读取后核对");
                }
                List<CreationCanvasOpDto> ops = JsonSerializer.Deserialize<List<CreationCanvasOpDto>>(
                    run.ApprovedOperationsJSON,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                await ValidateCreationCanvasDiffAsync(tx, userId, run, before, after, ops, cancellationToken)
                    .ConfigureAwait(false);
                string previous = canvas.PayloadJSON;
                canvas.PayloadJSON = JsonSerializer.Serialize(
                    ProjectCharacterService.SortedElement(request.Document.Value),
                    ProjectCharacterService.GoPayloadOptions);
                Platform.RuntimePolicySetting policy = _runtimePolicy.Current();
                UserStorageUsage usage = await tx
                    .UserStorageUsageAsync(userId, cancellationToken).ConfigureAwait(false);
                ValidateStructuredCanvasQuota(
                    usage, creating: false,
                    delta: canvas.PayloadJSON.Length - previous.Length, policy);
                await tx.CompareSaveCreationCanvasAsync(
                    canvas, previous, cancellationToken).ConfigureAwait(false);
                return new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
                {
                    ["snapshotHash"] = JsonSerializer.SerializeToElement(
                        CreationHash(ParseJson(JsonSerializer.Serialize(after)))),
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ diff 校验

    /// <summary>获批范围 diff 校验。对应 Go: <c>validateCreationCanvasDiff</c>。</summary>
    private static async Task ValidateCreationCanvasDiffAsync(
        CreationRunMutationContext tx,
        string userId,
        CreationRun run,
        Dictionary<string, JsonElement> before,
        Dictionary<string, JsonElement> after,
        List<CreationCanvasOpDto> ops,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, JsonElement>> baselineNodes = new(StringComparer.Ordinal);
        if (run.ApprovedCanvasJSON.Length > 0)
        {
            JsonElement baseline = ParseJson(run.ApprovedCanvasJSON);
            if (baseline.ValueKind == JsonValueKind.Object
                && baseline.TryGetProperty("nodes", out JsonElement baseNodes))
            {
                baselineNodes = NodeMap(baseNodes);
            }
        }
        // 顶层字段（nodes/connections/updatedAt 之外）不得变化或新增。
        foreach (string key in before.Keys)
        {
            if (key is "nodes" or "connections" or "updatedAt")
            {
                continue;
            }
            after.TryGetValue(key, out JsonElement nextValue);
            if (!SameJson(before[key], nextValue))
            {
                throw CreationConflict("画布提交修改了方案外内容: " + key);
            }
        }
        foreach (string key in after.Keys)
        {
            if (!before.ContainsKey(key) && key != "updatedAt")
            {
                throw CreationConflict("画布提交包含方案外字段");
            }
        }

        Dictionary<string, Dictionary<string, JsonElement>> oldNodes =
            before.TryGetValue("nodes", out JsonElement oldN) ? NodeMap(oldN) : [];
        Dictionary<string, Dictionary<string, JsonElement>> newNodes =
            after.TryGetValue("nodes", out JsonElement newN) ? NodeMap(newN) : [];
        Dictionary<string, Dictionary<string, JsonElement>> oldEdges =
            before.TryGetValue("connections", out JsonElement oldE) ? NodeMap(oldE) : [];
        Dictionary<string, Dictionary<string, JsonElement>> newEdges =
            after.TryGetValue("connections", out JsonElement newE) ? NodeMap(newE) : [];

        Dictionary<string, CreationCanvasOpDto> add = new(StringComparer.Ordinal);
        Dictionary<string, List<CreationCanvasOpDto>> updates = new(StringComparer.Ordinal);
        Dictionary<string, CreationCanvasOpDto> edges = new(StringComparer.Ordinal);
        HashSet<string> approvedIds = new(StringComparer.Ordinal);
        foreach (CreationCanvasOpDto op in ops)
        {
            switch (op.Type)
            {
                case "add_node":
                    add[op.ID] = op;
                    approvedIds.Add(op.ID);
                    break;
                case "update_node":
                    if (!updates.TryGetValue(op.ID, out List<CreationCanvasOpDto>? list))
                    {
                        list = [];
                        updates[op.ID] = list;
                    }
                    list.Add(op);
                    approvedIds.Add(op.ID);
                    break;
                case "connect_nodes":
                    edges[op.ID] = op;
                    break;
            }
        }

        foreach ((string id, Dictionary<string, JsonElement> oldNode) in oldNodes)
        {
            if (!newNodes.TryGetValue(id, out Dictionary<string, JsonElement>? next))
            {
                throw CreationConflict("不允许删除已有节点");
            }
            if (SameJson(JsonSerializer.SerializeToElement(oldNode), JsonSerializer.SerializeToElement(next)))
            {
                continue;
            }
            Dictionary<string, JsonElement> metadata = MetaMap(
                oldNode.TryGetValue("metadata", out JsonElement m) ? m : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()));
            Dictionary<string, JsonElement> expected = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> pair in oldNode)
            {
                expected[pair.Key] = pair.Value.Clone();
            }
            if (updates.TryGetValue(id, out List<CreationCanvasOpDto>? nodeUpdates))
            {
                foreach (CreationCanvasOpDto op in nodeUpdates)
                {
                    if (baselineNodes.TryGetValue(id, out Dictionary<string, JsonElement>? baseline))
                    {
                        foreach (string key in op.Patch?.Keys ?? (IEnumerable<string>)Array.Empty<string>())
                        {
                            if (key == "metadata")
                            {
                                continue;
                            }
                            oldNode.TryGetValue(key, out JsonElement beforeValue);
                            baseline.TryGetValue(key, out JsonElement baselineValue);
                            if (!SameJson(beforeValue, baselineValue)
                                && op.Patch is not null
                                && op.Patch.TryGetValue(key, out JsonElement want)
                                && !SameJson(beforeValue, want))
                            {
                                throw CreationConflict("节点已被手工编辑，请重新确认修改范围");
                            }
                        }
                        Dictionary<string, JsonElement> baselineMeta = MetaMap(
                            baseline.TryGetValue("metadata", out JsonElement bm) ? bm : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()));
                        Dictionary<string, JsonElement> patchMeta = MetaMap(
                            op.Patch is not null && op.Patch.TryGetValue("metadata", out JsonElement pm)
                                ? pm
                                : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()));
                        foreach (string key in MergeKeys(patchMeta, op.Metadata))
                        {
                            metadata.TryGetValue(key, out JsonElement beforeMetaValue);
                            baselineMeta.TryGetValue(key, out JsonElement baselineValue);
                            JsonElement? want = null;
                            if (patchMeta.TryGetValue(key, out JsonElement patchValue))
                            {
                                want = patchValue;
                            }
                            else if (op.Metadata is not null && op.Metadata.TryGetValue(key, out JsonElement metaValue))
                            {
                                want = metaValue;
                            }
                            if (!SameJson(beforeMetaValue, baselineValue)
                                && (want is null || !SameJson(beforeMetaValue, want.Value)))
                            {
                                throw CreationConflict("节点内容已被手工编辑，请重新确认");
                            }
                        }
                    }
                    if (op.Patch is not null)
                    {
                        foreach (KeyValuePair<string, JsonElement> patch in op.Patch)
                        {
                            if (patch.Key == "metadata")
                            {
                                foreach (KeyValuePair<string, JsonElement> pair in MetaMap(patch.Value))
                                {
                                    metadata[pair.Key] = pair.Value.Clone();
                                }
                            }
                            else
                            {
                                expected[patch.Key] = patch.Value.Clone();
                            }
                        }
                    }
                    if (op.Metadata is not null)
                    {
                        foreach (KeyValuePair<string, JsonElement> pair in op.Metadata)
                        {
                            metadata[pair.Key] = pair.Value.Clone();
                        }
                    }
                }
            }
            expected["metadata"] = JsonSerializer.SerializeToElement(metadata);
            if (SameJson(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(next)))
            {
                continue;
            }
            if (!approvedIds.Contains(id))
            {
                throw CreationConflict("不能修改方案外节点");
            }
            Dictionary<string, JsonElement> actualMeta = MetaMap(
                next.TryGetValue("metadata", out JsonElement am) ? am : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>()));
            Dictionary<string, JsonElement> withoutMeta = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> pair in next)
            {
                withoutMeta[pair.Key] = pair.Value.Clone();
            }
            withoutMeta["metadata"] = JsonSerializer.SerializeToElement(metadata);
            if (!SameJson(JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(withoutMeta)))
            {
                throw CreationConflict("节点字段超出批准范围");
            }
            await ValidateCreationResultMetadataAsync(
                tx, userId, run.ID, id, metadata, actualMeta, cancellationToken).ConfigureAwait(false);
        }

        foreach ((string id, Dictionary<string, JsonElement> node) in newNodes)
        {
            if (oldNodes.ContainsKey(id))
            {
                continue;
            }
            if (!add.TryGetValue(id, out CreationCanvasOpDto? op))
            {
                throw CreationConflict("节点未获方案批准");
            }
            Dictionary<string, JsonElement> expectedNode = AddedNode(op);
            if (!SameJson(expectedNode, node))
            {
                throw CreationConflict("新增节点参数与批准方案不同");
            }
        }

        foreach ((string id, Dictionary<string, JsonElement> edge) in oldEdges)
        {
            newEdges.TryGetValue(id, out Dictionary<string, JsonElement>? nextEdge);
            if (!SameJson(edge, nextEdge))
            {
                throw CreationConflict("不允许修改或删除已有连线");
            }
        }
        foreach ((string id, Dictionary<string, JsonElement> _) in newEdges)
        {
            if (oldEdges.ContainsKey(id))
            {
                continue;
            }
            if (!edges.TryGetValue(id, out CreationCanvasOpDto? op))
            {
                throw CreationConflict("连线未获批准");
            }
            Dictionary<string, JsonElement> expectedEdge = new(StringComparer.Ordinal)
            {
                ["id"] = JsonSerializer.SerializeToElement(id),
                ["fromNodeId"] = JsonSerializer.SerializeToElement(op.FromNodeID),
                ["toNodeId"] = JsonSerializer.SerializeToElement(op.ToNodeID),
            };
            if (op.FromNodeID.Length > 0 && op.FromHandleID.Length > 0)
            {
                expectedEdge["fromHandleId"] = JsonSerializer.SerializeToElement(op.FromHandleID);
            }
            if (op.ToHandleID.Length > 0)
            {
                expectedEdge["toHandleId"] = JsonSerializer.SerializeToElement(op.ToHandleID);
            }
            bool endpointsOk = newEdges.ContainsKey(op.FromNodeID) || op.FromNodeID.Length == 0;
            _ = endpointsOk;
            JsonElement edgeValue = newEdges.TryGetValue(id, out Dictionary<string, JsonElement> ev)
                ? JsonSerializer.SerializeToElement(ev)
                : JsonSerializer.SerializeToElement(false);
            if (!SameJson(JsonSerializer.SerializeToElement(expectedEdge), edgeValue)
                || !newNodes.ContainsKey(op.FromNodeID)
                || !newNodes.ContainsKey(op.ToNodeID))
            {
                throw CreationConflict("连线参数与批准范围不同");
            }
        }
    }

    /// <summary>结果回写校验。对应 Go: <c>validateCreationResultMetadata</c>。</summary>
    private static async Task ValidateCreationResultMetadataAsync(
        CreationRunMutationContext tx,
        string userId,
        string runId,
        string nodeId,
        Dictionary<string, JsonElement> beforeMeta,
        Dictionary<string, JsonElement> afterMeta,
        CancellationToken cancellationToken)
    {
        string taskID = MetaString(afterMeta, "taskId");
        if (taskID.Length == 0)
        {
            taskID = MetaString(afterMeta, "generationTaskId");
        }
        if (taskID.Length == 0)
        {
            throw CreationConflict("结果回写缺少真实任务");
        }
        IReadOnlyList<CreationSubmission> items = await tx
            .CreationSubmissionsAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        bool found = false;
        foreach (CreationSubmission item in items)
        {
            if (item.TaskID is null || item.TaskID != taskID)
            {
                continue;
            }
            CreateTaskRequestDto? request = JsonSerializer.Deserialize<CreateTaskRequestDto>(
                item.RequestJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (request?.Input is not null && request.Input.TryGetValue("nodeId", out JsonElement nodeEl)
                && nodeEl.ValueKind == JsonValueKind.String
                && nodeEl.GetString() == nodeId)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            throw CreationConflict("任务不属于当前创作节点");
        }
        TaskEntity? task = await tx.TaskForUserAsync(userId, taskID, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();
        if (task.Status != TaskStatus.TaskStatusSucceeded)
        {
            throw CreationConflict("生成任务尚未成功");
        }
        string storageKey = MetaString(afterMeta, "storageKey");
        if (!storageKey.StartsWith("resource:", StringComparison.Ordinal)
            || !task.ResultJSON.Contains(storageKey, StringComparison.Ordinal))
        {
            throw CreationConflict("回写素材不是任务生成资源");
        }
    }

    /// <summary>新增节点期望形态。对应 Go: <c>creationAddedNode</c>。</summary>
    private static Dictionary<string, JsonElement> AddedNode(CreationCanvasOpDto op)
    {
        (double width, double height, string title) = NodeDefaults.TryGetValue(op.NodeType, out var d)
            ? d
            : (340, 240, "Note");
        Dictionary<string, JsonElement> metadata = new(StringComparer.Ordinal)
        {
            ["content"] = JsonSerializer.SerializeToElement(""),
            ["status"] = JsonSerializer.SerializeToElement("idle"),
        };
        if (op.Width is not null)
        {
            width = op.Width.Value;
        }
        if (op.Height is not null)
        {
            height = op.Height.Value;
        }
        if (op.Title.Length > 0)
        {
            title = op.Title;
        }
        Dictionary<string, JsonElement> position = new(StringComparer.Ordinal)
        {
            ["x"] = JsonSerializer.SerializeToElement(0L),
            ["y"] = JsonSerializer.SerializeToElement(0L),
        };
        if (op.Position is not null)
        {
            position = new Dictionary<string, JsonElement>(op.Position, StringComparer.Ordinal);
        }
        else
        {
            if (op.X is not null)
            {
                position["x"] = JsonSerializer.SerializeToElement(op.X.Value);
            }
            if (op.Y is not null)
            {
                position["y"] = JsonSerializer.SerializeToElement(op.Y.Value);
            }
        }
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal)
        {
            ["id"] = JsonSerializer.SerializeToElement(op.ID),
            ["type"] = JsonSerializer.SerializeToElement(op.NodeType),
            ["title"] = JsonSerializer.SerializeToElement(title),
            ["position"] = JsonSerializer.SerializeToElement(position),
            ["width"] = JsonSerializer.SerializeToElement(width),
            ["height"] = JsonSerializer.SerializeToElement(height),
            ["metadata"] = JsonSerializer.SerializeToElement(MergeMaps(metadata, op.Metadata)),
        };
        return result;
    }

    private static Dictionary<string, JsonElement> MergeMaps(
        Dictionary<string, JsonElement> left, Dictionary<string, JsonElement>? right)
    {
        Dictionary<string, JsonElement> merged = new(left, StringComparer.Ordinal);
        if (right is not null)
        {
            foreach (KeyValuePair<string, JsonElement> pair in right)
            {
                merged[pair.Key] = pair.Value.Clone();
            }
        }
        return merged;
    }

    private static IEnumerable<string> MergeKeys(
        Dictionary<string, JsonElement> left, Dictionary<string, JsonElement>? right)
    {
        HashSet<string> keys = new(left.Keys, StringComparer.Ordinal);
        if (right is not null)
        {
            keys.UnionWith(right.Keys);
        }
        return keys;
    }

    /// <summary>把 JSON 对象展开为字典。对应 Go: map[string]any。</summary>
    private static Dictionary<string, JsonElement> MetaMap(JsonElement value)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        return result;
    }

    /// <summary>规范化 JSON 相等（字典重载）。</summary>
    private static bool SameJson(
        Dictionary<string, JsonElement> left, Dictionary<string, JsonElement> right) =>
        SameJson(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));

    /// <summary>
    /// JSON 语义相等：递归比较（对象按键名配对、数组按序、字符串按解码值），
    /// 不受键序与转义形式影响。对应 Go: <c>reflect.DeepEqual</c> 对解析后 any 的比较。
    /// </summary>
    private static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            // 数字/布尔等标量在 Go 里都归一为 float64/bool；这里按 token 类型比较。
            return false;
        }
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                Dictionary<string, JsonElement> leftMap = new(StringComparer.Ordinal);
                foreach (JsonProperty property in left.EnumerateObject())
                {
                    leftMap[property.Name] = property.Value;
                }
                Dictionary<string, JsonElement> rightMap = new(StringComparer.Ordinal);
                foreach (JsonProperty property in right.EnumerateObject())
                {
                    rightMap[property.Name] = property.Value;
                }
                if (leftMap.Count != rightMap.Count)
                {
                    return false;
                }
                foreach (KeyValuePair<string, JsonElement> pair in leftMap)
                {
                    if (!rightMap.TryGetValue(pair.Key, out JsonElement rightValue)
                        || !SameJson(pair.Value, rightValue))
                    {
                        return false;
                    }
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
                JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
                while (leftItems.MoveNext())
                {
                    if (!rightItems.MoveNext() || !SameJson(leftItems.Current, rightItems.Current))
                    {
                        return false;
                    }
                }
                return !rightItems.MoveNext();
            }
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return left.GetRawText() == right.GetRawText()
                    || left.GetDouble() == right.GetDouble();
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            default:
                return false;
        }
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> NodeMap(JsonElement value)
    {
        Dictionary<string, Dictionary<string, JsonElement>> result = new(StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string id = item.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? ""
                        : "";
                if (id.Length > 0)
                {
                    Dictionary<string, JsonElement> copy = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in item.EnumerateObject())
                    {
                        copy[property.Name] = property.Value.Clone();
                    }
                    result[id] = copy;
                }
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> NodeMap(Dictionary<string, JsonElement> wrapped)
    {
        if (wrapped.TryGetValue("nodes", out JsonElement nodes))
        {
            return NodeMap(nodes);
        }
        return [];
    }

    private static Dictionary<string, JsonElement> CanvasMap(JsonElement document)
    {
        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }
        return map;
    }

    private static Dictionary<string, JsonElement?> CanvasOut(CreationRun run, string canvasId) => new(StringComparer.Ordinal)
    {
        ["run"] = JsonSerializer.SerializeToElement(RunOutput(run)),
        ["canvasId"] = JsonSerializer.SerializeToElement(canvasId),
    };

    private static void ValidateStructuredCanvasQuota(
        UserStorageUsage usage, bool creating, long delta, Platform.RuntimePolicySetting policy)
    {
        long structuredBytes = usage.AssetBytes + usage.CanvasBytes;
        long limitMB = policy.Resource.StructuredDataMB;
        long deltaValue = creating ? delta : delta;
        if (!creating && deltaValue < 0)
        {
            deltaValue = 0;
        }
        _ = creating;
        if (structuredBytes + deltaValue > limitMB * 1024L * 1024L)
        {
            throw AppError.QuotaExceeded(
                $"账号画布和素材数据已达到 {limitMB}MB 上限，请先删除不需要的内容");
        }
    }
}
