#nullable enable
using System.Globalization;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application.Prompts;

/// <summary>编译后的提示词。对应 Go: <c>prompts.CompiledPrompt</c>。</summary>
/// <remarks>只有 <see cref="Content"/> 会被执行侧使用；其余字段用于追溯「这次生成用的哪一版模板/定制」。</remarks>
public sealed class CompiledPrompt
{
    public string Content { get; set; } = "";
    public string TemplateID { get; set; } = "";
    public long TemplateVersion { get; set; }
    public string CustomizationID { get; set; } = "";
    public string CustomizationUpdated { get; set; } = "";
}

/// <summary>
/// 提示词模板的<b>编译与结果校验</b>。
/// 对应 Go: <c>internal/prompts/prompt_template.go</c> 的 <c>CompilePrompt</c> /
/// <c>RenderPromptTemplate</c> / <c>ValidatePromptTemplateResult</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这两步是「模板」真正生效的地方，缺任何一步，运营/用户定制的模板都只是死数据：
/// 编译决定模型<b>收到什么</b>，校验决定模型<b>返回什么才算合格</b>。
/// </para>
/// <para>
/// 触发开关只有一个：任务输入的 <c>metadata.promptTemplateOperation</c>
/// （见 <c>TaskWorkerService.ExecuteProviderTaskAsync</c>）。
/// </para>
/// </remarks>
public sealed partial class PromptTemplateService
{
    /// <summary>用户定制为 append 时，接在模板正文后的标题。对应 Go 的同名字面量。</summary>
    private const string CustomizationAppendHeading = "\n\n【用户个性化创作要求】\n";

    // ------------------------------------------------------------ 编译

    /// <summary>
    /// 按操作编译提示词：启用模板 → 叠加用户定制 → 渲染变量 → 追加受保护上下文。
    /// 对应 Go: <c>CompilePrompt</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 找不到启用版本时回落到内置默认正文（<see cref="PromptOperationDefinition.DefaultContent"/>），
    /// 因此编译<b>不会因为运维没配模板而失败</b>。
    /// </para>
    /// <para>
    /// 受保护上下文（剧情/画布资产/角色版本/JSON Schema 契约）在定制<b>之后</b>拼接：
    /// 用户的 rewrite 只能换掉创意部分，不能换掉契约。
    /// </para>
    /// </remarks>
    public async Task<CompiledPrompt> CompilePromptAsync(
        string userID,
        string operation,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        PromptOperationDefinition? definition = FindDefinition(operation);
        if (definition is null)
        {
            throw AppError.BadAuthRequest($"不支持的提示词模板类型：{operation}");
        }

        PromptTemplate? template = await _repository
            .ActivePromptTemplateAsync(operation, cancellationToken).ConfigureAwait(false);
        string creative;
        CompiledPrompt compiled;
        if (template is null)
        {
            creative = definition.DefaultContent;
            compiled = new CompiledPrompt { TemplateVersion = 1 };
        }
        else
        {
            creative = template.Content;
            compiled = new CompiledPrompt
            {
                TemplateID = template.ID,
                TemplateVersion = template.Version,
            };
        }

        UserPromptCustomization? customization = await _repository
            .UserPromptCustomizationAsync(userID, operation, cancellationToken).ConfigureAwait(false);
        if (customization is not null)
        {
            compiled.CustomizationID = customization.ID;
            compiled.CustomizationUpdated = customization.UpdatedAt
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            switch (customization.Mode)
            {
                case CustomizationAppend:
                    creative += CustomizationAppendHeading + customization.Content;
                    break;
                case CustomizationRewrite:
                    creative = customization.Content;
                    break;
                default:
                    break;
            }
        }

        string rendered = RenderPromptTemplate(definition, creative, values);
        List<string> parts = [rendered.Trim()];
        string protectedContext = PromptProtectedContext.For(operation, values).Trim();
        if (protectedContext.Length > 0)
        {
            parts.Add(protectedContext);
        }
        compiled.Content = string.Join("\n\n", parts);
        return compiled;
    }

