#nullable enable

namespace OpenAICanvas.Protocol;

/// <summary>
/// 把清单里的一次操作求值成出站请求。<c>Body</c> 走表达式引擎，
/// 旧版 <c>Fields</c> 走点路径表达式，两者都会经过「连续数字键还原成数组」的归一化。
/// </summary>
public static class ProtocolManifestOperation
{
    /// <summary>对应 Go: <c>buildManifestOperation</c>。</summary>
    public static RequestSpec Build(
        ManifestOperation operation,
        ManifestAuth auth,
        GenerationRequest request,
        string taskID)
    {
        Dictionary<string, object?> requestValues = ProtocolManifestValues.RequestValues(request);
        Dictionary<string, object?> env = new(StringComparer.Ordinal)
        {
            ["request"] = requestValues,
            ["taskId"] = taskID,
        };

        object? body = null;
        if (operation.Body is not null)
        {
            object? value = Evaluate(operation.Body, env, "evaluate request body");
            body = ProtocolManifestValues.NormalizeValue(value);
        }
        else if (operation.Fields is { Count: > 0 } fields)
        {
            Dictionary<string, object?> legacyBody = new(fields.Count, StringComparer.Ordinal);
            foreach ((string key, string expression) in fields)
            {
                object? value = ExpressionValue(expression, request, taskID);
                if (value is null)
                {
                    continue;
                }
                ProtocolManifestValues.SetMapPath(legacyBody, key, value);
            }
            body = ProtocolManifestValues.NormalizeValue(legacyBody);
        }

        object? pathTemplate = operation.PathTemplate ?? operation.Path;
        object? evaluatedPath = Evaluate(pathTemplate, env, "evaluate request path");
        string path = ProtocolExpression.TextOf(evaluatedPath)
            .Replace("{{taskId}}", Uri.EscapeDataString(taskID), StringComparison.Ordinal)
            .Replace("{{model}}", Uri.EscapeDataString(request.Model), StringComparison.Ordinal);
        path = ProtocolExpression.Interpolate(path, env);
        if (!ProtocolManifestValues.IsRelativePath(path))
        {
            throw new InvalidOperationException($"evaluated request path must be relative: \"{path}\"");
        }

        Dictionary<string, string>? headers = EvaluateStringMap(operation.Headers, env);
        Dictionary<string, List<string>>? query = EvaluateQuery(operation.Query, env);
        List<RequestFilePart> files = EvaluateFiles(operation.Files, env);
        object? contentTypeTemplate = operation.ContentTypeTemplate ?? operation.ContentType;
        object? evaluatedContentType = Evaluate(contentTypeTemplate, env, "evaluate request content type");
        string contentType = ProtocolManifestValues.DefaultValue(
            ProtocolExpression.TextOf(evaluatedContentType),
            "application/json");

        return new RequestSpec
        {
            Method = operation.Method.ToUpperInvariant(),
            Path = path,
            OriginPath = operation.OriginPath,
            ContentType = contentType,
            Headers = headers,
            Query = query,
            Body = body,
            Files = files.Count == 0 ? null : files,
            Auth = auth,
        };
    }

    /// <summary>对应 Go: <c>manifestExpressionValue</c>（旧版 <c>fields</c> 的 <c>source|transform</c> 写法）。</summary>
    public static object? ExpressionValue(string expression, GenerationRequest request, string taskID)
    {
        string trimmed = (expression ?? "").Trim();
        string source = trimmed;
        string[] transforms = [];
        int separator = trimmed.IndexOf('|');
        if (separator >= 0)
        {
            source = trimmed[..separator].Trim();
            transforms = trimmed[(separator + 1)..].Split('|');
        }

        object? value;
        if (source == "taskId")
        {
            value = taskID;
        }
        else if (source.StartsWith("request.", StringComparison.Ordinal))
        {
            value = ProtocolManifestValues.PathValue(
                ProtocolManifestValues.RequestValues(request),
                source["request.".Length..]);
        }
        else
        {
            value = source;
        }

        foreach (string transform in transforms)
        {
            value = ProtocolManifestValues.ApplyTransform(value, transform, request);
            if (value is null)
            {
                break;
            }
        }
        return value;
    }

    /// <summary>对应 Go: <c>evaluateManifestStringMap</c>（求值后为空的表头不写入）。</summary>
    public static Dictionary<string, string>? EvaluateStringMap(
        Dictionary<string, object?>? values,
        IReadOnlyDictionary<string, object?> env)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }
        Dictionary<string, string> result = new(values.Count, StringComparer.Ordinal);
        foreach ((string key, object? template) in values)
        {
            object? value = Evaluate(template, env, "evaluate request headers");
            string text = ProtocolExpression.TextOf(value).Trim();
            if (text.Length != 0)
            {
                result[key] = text;
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>evaluateManifestQuery</c>（数组展开为同名多值，空值丢弃）。</summary>
    public static Dictionary<string, List<string>>? EvaluateQuery(
        Dictionary<string, object?>? values,
        IReadOnlyDictionary<string, object?> env)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }
        Dictionary<string, List<string>> result = new(values.Count, StringComparer.Ordinal);
        foreach ((string key, object? template) in values)
        {
            object? value = Evaluate(template, env, "evaluate request query");
            foreach (object? item in ProtocolExpression.Array(value))
            {
                string text = ProtocolExpression.TextOf(item).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                if (!result.TryGetValue(key, out List<string>? list))
                {
                    list = [];
                    result[key] = list;
                }
                list.Add(text);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>evaluateManifestFiles</c>。</summary>
    public static List<RequestFilePart> EvaluateFiles(
        List<ManifestFilePart>? parts,
        IReadOnlyDictionary<string, object?> env)
    {
        List<RequestFilePart> result = [];
        if (parts is null)
        {
            return result;
        }
        foreach (ManifestFilePart part in parts)
        {
            object? source = Evaluate(part.Source, env, $"evaluate multipart file \"{part.Name}\"");
            object? filename = Evaluate(part.Filename, env, $"evaluate multipart filename \"{part.Name}\"");
            object? mimeType = Evaluate(part.MIMEType, env, $"evaluate multipart MIME type \"{part.Name}\"");
            List<object?> items = ProtocolExpression.Array(source);
            for (int index = 0; index < items.Count; index++)
            {
                foreach (MediaReference reference in ProtocolManifestResponse.MediaReferencesFromValue(items[index], "file", false))
                {
                    string name = ProtocolExpression.TextOf(filename).Trim();
                    if (name.Length == 0)
                    {
                        name = reference.Name;
                    }
                    if (name.Length == 0)
                    {
                        name = part.Name + "-" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    string contentType = ProtocolExpression.TextOf(mimeType).Trim();
                    if (contentType.Length == 0)
                    {
                        contentType = reference.MIMEType;
                    }
                    result.Add(new RequestFilePart
                    {
                        Name = part.Name,
                        Filename = name,
                        MIMEType = contentType,
                        Reference = reference,
                    });
                }
            }
        }
        return result;
    }

    private static object? Evaluate(object? template, IReadOnlyDictionary<string, object?> env, string label)
    {
        try
        {
            return ProtocolExpression.Evaluate(template, env);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{label}: {error.Message}", error);
        }
    }
}
