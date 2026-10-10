#nullable enable
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.Application;

/// <summary>
/// 批量创作表「追加行继承上一行参考图」的行为契约。
/// 对应 Go: <c>internal/app/cloud_agent_batch_table.go</c> append 分支
/// （<c>inheritedInputNodeIDs := []any{}; if len(rows) &gt; 0 { ... }</c>）。
/// </summary>
/// <remarks>
/// 2026-10-10 事故：.NET 移植漏了 Go 的 <c>len(rows) &gt; 0</c> 守卫，空表首次 append
/// 直接索引 <c>rows[^1]</c> 抛 <c>ArgumentOutOfRangeException</c>。该异常不是
/// <c>AppError</c>，运行终态化接不住，scheduler worker 每 2 秒重抛一次，
/// run <c>ag2f3a000de787fd102a7f9d516a22920b</c> 永久停在 running。
/// </remarks>
public sealed class BatchTableInheritedInputNodeIDsTests
{
    private static JsonObject Row(params string[] ids) => new()
    {
        ["id"] = "row-" + ids.Length,
        ["inputNodeIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)!).ToArray()),
    };

    [Fact]
    public void 空表_返回空数组而不是抛异常()
    {
        // 事故场景：空批量创作表追加第一行。
        List<JsonObject> rows = [];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 2);

        Assert.Empty(inherited);
    }

    [Fact]
    public void 有行时_继承最后一行的参考图()
    {
        List<JsonObject> rows = [Row("image-1"), Row("image-2", "image-3")];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 3);

        Assert.Equal(["image-2", "image-3"], inherited.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void 继承的是最后一行而不是第一行()
    {
        List<JsonObject> rows = [Row("image-1"), Row("image-9")];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 2);

        Assert.Equal(["image-9"], inherited.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void 参考图按参考列数量截断()
    {
        List<JsonObject> rows = [Row("image-1", "image-2", "image-3", "image-4")];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 2);

        Assert.Equal(["image-1", "image-2"], inherited.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void 参考图去重()
    {
        List<JsonObject> rows = [Row("image-1", "image-1", "image-2")];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 4);

        Assert.Equal(["image-1", "image-2"], inherited.Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void 上一行没有参考图时返回空数组()
    {
        List<JsonObject> rows = [Row("image-1"), new JsonObject { ["id"] = "row-empty" }];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 2);

        Assert.Empty(inherited);
    }

    [Fact]
    public void 继承的是深拷贝_改动结果不影响原行()
    {
        JsonObject source = Row("image-1");
        List<JsonObject> rows = [source];

        JsonArray inherited = CloudAgentCanvasState.BatchInheritedInputNodeIDs(rows, 2);
        ((JsonArray)source["inputNodeIds"]!).Add(JsonValue.Create("image-2"));

        Assert.Single(inherited);
        Assert.Equal("image-1", inherited[0]!.GetValue<string>());
    }
}
