#nullable enable
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// 声明式协议请求投影与结果整形的契约测试。
/// 对应 Go: <c>provider_protocol.go</c> 的
/// <c>protocolRequestFromInput</c> / <c>protocolImageReferences</c> /
/// <c>protocolVideoImageReferences</c> / <c>protocolMediaReferences</c> /
/// <c>protocolMediaReference</c> / <c>finishProtocolResult</c> /
/// <c>protocolResultHasOutput</c> / <c>protocolResultError</c>。
/// </summary>
public sealed class ProviderProtocolPayloadTests
{
    private static TextTaskInput Input(string mode = "text") => new()
    {
        Mode = mode,
        Prompt = "提示",
        Config = new ProviderConfig { Model = "m" },
    };

    // ------------------------------------------------------------ 基础投影

    [Fact]
    public void 投影_基础字段()
    {
        TextTaskInput input = Input("image");
        input.Config.Size = "1024x1024";
        input.Config.VQuality = "high";
        input.Config.Quality = "hd";
        input.Config.SystemPrompt = " 指令 ";

        ProtocolGenerationRequest request = ProviderProtocolPayload.FromInput(input);

        Assert.Equal("image", request.Capability);
        Assert.Equal("m", request.Model);
        Assert.Equal("提示", request.Prompt);
        Assert.Equal("指令", request.Instructions);
        Assert.Equal("1024x1024", request.AspectRatio);
        Assert.Equal("high", request.Resolution);
        Assert.Equal("hd", request.Quality);
    }

    [Fact]
    public void 投影_布尔参数只认真值()
    {
        TextTaskInput input = Input();
        input.Config.VideoGenerateAudio = "true";
        input.Config.VideoWatermark = "no";

        ProtocolGenerationRequest request = ProviderProtocolPayload.FromInput(input);

        Assert.True(request.GenerateAudio);
        Assert.False(request.Watermark);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("maybe", false)]
    public void 解析布尔(string? value, bool expected) =>
        Assert.Equal(expected, ProviderProtocolPayload.ParseBool(value));

    [Fact]
    public void 投影_编辑操作优先取videoEditOperation()
    {
        TextTaskInput input = Input("video");
        input.Metadata["videoEditOperation"] = "extend";
        input.Metadata["videoOperation"] = "generate";

        Assert.Equal("extend", ProviderProtocolPayload.FromInput(input).Operation);
    }

    [Fact]
    public void 投影_编辑操作回落到videoOperation()
    {
        TextTaskInput input = Input("video");
        input.Metadata["videoOperation"] = "generate";

        Assert.Equal("generate", ProviderProtocolPayload.FromInput(input).Operation);
    }

    [Fact]
    public void 投影_Extra含视频音频参数()
    {
        TextTaskInput input = Input();
        input.Config.VideoSeconds = "5";
        input.Config.AudioVoice = "alloy";
        input.Config.AudioFormat = "mp3";
        input.Config.Count = "2";

        Dictionary<string, object?> extra = ProviderProtocolPayload.FromInput(input).Extra;

        Assert.Equal("5", extra["videoSeconds"]);
        Assert.Equal("alloy", extra["audioVoice"]);
        Assert.Equal("mp3", extra["audioFormat"]);
        Assert.Equal("2", extra["count"]);
    }

    [Fact]
    public void 投影_输出上限写入两个键()
    {
        TextTaskInput input = Input();
        input.MaxOutputTokens = 512;

        Dictionary<string, object?> extra = ProviderProtocolPayload.FromInput(input).Extra;

        // Go 同时写 max_output_tokens 与 max_tokens，兼容不同上游的字段命名。
        Assert.Equal(512, extra["max_output_tokens"]);
        Assert.Equal(512, extra["max_tokens"]);
    }

    [Fact]
    public void 投影_输出上限为0时不写入()
    {
        Dictionary<string, object?> extra = ProviderProtocolPayload.FromInput(Input()).Extra;

        Assert.False(extra.ContainsKey("max_output_tokens"));
        Assert.False(extra.ContainsKey("max_tokens"));
    }