    /// <summary>
    /// 渲染模板变量。对应 Go: <c>RenderPromptTemplate</c>。
    /// </summary>
    /// <remarks>
    /// 模板里的每个占位符都必须在该操作声明过的变量表内（先在保存时校验，这里再兜一道，
    /// 因为用户定制正文是另一条写入路径）。取值按 Go 语义：缺键即为空串，且一律 TrimSpace。
    /// </remarks>
    public static string RenderPromptTemplate(
        PromptOperationDefinition definition,
        string content,
        IReadOnlyDictionary<string, string> values)
    {
        ValidatePlaceholders(definition, content);

        string rendered = content;
        foreach (PromptTemplateVariable variable in definition.Variables)
        {
            string key = variable.Placeholder.TrimStart('{').TrimEnd('}');
            string replacement = values.TryGetValue(key, out string? value) ? value.Trim() : "";
            rendered = rendered.Replace(variable.Placeholder, replacement, StringComparison.Ordinal);
        }
        return rendered.Trim();
    }

    /// <summary>
    /// 把任务输入里的 <c>metadata.promptTemplateVariables</c> 摊平成字符串表。
    /// 对应 Go: <c>metadataStringValues</c>（<c>provider.go</c>）。
    /// </summary>
    /// <remarks>
    /// 值可能是 JSON 反序列化后的 <see cref="JsonElement"/>，也可能是普通 <c>string</c>
    /// （测试直接构造）。Go 用 <c>fmt.Sprint</c> 统一成字符串，这里按 JSON 种类等价处理。
    /// </remarks>
    public static Dictionary<string, string> TemplateValues(object? raw)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (raw is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return values;
        }
        foreach (JsonProperty property in element.EnumerateObject())
        {
            values[property.Name] = ScalarText(property.Value).Trim();
        }
        return values;
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        // 与 Go 的 fmt.Sprint(nil) 一致（JSON null 反序列化成 interface{} 就是 nil）。
        JsonValueKind.Null or JsonValueKind.Undefined => "<nil>",
        _ => element.GetRawText(),
    };

    // ------------------------------------------------------------ 结果校验

    /// <summary>
    /// 校验模型返回是否符合该操作的受保护 JSON 契约。
    /// 对应 Go: <c>ValidatePromptTemplateResult</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只对 <c>outputType == "json"</c> 的操作生效。第一步统一从正文里抽 JSON
    /// （模型经常夹带解释与 Markdown 代码块），抽出失败即判契约违规。
    /// </para>
    /// <para>
    /// 失败一律抛 <see cref="AppError"/>：任务级失败文案走
    /// <c>ProviderErrorMessages.UserFacing</c>，只有 <c>AppError.Message</c> 会原样透出给用户，
    /// 兜底分支会把它显示成「连接模型服务失败」，那会把排障方向带偏。
    /// </para>
    /// </remarks>
    public static void ValidatePromptTemplateResult(
        string operation, IReadOnlyDictionary<string, object?> result)
    {
        PromptOperationDefinition? definition = FindDefinition(operation);
        if (definition is null || definition.OutputType != "json")
        {
            return;
        }

        // Go 用带 ok 的类型断言：非字符串一律当空正文（从而落到契约错误），不抛类型异常。
        string text = result.TryGetValue("text", out object? value) ? value as string ?? "" : "";
        if (!TryExtractContractJson(operation, text, out string jsonText))
        {
            throw AppError.BadAuthRequest(
                $"{definition.Label} 返回内容不符合受保护 JSON 契约：{PromptJsonExtract.NotJsonMessage}");
        }

        switch (operation)
        {
            case "chapter_assets_extract":
                ValidateChapterAssetsResult(jsonText);
                break;
            case "character_extract":
                ValidateCharacterExtractResult(jsonText);
                break;
            case "short_drama_outline":
                ValidateShortDramaOutlineResult(jsonText);
                break;
            case "skill_draft":
                ValidateSkillDraftResult(jsonText);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 按操作选择 JSON 抽取策略。对应 Go: <c>promptResultJSONExtractor</c>。
    /// </summary>
    /// <remarks>
    /// 角色卡 / 章节资产要求顶层对象，但模型常先用正文列一遍角色名数组；
    /// 大纲与技能草稿同理。这几类必须优先命中有契约键的<b>对象</b>，否则会抽到旁枝片段。
    /// </remarks>
    private static bool TryExtractContractJson(string operation, string raw, out string jsonText)
    {
        jsonText = "";
        return operation switch
        {
            "character_extract" or "chapter_assets_extract" =>
                PromptJsonExtract.TryExtractPreferredJsonText(raw, "characters", out jsonText),
            "short_drama_outline" =>
                PromptJsonExtract.TryExtractPreferredJsonText(raw, "chapters", out jsonText),
            "skill_draft" =>
                PromptJsonExtract.TryExtractPreferredJsonText(raw, "skillName", out jsonText),
            _ => PromptJsonExtract.TryExtractJsonText(raw, out jsonText),
        };
    }

    /// <summary>
    /// 章节资产：三段数组都必须存在，场景与道具的 name/description/prompt 必须非空；
    /// 角色段非空时再按角色卡契约校验。对应 Go: <c>validateChapterAssetsResult</c>。
    /// </summary>
    private static void ValidateChapterAssetsResult(string jsonText)
    {
        if (!TryParseObject(jsonText, out JsonElement root))
        {
            throw AppError.BadAuthRequest("章节资产提取 JSON 无法解析：顶层必须是一个 JSON 对象");
        }

        foreach (string group in new[] { "characters", "scenes", "props" })
        {
            if (!root.TryGetProperty(group, out JsonElement element)
                || element.ValueKind != JsonValueKind.Array)
            {
                throw AppError.BadAuthRequest("章节资产提取结果必须包含 characters、scenes 和 props 数组");
            }
        }

        foreach (string group in new[] { "scenes", "props" })
        {
            foreach (JsonElement asset in root.GetProperty(group).EnumerateArray())
            {
                foreach (string field in new[] { "name", "description", "prompt" })
                {
                    if (!asset.TryGetProperty(field, out JsonElement value)
                        || value.ValueKind != JsonValueKind.String
                        || value.GetString()!.Trim().Length == 0)
                    {
                        throw AppError.BadAuthRequest($"章节场景或道具缺少有效的 {field}");
                    }
                }
            }
        }

        if (root.GetProperty("characters").GetArrayLength() > 0)
        {
            ValidateCharacterExtractResult(jsonText);
        }
    }

    /// <summary>角色卡的必填字段（顺序即报错顺序）。对应 Go 的 <c>required</c>。</summary>
    private static readonly string[] CharacterRequiredFields =
    [
        "name", "aliases", "role", "appearance", "clothing", "physique", "personality",
        "props", "consistencyPrompt", "multiViewPrompt", "voiceLanguage", "voiceAge", "voiceTimbre",
    ];

    /// <summary>
    /// 角色卡提取结果校验。对应 Go: <c>validateCharacterExtractResult</c>。
    /// </summary>
    /// <remarks>
    /// 先做形态归一（见 <see cref="NormalizeCharacterBreakdownRootJson"/>），再做必填校验。
    /// 必填校验只判断字段<b>是否存在</b>（值可以是 null）——与 Go 的 <c>_, exists :=</c> 语义一致。
    /// </remarks>
    private static void ValidateCharacterExtractResult(string jsonText)
    {
        string normalized = NormalizeCharacterBreakdownRootJson(jsonText);
        if (!TryParseObject(normalized, out JsonElement root))
        {
            throw AppError.BadAuthRequest("角色卡提取返回的 JSON 无法解析：顶层必须是一个 JSON 对象");
        }
        if (!root.TryGetProperty("characters", out JsonElement characters)
            || characters.ValueKind != JsonValueKind.Array)
        {
            throw AppError.BadAuthRequest("角色卡提取结果缺少 characters 数组");
        }
        if (characters.GetArrayLength() == 0)
        {
            throw AppError.BadAuthRequest("角色卡提取结果没有识别到任何角色，请调整正文或重新提取");
        }

        int index = 0;
        foreach (JsonElement character in characters.EnumerateArray())
        {
            index++;
            foreach (string field in CharacterRequiredFields)
            {
                if (!character.TryGetProperty(field, out _))
                {
                    throw AppError.BadAuthRequest($"角色卡提取结果中第 {index} 个角色缺少字段 {field}");
                }
            }
        }
    }

    /// <summary>
    /// 短剧大纲校验。对应 Go: <c>validateShortDramaOutlineResult</c>。
    /// </summary>
    private static void ValidateShortDramaOutlineResult(string jsonText)
    {
        if (!TryParseObject(jsonText, out JsonElement root))
        {
            throw AppError.BadAuthRequest("短剧大纲 JSON 无法解析：顶层必须是一个 JSON 对象");
        }
        if (TrimmedString(root, "title").Length == 0)
        {
            throw AppError.BadAuthRequest("短剧大纲缺少 title");
        }
        if (TrimmedString(root, "synopsis").Length == 0)
        {
            throw AppError.BadAuthRequest("短剧大纲缺少 synopsis");
        }
        if (!root.TryGetProperty("chapters", out JsonElement chapters)
            || chapters.ValueKind != JsonValueKind.Array)
        {
            throw AppError.BadAuthRequest("短剧大纲缺少 chapters 数组");
        }
        if (chapters.GetArrayLength() == 0)
        {
            throw AppError.BadAuthRequest("短剧大纲没有生成任何章节");
        }

        int index = 0;
        foreach (JsonElement chapter in chapters.EnumerateArray())
        {
            index++;
            if (TrimmedString(chapter, "title").Length == 0
                || TrimmedString(chapter, "content").Length == 0)
            {
                throw AppError.BadAuthRequest($"短剧大纲第 {index} 章缺少 title 或 content");
            }
        }
    }

    /// <summary>
    /// 技能草稿校验。对应 Go: <c>validateSkillDraftResult</c>。
    /// </summary>
    private static void ValidateSkillDraftResult(string jsonText)
    {
        if (!TryParseObject(jsonText, out JsonElement root))
        {
            throw AppError.BadAuthRequest("技能草稿 JSON 无法解析：顶层必须是一个 JSON 对象");
        }
        foreach ((string field, string label) in new[]
        {
            ("skillName", "skillName"),
            ("tag", "tag"),
            ("description", "description"),
            ("instruction", "instruction"),
        })
        {
            if (TrimmedString(root, field).Length == 0)
            {
                throw AppError.BadAuthRequest($"技能草稿缺少 {label}");
            }
        }
    }

    // ------------------------------------------------------------ 角色卡形态归一

    /// <summary>
    /// 把模型返回的角色卡结果收敛为 character-breakdown/v1 的 <c>{"characters":[...]}</c> 形态。
    /// 对应 Go: <c>normalizeCharacterBreakdownRootJSON</c>。
    /// </summary>
    /// <remarks>
    /// <para>模型并不总是遵守顶层 object 契约，实测偏离包括：</para>
    /// <list type="bullet">
    /// <item>顶层直接返回角色数组 <c>[{name,...},...]</c>；</item>
    /// <item>数组外再包一层 <c>[[{...}]]</c>；</item>
    /// <item>每个角色单独包一层 <c>[{"characters":[...]}, ...]</c>；</item>
    /// <item>数组里混入非角色对象或 prose 片段 <c>[{"index":1},{...}]</c>。</item>
    /// </list>
    /// <para>
    /// 这里<b>只做形态归一</b>，字段完整性仍交给上面的必填校验，
    /// 因此收集阶段按宽松规则保留候选，再由必填校验给出「第 N 个角色缺少字段 X」的可读提示。
    /// </para>
    /// </remarks>
    private static string NormalizeCharacterBreakdownRootJson(string jsonText)
    {
        string trimmed = jsonText.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }
        if (trimmed[0] != '[')
        {
            return NormalizeCharacterBreakdownObject(trimmed);
        }
        if (!TryParseRawArray(trimmed, out List<string>? root))
        {
            return trimmed;
        }
        return MarshalCharacterBreakdown(CollectCharacterCards(root!));
    }

    /// <summary>
    /// 顶层已是对象时，只把 <c>characters</c> 收敛成数组。
    /// 对应 Go: <c>normalizeCharacterBreakdownObject</c>。
    /// </summary>
    private static string NormalizeCharacterBreakdownObject(string jsonText)
    {
        if (!TryParseRawObject(jsonText, out List<KeyValuePair<string, string>>? members))
        {
            return jsonText;
        }
        int index = members!.FindIndex(member => member.Key == "characters");
        if (index < 0)
        {
            return jsonText;
        }

        string raw = members[index].Value;
        if (TryParseRawArray(raw, out List<string>? cards))
        {
            // characters 已位于契约位置：只摊平嵌套，**不过滤元素**——
            // 缺少 name 的角色要留给必填校验报出具体字段，而不是被静默丢弃。
            members[index] = new KeyValuePair<string, string>(
                "characters", MarshalRawArray(FlattenCharacterCardValues(cards!)));
        }
        else if (TryParseRawObject(raw, out List<KeyValuePair<string, string>>? indexed))
        {
            // characters 被写成以角色名为键的对象时降级为取值列表。
            members[index] = new KeyValuePair<string, string>(
                "characters", MarshalRawArray(indexed!.Select(entry => entry.Value)));
        }
        else
        {
            return jsonText;
        }

        return MarshalRawObject(members);
    }

    /// <summary>
    /// 递归展开顶层数组，只保留「可辨认的角色卡对象」（带 <c>name</c>）。
    /// 对应 Go: <c>collectCharacterCards</c>。
    /// </summary>
    private static List<string> CollectCharacterCards(IEnumerable<string> values)
    {
        List<string> result = [];
        foreach (string raw in FlattenCharacterCardValues(values))
        {
            if (LooksLikeCharacterCard(raw))
            {
                result.Add(raw);
            }
        }
        return result;
    }

    /// <summary>
    /// 递归摊平嵌套数组与 <c>{"characters":[...]}</c> 包装，不判断元素是否是角色。
    /// 对应 Go: <c>flattenCharacterCardValues</c>。
    /// </summary>
    private static List<string> FlattenCharacterCardValues(IEnumerable<string> values)
    {
        List<string> result = [];
        foreach (string raw in values)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (trimmed[0] == '[')
            {
                if (TryParseRawArray(trimmed, out List<string>? nested))
                {
                    result.AddRange(FlattenCharacterCardValues(nested!));
                }
                continue;
            }
            if (trimmed[0] != '{')
            {
                continue;
            }
            if (!TryParseRawObject(trimmed, out List<KeyValuePair<string, string>>? members))
            {
                continue;
            }
            int index = members!.FindIndex(member => member.Key == "characters");
            if (index >= 0 && TryParseRawArray(members[index].Value, out List<string>? inner))
            {
                result.AddRange(FlattenCharacterCardValues(inner!));
                continue;
            }
            result.Add(trimmed);
        }
        return result;
    }

    /// <summary>带 <c>name</c> 的对象即视为角色候选。对应 Go: <c>looksLikeCharacterCard</c>。</summary>
    private static bool LooksLikeCharacterCard(string raw) =>
        TryParseRawObject(raw, out List<KeyValuePair<string, string>>? members)
        && members!.Any(member => member.Key == "name");

    private static string MarshalCharacterBreakdown(IEnumerable<string> cards) =>
        "{\"characters\":" + MarshalRawArray(cards) + "}";

    private static string MarshalRawArray(IEnumerable<string> values) =>
        "[" + string.Join(",", values) + "]";

    private static string MarshalRawObject(IEnumerable<KeyValuePair<string, string>> members) =>
        "{" + string.Join(",", members.Select(member =>
            JsonSerializer.Serialize(member.Key) + ":" + member.Value)) + "}";

    // ------------------------------------------------------------ JSON 小工具

    /// <summary>解析为 JSON 对象；失败或非对象返回 false。</summary>
    private static bool TryParseObject(string text, out JsonElement root)
    {
        root = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>解析为 JSON 数组，保留每个元素的原始文本（供后续原样回填）。</summary>
    private static bool TryParseRawArray(string text, out List<string>? values)
    {
        values = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            values = document.RootElement.EnumerateArray()
                .Select(element => element.GetRawText())
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>解析为 JSON 对象，按文档顺序保留每个属性的原始文本。</summary>
    private static bool TryParseRawObject(string text, out List<KeyValuePair<string, string>>? members)
    {
        members = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            members = document.RootElement.EnumerateObject()
                .Select(property => new KeyValuePair<string, string>(property.Name, property.Value.GetRawText()))
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>取字符串字段并 Trim；缺字段、非字符串一律为空串。</summary>
    private static string TrimmedString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : "";
}
