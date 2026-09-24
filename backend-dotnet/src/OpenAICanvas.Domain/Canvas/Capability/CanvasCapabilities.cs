#nullable enable
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Domain.Canvas.Capability;

/// <summary>patch 字段类型契约。对应 Go: <c>capability.patchKind*</c>。</summary>
internal static class PatchKinds
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
}

/// <summary>连线策略。对应 Go: <c>capability.ConnectionPolicy</c>。</summary>
public sealed class ConnectionPolicy
{
    public bool CanSource { get; init; }
    public bool CanTarget { get; init; }
    public bool CanReference { get; init; }
    public string[] AcceptedInputKinds { get; init; } = [];
    public string[] RejectedInputKinds { get; init; } = [];
    public int MaxInputCount { get; init; }
}

/// <summary>可更新字段契约。对应 Go: <c>capability.PatchField</c>。</summary>
public sealed class PatchField
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public int Order { get; init; }
    public string Description { get; init; } = "";
    public int MaxRunes { get; init; }
}

/// <summary>
/// 服务端持有的画布节点契约。Agent 工具、创作、持久化和状态投影共用本注册表，
/// 不存在第二份节点白名单。对应 Go: <c>capability.Descriptor</c>。
/// </summary>
public sealed class CapabilityDescriptor
{
    public string Type { get; init; } = "";
    public string Version { get; init; } = "";
    public string Label { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string[] GoodFor { get; init; } = [];
    public string[] NotIdealFor { get; init; } = [];
    public string[] Tradeoffs { get; init; } = [];
    public string[] Actions { get; init; } = [];
    public double DefaultWidth { get; init; }
    public double DefaultHeight { get; init; }
    public string InputKind { get; init; } = "";
    public string GenerationMode { get; init; } = "";
    public ConnectionPolicy Connection { get; init; } = new();
    public bool CanUpdate { get; init; }
    public string[] SummaryFields { get; init; } = [];
    public string[] DetailFields { get; init; } = [];
    public string ProjectionKind { get; init; } = "";
    public string ProjectionField { get; init; } = "";
    public IReadOnlyDictionary<string, PatchField> PatchFields { get; init; } =
        new Dictionary<string, PatchField>(StringComparer.Ordinal);

    public Func<string, JsonObject?>? CreateMetadata { get; init; }

    public JsonObject? Metadata(string content) => CreateMetadata is null
        ? new JsonObject { ["content"] = content, ["status"] = "idle" }
        : CreateMetadata(content);

    public bool AllowsInput(string kind)
    {
        if (!Connection.CanTarget)
        {
            return false;
        }
        kind = kind.Trim().ToLowerInvariant();
        foreach (string rejected in Connection.RejectedInputKinds)
        {
            if (rejected == kind)
            {
                return false;
            }
        }
        if (Connection.AcceptedInputKinds.Length == 0)
        {
            return true;
        }
        foreach (string accepted in Connection.AcceptedInputKinds)
        {
            if (accepted == kind)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>校验 patch 字段。失败抛出与 Go 相同文案的 <see cref="ArgumentException"/>。</summary>
    public void ValidatePatch(JsonObject patch)
    {
        if (!CanUpdate)
        {
            throw new ArgumentException($"{Label} 节点不支持更新");
        }
        if (patch.Count == 0)
        {
            throw new ArgumentException($"{Label} 节点更新内容不能为空");
        }
        foreach ((string key, JsonNode? value) in patch)
        {
            if (!PatchFields.TryGetValue(key, out PatchField? field))
            {
                throw new ArgumentException($"{Label} 节点不支持更新字段 {key}");
            }
            switch (field.Kind)
            {
                case "string":
                    if (value is not JsonValue || value.GetValueKind() != JsonValueKind.String)
                    {
                        throw new ArgumentException($"{Label} 字段 {key} 必须是字符串");
                    }
                    if (field.MaxRunes > 0 && value!.GetValue<string>().Length > field.MaxRunes)
                    {
                        throw new ArgumentException($"{Label} 字段 {key} 超出长度限制");
                    }
                    break;
                case "number":
                    if (value is not JsonValue || value.GetValueKind() != JsonValueKind.Number)
                    {
                        throw new ArgumentException($"{Label} 字段 {key} 必须是数字");
                    }
                    break;
                case "boolean":
                    if (value is not JsonValue || (value.GetValueKind() != JsonValueKind.True && value.GetValueKind() != JsonValueKind.False))
                    {
                        throw new ArgumentException($"{Label} 字段 {key} 必须是布尔值");
                    }
                    break;
                default:
                    throw new ArgumentException($"{Label} 字段 {key} 的类型契约无效");
            }
        }
    }

    /// <summary>按字段 Path 把 patch 写入节点。对应 Go: <c>ApplyPatch</c>。</summary>
    public void ApplyPatch(JsonObject node, JsonObject patch)
    {
        ValidatePatch(patch);
        foreach ((string key, JsonNode? value) in patch)
        {
            string[] parts = PatchFields[key].Path.Split('.');
            JsonObject target = node;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                if (target[parts[index]] is not JsonObject child)
                {
                    child = new JsonObject();
                    target[parts[index]] = child;
                }
                target = child;
            }
            target[parts[^1]] = value?.DeepClone();
        }
    }

    /// <summary>校验目标节点可否接收指定输入。错误文案与 Go 一致。</summary>
    public void ValidateConnection(string fromKind)
    {
        if (!Connection.CanTarget)
        {
            throw new ArgumentException($"{Label} 节点不能接收参考输入");
        }
        if (!AllowsInput(fromKind))
        {
            throw new ArgumentException($"{Label}生成节点不接受{CloudAgentInputKindLabel(fromKind)}输入");
        }
    }

    internal static string CloudAgentInputKindLabel(string kind) => kind switch
    {
        "image" => "图片",
        "video" => "视频",
        "audio" => "音频",
        _ => "文本",
    };
}

/// <summary>
/// 画布能力注册表。对应 Go: <c>capability.Registry</c>；
/// 哈希必须与 Go json.Marshal(registryHashItems) 逐字节一致。
/// </summary>
public sealed class CapabilityRegistry
{
    public const string SetVersion = "canvas-capabilities/v4";

    private readonly ConcurrentDictionary<string, CapabilityDescriptor> _descriptors;

    private CapabilityRegistry(IEnumerable<CapabilityDescriptor> descriptors)
    {
        _descriptors = new(descriptors.ToDictionary(d => d.Type, d => d), StringComparer.Ordinal);
    }

    public static CapabilityRegistry Build(IEnumerable<CapabilityDescriptor> descriptors)
    {
        // 内置清单为编译期常量，注册期校验仅在 Build 可见；非法配置直接抛出。
        List<CapabilityDescriptor> items = [];
        Dictionary<string, string> generationModes = new(StringComparer.Ordinal);
        foreach (CapabilityDescriptor source in descriptors)
        {
            CapabilityDescriptor descriptor = source;
            if (descriptor.Type.Length == 0 || descriptor.Version.Length == 0 || descriptor.Label.Length == 0
                || descriptor.DefaultWidth <= 0 || descriptor.DefaultHeight <= 0)
            {
                throw new InvalidOperationException($"invalid canvas capability descriptor {descriptor.Type}");
            }
            if (descriptor.GenerationMode.Length > 0)
            {
                if (generationModes.TryGetValue(descriptor.GenerationMode, out string? existing))
                {
                    throw new InvalidOperationException(
                        $"generation mode \"{descriptor.GenerationMode}\" is registered by both \"{existing}\" and \"{descriptor.Type}\"");
                }
                generationModes[descriptor.GenerationMode] = descriptor.Type;
            }
            if ((descriptor.ProjectionKind == "") != (descriptor.ProjectionField == ""))
            {
                throw new InvalidOperationException(
                    $"capability \"{descriptor.Type}\" must declare projection kind and field together");
            }
            if (descriptor.Connection.MaxInputCount < 0)
            {
                throw new InvalidOperationException($"capability \"{descriptor.Type}\" has invalid max input count");
            }
            HashSet<string> rejected = new(descriptor.Connection.RejectedInputKinds, StringComparer.Ordinal);
            foreach (string kind in descriptor.Connection.AcceptedInputKinds)
            {
                if (rejected.Contains(kind))
                {
                    throw new InvalidOperationException(
                        $"capability \"{descriptor.Type}\" accepts and rejects input kind \"{kind}\"");
                }
            }

            if (descriptor.Connection.CanSource && descriptor.InputKind.Length == 0)
            {
                throw new InvalidOperationException($"capability \"{descriptor.Type}\" can source without an input kind");
            }
            if (descriptor.CanUpdate != (descriptor.PatchFields.Count > 0))
            {
                throw new InvalidOperationException(
                    $"capability \"{descriptor.Type}\" must declare both canUpdate and patch fields, or neither");
            }
            items.Add(descriptor);
        }
        return new CapabilityRegistry(items);
    }

    public (CapabilityDescriptor Descriptor, bool Found) Resolve(string nodeType) =>
        _descriptors.TryGetValue(nodeType.Trim().ToLowerInvariant(), out CapabilityDescriptor? descriptor)
            ? (descriptor, true)
            : (new CapabilityDescriptor(), false);

    public List<CapabilityDescriptor> List() =>
        [.. _descriptors.Values.OrderBy(d => d.Type, StringComparer.Ordinal)];

    public string[] Types() => [.. List().Select(d => d.Type)];

    public string[] GenerationModeNames() =>
        [.. List().Select(d => d.GenerationMode).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal)];

    public bool SupportsGenerationMode(string mode) => ResolveGenerationMode(mode).Found;

    public (CapabilityDescriptor Descriptor, bool Found) ResolveGenerationMode(string mode)
    {
        mode = mode.Trim().ToLowerInvariant();
        if (mode.Length == 0)
        {
            return (new CapabilityDescriptor(), false);
        }
        foreach (CapabilityDescriptor descriptor in _descriptors.Values)
        {
            if (descriptor.GenerationMode == mode)
            {
                return (descriptor, true);
            }
        }
        return (new CapabilityDescriptor(), false);
    }


    /// <summary>与 Go json.Marshal(registryHashItems(...)) 逐字节一致的能力集哈希。</summary>
    public string Hash()
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(HashInputJson()));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>哈希原文（测试诊断用）。</summary>
    public string HashInputJsonForTest() => HashInputJson();

