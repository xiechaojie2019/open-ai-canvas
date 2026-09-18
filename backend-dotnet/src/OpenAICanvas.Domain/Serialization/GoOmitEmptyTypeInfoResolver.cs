using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenAICanvas.Domain.Serialization;

/// <summary>
/// 让带 <see cref="GoOmitEmptyAttribute"/> 的属性按 Go <c>omitempty</c> 语义决定是否序列化。
/// </summary>
/// <remarks>
/// Go 的判定规则（<c>encoding/json</c> 文档）：
/// false、0、nil 指针、nil 接口、以及 len == 0 的数组/切片/map/字符串。
/// <para>
/// 结构体本身不算"空"，除非它是 nil 指针——所以这里对普通对象一律返回"非空"。
/// </para>
/// </remarks>
public static class GoOmitEmptyTypeInfoResolver
{
    /// <summary>在 <see cref="JsonSerializerOptions.TypeInfoResolver"/> 链上追加本修饰器。</summary>
    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (!HasOmitEmpty(property))
            {
                continue;
            }

            property.ShouldSerialize = static (_, value) => !IsGoEmpty(value);
        }
    }

    private static bool HasOmitEmpty(JsonPropertyInfo property)
    {
        ICustomAttributeProvider? provider = property.AttributeProvider;
        if (provider is null)
        {
            return false;
        }

        return provider.IsDefined(typeof(GoOmitEmptyAttribute), inherit: true);
    }

    /// <summary>精确复刻 Go 的 isEmptyValue 判定。</summary>
    private static bool IsGoEmpty(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        bool flag => !flag,
        // 所有数值类型：零值即空。
        sbyte number => number == 0,
        byte number => number == 0,
        short number => number == 0,
        ushort number => number == 0,
        int number => number == 0,
        uint number => number == 0,
        long number => number == 0,
        ulong number => number == 0,
        float number => number == 0f,
        double number => number == 0d,
        decimal number => number == 0m,
        // Go 的 len(array) == 0 / len(slice) == 0 / len(map) == 0。
        Array array => array.Length == 0,
        ICollection collection => collection.Count == 0,
        // 其余对象（含结构体、指针目标）在 Go 中不算空。
        _ => false,
    };
}