    [Fact]
    public void 投影_时长与数量用严格整数解析()
    {
        TextTaskInput input = Input();
        input.Config.VideoSeconds = " 8 ";
        input.Config.Count = "3";

        ProtocolGenerationRequest request = ProviderProtocolPayload.FromInput(input);

        Assert.Equal(8, request.Duration);
        Assert.Equal(3, request.ImageCount);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("")]
    public void 投影_非法时长数量不写入(string value)
    {
        TextTaskInput input = Input();
        input.Config.VideoSeconds = value;
        input.Config.Count = value;

        ProtocolGenerationRequest request = ProviderProtocolPayload.FromInput(input);

        Assert.Equal(0, request.Duration);
        Assert.Equal(0, request.ImageCount);
    }

    // ------------------------------------------------------------ 消息

    [Fact]
    public void 投影_历史消息允许system角色()
    {
        TextTaskInput input = Input();
        input.TextHistory =
        [
            new ProviderTextMessage { Role = " System ", Content = " 系统 " },
            new ProviderTextMessage { Role = "user", Content = "问" },
        ];

        List<ProtocolMessage> messages = ProviderProtocolPayload.FromInput(input).Messages;

        // 声明式协议与文本协议不同：这里允许 system 角色。
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("系统", messages[0].Content);
    }

    [Fact]
    public void 投影_历史消息丢弃未知角色与空内容()
    {
        TextTaskInput input = Input();
        input.TextHistory =
        [
            new ProviderTextMessage { Role = "tool", Content = "x" },
            new ProviderTextMessage { Role = "user", Content = "   " },
            new ProviderTextMessage { Role = "user", Content = "有效" },
        ];

        List<ProtocolMessage> messages = ProviderProtocolPayload.FromInput(input).Messages;

        Assert.Single(messages);
        Assert.Equal("有效", messages[0].Content);
    }

    // ------------------------------------------------------------ Images

    [Fact]
    public void 图片引用_非图片模式为referenceImage()
    {
        TextTaskInput input = Input("text");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Single(images);
        Assert.Equal("reference_image", images[0].Role);
        Assert.Equal(0, images[0].Order);
    }

    [Fact]
    public void 图片引用_图片模式为editSource()
    {
        TextTaskInput input = Input("image");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Equal("edit_source", images[0].Role);
    }

