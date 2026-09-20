#nullable enable

using System.Text.RegularExpressions;

namespace OpenAICanvas.Protocol;

/// <summary>
/// 清单响应映射的求值层：把 <c>response</c> 声明里的模板与路径解析成状态机字段和媒体引用。
/// 对应 Go: <c>internal/protocol/manifest.go</c> 的 <c>manifestResponseString</c> /
/// <c>manifestResponseMedia</c> / <c>mediaReferencesFromManifestValue</c> / <c>mediaPathValues</c>。
/// </summary>
public static class ProtocolManifestResponse
{
    private static readonly Regex MarkdownImageRegex =
        new(@"!\[[^\]]*\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);

    private static readonly Regex HtmlVideoRegex =
        new("<video[^>]+src=['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 求值模板并把结果拼成字符串。null 模板与求值失败都返回空串；
    /// 数组元素逐个 trim 后无分隔符连接（Go 的 strings.Join(parts, "")）。
    /// 对应 Go: manifestResponseString。
    /// </summary>
    public static string ManifestResponseString(object? template, IReadOnlyDictionary<string, object?> env)
    {
        if (template is null)
        {
            return "";
        }
        object? value;
        try
        {
            value = ProtocolExpression.Evaluate(template, env);
        }
        catch (InvalidOperationException)
        {
            // Go 在这里吞掉表达式错误并返回空串，让状态机继续走路径回落。
            return "";
        }
        List<string> parts = [];
        foreach (object? item in ProtocolExpression.Array(value))
        {
            string text = ProtocolExpression.TextOf(item).Trim();
            if (text.Length != 0)
            {
                parts.Add(text);
            }
        }
        return string.Concat(parts);
    }

    /// <summary>对应 Go: <c>manifestResponseMedia</c>（模板为 <c>nil</c> 时返回空集合）。</summary>
    public static List<MediaReference> ManifestResponseMedia(
        object? template,
        IReadOnlyDictionary<string, object?> env,
        string kind,
        bool ephemeral)
    {
        if (template is null)
        {
            return [];
        }
        object? value;
        try
        {
            value = ProtocolExpression.Evaluate(template, env);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        return MediaReferencesFromValue(value, kind, ephemeral);
    }

    /// <summary>
    /// 把声明里的媒体值（字符串、对象或它们的数组）归一为 <see cref="MediaReference"/>。
    /// 字符串以 <c>data:</c> 开头进 <c>DataURL</c>，否则进 <c>URL</c>；
    /// 对象支持 <c>id/url/dataUrl/kind/role/mimeType/name/order/weight/ephemeral</c>，
    /// 以及 OpenAI 风格的回落字段。地址与 DataURL 都为空的对象会被丢弃。
    /// 对应 Go: <c>mediaReferencesFromManifestValue</c>。
    /// </summary>
    public static List<MediaReference> MediaReferencesFromValue(object? value, string kind, bool ephemeral)
    {
        List<MediaReference> result = [];
        foreach (object? item in ProtocolExpression.Array(value))
        {
            if (item is string text)
            {
                string trimmed = text.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                MediaReference reference = new() { Kind = kind, Ephemeral = ephemeral };
                if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    reference.DataURL = trimmed;
                }
                else
                {
                    reference.URL = trimmed;
                }
                result.Add(reference);
                continue;
            }

            if (item is not Dictionary<string, object?> typed)
            {
                continue;
            }

            MediaReference media = new()
            {
                ID = ProtocolExpression.TextOf(Field(typed, "id")),
                URL = ProtocolExpression.TextOf(Field(typed, "url")),
                DataURL = ProtocolExpression.TextOf(Field(typed, "dataUrl")),
                Kind = ProtocolManifestValues.DefaultValue(ProtocolExpression.TextOf(Field(typed, "kind")), kind),
                Role = ProtocolExpression.TextOf(Field(typed, "role")),
                MIMEType = ProtocolExpression.TextOf(Field(typed, "mimeType")),
                Name = ProtocolExpression.TextOf(Field(typed, "name")),
                Order = (int)ProtocolExpression.IntOf(Field(typed, "order")),
                Weight = ProtocolExpression.FloatOf(Field(typed, "weight")),
                Ephemeral = ephemeral || ProtocolExpression.Truthy(Field(typed, "ephemeral")),
            };
            if (media.URL.Length == 0)
            {
                media.URL = ProtocolManifestValues.FirstString(
                    typed,
                    "file_url", "fileUrl", "image_url", "imageUrl", "video_url", "videoUrl", "audio_url", "audioUrl", "uri");
            }
            if (media.DataURL.Length == 0)
            {
                media.DataURL = ProtocolManifestValues.FirstString(typed, "data_url", "b64_json");
            }
            if (media.URL.Length != 0 || media.DataURL.Length != 0)
            {
                result.Add(media);
            }
        }
        return result;
    }

    /// <summary>
    /// 从响应路径里取出结果地址列表。字符串先尝试 Markdown 图片语法、再尝试
    /// HTML <c>&lt;video&gt;</c>，都没有命中就原样返回单个 trim 后的地址；数组则逐项取字符串
    /// 或对象的 <c>url</c> 族字段。对应 Go: <c>mediaPathValues</c>。
    /// </summary>
    public static List<string> MediaPathValues(Dictionary<string, object?>? payload, string path)
    {
        object? value = ProtocolManifestValues.PathValue(payload, path);
        if (value is string text && text.Trim().Length != 0)
        {
            string trimmed = text.Trim();
            List<string> markdown = Captures(MarkdownImageRegex, trimmed);
            if (markdown.Count != 0)
            {
                return markdown;
            }
            List<string> html = Captures(HtmlVideoRegex, trimmed);
            if (html.Count != 0)
            {
                return html;
            }
            return [trimmed];
        }

        if (value is not List<object?> items)
        {
            return [];
        }

        List<string> values = [];
        foreach (object? item in items)
        {
            if (item is string itemText)
            {
                string trimmed = itemText.Trim();
                if (trimmed.Length != 0)
                {
                    values.Add(trimmed);
                }
                continue;
            }
            if (item is Dictionary<string, object?> itemObject)
            {
                string url = ProtocolManifestValues.FirstString(
                    itemObject, "url", "file_url", "fileUrl", "video_url", "videoUrl", "image_url", "imageUrl");
                if (url.Length != 0)
                {
                    values.Add(url);
                }
            }
        }
        return values;
    }

    private static object? Field(Dictionary<string, object?> payload, string key) =>
        payload.TryGetValue(key, out object? value) ? value : null;

    private static List<string> Captures(Regex pattern, string input)
    {
        List<string> values = [];
        foreach (Match match in pattern.Matches(input))
        {
            if (match.Groups.Count > 1 && match.Groups[1].Value.Length != 0)
            {
                values.Add(match.Groups[1].Value);
            }
        }
        return values;
    }
}
