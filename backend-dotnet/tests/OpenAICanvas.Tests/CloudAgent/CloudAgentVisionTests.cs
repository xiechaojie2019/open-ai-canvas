#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// Agent 看图的回执/图片配对与轮内裁剪契约测试。
/// 对应 Go: <c>cloud_agent_vision_pairing_test.go</c> / <c>cloud_agent_vision_delivery_test.go</c>（核心不变式）。
/// </summary>
public sealed class CloudAgentVisionTests
{
    // ------------------------------------------------------------ 配对不变式

    /// <summary>每个 assistant 声明的 tool_call_id 必须在下一条非 tool 消息前的连续 tool 消息里按序回应。</summary>
    private static string? PairingViolation(IReadOnlyList<Dictionary<string, JsonElement>> messages)
    {
        for (int index = 0; index < messages.Count; index++)
        {
            Dictionary<string, JsonElement> message = messages[index];
            if (!message.TryGetValue("role", out JsonElement roleElement)
                || roleElement.GetString() != "assistant"
                || !message.TryGetValue("tool_calls", out JsonElement calls)
                || calls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            List<string> declared = [];
            foreach (JsonElement call in calls.EnumerateArray())
            {
                if (call.ValueKind == JsonValueKind.Object
                    && call.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    declared.Add(id.GetString()!);
                }
            }
            if (declared.Count == 0)
            {
                continue;
            }
            List<string> answered = [];
            foreach (Dictionary<string, JsonElement> follow in messages.Skip(index + 1))
            {
                if (!follow.TryGetValue("role", out JsonElement followRole)
                    || followRole.GetString() != "tool")
                {
                    break;
                }
                if (follow.TryGetValue("tool_call_id", out JsonElement followID)
                    && followID.ValueKind == JsonValueKind.String)
                {
                    answered.Add(followID.GetString()!);
                }
            }
            if (answered.Count < declared.Count)
            {
                return $"assistant#{index} declared {string.Join(",", declared)} but only {answered.Count} tool messages follow";
            }
            for (int position = 0; position < declared.Count; position++)
            {
                if (answered[position] != declared[position])
                {
                    return $"assistant#{index} tool results are out of order";
                }
            }
        }
        return null;
    }

    private static int ImagePartCount(Dictionary<string, JsonElement> message)
    {
        if (!message.TryGetValue("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }
        int count = 0;
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "image_url")
            {
                count++;
            }
        }
        return count;
    }

    private static CloudAgentImageInspectionDto Inspection(string nodeID) => new()
    {
        Receipt = new JsonObject { ["nodeId"] = nodeID, ["mimeType"] = "image/png", ["title"] = nodeID },
        ImageURL = "resource:" + nodeID,
    };

    private static CloudAgentCallDto Call(string id) => new()
    {
        ID = id,
        Function = new CloudAgentCallFunctionDto { Name = "canvas_inspect_image", Arguments = "{}" },
    };

    [Fact]
    public void 配对_不变式必须能抓住图文交错()
    {
        const int batch = 4;
        List<object> declared = [];
        List<CloudAgentImageInspectionDto> inspections = [];
        for (int index = 0; index < batch; index++)
        {
            declared.Add(new Dictionary<string, object?>
            {
                ["id"] = $"call-{index}",
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = "canvas_inspect_image", ["arguments"] = "{}" },
            });
            inspections.Add(Inspection($"image-{index}"));
        }
        Dictionary<string, JsonElement> initialUser = new(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("user"),
            ["content"] = JsonSerializer.SerializeToElement("看这四张图"),
        };
        Dictionary<string, JsonElement> assistant = new(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("assistant"),
            ["content"] = JsonSerializer.SerializeToElement(""),
            ["tool_calls"] = JsonSerializer.SerializeToElement(declared),
        };
        // 修复前的形态：tool 与 user(图) 交错。
        List<Dictionary<string, JsonElement>> buggyMessages = [initialUser, assistant];
        for (int index = 0; index < batch; index++)
        {
            buggyMessages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("tool"),
                ["tool_call_id"] = JsonSerializer.SerializeToElement($"call-{index}"),
                ["content"] = JsonSerializer.SerializeToElement($$"""{"nodeId":"image-{{index}}"}"""),
            });
            buggyMessages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("user"),
                ["content"] = JsonSerializer.SerializeToElement(
                    CloudAgentRuntimeVisionProbe.ImageContentParts([inspections[index]])),
            });
        }
        Assert.NotNull(PairingViolation(buggyMessages));

        // 修复后：tool 连续排开，图片合并成一条 user 消息放最后。
        List<Dictionary<string, JsonElement>> fixedMessages = [initialUser, assistant];
        for (int index = 0; index < batch; index++)
        {
            fixedMessages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("tool"),
                ["tool_call_id"] = JsonSerializer.SerializeToElement($"call-{index}"),
                ["content"] = JsonSerializer.SerializeToElement("{}"),
            });
        }
        fixedMessages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement("user"),
            ["content"] = JsonSerializer.SerializeToElement(
                CloudAgentRuntimeVisionProbe.ImageContentParts(inspections)),
        });
        Assert.Null(PairingViolation(fixedMessages));
        Assert.Equal(batch, ImagePartCount(fixedMessages[^1]));
    }

    [Fact]
    public void 批次执行_图文不交错_兜底flush幂等()
    {
        const int batch = 3;
        List<CloudAgentCallDto> calls = [];
        for (int index = 0; index < batch; index++)
        {
            calls.Add(Call($"call-{index}"));
        }
        CloudAgentRuntimeDto state = new()
        {
            Calls = calls,
            Canonical = new CloudAgentCanonicalRequestDto
            {
                Messages =
                [
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["role"] = JsonSerializer.SerializeToElement("assistant"),
                        ["content"] = JsonSerializer.SerializeToElement(""),
                        ["tool_calls"] = JsonSerializer.SerializeToElement(
                            calls.Select(call => (object)new Dictionary<string, object?>
                            {
                                ["id"] = call.ID,
                                ["type"] = "function",
                                ["function"] = new Dictionary<string, object?>
                                {
                                    ["name"] = "canvas_inspect_image",
                                    ["arguments"] = "{}",
                                },
                            }).ToList()),
                    },
                ],
            },
        };
        for (int index = 0; index < batch; index++)
        {
            CloudAgentImageInspectionDto inspection = Inspection($"image-{index}");
            CloudAgentRuntimeVisionProbe.ToolResult(
                "run-1", state, calls[index], inspection.Receipt, null, inspection);
        }
        // 本批最后一个是看图：ToolResult 已 flush，缓冲清空、图片合并成一条 user 消息。
        Assert.Null(state.PendingImageInspections);
        Assert.Null(PairingViolation(state.Canonical.Messages));
        Assert.Equal(batch, state.Canonical.Messages.Sum(message => ImagePartCount(message)));
        Assert.Equal(1, state.Canonical.Messages.Count(message => ImagePartCount(message) > 0));

        // 兜底 flush 幂等：重复调用是空操作。
        Assert.False(CloudAgentRuntimeVisionProbe.Flush(state));

        // 重复查看（ImageURL 为空）只回执文字、不入缓冲。
        Dictionary<string, JsonElement> toolMessage = state.Canonical.Messages[1];
        Assert.True(toolMessage["content"].GetString()!.Contains("\"nodeId\"", StringComparison.Ordinal));
    }

    [Fact]
    public void 批次执行_非看图结尾时兜底flush不丢图()
    {
        CloudAgentCallDto inspect = Call("call-0");
        CloudAgentCallDto plain = new()
        {
            ID = "call-1",
            Function = new CloudAgentCallFunctionDto { Name = "canvas_get_state", Arguments = "{}" },
        };
        CloudAgentRuntimeDto state = new()
        {
            Calls = [inspect, plain],
            Canonical = new CloudAgentCanonicalRequestDto(),
        };
        CloudAgentImageInspectionDto inspection = Inspection("image-0");
        CloudAgentRuntimeVisionProbe.ToolResult("run-1", state, inspect, inspection.Receipt, null, inspection);
        // 最后一个调用不是看图：缓冲保留，等 advance 底部兜底 flush。
        Assert.NotNull(state.PendingImageInspections);
        Assert.Equal(0, state.Canonical.Messages.Count(message => ImagePartCount(message) > 0));

        CloudAgentRuntimeVisionProbe.ToolResult(
            "run-1", state, plain, new JsonObject { ["ok"] = true }, null, null);
        Assert.Null(PairingViolation(state.Canonical.Messages));
        // 兜底路径（advance 组装 canonical 前）。
        Assert.True(CloudAgentRuntimeVisionProbe.Flush(state));
        Assert.Null(PairingViolation(state.Canonical.Messages));
        Assert.Equal(1, state.Canonical.Messages.Count(message => ImagePartCount(message) > 0));
    }

    // ------------------------------------------------------------ 计数与预算

    [Fact]
    public void 计数_旧检查点按逐图计数迁移()
    {
        CloudAgentRuntimeDto state = new()
        {
            ImageInspectCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["a"] = 2,
                ["b"] = 1,
            },
        };
        Assert.Equal(3, CloudAgentRuntimeVisionProbe.Calls(state));
        CloudAgentRuntimeVisionProbe.Mark(state, "a", attached: false);
        Assert.Equal(4, state.ImageInspectCalls);
        Assert.Equal(3, CloudAgentRuntimeVisionProbe.Count(state, "a"));
        // 附图时锚点资产标记待观察。
        state.CreativeAnchor = new CloudAgentCreativeAnchorDto
        {
            ReferenceAssets =
            [
                new CloudAgentReferenceAnchorDto { NodeID = "b", VisualIdentity = "ready" },
            ],
        };
        CloudAgentRuntimeVisionProbe.Mark(state, "b", attached: true);
        Assert.Equal("unknown", state.CreativeAnchor.ReferenceAssets[0].VisualIdentity);
        Assert.True(state.CreativeAnchor.ReferenceAssets[0].RequiresVisualInspection);
    }

    // ------------------------------------------------------------ 轮内裁剪

    [Fact]
    public void 裁剪_只保留最近三轮_占位符逐图带观察()
    {
        CloudAgentCanonicalRequestDto request = new();
        // 5 个工具轮次：每轮 assistant(tool_calls) + tool(图片回执) + user(图片)。
        for (int round = 0; round < 5; round++)
        {
            string nodeID = $"img-{round}";
            request.Messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("assistant"),
                ["content"] = JsonSerializer.SerializeToElement(""),
                ["tool_calls"] = JsonSerializer.SerializeToElement(new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = $"call-{round}",
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = "canvas_inspect_image",
                            ["arguments"] = "{}",
                        },
                    },
                }),
            });
            request.Messages.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("tool"),
                ["tool_call_id"] = JsonSerializer.SerializeToElement($"call-{round}"),
                ["content"] = JsonSerializer.SerializeToElement($$"""{"nodeId":"{{nodeID}}"}"""),
            });
            Dictionary<string, JsonElement> imageMessage = new(StringComparer.Ordinal)
            {
                ["role"] = JsonSerializer.SerializeToElement("user"),
                ["content"] = JsonSerializer.SerializeToElement(
                    CloudAgentRuntimeVisionProbe.ImageContentParts([Inspection(nodeID)])),
            };
            request.Messages.Add(imageMessage);
        }
        // 被裁剪节点的观察注记随占位符写给模型。
        (bool changed, int pruned) = CloudAgentRuntimeVisionProbe.Prune(
            request, new Dictionary<string, string> { ["img-0"] = "红色跑车在雨天" });
        Assert.True(changed);
        // 超出 3 轮保留期的前 2 轮图片被移出。
        Assert.Equal(2, pruned);
        int remaining = 0;
        foreach (Dictionary<string, JsonElement> message in request.Messages)
        {
            remaining += ImagePartCount(message);
        }
        Assert.Equal(3, remaining);
        // 被裁剪的消息保留文字回执与 nodeId 标记，占位符可提取 nodeId。
        Assert.Contains(
            request.Messages,
            message => CloudAgentRuntimeVisionProbe.NodeIDs(message).Contains("img-0"));
        string allContent = string.Join(
            "|", request.Messages.Select(message => message.TryGetValue("content", out JsonElement content)
                ? content.ToString() : ""));
        Assert.Contains("红色跑车在雨天", allContent, StringComparison.Ordinal);
        Assert.Contains("以这段观察为准", allContent, StringComparison.Ordinal);
    }

    [Fact]
    public void 裁剪_无工具轮次时只留最后一条消息()
    {
        CloudAgentCanonicalRequestDto request = new()
        {
            Messages =
            [
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["role"] = JsonSerializer.SerializeToElement("user"),
                    ["content"] = JsonSerializer.SerializeToElement(
                        CloudAgentRuntimeVisionProbe.ImageContentParts([Inspection("img-0")])),
                },
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["role"] = JsonSerializer.SerializeToElement("user"),
                    ["content"] = JsonSerializer.SerializeToElement("latest"),
                },
            ],
        };
        (bool changed, int pruned) = CloudAgentRuntimeVisionProbe.Prune(request, null);
        Assert.True(changed);
        Assert.Equal(1, pruned);
        Assert.Equal(0, request.Messages.Sum(message => ImagePartCount(message)));
    }

    [Fact]
    public void 工具注册_看图开关跟随VisionEnabled()
    {
        CloudAgentRequestDto vision = new()
        {
            PermissionMode = "auto",
            ContextScope = ["canvas"],
            VisionEnabled = true,
        };
        CloudAgentRequestDto plain = new()
        {
            PermissionMode = "auto",
            ContextScope = ["canvas"],
        };
        Assert.True(CloudAgentTools.Allowed(vision, "canvas_inspect_image"));
        Assert.False(CloudAgentTools.Allowed(plain, "canvas_inspect_image"));
        // 无画布范围时不注册。
        Assert.False(CloudAgentTools.Allowed(
            new CloudAgentRequestDto { VisionEnabled = true }, "canvas_inspect_image"));
    }
}