    [Fact]
    public void 图片引用_蒙版追加为mask且序号递增()
    {
        TextTaskInput input = Input("image");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });
        input.Mask = new ProviderMedia { URL = "https://x/mask.png" };

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Equal(2, images.Count);
        Assert.Equal("mask", images[1].Role);
        Assert.Equal(1, images[1].Order);
    }

    [Fact]
    public void 图片引用_无地址的素材被丢弃()
    {
        TextTaskInput input = Input();
        input.ReferenceImages.Add(new ProviderMedia { Name = "no-url" });
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/a.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Single(images);
    }

    // ------------------------------------------------------------ Videos

    [Fact]
    public void 视频引用_默认无角色()
    {
        TextTaskInput input = Input("video");
        input.ReferenceImages.Add(new ProviderMedia { ID = "n1", URL = "https://x/a.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        // 未声明首/尾帧节点时回落角色为空。
        Assert.Equal("", images[0].Role);
    }

    [Fact]
    public void 视频引用_声明首尾帧后回落为referenceImage()
    {
        TextTaskInput input = Input("video");
        input.Metadata["videoStartFrameNodeId"] = "n1";
        input.ReferenceImages.Add(new ProviderMedia { ID = "other", URL = "https://x/a.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Equal("reference_image", images[0].Role);
    }

    [Fact]
    public void 视频引用_节点ID匹配首帧()
    {
        TextTaskInput input = Input("video");
        input.Metadata["videoStartFrameNodeId"] = "n1";
        input.Metadata["videoEndFrameNodeId"] = "n2";
        input.ReferenceImages.Add(new ProviderMedia { ID = "n1", URL = "https://x/a.png" });
        input.ReferenceImages.Add(new ProviderMedia { ID = "n2", URL = "https://x/b.png" });

        List<OpenAICanvas.Protocol.MediaReference> images = ProviderProtocolPayload.ImageReferences(input);

        Assert.Equal("start_frame", images[0].Role);
        Assert.Equal("end_frame", images[1].Role);
    }

    [Fact]
    public void 视频引用_蒙版不参与视频模式()
    {
        TextTaskInput input = Input("video");
        input.Mask = new ProviderMedia { URL = "https://x/mask.png" };

        Assert.Empty(ProviderProtocolPayload.ImageReferences(input));
    }

    // ------------------------------------------------------------ 音视频素材

    [Fact]
    public void 媒体引用_视频与音频角色()
    {
        List<OpenAICanvas.Protocol.MediaReference> videos = ProviderProtocolPayload.MediaReferences(
            [new ProviderMedia { URL = "https://x/v.mp4" }], "video");
        List<OpenAICanvas.Protocol.MediaReference> audios = ProviderProtocolPayload.MediaReferences(
            [new ProviderMedia { URL = "https://x/a.mp3" }], "audio");

        Assert.Equal("reference_video", videos[0].Role);
        Assert.Equal("reference_audio", audios[0].Role);
    }

    [Fact]
    public void 媒体引用_MIME回落与元信息()
    {
        OpenAICanvas.Protocol.MediaReference reference = ProviderProtocolPayload.MediaReferenceOf(
            new ProviderMedia
            {
                ID = " i ",
                URL = " https://x/a.png ",
                Type = "image/png",
                StorageKey = " key ",
                Bytes = 10,
                Width = 2,
                Height = 3,
                DurationMs = 4,
            },
            "image",
            7);

        Assert.Equal("i", reference.ID);
        Assert.Equal("https://x/a.png", reference.URL);
        Assert.Equal("image/png", reference.MIMEType);
        Assert.Equal("key", reference.StorageKey);
        Assert.Equal(7, reference.Order);
        Assert.Equal(10L, reference.Metadata!["bytes"]);
        Assert.Equal(2, reference.Metadata["width"]);
        Assert.Equal(3, reference.Metadata["height"]);
        Assert.Equal(4L, reference.Metadata["durationMs"]);
        Assert.Equal("key", reference.Metadata["storageKey"]);
    }

    [Fact]
    public void 媒体引用_显式MIME优先于类型()
    {
        OpenAICanvas.Protocol.MediaReference reference = ProviderProtocolPayload.MediaReferenceOf(
            new ProviderMedia { MIMEType = "image/webp", Type = "image/png" }, "image", 0);

        Assert.Equal("image/webp", reference.MIMEType);
    }

    // ------------------------------------------------------------ Inputs 汇总

    [Fact]
    public void 投影_Inputs为三类素材拼接()
    {
        TextTaskInput input = Input("video");
        input.ReferenceImages.Add(new ProviderMedia { URL = "https://x/i.png" });
        input.ReferenceVideos.Add(new ProviderMedia { URL = "https://x/v.mp4" });
        input.ReferenceAudios.Add(new ProviderMedia { URL = "https://x/a.mp3" });

        ProtocolGenerationRequest request = ProviderProtocolPayload.FromInput(input);

        Assert.Equal(3, request.Inputs.Count);
        Assert.Equal("reference_video", request.Inputs[1].Role);
        Assert.Equal("reference_audio", request.Inputs[2].Role);
    }

    // ------------------------------------------------------------ providerOptions

    [Fact]
    public void 投影_命名空间化插件选项()
    {
        TextTaskInput input = Input();
        input.Metadata["providerOptions"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [" seedance "] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["camera"] = "fixed" },
            ["bad"] = "不是对象",
        };

        Dictionary<string, Dictionary<string, object?>> options =
            ProviderProtocolPayload.FromInput(input).ProviderOptions;

        Assert.Single(options);
        Assert.True(options.ContainsKey("seedance"));
        Assert.Equal("fixed", options["seedance"]["camera"]);
    }

    [Fact]
    public void 投影_无插件选项时为空字典() =>
        Assert.Empty(ProviderProtocolPayload.FromInput(Input()).ProviderOptions);

    // ------------------------------------------------------------ 结果整形

    [Fact]
    public void 结果_文本模式无推理时只有text()
    {
        Dictionary<string, object?> output = ProviderProtocolPayload.FinishResult(
            "text", new ProtocolResult { Text = "正文" }, []);

        Assert.Equal("text", output["mode"]);
        Assert.Equal("正文", output["text"]);
        Assert.False(output.ContainsKey("reasoning"));
    }

    [Fact]
    public void 结果_文本模式带推理时输出reasoning()
    {
        Dictionary<string, object?> output = ProviderProtocolPayload.FinishResult(
            "text", new ProtocolResult { Text = "正文", Reasoning = "推演" }, []);

        Assert.Equal("推演", output["reasoning"]);
    }

    [Fact]
    public void 结果_图片模式为数组()
    {
        Dictionary<string, object?> output = ProviderProtocolPayload.FinishResult(
            "image",
            new ProtocolResult(),
            [new ProtocolMediaItem("data:image/png;base64,AA", "image/png")]);

        Assert.Equal("image", output["mode"]);
        List<Dictionary<string, object?>> images =
            Assert.IsType<List<Dictionary<string, object?>>>(output["images"]);
        Assert.Single(images);
        Assert.Equal("image/png", images[0]["mimeType"]);
    }

    [Fact]
    public void 结果_视频模式取首项为对象()
    {
        Dictionary<string, object?> output = ProviderProtocolPayload.FinishResult(
            "video",
            new ProtocolResult(),
            [new ProtocolMediaItem("data:video/mp4;base64,AA", "video/mp4")]);

        Assert.Equal("video", output["mode"]);
        Dictionary<string, object?> video =
            Assert.IsType<Dictionary<string, object?>>(output["video"]);
        Assert.Equal("video/mp4", video["mimeType"]);
    }

    [Fact]
    public void 结果_音频模式落到默认分支()
    {
        Dictionary<string, object?> output = ProviderProtocolPayload.FinishResult(
            "audio",
            new ProtocolResult(),
            [new ProtocolMediaItem("data:audio/mp3;base64,AA", "audio/mp3")]);

        Assert.Equal("audio", output["mode"]);
        Assert.True(output.ContainsKey("audio"));
    }

    // ------------------------------------------------------------ ResultHasOutput

    [Theory]
    [InlineData("text", "x", true)]
    [InlineData("text", "   ", false)]
    [InlineData("image", "", false)]
    public void 结果判定_文本模式(string mode, string text, bool expected) =>
        Assert.Equal(expected, ProviderProtocolPayload.ResultHasOutput(
            mode, new ProtocolResult { Text = text }));

    [Fact]
    public void 结果判定_媒体模式看列表非空()
    {
        Assert.True(ProviderProtocolPayload.ResultHasOutput(
            "image", new ProtocolResult { Images = [new OpenAICanvas.Protocol.MediaReference()] }));
        Assert.False(ProviderProtocolPayload.ResultHasOutput("video", new ProtocolResult()));
        Assert.False(ProviderProtocolPayload.ResultHasOutput("audio", new ProtocolResult()));
    }

    [Fact]
    public void 结果判定_空结果或未知模式为false()
    {
        Assert.False(ProviderProtocolPayload.ResultHasOutput("text", null));
        Assert.False(ProviderProtocolPayload.ResultHasOutput("weird", new ProtocolResult { Text = "x" }));
    }

    // ------------------------------------------------------------ ResultError

    [Fact]
    public void 结果错误_空消息用默认文案()
    {
        InvalidOperationException error = ProviderProtocolPayload.ResultError("  ", "");

        Assert.Equal("上游返回失败状态", error.Message);
    }

    [Fact]
    public void 结果错误_无任务ID时只输出消息()
    {
        InvalidOperationException error = ProviderProtocolPayload.ResultError("额度不足", "");

        Assert.Equal("额度不足", error.Message);
    }

    [Fact]
    public void 结果错误_有任务ID时带前缀()
    {
        InvalidOperationException error = ProviderProtocolPayload.ResultError("额度不足", "task-1");

        Assert.Equal("声明式协议任务失败（任务 task-1）：额度不足", error.Message);
    }
}
