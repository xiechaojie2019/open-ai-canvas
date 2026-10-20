#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 画布摘要契约：中文不转义、token 预算裁剪、字符兜底。
/// 背景：改动前 <c>ToJsonString()</c> 用 .NET 默认编码器把中文转义成 <c>\uXXXX</c>，
/// 46 个节点的画布就被撑到 64,054 字符撞 64,000 上限整单拒绝（2026-10-20 实测）。
/// </summary>
public sealed class CloudAgentCanvasSummaryTests
{
    private static CanvasProject Project(string title, params JsonObject[] nodes)
    {
        JsonObject doc = new()
        {
            ["nodes"] = new JsonArray(nodes.Cast<JsonNode?>().ToArray()),
        };
        return new CanvasProject
        {
            ID = "canvas-summary-1",
            UserID = "user-1",
            Title = title,
            PayloadJSON = doc.ToJsonString(),
            Revision = 1,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
        };
    }

    /// <summary>图片/媒体节点摘要字段见 CanvasCapabilities.cs:552（prompt 等，取 node 顶层优先）。</summary>
    private static JsonObject ImageNode(string id, string prompt) => new()
    {
        ["id"] = id,
        ["type"] = "image",
        ["title"] = "图片 " + id,
        ["prompt"] = prompt,
    };

    private static JsonObject Parse(string data) =>
        JsonNode.Parse(data)!.AsObject();

    [Fact]
    public void 摘要_中文不转义()
    {
        CanvasProject canvas = Project("测试画布", ImageNode("n1", "一只橘色的猫坐在窗台上晒太阳"));
        string data = CloudAgentSessionService.CanvasSummary(canvas);
        Assert.Contains("一只橘色的猫坐在窗台上晒太阳", data);
        // 改动前这里会是 \u4e00\u7684\u6a58... ，既涨字符又让 token 估算失真
        Assert.DoesNotContain("\\u", data);
    }

    [Fact]
    public void 摘要_小画布完整纳入_无裁剪标记()
    {
        CanvasProject canvas = Project(
            "小画布",
            ImageNode("n1", "短提示词"),
            ImageNode("n2", "另一个短提示词"));
        JsonObject payload = Parse(CloudAgentSessionService.CanvasSummary(canvas));
        Assert.Equal(2, payload["includedNodes"]!.GetValue<int>());
        Assert.False(payload.ContainsKey("omittedNodes"));
        Assert.False(payload.ContainsKey("note"));
    }

    [Fact]
    public void 摘要_超预算裁剪_保留首节点并提示单独读取()
    {
        // 每节点 600 汉字 ≈ 900+ token，远不到 80 个就会撞 20,000 预算
        JsonObject[] nodes = Enumerable.Range(1, 60)
            .Select(i => ImageNode("n" + i, new string('画', 600)))
            .ToArray();
        CanvasProject canvas = Project("大画布", nodes);
        JsonObject payload = Parse(CloudAgentSessionService.CanvasSummary(canvas));

        Assert.Equal(60, payload["totalNodes"]!.GetValue<int>());
        int included = payload["includedNodes"]!.GetValue<int>();
        Assert.True(included is > 0 and < 60, $"included={included}");
        Assert.Equal(60 - included, payload["omittedNodes"]!.GetValue<int>());
        Assert.Contains("canvas_read", payload["note"]!.GetValue<string>());

        // 首节点无条件纳入：Agent 至少看得见画布里有什么
        JsonArray items = payload["nodes"]!.AsArray();
        Assert.Equal("n1", items[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void 摘要_改动前必撞64KB的画布_现在不再整单拒绝()
    {
        // 40 个图片节点各 300 汉字：默认编码器转义后 72,000+ 字符，
        // 改动前稳定抛「画布摘要超过 64KB」。
        JsonObject[] nodes = Enumerable.Range(1, 40)
            .Select(i => ImageNode($"image-{i}", new string('画', 300)))
            .ToArray();
        CanvasProject canvas = Project("大画布", nodes);

        string data = CloudAgentSessionService.CanvasSummary(canvas);

        Assert.DoesNotContain("\\u", data);
        // 不转义后字符数远低于旧的 64,000 卡尺；即便被 token 预算裁过也成立
        Assert.True(data.Length < 64_000, $"len={data.Length}");
    }

    [Fact]
    public void 摘要_未知类型节点_只给基础信息()
    {
        CanvasProject canvas = Project("混合", new JsonObject
        {
            ["id"] = "x1",
            ["type"] = "totally-unknown-type",
            ["title"] = "未知节点",
        });
        JsonObject payload = Parse(CloudAgentSessionService.CanvasSummary(canvas));
        JsonObject item = payload["nodes"]!.AsArray()[0]!.AsObject();
        Assert.False(item["agentSupported"]!.GetValue<bool>());
        Assert.Contains("不支持", item["agentUnsupportedReason"]!.GetValue<string>());
    }

    [Fact]
    public void 摘要_预算与兜底常量_保持声明值()
    {
        // 预算约占默认 128K 窗口输入预算（约 106K token）的 20%
        Assert.Equal(20_000, CloudAgentSessionService.CanvasSummaryTokenBudget);
        Assert.True(CloudAgentSessionService.CanvasSummaryCharLimit > CloudAgentSessionService.CanvasSummaryTokenBudget);
    }
}