/// <summary>访问 internal 看图助手的探针。</summary>
public static class CloudAgentRuntimeVisionProbe
{
    public static List<Dictionary<string, JsonElement>> ImageContentParts(
        IReadOnlyList<CloudAgentImageInspectionDto> inspections) =>
        CloudAgentRuntimeService.ImageContentParts(inspections);

    public static bool Flush(CloudAgentRuntimeDto state) =>
        CloudAgentRuntimeService.FlushPendingImages(state);

    public static (bool Changed, int Pruned) Prune(
        CloudAgentCanonicalRequestDto request, Dictionary<string, string>? notes) =>
        CloudAgentRuntimeService.PruneInspectedImages(request, notes);

    public static List<string> NodeIDs(Dictionary<string, JsonElement> message) =>
        CloudAgentRuntimeService.ImageMessageNodeIDs(message);

    public static int Calls(CloudAgentRuntimeDto state) =>
        CloudAgentRuntimeService.ImageInspectionCallsTotal(state);

    public static int Count(CloudAgentRuntimeDto state, string nodeID) =>
        CloudAgentRuntimeService.ImageInspectionCount(state, nodeID);

    public static void Mark(CloudAgentRuntimeDto state, string nodeID, bool attached) =>
        CloudAgentRuntimeService.MarkCanvasImageInspection(state, nodeID, attached);

    public static void ToolResult(
        string runID, CloudAgentRuntimeDto state, CloudAgentCallDto call,
        JsonObject? result, Exception? cause, CloudAgentImageInspectionDto? inspection) =>
        CloudAgentRuntimeService.ToolResult(runID, state, call, result, cause, inspection);
}
