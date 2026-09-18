namespace OpenAICanvas.Domain.Serialization;

/// <summary>
/// 标记该属性使用 Go <c>encoding/json</c> 的 <c>omitempty</c> 语义。
/// </summary>
/// <remarks>
/// Go 的 omitempty 定义为：false、0、nil 指针、nil 接口、以及长度为零的
/// 数组 / 切片 / map / 字符串。
/// <para>
/// System.Text.Json 的 <c>JsonIgnoreCondition.WhenWritingDefault</c> 只覆盖
/// 前四类，<b>不会</b>省略空字符串和空集合。因此必须用本特性 + 
/// <see cref="GoOmitEmptyTypeInfoResolver"/> 才能与 Go 输出逐字节一致。
/// </para>
/// <para>对应 Go 源码中 struct tag 的 <c>json:"xxx,omitempty"</c>。</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GoOmitEmptyAttribute : Attribute
{
}
