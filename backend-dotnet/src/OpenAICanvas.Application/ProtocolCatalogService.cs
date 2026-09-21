#nullable enable

using OpenAICanvas.Protocol;

namespace OpenAICanvas.Application;

/// <summary>协议目录条目投影。对应 Go: <c>app.PluginProviderCatalogItem</c>。</summary>
public sealed class PluginProviderCatalogItem
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("categories")]
    public System.Collections.Generic.List<string> Categories { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("scopes")]
    public System.Collections.Generic.List<string> Scopes { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("create")]
    [Domain.Serialization.GoOmitEmpty]
    public string Create { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("poll")]
    [Domain.Serialization.GoOmitEmpty]
    public string Poll { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("contentType")]
    [Domain.Serialization.GoOmitEmpty]
    public string ContentType { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("baseUrl")]
    [Domain.Serialization.GoOmitEmpty]
    public string BaseURL { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("unavailableReason")]
    [Domain.Serialization.GoOmitEmpty]
    public string UnavailableReason { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("workflows")]
    public System.Collections.Generic.List<Protocol.ManifestWorkflow> Workflows { get; set; } = [];
}

/// <summary>
/// 插件目录查询服务（本轮只覆盖官方协议插件包；插件中心 10.1 另行移植）。
/// 对应 Go: <c>app.Service.PluginProviderCatalog</c>。
/// </summary>
public sealed class ProtocolCatalogService
{
    /// <summary>
    /// 投影当前可用的协议 provider。清单归一化已把 create/poll/contentType 摘要写入
    /// <see cref="Protocol.Metadata"/>，直接取用即可（等价 Go 的「registry 元数据覆盖」）。
    /// </summary>
    public static System.Collections.Generic.List<PluginProviderCatalogItem> PluginProviderCatalog(
        string scope, string capability, bool includeUnavailable)
    {
        System.Collections.Generic.List<Metadata> items = ProtocolAdapterLookup.OfficialFallback.List(
            scope.Trim(), capability.Trim(), includeUnavailable);

        System.Collections.Generic.List<PluginProviderCatalogItem> providers = [];
        foreach (Metadata item in items)
        {
            providers.Add(new PluginProviderCatalogItem
            {
                ID = item.ID,
                Version = item.Version,
                Name = item.Name,
                Vendor = item.Vendor,
                Categories = item.Categories,
                Scopes = item.Scopes,
                Create = item.Create,
                Poll = item.Poll,
                ContentType = item.ContentType,
                BaseURL = "",
                Enabled = item.Enabled && item.UnavailableReason.Length == 0,
                UnavailableReason = item.UnavailableReason,
                Workflows = [],
            });
        }
        return providers;
    }
}
