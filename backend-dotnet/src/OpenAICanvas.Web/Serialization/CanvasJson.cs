using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using OpenAICanvas.Domain.Serialization;

namespace OpenAICanvas.Web.Serialization;

/// <summary>
/// 全局 JSON 契约。所有出参都必须经由此配置，才能与 Go 版逐字节一致。
/// 权威定义已下沉到 <see cref="GoJson"/>（Domain 层），这里仅为既有调用面保留别名。
/// </summary>
public static class CanvasJson
{
    /// <summary>请求体反序列化用的配置。</summary>
    public static JsonSerializerOptions ReadOptions => GoJson.ReadOptions;

    /// <summary>响应体序列化用的配置。</summary>
    public static JsonSerializerOptions WriteOptions => GoJson.WriteOptions;
}
