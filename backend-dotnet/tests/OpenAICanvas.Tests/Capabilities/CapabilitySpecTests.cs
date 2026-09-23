#nullable enable
using System.Text.Json;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.Capabilities;

/// <summary>
/// 能力规格核心纯函数的契约测试，直接对照 Go 侧行为
/// （<c>model_router.go</c> / <c>model_capability.go</c> / <c>channel_models.go</c>）。
/// </summary>
public class CapabilitySpecTests
{
    private static JsonElement Str(string value) => JsonSerializer.SerializeToElement(value);

    // ------------------------------------------------------------ 归一化

    [Fact]
    public void 版本不是1时拒绝()
    {
        AppError error = Assert.Throws<AppError>(() => CapabilitySpecOps.NormalizeCapabilitySpec(
            new CapabilitySpec { Version = 2, Capability = "video" }));
        Assert.Equal(400, error.Status);
        Assert.Contains("version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺少能力类型时拒绝()
    {
        AppError error = Assert.Throws<AppError>(() => CapabilitySpecOps.NormalizeCapabilitySpec(
            new CapabilitySpec { Version = 1, Capability = "" }));
        Assert.Contains("capability", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 操作去重并转小写()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "Video",
            Operations = ["text_to_video", "TEXT_TO_VIDEO", "image_to_video"],
        });
        Assert.Equal(["text_to_video", "image_to_video"], spec.Operations);
    }

    [Fact]
    public void 参数同时声明values和数值范围时拒绝()
    {
        AppError error = Assert.Throws<AppError>(() => CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["videoSeconds"] = new OptionConstraint
                {
                    Values = [Str("5")],
                    Min = 1,
                    Max = 10,
                },
            },
        }));
        Assert.Contains("不能同时声明", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 参数别名归一到规范名()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                // duration/aspectRatio/resolution 是历史别名。
                ["duration"] = new OptionConstraint { Values = [Str("5")] },
            },
        });
        Assert.True(spec.Options!.ContainsKey("videoSeconds"));
    }

    // ------------------------------------------------------------ 匹配

    [Fact]
    public void 能力类型不匹配给出原因()
    {
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(
            new CapabilitySpec { Version = 1, Capability = "video" },
            new ModelRequestIntent { Capability = "image" });
        Assert.False(match.Matched);
        Assert.Equal(["能力类型不匹配"], match.Reasons);
    }

    [Fact]
    public void 输入数量超限给出范围提示()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Inputs = new Dictionary<string, InputConstraint>(StringComparer.Ordinal)
            {
                ["image"] = new InputConstraint { Min = 0, Max = 2 },
            },
        });
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(
            spec,
            new ModelRequestIntent
            {
                Capability = "video",
                Inputs = new Dictionary<string, long>(StringComparer.Ordinal) { ["image"] = 3 },
            });
        Assert.False(match.Matched);
        Assert.Contains("参考图片数量需在 0-2 之间", match.Reasons!);
    }

    [Fact]
    public void 未声明参数给出不支持提示()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("16:9")] },
            },
        });
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(
            spec,
            new ModelRequestIntent
            {
                Capability = "video",
                Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["aspectRatio"] = Str("16:9"),
                    ["videoSeconds"] = Str("5"),
                },
            });
        Assert.False(match.Matched);
        // aspectRatio 归一成 size 后命中；videoSeconds 缺失 → "不支持参数 视频时长"。
        Assert.Contains("不支持参数 视频时长", match.Reasons!);
        Assert.DoesNotContain("画面尺寸", match.Reasons!);
    }

    [Fact]
    public void vquality别名与后缀可互换比较()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["vquality"] = new OptionConstraint { Values = [Str("480p")] },
            },
        });
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(
            spec,
            new ModelRequestIntent
            {
                Capability = "video",
                Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["resolution"] = Str("low"),
                },
            });
        Assert.True(match.Matched, string.Join(";", match.Reasons ?? []));
    }

    [Fact]
    public void 数值范围步长匹配()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["videoSeconds"] = CapabilitySpecOps.NumericRange(4, 12, 2),
            },
        });
        foreach (int seconds in new[] { 4, 6, 12 })
        {
            CapabilityMatch match = CapabilitySpecOps.MatchCapability(spec, new ModelRequestIntent
            {
                Capability = "video",
                Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["duration"] = JsonSerializer.SerializeToElement(seconds),
                },
            });
            Assert.True(match.Matched, $"{seconds} 应匹配");
        }

        CapabilityMatch invalid = CapabilitySpecOps.MatchCapability(spec, new ModelRequestIntent
        {
            Capability = "video",
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["duration"] = JsonSerializer.SerializeToElement(5),
            },
        });
        Assert.False(invalid.Matched);
    }

    [Fact]
    public void 通配值接受任意取值()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "image",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("*")] },
            },
        });
        CapabilityMatch match = CapabilitySpecOps.MatchCapability(spec, new ModelRequestIntent
        {
            Capability = "image",
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["size"] = Str("2048x1152"),
            },
        });
        Assert.True(match.Matched);
    }

    // ------------------------------------------------------------ 供应线路覆盖

    [Fact]
    public void 创作端参数超出供应线路时报错()
    {
        CapabilitySpec product = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["vquality"] = new OptionConstraint { Values = [Str("720p"), Str("1080p")] },
            },
        });
        CapabilitySpec route = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["vquality"] = new OptionConstraint { Values = [Str("720p")] },
            },
        });

        AppError error = Assert.Throws<AppError>(
            () => CapabilitySpecOps.ValidateProductSpecWithinRoutes(product, [route]));
        Assert.Contains("创作端参数超出供应线路能力：vquality", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 输入范围必须被线路覆盖()
    {
        CapabilitySpec product = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Inputs = new Dictionary<string, InputConstraint>(StringComparer.Ordinal)
            {
                ["image"] = new InputConstraint { Min = 0, Max = 9 },
            },
        });
        CapabilitySpec routeA = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Inputs = new Dictionary<string, InputConstraint>(StringComparer.Ordinal)
            {
                ["image"] = new InputConstraint { Min = 0, Max = 4 },
            },
        });
        CapabilitySpec routeB = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Inputs = new Dictionary<string, InputConstraint>(StringComparer.Ordinal)
            {
                ["image"] = new InputConstraint { Min = 5, Max = 9 },
            },
        });

        // 两条线路拼起来可以覆盖 0-9。
        CapabilitySpecOps.ValidateProductSpecWithinRoutes(product, [routeA, routeB]);

        AppError error = Assert.Throws<AppError>(
            () => CapabilitySpecOps.ValidateProductSpecWithinRoutes(product, [routeA]));
        Assert.Contains("创作端输入范围超出供应线路能力：image", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 默认参数

    [Fact]
    public void 默认参数通配符回落到第一个候选()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "image",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("1024x1024"), Str("*")] },
            },
        });
        Dictionary<string, JsonElement> defaults = CapabilitySpecOps.NormalizeLogicalDefaults(
            spec,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["size"] = Str("*"),
            });
        // "*" 不是可发送默认值，回落到第一个非通配候选。
        Assert.Equal("1024x1024", defaults["size"].GetString());
    }

    [Fact]
    public void 默认参数超出范围时报错()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["videoSeconds"] = new OptionConstraint { Values = [Str("5")] },
            },
        });
        AppError error = Assert.Throws<AppError>(() => CapabilitySpecOps.NormalizeLogicalDefaults(
            spec,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["duration"] = Str("10"),
            }));
        Assert.Equal("默认参数 videoSeconds 不在前台模型能力范围内", error.Message);
    }

    // ------------------------------------------------------------ 能力配置归一化

    [Fact]
    public void 文本能力补默认流式开关()
    {
        ModelCapabilityConfig? normalized = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            "text", "openai",
            "", new ModelCapabilityConfig
            {
                Version = 1,
                Text = new TextCapabilityConfig
                {
                    References = new TextReferenceConfig { PromptMaxChars = 32000 },
                },
            });
        Assert.NotNull(normalized);
        Assert.True(normalized.Text!.Streaming);
        Assert.Equal(1, normalized.Version);
        Assert.Null(normalized.Image);
        Assert.Null(normalized.Video);
    }

    [Fact]
    public void 文本能力缺少配置时报错()
    {
        AppError error = Assert.Throws<AppError>(() =>
            ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
                "text", "openai", "", new ModelCapabilityConfig { Version = 1 }));
        Assert.Equal("请配置文本模型能力参数", error.Message);
    }

    [Fact]
    public void 未知能力类型静默返回null()
    {
        // Go 返回 (nil, nil)。
        Assert.Null(ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            "audio", "openai", "", new ModelCapabilityConfig { Version = 1 }));
    }

    [Fact]
    public void 方舟视频能力补齐参考素材操作和输入规格()
    {
        ModelCapabilityConfig config = JsonSerializer.Deserialize<ModelCapabilityConfig>(
            """
            {"version":1,"video":{"references":{"promptMaxChars":1000,"minImages":0,"maxImages":9,"maxImageBytes":31457280,"maxVideos":3,"maxVideoBytes":209715200,"maxVideoDurationSeconds":15,"maxAudios":3,"maxAudioBytes":15728640,"maxAudioDurationSeconds":15},"duration":{"selection":"enum","values":[5,10],"default":5},"ratios":["16:9","9:16"],"defaultRatio":"16:9","resolutions":["720p","1080p"],"defaultResolution":"720p","generateAudio":{"supported":true,"default":true},"watermark":{"supported":false,"default":false},"operations":["text_to_video","image_to_video"],"defaultOperation":"text_to_video"}}
            """,
            CapabilityJson.ReadOptions)!;

        ModelCapabilityConfig normalized = ModelCapabilityConfigOps.NormalizeModelCapabilityConfigForModel(
            "video", "volcengine-ark-video", "seedance", config)!;
        CapabilitySpec spec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(normalized, "video");

        Assert.Equal(
            ["text_to_video", "image_to_video", "reference_to_video", "audio_to_video"],
            spec.Operations);
        Assert.Equal(9, spec.Inputs!["image"].Max);
        Assert.Equal(3, spec.Inputs!["video"].Max);
        Assert.Equal(3, spec.Inputs!["audio"].Max);
        Assert.Equal(["5", "10"], spec.Options!["videoSeconds"].Values!.Select(v => v.GetRawText()));
        Assert.Equal(["16:9", "9:16"], spec.Options!["size"].Values!.Select(v => v.GetString()));
        Assert.Equal(["720p", "1080p"], spec.Options!["vquality"].Values!.Select(v => v.GetString()));
        Assert.Equal(2, spec.Options!["videoGenerateAudio"].Values!.Count);
        Assert.Equal([false, true], spec.Options!["videoWatermark"].Values!.Select(v => v.GetBoolean()));
    }

    [Fact]
    public void 音频能力是空规格()
    {
        CapabilitySpec spec = ModelCapabilityConfigOps.CapabilitySpecFromModelCapabilityConfig(null, "audio");
        Assert.Equal("audio", spec.Capability);
        Assert.Null(spec.Options);
        Assert.Null(spec.Inputs);
    }

    [Fact]
    public void 渠道能力JSON损坏时报业务错误()
    {
        AppError error = Assert.Throws<AppError>(
            () => ModelCapabilityConfigOps.DecodeModelCapabilityConfig("{invalid"));
        Assert.StartsWith("解析渠道模型能力配置失败", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ 解析度与SKU

    [Theory]
    [InlineData("720p", "720p")]
    [InlineData("720P", "720p")]
    [InlineData("720", "720p")]
    [InlineData("low", "480p")]
    [InlineData("2k", "1440p")]
    [InlineData("4K", "2160p")]
    [InlineData("768P", "768p")]
    [InlineData("*", "*")]
    [InlineData("any", "*")]
    [InlineData("", "*")]
    public void 渠道档解析度归一(string raw, string expected)
    {
        Assert.Equal(expected, ModelCapabilityConfigOps.NormalizeChannelModelTierResolution(raw));
    }

    [Fact]
    public void 意图选择器按参考素材归类操作()
    {
        Dictionary<string, string> selector = ModelSku.SkuSelectorForIntent(new ModelRequestIntent
        {
            Capability = "video",
            Inputs = new Dictionary<string, long>(StringComparer.Ordinal) { ["image"] = 2 },
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["vquality"] = Str("1080p"),
                ["videoSeconds"] = JsonSerializer.SerializeToElement(10),
            },
        });
        // 视频参考优先：有图片参考 → image_to_video。
        Assert.Equal("image_to_video", selector["operation"]);
        Assert.Equal("2", selector["imageCount"]);
        Assert.Equal("1080p", selector["vquality"]);
        Assert.Equal("10", selector["videoSeconds"]);
    }

    [Fact]
    public void 图片意图按像素面积推断档位()
    {
        Assert.Equal("2k", ModelSku.NormalizeImagePriceQuality("auto", "2048x2048"));
        Assert.Equal("1k", ModelSku.NormalizeImagePriceQuality("", "1024x1024"));
        Assert.Equal("4k", ModelSku.NormalizeImagePriceQuality("", "3840x2160"));
        Assert.Equal("", ModelSku.NormalizeImagePriceQuality("", "16:9"));
    }

    [Fact]
    public void 价格档选择精确规格优先于通配()
    {
        ChannelModel channelModel = new()
        {
            ID = "CM_1",
            PriceTiers =
            [
                new ChannelModelPriceTier
                {
                    ID = "TIER_WILD",
                    SelectorJSON = "{}",
                    Resolution = "*",
                    VideoSeconds = 0,
                    Enabled = true,
                    PriceConfigured = true,
                    UnitPriceMicrocredits = 100,
                },
                new ChannelModelPriceTier
                {
                    ID = "TIER_EXACT",
                    SelectorJSON = "{\"vquality\":\"720p\",\"videoSeconds\":\"5\"}",
                    Resolution = "720p",
                    VideoSeconds = 5,
                    Enabled = true,
                    PriceConfigured = true,
                    UnitPriceMicrocredits = 200,
                },
            ],
        };
        ModelRequestIntent intent = new()
        {
            Capability = "video",
            Options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["vquality"] = Str("720p"),
                ["videoSeconds"] = JsonSerializer.SerializeToElement(5),
            },
        };

        ChannelModelPriceTier? selected = ModelSku.ChannelModelPriceTierForIntent(channelModel, intent);
        Assert.NotNull(selected);
        Assert.Equal("TIER_EXACT", selected.ID);
    }

    [Fact]
    public void 未启用或未定价的档位不参与匹配()
    {
        ChannelModel channelModel = new()
        {
            ID = "CM_1",
            PriceTiers =
            [
                new ChannelModelPriceTier
                {
                    ID = "TIER_DISABLED",
                    SelectorJSON = "{}",
                    Resolution = "*",
                    Enabled = false,
                    PriceConfigured = true,
                },
                new ChannelModelPriceTier
                {
                    ID = "TIER_UNPRICED",
                    SelectorJSON = "{}",
                    Resolution = "*",
                    Enabled = true,
                    PriceConfigured = false,
                },
            ],
        };

        Assert.Null(ModelSku.ChannelModelPriceTierForIntent(channelModel, new ModelRequestIntent
        {
            Capability = "video",
        }));
    }

    [Fact]
    public void 规范选择器丢弃空值并输出规范JSON()
    {
        (Dictionary<string, string> selector, string canonical) = ModelSku.CanonicalSkuSelector(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["videoSeconds"] = " 5 ",
                ["vquality"] = "",
                ["operation"] = "text_to_video",
            });
        Assert.Equal(new[] { "operation", "videoSeconds" }, selector.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("5", selector["videoSeconds"]);
        Assert.Equal("{\"operation\":\"text_to_video\",\"videoSeconds\":\"5\"}", canonical);
    }

    // ------------------------------------------------------------ 指纹

    [Fact]
    public void 枚举顺序不同不产生重复画像()
    {
        CapabilitySpec a = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("16:9"), Str("9:16")] },
            },
        });
        CapabilitySpec b = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("9:16"), Str("16:9")] },
            },
        });
        Assert.Equal(
            CapabilitySpecOps.CapabilityFingerprint(a),
            CapabilitySpecOps.CapabilityFingerprint(b));
    }

    // ------------------------------------------------------------ 路由预设修复

    [Fact]
    public void 通配约束从供应线路恢复预设()
    {
        CapabilitySpec product = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "image",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("*")] },
            },
        });
        CapabilitySpec route = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "image",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["size"] = new OptionConstraint { Values = [Str("1024x1024"), Str("1536x1024")] },
            },
        });

        CapabilitySpec restored = CapabilitySpecPresets.WithRoutePresets(product, [route]);
        List<string> values = restored.Options!["size"].Values!.Select(v => v.GetString()!).ToList();
        Assert.Contains("*", values);
        Assert.Contains("1024x1024", values);
        Assert.Contains("1536x1024", values);
    }

    [Fact]
    public void 视频规格按价格档收窄()
    {
        CapabilitySpec spec = CapabilitySpecOps.NormalizeCapabilitySpec(new CapabilitySpec
        {
            Version = 1,
            Capability = "video",
            Options = new Dictionary<string, OptionConstraint>(StringComparer.Ordinal)
            {
                ["vquality"] = new OptionConstraint { Values = [Str("480p"), Str("720p"), Str("1080p")] },
                ["videoSeconds"] = CapabilitySpecOps.NumericRange(1, 15, 1),
                ["size"] = new OptionConstraint { Values = [Str("16:9")] },
            },
        });
        ChannelModel channelModel = new()
        {
            ID = "CM_1",
            PriceTiers =
            [
                new ChannelModelPriceTier
                {
                    ID = "TIER_A",
                    Resolution = "720p",
                    VideoSeconds = 5,
                    Enabled = true,
                    PriceConfigured = true,
                },
                new ChannelModelPriceTier
                {
                    ID = "TIER_B",
                    Resolution = "1080p",
                    VideoSeconds = 10,
                    Enabled = true,
                    PriceConfigured = true,
                },
            ],
        };

        CapabilitySpec narrowed = CapabilitySpecPresets.WithPriceTiers(spec, channelModel);
        Assert.Equal(
            ["720p", "1080p"],
            narrowed.Options!["vquality"].Values!.Select(v => v.GetString()));
        Assert.Equal(
            ["5", "10"],
            narrowed.Options!["videoSeconds"].Values!.Select(v => v.GetRawText()));
        // 非规格化参数不受影响。
        Assert.Equal(["16:9"], narrowed.Options!["size"].Values!.Select(v => v.GetString()));
    }

    // ------------------------------------------------------------ Token 计费

    [Fact]
    public void token计费仅文本或方舟视频协议()
    {
        Assert.True(ModelCapabilityConfigOps.SupportsTokenBilling("text", "openai"));
        Assert.True(ModelCapabilityConfigOps.SupportsTokenBilling("video", "volcengine-ark-video"));
        Assert.False(ModelCapabilityConfigOps.SupportsTokenBilling("video", "openai"));
        Assert.False(ModelCapabilityConfigOps.SupportsTokenBilling("image", "openai"));
    }
}
