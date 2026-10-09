#nullable enable
using System.Text.Json;

namespace OpenAICanvas.Application.Prompts;

/// <summary>
/// 从模型正文中抽取完整 JSON 值。
/// 对应 Go: <c>internal/prompts/json_extract.go</c>。
/// </summary>
/// <remarks>
/// <para>
/// 文本模型常无视「只返回 JSON」的契约：前面写一段解释、后面包一层 Markdown 代码块。
/// 因此<b>不能</b>用「首个 <c>{</c> 配最后一个 <c>}</c>」这种朴素配对——正文里的散文会与 JSON
/// 拼在一起，让本来合法的结果校验失败。
/// </para>
/// <para>
/// 这里的做法是从左到右扫描每个可能的起点，用括号栈找出<b>第一个本身就能解析</b>的完整值。
/// </para>
/// </remarks>
public static class PromptJsonExtract
{
    /// <summary>无法抽出 JSON 时的错误文案。对应 Go 的 <c>errors.New("模型返回的不是 JSON")</c>。</summary>
    public const string NotJsonMessage = "模型返回的不是 JSON";

    /// <summary>抽出第一个完整且合法的 JSON 值。对应 Go: <c>extractJSONText</c>。</summary>
    public static bool TryExtractJsonText(string? raw, out string jsonText)
    {
        jsonText = "";
        string source = raw ?? "";
        for (int start = 0; start < source.Length; start++)
        {
            char value = source[start];
            if (value != '{' && value != '[')
            {
                continue;
            }
            int end = JsonValueEnd(source, start);
            if (end < start)
            {
                continue;
            }
            string candidate = source[start..(end + 1)];
            if (IsParsableJson(candidate, out _, out _))
            {
                jsonText = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 优先返回<b>顶层对象且含 <paramref name="preferKey"/></b> 的候选，找不到时回退到第一个可解析值。
    /// 对应 Go: <c>extractPreferredJSONText</c>。
    /// </summary>
    /// <remarks>
    /// 模型常在给出契约对象前用正文先列举一遍内容（例如先写一段角色名数组），
    /// 只取第一个可解析值会命中这些旁枝片段，导致后续校验拿到完全无关的结构。
    /// </remarks>
    public static bool TryExtractPreferredJsonText(string? raw, string preferKey, out string jsonText)
    {
        jsonText = "";
        string source = raw ?? "";
        string fallback = "";
        for (int start = 0; start < source.Length; start++)
        {
            char value = source[start];
            if (value != '{' && value != '[')
            {
                continue;
            }
            int end = JsonValueEnd(source, start);
            if (end < start)
            {
                continue;
            }
            string candidate = source[start..(end + 1)];
            if (!IsParsableJson(candidate, out bool isObject, out HashSet<string>? keys))
            {
                continue;
            }
            if (isObject && keys is not null && keys.Contains(preferKey))
            {
                jsonText = candidate;
                return true;
            }
            if (fallback.Length == 0)
            {
                fallback = candidate;
            }
        }
        if (fallback.Length > 0)
        {
            jsonText = fallback;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 从 <paramref name="start"/>（必须是 <c>{</c> 或 <c>[</c>）起，返回配对闭合字符的下标；
    /// 不配对返回 -1。对应 Go: <c>jsonValueEnd</c>。
    /// </summary>
    /// <remarks>
    /// 字符串状态必须跟踪转义：<c>{"role":"拿着{小夜灯}的租客"}</c> 里的花括号不算结构字符。
    /// </remarks>
    private static int JsonValueEnd(string source, int start)
    {
        List<char> stack = new(8);
        bool inString = false;
        bool escaped = false;
        for (int index = start; index < source.Length; index++)
        {
            char value = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == '\\')
                {
                    escaped = true;
                }
                else if (value == '"')
                {
                    inString = false;
                }
                continue;
            }
            switch (value)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    stack.Add(value);
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0)
                    {
                        return -1;
                    }
                    char opener = stack[^1];
                    if ((value == '}' && opener != '{') || (value == ']' && opener != '['))
                    {
                        return -1;
                    }
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count == 0)
                    {
                        return index;
                    }
                    break;
                default:
                    break;
            }
        }
        return -1;
    }

    /// <summary>
    /// 候选是否为合法 JSON 值；是顶层对象时通过 <paramref name="keys"/> 交出它的键集合。
    /// 非对象（数组、标量）时 <paramref name="keys"/> 为 <c>null</c>。
    /// </summary>
    private static bool IsParsableJson(string candidate, out bool isObject, out HashSet<string>? keys)
    {
        isObject = false;
        keys = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }
            isObject = true;
            HashSet<string> collected = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                collected.Add(property.Name);
            }
            keys = collected;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
