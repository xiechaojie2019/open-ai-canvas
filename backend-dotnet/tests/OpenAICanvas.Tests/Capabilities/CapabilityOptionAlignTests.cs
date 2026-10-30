#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.Capabilities;
using Xunit;

namespace OpenAICanvas.Tests.Capabilities;

/// <summary>
/// 图片质量档位「对齐 + 别名准入」的契约测试。
/// </summary>
/// <remarks>
/// 线上问题（2026-10-30）：Agent 填了 <c>quality=high</c>，渠道 <c>quality.values</c> 是
/// <c>1k/2k/4k</c> ⇒ 准入判「参数 质量超出支持范围」、任务根本没提交，而计费侧的
/// <see cref="ModelSku.PricingQualityForImage"/> 本来就能把 <c>high</c> 折成 <c>4k</c>。
/// 本组测试同时钉住三件事：对齐写回声明词、准入认别名、真非法值仍被拒，
/// 以及别名与原值落在同一价格档（计费不受影响）。
/// </remarks>
public class CapabilityOptionAlignTests
{
    private static JsonElement Str(string value) => JsonSerializer.SerializeToElement(value);

    private static CapabilitySpec ImageSpec(params string[] qualityValues) => new()
    {
        Version = 1,
        Capability = "image",
        Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
        {
            ["quality"] = CapabilitySpecOps.AnyValues(qualityValues),
        },
    };

    private static Dictionary<string, JsonElement> Options(string quality) =>
        new(StringComparer.Ordinal) { ["quality"] = Str(quality) };

    private static ModelRequestIntent Intent(string quality) => new()
    {
        Capability = "image",
        Options = Options(quality),
    };

    // ------------------------------------------------------------ 值对齐

    [Fact]
    public void 别名折到声明词_high写成4k()
    {
        Dictionary<string, JsonElement> options = Options("high");
        CapabilityOptionAlign.AlignQuality(ImageSpec("1k", "2k", "4k"), options);
        Assert.Equal("4k", options["quality"].GetString());
    }

    [Fact]
    public void 别名折到声明词_4k写成high()
    {
        Dictionary<string, JsonElement> options = Options("4k");
        CapabilityOptionAlign.AlignQuality(ImageSpec("low", "medium", "high"), options);
        Assert.Equal("high", options["quality"].GetString());
    }

    [Fact]
    public void 原值已合法时统一成声明里的大小写()
    {
        Dictionary<string, JsonElement> options = Options("4K");
        CapabilityOptionAlign.AlignQuality(ImageSpec("1k", "2k", "4k"), options);
        Assert.Equal("4k", options["quality"].GetString());
    }

    [Fact]
    public void 折不到时删除_留给默认值补全()
    {
        Dictionary<string, JsonElement> options = Options("ultra");
        CapabilityOptionAlign.AlignQuality(ImageSpec("1k", "2k", "4k"), options);
        Assert.False(options.ContainsKey("quality"));
    }

    [Fact]
    public void 模型未声明该参数时不干预()
    {
        Dictionary<string, JsonElement> options = Options("high");
        CapabilityOptionAlign.AlignQuality(new CapabilitySpec { Version = 1, Capability = "image" }, options);
        Assert.Equal("high", options["quality"].GetString());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("any")]
    [InlineData("")]
    public void 未指定档位时不干预(string quality)
    {
        Dictionary<string, JsonElement> options = Options(quality);
        CapabilityOptionAlign.AlignQuality(ImageSpec("1k", "2k", "4k"), options);
        Assert.Equal(quality, options["quality"].GetString());
    }

    // ------------------------------------------------------------ 准入别名

    [Fact]
    public void 准入接受别名_声明1k2k4k时high通过()
    {
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(ImageSpec("1k", "2k", "4k"), Intent("high"));
        Assert.True(match.Matched, string.Join("；", match.Reasons ?? []));
    }

    [Fact]
    public void 准入接受别名_声明lowmediumhigh时4k通过()
    {
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(ImageSpec("low", "medium", "high"), Intent("4k"));
        Assert.True(match.Matched, string.Join("；", match.Reasons ?? []));
    }

    [Fact]
    public void 准入接受别名_standard等价1k()
    {
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(ImageSpec("1k", "2k", "4k"), Intent("standard"));
        Assert.True(match.Matched, string.Join("；", match.Reasons ?? []));
    }

    [Fact]
    public void 真非法值仍被拒()
    {
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(ImageSpec("1k", "2k", "4k"), Intent("ultra"));
        Assert.False(match.Matched);
    }

    [Fact]
    public void 声明之外的高档位仍被拒()
    {
        // 只支持到 2k 的模型收到 4k ⇒ 不能因为「都是别名」就放过。
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(ImageSpec("1k", "2k"), Intent("4k"));
        Assert.False(match.Matched);
    }

    // ------------------------------------------------------------ 计费

    [Fact]
    public void 别名与原值落在同一价格档()
    {
        Assert.Equal(ModelSku.PricingQualityForImage("4k", ""), ModelSku.PricingQualityForImage("high", ""));
        Assert.Equal(ModelSku.PricingQualityForImage("2k", ""), ModelSku.PricingQualityForImage("medium", ""));
        Assert.Equal(ModelSku.PricingQualityForImage("1k", ""), ModelSku.PricingQualityForImage("low", ""));
        Assert.Equal(ModelSku.PricingQualityForImage("1k", ""), ModelSku.PricingQualityForImage("standard", ""));
    }
}
