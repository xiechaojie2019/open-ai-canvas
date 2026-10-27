#nullable enable
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// Agent 基于图片产出文本节点时的两个可见缺陷回归：
/// 图片→文本的参考连线被能力表拒绝；文本节点只写 content，提示词输入框为空。
/// </summary>
public sealed class CloudAgentTextNodePromptContractTests
{
    private static JsonObject Document() => new()
    {
        ["nodes"] = new JsonArray
        {
            new JsonObject { ["id"] = "image-1", ["type"] = "image", ["title"] = "红色上衣" },
        },
        ["connections"] = new JsonArray(),
    };

    [Fact]
    public void 图片到文本节点的参考连线被接受并保留提示词草稿()
    {
        JsonObject doc = Document();
        List<CloudAgentApprovalPreviewItemDto> items = CloudAgentMutations.ApplyCanvasPlan(doc,
        [
            new CloudAgentMutations.CanvasOp
            {
                Type = "add_node", ID = "text-1", NodeType = "text",
                Title = "红色上衣 · 卖点", Content = "卖点正文",
            },
            new CloudAgentMutations.CanvasOp
            {
                Type = "connect_nodes", ID = "edge-1", FromNodeID = "image-1", ToNodeID = "text-1",
            },
        ]);

        Assert.Equal(2, items.Count);
        Assert.Single(doc["connections"]!.AsArray());
        JsonNode? nodes = doc["nodes"];
        Assert.NotNull(nodes);
        JsonObject text = nodes!.AsArray()[1]!.AsObject();
        JsonObject metadata = text["metadata"]!.AsObject();
        Assert.Equal("卖点正文", metadata["content"]!.GetValue<string>());
        Assert.Equal("卖点正文", metadata["prompt"]!.GetValue<string>());
        Assert.Equal("卖点正文", metadata["composerContent"]!.GetValue<string>());
    }
}