    private string HashInputJson()
    {
        StringBuilder json = new("[");
        bool first = true;
        foreach (CapabilityDescriptor d in List())
        {
            if (!first)
            {
                json.Append(',');
            }
            first = false;
            json.Append("{\"Type\":").Append(GoString(d.Type));
            json.Append(",\"Version\":").Append(GoString(d.Version));
            json.Append(",\"Label\":").Append(GoString(d.Label));
            json.Append(",\"Purpose\":").Append(GoString(d.Purpose));
            json.Append(",\"GoodFor\":").Append(GoStrings(d.GoodFor));
            json.Append(",\"NotIdealFor\":").Append(GoStrings(d.NotIdealFor));
            json.Append(",\"Tradeoffs\":").Append(GoStrings(d.Tradeoffs));
            json.Append(",\"Actions\":").Append(GoStrings(d.Actions));
            json.Append(",\"DefaultWidth\":").Append(GoNumber(d.DefaultWidth));
            json.Append(",\"DefaultHeight\":").Append(GoNumber(d.DefaultHeight));
            json.Append(",\"InputKind\":").Append(GoString(d.InputKind));
            json.Append(",\"GenerationMode\":").Append(GoString(d.GenerationMode));
            json.Append(",\"Connection\":{\"CanSource\":").Append(d.Connection.CanSource ? "true" : "false");
            json.Append(",\"CanTarget\":").Append(d.Connection.CanTarget ? "true" : "false");
            json.Append(",\"CanReference\":").Append(d.Connection.CanReference ? "true" : "false");
            // Go normalizeConnectionKinds 输出按字典序排序（哈希契约依赖）。
            json.Append(",\"AcceptedInputKinds\":").Append(GoStrings([.. d.Connection.AcceptedInputKinds.OrderBy(k => k, StringComparer.Ordinal)]));
            json.Append(",\"RejectedInputKinds\":").Append(GoStrings([.. d.Connection.RejectedInputKinds.OrderBy(k => k, StringComparer.Ordinal)]));
            json.Append(",\"MaxInputCount\":").Append(d.Connection.MaxInputCount).Append('}');
            json.Append(",\"CanUpdate\":").Append(d.CanUpdate ? "true" : "false");
            json.Append(",\"SummaryFields\":").Append(GoStrings(d.SummaryFields));
            json.Append(",\"DetailFields\":").Append(GoStrings(d.DetailFields));
            json.Append(",\"ProjectionKind\":").Append(GoString(d.ProjectionKind));
            json.Append(",\"ProjectionField\":").Append(GoString(d.ProjectionField));
            json.Append(",\"PatchFields\":[");
            bool firstField = true;
            foreach ((string key, PatchField field) in d.PatchFields.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!firstField)
                {
                    json.Append(',');
                }
                firstField = false;
                json.Append("{\"Key\":").Append(GoString(key));
                json.Append(",\"Path\":").Append(GoString(field.Path));
                json.Append(",\"Kind\":").Append(GoString(field.Kind));
                json.Append(",\"Label\":").Append(GoString(field.Label));
                json.Append(",\"Order\":").Append(field.Order);
                json.Append(",\"Description\":").Append(GoString(field.Description));
                json.Append(",\"MaxRunes\":").Append(field.MaxRunes).Append('}');
            }
            json.Append("]}");
        }
        json.Append(']');
        return json.ToString();
    }

    /// <summary>Go json.Marshal 字符串转义（非 ASCII 原样，HTML 字符转义）。</summary>
    private static string GoString(string value)
    {
        StringBuilder result = new("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                case '<': result.Append("\\u003c"); break;
                case '>': result.Append("\\u003e"); break;
                case '&': result.Append("\\u0026"); break;
                case '\u2028': result.Append("\\u2028"); break;
                case '\u2029': result.Append("\\u2029"); break;
                default:
                    if (c < 0x20)
                    {
                        result.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        result.Append(c);
                    }
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    private static string GoStrings(string[] values)
    {
        // Go 侧经 cloneDescriptor 的 append(nil-slice) 后，空切片为 nil → 序列化为 null。
        if (values.Length == 0)
        {
            return "null";
        }
        StringBuilder result = new("[");
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                result.Append(',');
            }
            result.Append(GoString(values[index]));
        }
        return result.Append(']').ToString();
    }

    /// <summary>Go float64 编码：整数值不带小数点，其余用最短往返表示。</summary>
    private static string GoNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public static class BuiltinCanvasCapabilities
{
    private const int MaxAgentNodeTitleRunes = 240;
    private const int MaxAgentNodeContentRunes = 16000;

    public static CapabilityRegistry BuiltinRegistry() => CapabilityRegistry.Build(
    [
        new CapabilityDescriptor
        {
            Type = "text", Version = "1", Label = "文本", DefaultWidth = 340, DefaultHeight = 240,
            Purpose = "承载普通说明、创意草稿和单段提示词。",
            GoodFor = ["单个创意", "一次性提示词", "临时备注", "快速试验"],
            NotIdealFor = ["多镜头脚本", "需要逐镜修改的内容", "需要镜头级资产关系的内容"],
            Tradeoffs = ["创建和编辑最轻量", "没有镜头级字段和逐镜维护能力"],
            InputKind = "text", Connection = new ConnectionPolicy { CanSource = true }, CanUpdate = true,
            SummaryFields = ["content"], DetailFields = ["content"],
            PatchFields = EditableNodeFields("metadata.content", "正文", "节点正文"),
            CreateMetadata = content => new JsonObject
            {
                ["content"] = content, ["status"] = "idle", ["fontSize"] = 14d,
            },
        },
        new CapabilityDescriptor
        {
            Type = "markdown", Version = "1", Label = "Markdown", DefaultWidth = 420, DefaultHeight = 320,
            Purpose = "承载面向人阅读的方案、脚本草稿和格式化文档。",
            GoodFor = ["创意方案", "脚本草稿", "交付文档", "需要排版的长文本"],
            NotIdealFor = ["需要逐镜生成或审核的多镜头内容", "需要绑定媒体资产的结构化流程"],
            Tradeoffs = ["适合阅读和导出", "结构化程度低，不能替代可维护的分镜表"],
            InputKind = "text", Connection = new ConnectionPolicy { CanSource = true }, CanUpdate = true,
            SummaryFields = ["content"], DetailFields = ["content"],
            PatchFields = EditableNodeFields("metadata.content", "Markdown 正文", "Markdown 正文"),
        },
        GeneratedMediaDescriptor("image", "2", "图片", 720, 405, "image", new ConnectionPolicy
        {
            CanSource = true, CanTarget = true, CanReference = true, AcceptedInputKinds = ["text", "image"],
        }),
        GeneratedMediaDescriptor("video", "2", "视频", 720, 405, "video", new ConnectionPolicy
        {
            CanSource = true, CanTarget = true, CanReference = true, AcceptedInputKinds = ["text", "image", "video", "audio"],
        }),
        GeneratedMediaDescriptor("audio", "2", "音频", 340, 120, "audio", new ConnectionPolicy
        {
            CanSource = true, CanTarget = true, CanReference = true, MaxInputCount = 1, AcceptedInputKinds = ["text"],
        }),
        new CapabilityDescriptor
        {
            Type = "frame", Version = "1", Label = "背板", DefaultWidth = 760, DefaultHeight = 520,
            Purpose = "组织一组相关节点的画布区域。",
            GoodFor = ["按场景整理节点", "划分工作区域"],
            NotIdealFor = ["承载结构化镜头数据", "替代具体业务节点"],
            Tradeoffs = ["改善空间组织但不增加内容结构或生成能力"],
            SummaryFields = ["label"], DetailFields = ["label"],
            CreateMetadata = _ => new JsonObject
            {
                ["frame"] = new JsonObject
                {
                    ["collapsed"] = false, ["expandedWidth"] = 760d, ["expandedHeight"] = 520d,
                },
            },
        },
        new CapabilityDescriptor
        {
            Type = "batch-table", Version = "1", Label = "批量创作表", DefaultWidth = 900, DefaultHeight = 520,
            Purpose = "面向电商批量换装和创意生图的结构化任务表；每行绑定最多六组画布图片、可用 @参考图1 等位置引用编写独立提示词，并追踪生成结果。",
            GoodFor = ["商品与模特批量换装", "同一商品多场景创意图", "多组参考图组合生成", "批量结果追踪与失败重试"],
            NotIdealFor = ["通用数据库或库存管理", "单张图片快速试验", "多镜头叙事连续性"],
            Tradeoffs = ["参考图必须先作为图片节点进入画布", "批量提交会产生多项生成任务，执行前必须确认模型、数量和费用"],
            Actions = ["read_rows", "append_row", "update_row", "remove_row", "set_operation", "set_concurrency", "add_reference_column", "preview_batch_generation"],
            InputKind = "text",
            Connection = new ConnectionPolicy { CanSource = true, CanTarget = true, AcceptedInputKinds = ["image"] },
            CanUpdate = true, SummaryFields = ["batchTable"], DetailFields = ["batchTable"],
            ProjectionKind = "batch_table", ProjectionField = "batchTable",
            PatchFields = new Dictionary<string, PatchField>(StringComparer.Ordinal)
            {
                ["title"] = new PatchField
                {
                    Path = "title", Kind = PatchKinds.String, Label = "节点名称", Order = 10,
                    Description = "批量创作表标题", MaxRunes = MaxAgentNodeTitleRunes,
                },
            },
            CreateMetadata = _ => new JsonObject
            {
                ["status"] = "idle",
                ["batchTable"] = new JsonObject
                {
                    ["operation"] = "try_on", ["concurrency"] = 10d,
                    ["referenceColumns"] = new JsonArray(
                        new JsonObject { ["id"] = "reference-1", ["label"] = "参考图 1" },
                        new JsonObject { ["id"] = "reference-2", ["label"] = "参考图 2" },
                        new JsonObject { ["id"] = "reference-3", ["label"] = "参考图 3" }),
                    ["rows"] = new JsonArray(),
                },
            },
        },
        new CapabilityDescriptor
        {
            Type = "script", Version = "1", Label = "分镜脚本", DefaultWidth = 920, DefaultHeight = 360,
            Purpose = "维护结构化的多镜头脚本，支持镜头级审查、修改和媒体关联。",
            GoodFor = ["多镜头规划", "镜头连续性", "逐镜审查和微调", "逐镜生成图片或视频", "需要他人接手维护的内容"],
            NotIdealFor = ["只有一个画面的快速试验", "一次性临时提示词", "仅需要阅读排版的普通文档"],
            Tradeoffs = ["前期录入成本高于文本节点", "但能保留镜头级结构、资产关系和后续维护能力"],
            Actions = ["read_rows", "append_row", "update_row", "remove_row", "generate_storyboard"],
            SummaryFields = ["storyboard"], DetailFields = ["storyboard"],
            ProjectionKind = "storyboard", ProjectionField = "storyboard",
            CreateMetadata = _ => new JsonObject
            {
                ["status"] = "idle", ["workflowKind"] = "script",
                ["storyboard"] = new JsonObject
                {
                    ["rows"] = new JsonArray(),
                    ["visibleColumns"] = new JsonArray("shotNumber", "durationSeconds", "videoMotionPrompt", "dialogue", "assets"),
                    ["referenceNodeIds"] = new JsonArray(),
                },
            },
        },
    ]);

    private static CapabilityDescriptor GeneratedMediaDescriptor(
        string nodeType, string version, string label, double width, double height,
        string generationMode, ConnectionPolicy connection)
    {
        (string Purpose, string[] GoodFor, string[] NotIdealFor, string[] Tradeoffs, string[] Actions) semantics =
            GeneratedMediaSemantics(nodeType);
        return new CapabilityDescriptor
        {
            Type = nodeType, Version = version, Label = label, DefaultWidth = width, DefaultHeight = height,
            Purpose = semantics.Purpose, GoodFor = semantics.GoodFor, NotIdealFor = semantics.NotIdealFor,
            Tradeoffs = semantics.Tradeoffs, Actions = semantics.Actions,
            InputKind = nodeType, GenerationMode = generationMode, Connection = connection, CanUpdate = true,
            SummaryFields = ["prompt", "composerContent", "assetTags", "referenceNodeIds"],
            DetailFields = ["prompt", "composerContent", "assetTags", "referenceNodeIds"],
            PatchFields = EditableNodeFields("metadata.composerContent", "下一版提示词", "下次生成使用的提示词草稿；不覆盖已提交提示词或媒体结果"),
            CreateMetadata = prompt => new JsonObject
            {
                ["content"] = "", ["prompt"] = prompt, ["composerContent"] = prompt, ["status"] = "idle",
            },
        };
    }

    private static (string Purpose, string[] GoodFor, string[] NotIdealFor, string[] Tradeoffs, string[] Actions) GeneratedMediaSemantics(string nodeType) => nodeType switch
    {
        "image" => (
            "生成或承载一个静态画面，并作为后续图片或视频生成的真实参考素材。",
            new[] { "单张图片生成", "有参考图的图片生成", "分镜首帧和关键帧", "需要复用的视觉素材" },
            new[] { "承载多镜头脚本结构", "表达镜头运动或时间变化", "代替分镜表维护镜头连续性" },
            new[] { "一个节点对应一个静态媒体目标", "参考图数量和生成方式必须再由模型目录能力匹配" },
            new[] { "generate_media", "update_prompt", "use_as_reference" }),
        "video" => (
            "生成或承载一个连续视频片段；可按实际参考素材执行文生视频、图生视频或多图生视频。",
            new[] { "单镜头文生视频", "单图生视频", "多图参考视频", "已有视频或音频参与的视频生成" },
            new[] { "承载整部多镜头脚本", "用一个节点代替逐镜审查和维护", "未查询模型能力就假定支持任意参考数量" },
            new[] { "一个节点通常对应一个可独立生成和审核的视频片段", "参考类型、数量、时长、画幅、音频和价格受当前模型目录约束" },
            new[] { "generate_media", "update_prompt", "use_as_reference" }),
        "audio" => (
            "生成或承载一个音频素材，用于配音、音乐、音效或视频参考输入。",
            new[] { "文本转语音", "配音", "音乐或音效素材", "为视频提供音频参考" },
            new[] { "承载分镜结构", "表达画面构图或镜头运动", "代替视频节点" },
            new[] { "一个节点对应一个音频目标且最多接收一个文本输入", "音频类型、时长和价格仍以模型目录及任务准入为准" },
            new[] { "generate_media", "update_prompt", "use_as_reference" }),
        _ => ("", [], [], [], []),
    };

    private static Dictionary<string, PatchField> EditableNodeFields(string contentPath, string contentLabel, string contentDescription) => new(StringComparer.Ordinal)
    {
        ["title"] = new PatchField
        {
            Path = "title", Kind = PatchKinds.String, Label = "节点名称", Order = 10,
            Description = "节点标题", MaxRunes = MaxAgentNodeTitleRunes,
        },
        ["content"] = new PatchField
        {
            Path = contentPath, Kind = PatchKinds.String, Label = contentLabel, Order = 20,
            Description = contentDescription, MaxRunes = MaxAgentNodeContentRunes,
        },
    };
}
