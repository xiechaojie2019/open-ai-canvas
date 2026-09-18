#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Application.Prompts;

/// <summary>提示词模板变量。对应 Go: <c>prompts.PromptTemplateVariable</c>。</summary>
public sealed class PromptTemplateVariable
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = "";
}

/// <summary>
/// 提示词操作定义。对应 Go: <c>prompts.PromptOperationDefinition</c>。
/// </summary>
/// <remarks>
/// <c>DefaultContent</c> 在 Go 里是 <c>json:"-"</c>，<b>绝不下发给前端</b>
/// （它是内置模板正文，属于服务端资产）。
/// </remarks>
public sealed class PromptOperationDefinition
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("outputType")]
    public string OutputType { get; set; } = "";

    [JsonPropertyName("schemaKey")]
    [GoOmitEmpty]
    public string SchemaKey { get; set; } = "";

    [JsonPropertyName("variables")]
    public List<PromptTemplateVariable> Variables { get; set; } = [];

    [JsonPropertyName("outputContract")]
    public string OutputContract { get; set; } = "";

    /// <summary>内置模板正文。对应 Go 的 <c>json:"-"</c>，不参与序列化。</summary>
    [JsonIgnore]
    public string DefaultContent { get; set; } = "";
}

/// <summary>创建 / 更新提示词模板请求。对应 Go: <c>prompts.PromptTemplateRequest</c>。</summary>
public sealed class PromptTemplateRequest
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>Go 是指针：区分「未提交」与「提交 false」。</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
