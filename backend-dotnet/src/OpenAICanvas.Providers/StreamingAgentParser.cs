#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;

namespace OpenAICanvas.Providers;

/// <summary>流式解析出的工具调用。对应 Go: <c>streamingAgentToolCall</c>。</summary>
public sealed class StreamingToolCall
{
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>
/// 三种 wire 协议的 SSE 流式解析器。
/// 对应 Go: <c>internal/app/provider_text.go</c> 的 <c>streamingAgentParser</c>。
/// </summary>
/// <remarks>
/// 解析器是纯状态机：喂入 (MIME, chunk) 即可，便于用固定输入断言输出，
/// 不依赖真实网络。协议取值 <c>responses</c> / <c>chat-completion</c> / <c>claude-api</c>。
/// </remarks>
public sealed class StreamingAgentParser
{
    private const string ResponsesProtocol = "responses";
    private const string ClaudeProtocol = "claude-api";

    private readonly string _protocol;
    private readonly Action<string>? _emit;
    private readonly SortedDictionary<int, StreamingToolCall> _toolCalls = [];
    private readonly Dictionary<string, int> _toolCallById = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private string _buffer = "";
    private IReadOnlyDictionary<string, object?>? _completed;
    private Exception? _error;

    public StreamingAgentParser(string protocol, Action<string>? emit = null)
    {
        _protocol = protocol;
        _emit = emit;
    }

    /// <summary>推理增量回调。对应 Go 的 <c>parser.emitReasoning</c>。</summary>
    public Action<string>? EmitReasoning { get; set; }

    /// <summary>喂入一个网络分片。对应 Go: <c>consume</c>。</summary>
    public void Consume(string mimeType, ReadOnlySpan<byte> chunk)
    {
        if (_error is not null || chunk.Length == 0)
        {
            return;
        }
        if (!mimeType.ToLowerInvariant().Contains("event-stream", StringComparison.Ordinal))
        {
            return;
        }
        _buffer += Encoding.UTF8.GetString(chunk);
        ConsumeFrames(flush: false);
    }

    /// <summary>流结束时的收尾。对应 Go: <c>flush</c>。</summary>
    public void Flush()
    {
        if (_error is not null)
        {
            return;
        }
        ConsumeFrames(flush: true);
    }

