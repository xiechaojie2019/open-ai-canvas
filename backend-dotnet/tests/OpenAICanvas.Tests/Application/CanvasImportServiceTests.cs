#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using Xunit;

namespace OpenAICanvas.Tests.Application;

public sealed class CanvasImportServiceTests
{
    [Fact]
    public void LibTVAdapter_MapsMediaPlaceholdersAndConnections()
    {
        JsonElement detail = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "projectMeta":{"uuid":"project-uuid","name":"测试画布"},
              "nodeList":[
                {"nodeKey":"image-1","name":"图片","data":"{\"type\":\"image\",\"url\":[\"https://example.com/first.png\",\"https://example.com/second.png\"],\"params\":{\"prompt\":\"提示\",\"model\":\"model\"},\"_resourceMeta\":{\"items\":[{\"width\":1024,\"height\":768}]}}","position":{"positionX":"10","positionY":"20"},"measured":{"width":"640","height":"360"}},
                {"nodeKey":"failed","name":"失败视频","data":"{\"type\":\"video\",\"url\":[],\"taskInfo\":{\"status\":3,\"failedReason\":\"生成失败\"}}","position":{"positionX":"500","positionY":"20"}}
              ],
              "connectionList":[{"connectionId":"edge-1","source":"image-1","target":"failed"}]
            }
            """);

        LibTVImportResultDto result = CanvasImportService.AdaptLibTV(detail);

        Assert.Equal(2, result.ImportedNodeCount);
        Assert.Equal(1, result.ImportedConnectionCount);
        Assert.Equal("https://example.com/first.png", result.Nodes[0].Content);
        Assert.Equal(1024, result.Nodes[0].NaturalWidth);
        Assert.Equal("success", result.Nodes[0].Status);
        Assert.Equal("error", result.Nodes[1].Status);
        Assert.Equal("生成失败", result.Nodes[1].ErrorDetails);
        Assert.Equal("libtv", result.Nodes[0].Metadata.Provider);
        Assert.StartsWith("libtv-", result.Connections[0].ID, StringComparison.Ordinal);
    }

    [Fact]
    public void TapNowAdapter_DecodesStringData_RestrictsMediaAndUsesValueKey()
    {
        JsonElement detail = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "name":"TapNow 测试画布",
              "nodes":[
                {"id":"source","type":"image","data":"{\"title\":\"源图\",\"src\":\"https://files.tapnow.media/source.png\"}","position":{"x":1,"y":2},"measured":{"width":100,"height":80}},
                {"id":"target","type":"image","data":"{\"title\":\"目标图\",\"src\":\"https://evil.example/blocked.png\"}","position":{"x":200,"y":2},"measured":{"width":100,"height":80}}
              ],
              "connections":[{"id":"edge","source":"source","target":"target","data":{"valueKey":"images"}}]
            }
            """);

        TapNowImportResultDto result = CanvasImportService.AdaptTapNow(detail, "share_1");

        Assert.Equal(2, result.ImportedNodeCount);
        Assert.Equal("https://files.tapnow.media/source.png", result.Nodes[0].Content);
        Assert.Equal("success", result.Nodes[0].Status);
        Assert.Empty(result.Nodes[1].Content);
        Assert.Equal("idle", result.Nodes[1].Status);
        Assert.Equal("images", result.Connections[0].ToHandleID);
        Assert.Equal("tapnow", result.Nodes[0].Metadata.Provider);
    }

    [Fact]
    public void ImportIdentifiers_RejectInvalidFormats()
    {
        Assert.Throws<OpenAICanvas.Domain.Kernel.AppError>(() =>
            CanvasImportService.AdaptTapNow(JsonSerializer.SerializeToElement(new { nodes = new[] { new { } } }), "bad/share"));
    }
}
