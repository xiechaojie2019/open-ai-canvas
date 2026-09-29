#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Providers;

namespace OpenAICanvas.Application;

/// <summary>
/// Agent 规划图片的 resource: 占位校验与水合替换。
/// 只有水合后的引用才能替换协议占位；返回的是内存副本，绝不回写 Task.InputJSON。
/// 对应 Go: <c>app/creation_agent_references.go</c>。
/// </summary>
public static class AgentResourceReferences
{
    /// <summary>对应 Go: <c>validateAgentResourcePlaceholders</c>（创建侧：只校验不替换）。</summary>
    public static void Validate(TextTaskInput input) => Resolve(input, hydrate: false);

    /// <summary>对应 Go: <c>resolveAgentResourcePlaceholders(input, true)</c>（执行侧：替换为可读数据）。</summary>
    public static void Hydrate(TextTaskInput input) => Resolve(input, hydrate: true);

    private static void Resolve(TextTaskInput input, bool hydrate)
    {
        if (input.AgentRequests is null)
        {
            return;
        }
        string encoded = JsonSerializer.Serialize(input.AgentRequests, GoJson.WriteOptions);
        if (hydrate && !encoded.Contains("\"resource:", StringComparison.Ordinal))
        {
            return;
        }
        Dictionary<string, string> references = new(StringComparer.Ordinal);
        foreach (ProviderMedia media in input.ReferenceImages)
        {
            if (!media.StorageKey.StartsWith("resource:", StringComparison.Ordinal))
            {
                throw AppError.BadAuthRequest("规划图片必须引用当前账号资源");
            }
            string value = media.StorageKey;
            if (hydrate)
            {
                value = CloudAgentContracts.FirstNonEmpty(media.DataURL, media.URL);
                if (value.Length == 0)
                {
                    throw AppError.BadAuthRequest("规划图片尚未读取成功");
                }
            }
            references[media.StorageKey] = value;
        }
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(encoded);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Agent 协议请求不是合法 JSON：" + error.Message, error);
        }
        JsonElement resolved = Visit(root, references, hydrate);
        string transformed = JsonSerializer.Serialize(resolved, GoJson.WriteOptions);
        AgentToolRequestsInput? requests;
        try
        {
            requests = JsonSerializer.Deserialize<AgentToolRequestsInput>(
                transformed, ProjectCharacterService.GoPayloadOptions);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Agent 协议请求展开失败：" + error.Message, error);
        }
        input.AgentRequests = requests ?? throw AppError.BadAuthRequest("模型协议引用了未获准的图片");
    }

    private static JsonElement Visit(JsonElement value, Dictionary<string, string> references, bool hydrate)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                {
                    string text = value.GetString() ?? "";
                    if (text.StartsWith("resource:", StringComparison.Ordinal))
                    {
                        if (!references.TryGetValue(text, out string? resolved))
                        {
                            throw AppError.BadAuthRequest("模型协议引用了未获准的图片");
                        }
                        return JsonSerializer.SerializeToElement(resolved);
                    }
                    return value;
                }
            case JsonValueKind.Array:
                {
                    List<JsonElement> items = [];
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        items.Add(Visit(item, references, hydrate));
                    }
                    return JsonSerializer.SerializeToElement(items, GoJson.WriteOptions);
                }
            case JsonValueKind.Object:
                return VisitObject(value, references, hydrate);
            default:
                return value;
        }
    }

    private static JsonElement VisitObject(
        JsonElement typed, Dictionary<string, string> references, bool hydrate)
    {
        if (!hydrate)
        {
            object? imageRef = GetString(typed, "type") switch
            {
                "image_url" or "input_image" => ObjectValue(typed, "image_url"),
                "image" => ImageSourceURL(typed),
                _ => null,
            };
            if (imageRef is Dictionary<string, object?> wrapper)
            {
                imageRef = wrapper.GetValueOrDefault("url");
            }
            if (imageRef is not null
                && (imageRef is not string reference || references.GetValueOrDefault(reference, "").Length == 0))
            {
                throw AppError.BadAuthRequest("规划图片不在获准资源清单");
            }
            JsonElement fileData = TypedGetProperty(typed, "fileData");
            if (fileData.ValueKind == JsonValueKind.Object)
            {
                string fileUri = GetString(fileData, "fileUri");
                if (references.GetValueOrDefault(fileUri, "").Length == 0)
                {
                    throw AppError.BadAuthRequest("规划图片不在获准资源清单");
                }
            }
            if (TypedGetProperty(typed, "inlineData").ValueKind != JsonValueKind.Undefined)
            {
                throw AppError.BadAuthRequest("规划图片不能持久化内嵌数据");
            }
        }
        // Claude URL source 与 Gemini fileData 需要协议特定的 base64 封装。
        if (hydrate && GetString(typed, "type") == "url")
        {
            string urlRef = GetString(typed, "url");
            if (urlRef.StartsWith("resource:", StringComparison.Ordinal))
            {
                if (!references.TryGetValue(urlRef, out string? data))
                {
                    throw AppError.BadAuthRequest("模型图片未获准");
                }
                (string mime, string payload, bool ok) = SplitCreationDataURL(data);
                if (!ok)
                {
                    throw AppError.BadAuthRequest("模型图片需要可读取的图片数据");
                }
                return JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "base64",
                        ["media_type"] = mime,
                        ["data"] = payload,
                    },
                    GoJson.WriteOptions);
            }
        }
        if (hydrate)
        {
            JsonElement fileData = TypedGetProperty(typed, "fileData");
            if (fileData.ValueKind == JsonValueKind.Object)
            {
                string fileUri = GetString(fileData, "fileUri");
                if (fileUri.StartsWith("resource:", StringComparison.Ordinal))
                {
                    if (!references.TryGetValue(fileUri, out string? data))
                    {
                        throw AppError.BadAuthRequest("模型图片未获准");
                    }
                    (string mime, string payload, bool ok) = SplitCreationDataURL(data);
                    if (!ok)
                    {
                        throw AppError.BadAuthRequest("模型图片需要可读取的图片数据");
                    }
                    Dictionary<string, JsonElement> mutated = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in typed.EnumerateObject())
                    {
                        if (property.Name != "fileData")
                        {
                            mutated[property.Name] = property.Value.Clone();
                        }
                    }
                    mutated["inlineData"] = JsonSerializer.SerializeToElement(
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["mimeType"] = mime,
                            ["data"] = payload,
                        },
                        GoJson.WriteOptions);
                    return JsonSerializer.SerializeToElement(mutated, GoJson.WriteOptions);
                }
            }
        }
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        foreach (JsonProperty property in typed.EnumerateObject())
        {
            result[property.Name] = Visit(property.Value, references, hydrate);
        }
        return JsonSerializer.SerializeToElement(result, GoJson.WriteOptions);
    }

    /// <summary>对应 Go: <c>splitCreationDataURL</c>。</summary>
    public static (string MIMEType, string Payload, bool Ok) SplitCreationDataURL(string value)
    {
        if (!value.StartsWith("data:", StringComparison.Ordinal))
        {
            return ("", "", false);
        }
        string rest = value["data:".Length..];
        int separator = rest.IndexOf(";base64,", StringComparison.Ordinal);
        if (separator < 0)
        {
            return ("", "", false);
        }
        string mimeType = rest[..separator];
        if (!mimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            return ("", "", false);
        }
        return (mimeType, rest[(separator + ";base64,".Length)..], true);
    }

    private static object? ObjectValue(JsonElement element, string name)
    {
        JsonElement property = TypedGetProperty(element, name);
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(
                property.GetRawText(), ProjectCharacterService.GoPayloadOptions),
            _ => null,
        };
    }

    /// <summary>image 类型块：source 必须是 url 形态，返回 source.url。</summary>
    private static object? ImageSourceURL(JsonElement typed)
    {
        JsonElement source = TypedGetProperty(typed, "source");
        if (source.ValueKind != JsonValueKind.Object || GetString(source, "type") != "url")
        {
            throw AppError.BadAuthRequest("规划图片必须使用资源占位");
        }
        return ObjectValue(source, "url");
    }

    private static JsonElement TypedGetProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value))
        {
            return value;
        }
        return default;
    }

    private static string GetString(JsonElement element, string name)
    {
        JsonElement property = TypedGetProperty(element, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    }
}