    /// <summary>
    /// 输出结果。对应 Go: <c>result</c>。
    /// </summary>
    /// <remarks>
    /// 工具调用参数必须是完整 JSON；不完整时报错而不是静默降级 ——
    /// 半截 JSON 交给下游工具执行会造成难以定位的失败。
    /// </remarks>
    public Dictionary<string, object?> Result()
    {
        if (_error is not null)
        {
            throw _error;
        }
        if (_completed is not null)
        {
            Dictionary<string, object?> fromCompleted = AgentToolPayload.Parse(_completed, _protocol);
            if (_text.Length > 0)
            {
                fromCompleted["text"] = _text.ToString();
            }
            if (_reasoning.Length > 0)
            {
                fromCompleted["reasoning"] = _reasoning.ToString();
            }
            return fromCompleted;
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["mode"] = "text",
            ["text"] = _text.ToString(),
            ["toolCalls"] = new List<object?>(),
        };
        if (_reasoning.Length > 0)
        {
            result["reasoning"] = _reasoning.ToString();
        }

        List<object?> calls = [];
        foreach ((int _, StreamingToolCall call) in _toolCalls)
        {
            // 与 Go 一致：缺 id 或 name 的调用视为不完整，跳过。
            if (call.ID.Trim().Length == 0 || call.Name.Trim().Length == 0)
            {
                continue;
            }
            string arguments = call.Arguments;
            if (arguments.Trim().Length == 0)
            {
                arguments = "{}";
            }
            try
            {
                using JsonDocument _ = JsonDocument.Parse(arguments);
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException($"Agent 工具参数不是完整 JSON：{error.Message}", error);
            }
            calls.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = call.ID,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = call.Name,
                    ["arguments"] = arguments,
                },
            });
        }
        result["toolCalls"] = calls;
        return result;
    }

    // ------------------------------------------------------------ 内部

    /// <summary>
    /// 按 SSE 空行边界切分并消费完整帧。
    /// 对应 Go: <c>consumeFrames</c>（边界正则 <c>\r?\n\r?\n</c>）。
    /// </summary>
    private void ConsumeFrames(bool flush)
    {
        while (_error is null)
        {
            int boundary = FindFrameBoundary(_buffer, out int boundaryLength);
            if (boundary < 0)
            {
                break;
            }
            ConsumeFrame(_buffer[..boundary]);
            _buffer = _buffer[(boundary + boundaryLength)..];
        }
        if (flush && _error is null && _buffer.Trim().Length != 0)
        {
            ConsumeFrame(_buffer);
            _buffer = "";
        }
    }

    /// <summary>等价 Go 的 <c>\r?\n\r?\n</c>：返回起始位置与整体长度。</summary>
    private static int FindFrameBoundary(string value, out int length)
    {
        length = 0;
        for (int index = 0; index < value.Length; index++)
        {
            int cursor = index;
            if (value[cursor] == '\r')
            {
                cursor++;
            }
            if (cursor >= value.Length || value[cursor] != '\n')
            {
                continue;
            }
            cursor++;
            if (cursor < value.Length && value[cursor] == '\r')
            {
                cursor++;
            }
            if (cursor < value.Length && value[cursor] == '\n')
            {
                length = cursor - index + 1;
                return index;
            }
        }
        return -1;
    }

    /// <summary>
    /// 消费单个 SSE 帧：抽取 <c>event:</c> 与 <c>data:</c> 行。
    /// 对应 Go: <c>consumeFrame</c>。
    /// </summary>
    private void ConsumeFrame(string frame)
    {
        string eventName = "";
        List<string> dataLines = [];
        foreach (string line in frame.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // 与 Go 一致：剥掉 "data:" 后允许并剥掉一个前导空格。
                string linePayload = line["data:".Length..];
                if (linePayload.StartsWith(' '))
                {
                    linePayload = linePayload[1..];
                }
                dataLines.Add(linePayload);
            }
        }
        string raw = string.Join('\n', dataLines).Trim();
        if (raw.Length == 0 || raw == "[DONE]")
        {
            return;
        }
        Dictionary<string, object?>? payload;
        try
        {
            payload = JsonFields.ParseObject(raw);
            if (payload is null)
            {
                throw new JsonException("payload 不是 JSON 对象");
            }
        }
        catch (JsonException error)
        {
            _error = new InvalidOperationException($"Agent 流式事件解析失败：{error.Message}", error);
            return;
        }

        // 业务失败优先于协议解析：上游可能用 200 + 流内 error 表达失败。
        // 与 Go 的 validateTextPayload 对齐：code 必须是"数字且非 0"，
        // error 对象必须含非空 message 才算失败 —— 否则会误拦正常的 data 事件。
        Exception? validation = ValidateTextPayload(payload);
        if (validation is not null)
        {
            _error = validation;
            return;
        }

        switch (_protocol)
        {
            case ResponsesProtocol:
                ConsumeResponsesEvent(eventName, payload);
                break;
            case ClaudeProtocol:
                ConsumeClaudeEvent(payload);
                break;
            default:
                ConsumeChatCompletionEvent(payload);
                break;
        }
    }

    /// <summary>对应 Go: <c>consumeResponsesEvent</c>。</summary>
    private void ConsumeResponsesEvent(string eventName, Dictionary<string, object?> payload)
    {
        string eventType = eventName.Trim().Length > 0
            ? eventName.Trim()
            : ProviderHelpers.StringField(payload, "type");
        switch (eventType)
        {
            case "response.output_text.delta":
            case "output_text.delta":
                AppendText(ProviderHelpers.StringField(payload, "delta"));
                break;
            case "response.reasoning.delta":
            case "response.reasoning_text.delta":
            case "response.reasoning_summary_text.delta":
                AppendReasoning(ProviderHelpers.StringField(payload, "delta"));
                break;
            case "response.completed":
                _completed = JsonFields.NestedObject(payload, "response");
                break;
            case "response.output_item.added":
            {
                Dictionary<string, object?>? item = JsonFields.NestedObject(payload, "item");
                if (ProviderHelpers.StringField(item, "type") == "function_call")
                {
                    int index = IntField(payload, "output_index", _toolCalls.Count);
                    SetToolCall(
                        index,
                        ProviderHelpers.FirstNonEmpty(
                            ProviderHelpers.StringField(item, "call_id"),
                            ProviderHelpers.StringField(item, "id")),
                        ProviderHelpers.StringField(item, "name"),
                        ProviderHelpers.StringField(item, "arguments"));
                }
                break;
            }
            case "response.function_call_arguments.delta":
                ToolCall(ResponseToolCallIndex(payload)).Arguments += ProviderHelpers.StringField(payload, "delta");
                break;
            case "response.function_call_arguments.done":
            {
                int index = ResponseToolCallIndex(payload);
                string arguments = ProviderHelpers.StringField(payload, "arguments");
                if (arguments.Length > 0)
                {
                    ToolCall(index).Arguments = arguments;
                }
                break;
            }
        }
    }

    private int ResponseToolCallIndex(Dictionary<string, object?> payload)
    {
        string itemId = ProviderHelpers.FirstNonEmpty(
            ProviderHelpers.StringField(payload, "item_id"),
            ProviderHelpers.StringField(payload, "call_id"));
        if (_toolCallById.TryGetValue(itemId, out int found))
        {
            return found;
        }
        return IntField(payload, "output_index", _toolCalls.Count);
    }

    /// <summary>对应 Go: <c>consumeChatCompletionEvent</c>。</summary>
    private void ConsumeChatCompletionEvent(Dictionary<string, object?> payload)
    {
        foreach (object? value in InterfaceSlice(payload, "choices"))
        {
            Dictionary<string, object?>? choice = value as Dictionary<string, object?>;
            if (choice is null)
            {
                continue;
            }
            Dictionary<string, object?>? delta = JsonFields.NestedObject(choice, "delta");
            if (delta is null)
            {
                continue;
            }
            AppendText(StreamContentText(delta, "content"));
            AppendReasoning(ProviderHelpers.FirstNonEmpty(
                ProviderHelpers.StringField(delta, "reasoning_content"),
                ProviderHelpers.StringField(delta, "reasoning"),
                ProviderHelpers.StringField(delta, "reasoning_text")));

            int fallbackIndex = 0;
            foreach (object? toolValue in InterfaceSlice(delta, "tool_calls"))
            {
                Dictionary<string, object?>? tool = toolValue as Dictionary<string, object?>;
                if (tool is null)
                {
                    fallbackIndex++;
                    continue;
                }
                int index = IntField(tool, "index", fallbackIndex);
                Dictionary<string, object?>? function = JsonFields.NestedObject(tool, "function");
                StreamingToolCall current = ToolCall(index);
                string id = ProviderHelpers.StringField(tool, "id");
                if (id.Length > 0)
                {
                    current.ID = id;
                    _toolCallById[id] = index;
                }
                string name = ProviderHelpers.StringField(function, "name");
                if (name.Length > 0)
                {
                    current.Name += name;
                }
                current.Arguments += ProviderHelpers.StringField(function, "arguments");
                fallbackIndex++;
            }
        }
    }

    /// <summary>对应 Go: <c>consumeClaudeEvent</c>。</summary>
    private void ConsumeClaudeEvent(Dictionary<string, object?> payload)
    {
        switch (ProviderHelpers.StringField(payload, "type"))
        {
            case "content_block_start":
            {
                Dictionary<string, object?>? block = JsonFields.NestedObject(payload, "content_block");
                int index = IntField(payload, "index", _toolCalls.Count);
                switch (ProviderHelpers.StringField(block, "type"))
                {
                    case "text":
                        AppendText(ProviderHelpers.StringField(block, "text"));
                        break;
                    case "thinking":
                        AppendReasoning(ProviderHelpers.FirstNonEmpty(
                            ProviderHelpers.StringField(block, "thinking"),
                            ProviderHelpers.StringField(block, "text")));
                        break;
                    case "tool_use":
                    {
                        string arguments = "";
                        if (block is not null && block.TryGetValue("input", out object? input) && input is not null)
                        {
                            string encoded = JsonSerializer.Serialize(input, ProtocolJson.WriteOptions);
                            if (encoded != "{}")
                            {
                                arguments = encoded;
                            }
                        }
                        SetToolCall(index,
                            ProviderHelpers.StringField(block, "id"),
                            ProviderHelpers.StringField(block, "name"),
                            arguments);
                        break;
                    }
                }
                break;
            }
            case "content_block_delta":
            {
                Dictionary<string, object?>? delta = JsonFields.NestedObject(payload, "delta");
                int index = IntField(payload, "index", _toolCalls.Count - 1);
                string deltaType = ProviderHelpers.StringField(delta, "type");
                if (deltaType == "text_delta")
                {
                    AppendText(ProviderHelpers.StringField(delta, "text"));
                }
                if (deltaType == "thinking_delta")
                {
                    AppendReasoning(ProviderHelpers.FirstNonEmpty(
                        ProviderHelpers.StringField(delta, "thinking"),
                        ProviderHelpers.StringField(delta, "text")));
                }
                if (deltaType == "input_json_delta")
                {
                    ToolCall(index).Arguments += ProviderHelpers.StringField(delta, "partial_json");
                }
                break;
            }
            case "error":
            {
                Dictionary<string, object?>? errorValue = JsonFields.NestedObject(payload, "error");
                string message = ProviderHelpers.StringField(errorValue, "message");
                _error = Exception(ProviderMediaCodec.DefaultString(message, "Claude 上游返回失败"));
                break;
            }
        }
    }

    private static Exception Exception(string message) => new InvalidOperationException(message);

    /// <summary>
    /// 上游载荷的通用业务失败校验（先于协议分发）。
    /// 对应 Go: <c>validateTextPayload</c>。
    /// </summary>
    /// <remarks>
    /// 判定条件刻意很窄：<c>code</c> 必须是<b>数字且非 0</b>（字符串 "1001" 不算，
    /// 否则会把正常响应里作为业务字段的 code 误判为错误），
    /// 或 <c>error</c> 对象里含<b>非空 message</b>。
    /// </remarks>
    public static Exception? ValidateTextPayload(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("code", out object? codeValue)
            && codeValue is double numericCode && numericCode != 0)
        {
            string rawMessage = ProviderMediaCodec.DefaultString(
                ProviderHelpers.StringField(payload, "msg"), "请求失败");
            return new ProviderPayloadException(rawMessage, ProviderErrorMessages.PayloadError(rawMessage));
        }
        Dictionary<string, object?>? errorValue = JsonFields.NestedObject(payload, "error");
        if (errorValue is not null)
        {
            string message = ProviderHelpers.StringField(errorValue, "message");
            if (message.Length > 0)
            {
                return new ProviderPayloadException(message, ProviderErrorMessages.PayloadError(message));
            }
        }
        return null;
    }

    private void AppendText(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        _text.Append(delta);
        _emit?.Invoke(delta);
    }

    private void AppendReasoning(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        _reasoning.Append(delta);
        EmitReasoning?.Invoke(delta);
    }

    private StreamingToolCall ToolCall(int index)
    {
        if (index < 0)
        {
            index = 0;
        }
        if (!_toolCalls.TryGetValue(index, out StreamingToolCall? call))
        {
            call = new StreamingToolCall();
            _toolCalls[index] = call;
        }
        return call;
    }

    private void SetToolCall(int index, string id, string name, string arguments)
    {
        StreamingToolCall call = ToolCall(index);
        call.ID = id;
        call.Name = name;
        call.Arguments = arguments;
        if (id.Length > 0)
        {
            _toolCallById[id] = index;
        }
    }

    /// <summary>取整数字段。对应 Go 的 <c>intField</c>。</summary>
    internal static int IntField(IReadOnlyDictionary<string, object?>? payload, string key, int fallback)
    {
        if (payload is null || !payload.TryGetValue(key, out object? value) || value is null)
        {
            return fallback;
        }
        return value switch
        {
            double number => (int)number,
            float number => (int)number,
            int number => number,
            long number => (int)number,
            decimal number => (int)number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                => parsed,
            _ => fallback,
        };
    }

    /// <summary>对应 Go 的 <c>interfaceSlice</c>（非数组一律空）。</summary>
    internal static List<object?> InterfaceSlice(IReadOnlyDictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out object? value))
        {
            return [];
        }
        return value switch
        {
            List<object?> list => list,
            System.Collections.IEnumerable items when value is not string => [.. items.Cast<object?>()],
            _ => [],
        };
    }

    /// <summary>
    /// 聊天流式内容抽取：字符串直接返回；段落数组拼接各段 <c>text</c>。
    /// 对应 Go: <c>streamContentText</c>。
    /// </summary>
    internal static string StreamContentText(IReadOnlyDictionary<string, object?>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out object? value) || value is null)
        {
            return "";
        }
        if (value is string text)
        {
            return text;
        }
        if (value is List<object?> parts)
        {
            StringBuilder builder = new();
            foreach (object? part in parts)
            {
                if (part is Dictionary<string, object?> record)
                {
                    builder.Append(ProviderHelpers.StringField(record, "text"));
                }
            }
            return builder.ToString();
        }
        return "";
    }
}
