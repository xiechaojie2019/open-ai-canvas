using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAICanvas.Persistence;

/// <summary>云 Agent 执行日志的 JSON 语义比较。对应 Go: <c>sameJSONDocument</c>。</summary>
/// <remarks>
/// 事件体按 JSON 含义而非源字节比较：payload 可能含原始 JSON 片段（如工具参数），
/// 解码再编码会合法地归一化空白或键序，而事件契约本身不变。
/// </remarks>
public static class CloudAgentJson
{
    public static bool SameJsonDocument(string left, string right)
    {
        if (left == right)
        {
            return true;
        }
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
        }
        catch (JsonException)
        {
            return false;
        }
    }

}
