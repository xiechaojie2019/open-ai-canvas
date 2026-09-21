#nullable enable
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Outbound;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;
using Xunit;

namespace OpenAICanvas.Tests.Providers;

/// <summary>
/// RunningHub 工作流协议的契约测试。
/// 对应 Go: <c>internal/app/workflow_provider.go</c> 与
/// <c>internal/app/runninghub_management.go</c> 的字段收集、nodeInfo 组装、
/// 提交 / 轮询 / 下载链路与状态码归一。
/// </summary>
public sealed class ProviderWorkflowTaskTests
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    private const string TestJSON = """
        {
          "60": {
            "class_type": "CLIPTextEncode",
            "_meta": {"title": "Positive Prompt"},
            "inputs": {"text": "旧提示词", "clip": ["4", 1]}
          },
          "71": {
            "class_type": "EmptyLatentImage",
            "_meta": {"title": "Latent"},
            "inputs": {"width": 1024, "height": 1024, "batch_size": 1}
          },
          "88": {
            "class_type": "LoadImage",
            "_meta": {"title": "参考图"},
            "inputs": {"image": "default.png"}
          },
          "95": {
            "class_type": "ImageResize+",
            "_meta": {"title": "Resize"},
            "inputs": {"width": 512, "height": 768, "multiple_of": 8}
          },
          "97": {
            "class_type": "INT",
            "_meta": {"title": "IntValue"},
            "inputs": {"value": 3}
          },
          "99": {
            "class_type": "KSampler",
            "_meta": {"title": "Sampler"},
            "inputs": {"seed": 42, "noise_seed": 7}
          }
        }
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) =>
            _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return _responder(request, body);
        }
    }

    private static VideoPollPolicy Policy() => new()
    {
        InitialDelay = TimeSpan.FromMilliseconds(1),
        Interval = TimeSpan.FromMilliseconds(1),
        TotalTimeout = TimeSpan.FromSeconds(2),
        Sleep = (_, _) => Task.CompletedTask,
    };

    private static ProviderWorkflowTask NewTask(StubHandler handler) =>
        new(null, () => new HttpClient(handler));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Binary() =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };

    /// <summary>getJsonApiFormat 的协议信封：真实工作流图放在 data.prompt 字符串里。</summary>
    private static string WorkflowEnvelope() => JsonSerializer.Serialize(
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = 0,
            ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["prompt"] = TestJSON,
            },
        });

    private static ProviderConfig Config(string mode) => new()
    {
        InterfaceType = mode switch
        {
            "video" => ChannelInterfaceType.ChannelInterfaceRunningHubVideo,
            "audio" => ChannelInterfaceType.ChannelInterfaceRunningHubAudio,
            _ => ChannelInterfaceType.ChannelInterfaceRunningHubImage,
        },
        BaseURL = "https://127.0.0.1",
        APIKey = "points-key",
        RunningHubUploadKey = "upload-key",
        Model = "wf-1",
    };

    private static TextTaskInput Input(string mode = "image", string prompt = "一只猫") => new()
    {
        Mode = mode,
        Prompt = prompt,
        Config = Config(mode),
    };

    /// <summary>127.0.0.1 需要显式放行， SSRF 守卫默认拒绝本机地址。</summary>
    private static void AllowPrivateHosts()
    {
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", "127.0.0.1");
    }

    private static void RestorePrivateHosts(string? previous) =>
        Environment.SetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS", previous);

    private static Dictionary<string, object?>? Parse(string? json) =>
        json is null ? null : JsonFields.ParseObject(json);

    private static object? FindItem(List<Dictionary<string, object?>> items, string nodeID, string fieldName) =>
        items.FirstOrDefault(item =>
            ProviderWorkflowValues.WorkflowScalarString(item.GetValueOrDefault("nodeId")) == nodeID
            && ProviderWorkflowValues.WorkflowScalarString(item.GetValueOrDefault("fieldName")) == fieldName);

    private static Dictionary<string, object?> Workflow() =>
        Parse(TestJSON) ?? throw new InvalidOperationException("测试工作流 JSON 无效");

    /// <summary>Parse 产出的 nodeInfoList 是 List&lt;object&gt;，统一收敛成字典列表。</summary>
    private static List<Dictionary<string, object?>> NodeInfoList(object? raw)
    {
        System.Collections.IEnumerable? items = raw as System.Collections.IEnumerable;
        return items?.Cast<Dictionary<string, object?>>().ToList() ?? [];
    }

    // ------------------------------------------------------------ 配置校验与接口判定

    [Fact]
    public void 接口判定_工作流接口与模式()
    {
        Assert.True(ProviderWorkflowValues.IsWorkflowProviderInterface(
            ChannelInterfaceType.ChannelInterfaceRunningHubImage));
        Assert.True(ProviderWorkflowValues.IsRunningHubInterface(
            ChannelInterfaceType.ChannelInterfaceRunningHubVideo));
        Assert.False(ProviderWorkflowValues.IsWorkflowProviderInterface("openai-image"));
        Assert.True(ProviderWorkflowValues.WorkflowInterfaceSupportsMode(
            ChannelInterfaceType.ChannelInterfaceRunningHubAudio, "audio"));
        Assert.False(ProviderWorkflowValues.WorkflowInterfaceSupportsMode(
            ChannelInterfaceType.ChannelInterfaceRunningHubImage, "video"));
        Assert.Equal(("runninghub", true),
            ProviderWorkflowValues.WorkflowPluginIDForInterface("runninghub-workflow-image"));
        Assert.Equal(("", false), ProviderWorkflowValues.WorkflowPluginIDForInterface("other"));
    }

    [Fact]
    public async Task 配置校验_缺Key缺WorkflowId报错()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            // Go 同样先做出站校验再查 workflowId，本机测试地址需要显式放行。
            ProviderConfig config = Config("image");
            config.APIKey = "";
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProviderWorkflowValues.ValidateWorkflowProviderConfig("image", config));

            ProviderConfig noTarget = Config("image");
            noTarget.Model = "";
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProviderWorkflowValues.ValidateWorkflowProviderConfig("image", noTarget));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProviderWorkflowValues.ValidateWorkflowProviderConfig("text", Config("text")));
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public void 根地址归一_剥离API前缀()
    {
        Assert.Equal("https://www.runninghub.cn",
            ProviderWorkflowValues.RunningHubRootURL("https://www.runninghub.cn/openapi/v2/"));
        Assert.Equal("https://rh.example.com",
            ProviderWorkflowValues.RunningHubRootURL("https://rh.example.com/openapi"));
        Assert.Equal("https://www.runninghub.cn",
            ProviderWorkflowValues.RunningHubRootURL(""));
    }

    // ------------------------------------------------------------ 字段收集与默认值

    [Fact]
    public void 字段收集_安全边界与默认来源()
    {
        List<WorkflowField> fields =
            WorkflowProviderManagement.FieldsFromManagement(Workflow(), "image");

        WorkflowField prompt = Assert.Single(fields, field => field.Source == "prompt");
        Assert.Equal("60", prompt.NodeID);
        Assert.Equal("text", prompt.FieldName);
        Assert.True(prompt.Required);
        Assert.True(prompt.Enabled);
        Assert.Equal("Positive Prompt · text", prompt.Label);

        // INT.value 与 ImageResize+ 的宽高属于工作流拓扑，服务端强制不可覆盖。
        WorkflowField intValue = Assert.Single(fields, field =>
            field.NodeID == "97" && field.FieldName == "value");
        Assert.False(intValue.SafeToOverride);
        Assert.Equal("internal", intValue.Role);
        Assert.False(intValue.Enabled);
        foreach (string name in new[] { "width", "height", "multiple_of" })
        {
            WorkflowField resize = Assert.Single(fields, field =>
                field.NodeID == "95" && field.FieldName == name);
            Assert.False(resize.SafeToOverride);
            Assert.Equal("internal", resize.Role);
            Assert.False(resize.Enabled);
        }

        // LoadImage.image 按类型自动绑定参考图：首图必填、序号从 0 起。
        WorkflowField image = Assert.Single(fields, field => field.Source == "referenceImage");
        Assert.Equal("88", image.NodeID);
        Assert.Equal(0, image.SourceIndex);
        Assert.Equal(1, image.ImageOrder);
        Assert.True(image.Required);
        Assert.Equal("media", image.Role);

        // KSampler 的 seed/noise_seed 默认启用随机。
        Assert.Contains(fields, field => field.NodeID == "99" && field.FieldName == "seed" && field.RandomEnabled);
        Assert.Contains(fields, field => field.NodeID == "99" && field.FieldName == "noise_seed" && field.RandomEnabled);
        // 已连接输入（clip -> [4, 1]）不进字段列表。
        Assert.DoesNotContain(fields, field => field.FieldName == "clip");
    }

    [Fact]
    public void 字段收集_多图槽位按ImageOrder分配()
    {
        Dictionary<string, object?> workflow = Workflow();
        workflow["88"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["class_type"] = "LoadImage",
            ["inputs"] = Parse("""{"image": "a.png", "image_2": "b.png"}"""),
        };
        List<WorkflowField> fields =
            WorkflowProviderManagement.FieldsFromManagement(workflow, "image");
        List<WorkflowField> images = fields.Where(field => field.Source == "referenceImage").ToList();
        Assert.Equal(2, images.Count);
        Assert.All(images, field => Assert.True(field.Enabled));
        Assert.Contains(images, field => field.FieldName == "image" && field.ImageOrder == 1 && field.SourceIndex == 0);
        Assert.Contains(images, field => field.FieldName == "image_2" && field.ImageOrder == 2 && field.SourceIndex == 1);
        // 只有首图必填。
        Assert.Single(images, field => field.Required);
    }

    [Fact]
    public void PromptFallback_按负向词扣分并返回默认值()
    {
        List<Dictionary<string, object?>> fallback = WorkflowProviderManagement.PromptFallback(
            Workflow(), "新的提示词");
        Dictionary<string, object?> item = Assert.Single(fallback);
        Assert.Equal("60", ProviderWorkflowValues.WorkflowScalarString(item.GetValueOrDefault("nodeId")));
        Assert.Equal("text", ProviderWorkflowValues.WorkflowScalarString(item.GetValueOrDefault("fieldName")));
        Assert.Equal("新的提示词", ProviderWorkflowValues.WorkflowScalarString(item.GetValueOrDefault("fieldValue")));

        Assert.Empty(WorkflowProviderManagement.PromptFallback(Workflow(), "  "));
        Assert.Empty(WorkflowProviderManagement.PromptFallback(null, "提示词"));
    }

    [Fact]
    public void UpsertNodeInfo_同节点同字段替换()
    {
        List<Dictionary<string, object?>> items =
        [
            new(StringComparer.Ordinal) { ["nodeId"] = "60", ["fieldName"] = "text", ["fieldValue"] = "旧" },
            new(StringComparer.Ordinal) { ["nodeId"] = "71", ["fieldName"] = "width", ["fieldValue"] = "1024" },
        ];
        List<Dictionary<string, object?>> overrides =
        [
            new(StringComparer.Ordinal) { ["nodeId"] = "60", ["fieldName"] = "text", ["fieldValue"] = "新" },
            new(StringComparer.Ordinal) { ["nodeId"] = "99", ["fieldName"] = "seed", ["fieldValue"] = "1" },
        ];
        List<Dictionary<string, object?>> merged =
            WorkflowProviderManagement.UpsertNodeInfo(items, overrides);
        Assert.Equal(3, merged.Count);
        Assert.Equal("新", ProviderWorkflowValues.WorkflowScalarString(
            Assert.IsAssignableFrom<Dictionary<string, object?>>(
                FindItem(merged, "60", "text")).GetValueOrDefault("fieldValue")));
    }

    // ------------------------------------------------------------ 值函数：比例 / 数值 / 随机

    [Fact]
    public void 比例归一_剥离展示文案()
    {
        Assert.Equal("9:16", ProviderWorkflowValues.WorkflowAspectRatio("9:16 (Portrait Widescreen)"));
        Assert.Equal("16:9", ProviderWorkflowValues.WorkflowAspectRatio("16:9"));
        Assert.Equal("1:1", ProviderWorkflowValues.WorkflowAspectRatio("1:1 (Square)"));
        Assert.Equal("", ProviderWorkflowValues.WorkflowAspectRatio("not-a-ratio"));
    }

    [Fact]
    public void ResolutionSelector枚举_按比例匹配完整选项()
    {
        WorkflowField field = new()
        {
            ID = "ratio",
            NodeID = "80",
            FieldName = "aspect_ratio",
            ClassType = "ResolutionSelector",
        };
        List<object?> options = ProviderWorkflowValues.WorkflowFieldAllowedOptions(field);
        Assert.Equal(8, options.Count);
        Assert.Equal("9:16 (Portrait Widescreen)",
            ProviderWorkflowValues.WorkflowAspectRatioValue(field, "9:16"));
        Assert.Equal("16:9 (Widescreen)",
            ProviderWorkflowValues.WorkflowAspectRatioValue(field, "16:9"));
        // 选项里没有的比例：无默认值时按 Go 语义原样返回，不强行落回首个选项。
        Assert.Equal("3:5",
            ProviderWorkflowValues.WorkflowAspectRatioValue(field, "3:5"));
    }

    [Fact]
    public void 数值边界_越界与步长校验()
    {
        WorkflowField slider = new()
        {
            ID = "steps::value",
            NodeID = "10",
            FieldName = "value",
            FieldType = "SLIDER",
            Min = 1,
            Max = 30,
            Step = 1,
        };
        ProviderWorkflowValues.ValidateRunningHubFieldValue(slider, 8);
        ProviderWorkflowValues.ValidateRunningHubFieldValue(slider, "15");
        Assert.Throws<InvalidOperationException>(() =>
            ProviderWorkflowValues.ValidateRunningHubFieldValue(slider, 31));
        Assert.Throws<InvalidOperationException>(() =>
            ProviderWorkflowValues.ValidateRunningHubFieldValue(slider, 0));

        WorkflowField stepped = new()
        {
            ID = "cfg",
            NodeID = "11",
            FieldName = "cfg",
            FieldType = "NUMBER",
            Min = 0,
            Max = 1,
            Step = 0.5,
        };
        ProviderWorkflowValues.ValidateRunningHubFieldValue(stepped, 0.5);
        Assert.Throws<InvalidOperationException>(() =>
            ProviderWorkflowValues.ValidateRunningHubFieldValue(stepped, 0.4));

        Assert.True(ProviderWorkflowValues.TryNumericBound("2.5", out double parsed));
        Assert.Equal(2.5, parsed);
        Assert.False(ProviderWorkflowValues.TryNumericBound("abc", out _));
        Assert.Equal(7, ProviderWorkflowValues.WorkflowIntegerBound("7", 1));
        Assert.Equal(1, ProviderWorkflowValues.WorkflowIntegerBound(null, 1));
        Assert.Throws<InvalidOperationException>(() =>
            ProviderWorkflowValues.WorkflowIntegerBound("xyz", 1));
    }

    [Fact]
    public void 随机数_范围与uint32上限()
    {
        long value = ProviderWorkflowValues.RandomWorkflowInteger(10, 10);
        Assert.Equal(10, value);
        Assert.Throws<InvalidOperationException>(
            () => ProviderWorkflowValues.RandomWorkflowInteger(5, 4));
        long small = ProviderWorkflowValues.RandomWorkflowInteger(0, 3);
        Assert.InRange(small, 0, 3);
    }

    // ------------------------------------------------------------ 状态码归一

    [Fact]
    public void 状态码_字符串状态与嵌套data()
    {
        Assert.Equal((0, true), ProviderWorkflowValues.RunningHubStatusCode("success"));
        Assert.Equal((0, true), ProviderWorkflowValues.RunningHubStatusCode("completed"));
        Assert.Equal((813, true), ProviderWorkflowValues.RunningHubStatusCode("QUEUED"));
        Assert.Equal((804, true), ProviderWorkflowValues.RunningHubStatusCode("running"));
        Assert.Equal((805, true), ProviderWorkflowValues.RunningHubStatusCode("failed"));
        Assert.Equal((0, true), ProviderWorkflowValues.RunningHubStatusCode("3"));
        Assert.Equal((0, false), ProviderWorkflowValues.RunningHubStatusCode("unknown-state"));

        Dictionary<string, object?>? payload = Parse(
            """{"code": 0, "data": {"code": 804, "status": "running"}}""");
        Assert.Equal((804, true), ProviderWorkflowValues.RunningHubPayloadCode(payload));

        Dictionary<string, object?>? completed = Parse(
            """{"code": 0, "data": {"taskId": "t1", "status": "success"}}""");
        Assert.Equal((0, true), ProviderWorkflowValues.RunningHubPayloadCode(completed));

        Assert.Equal((0, false), ProviderWorkflowValues.RunningHubPayloadCode(null));
        Assert.Equal((0, false), ProviderWorkflowValues.RunningHubPayloadCode(
            Parse("""{"msg": "ok"}""")));
    }

    // ------------------------------------------------------------ 输出 URL 与 MIME

    [Fact]
    public void 输出URL解析_相对路径协议头与COS映射()
    {
        const string root = "https://www.runninghub.cn";
        Assert.Equal("https://cdn.example.com/a.png",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(root, "https://cdn.example.com/a.png"));
        Assert.Equal(root + "/task/output/x.png",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(root, "/task/output/x.png"));
        Assert.Equal(root + "/output/x.png",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(root, "output/x.png"));
        Assert.Equal("https:" + "//img.example.com/a.png",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(root, "//img.example.com/a.png"));
        Assert.StartsWith("data:image/png;base64,",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(root, "data:image/png;base64,AAAA"));
        Assert.Equal("https://rh-images.xiaoyaoyou.com/img/a.png",
            ProviderWorkflowValues.ResolveRunningHubOutputURL(
                root,
                "https://rh-images-1252422369.cos.ap-beijing.myqcloud.com/img/a.png"));
        Assert.True(ProviderWorkflowValues.RunningHubRelativeOutputPath("assets/a.mp4"));
        Assert.False(ProviderWorkflowValues.RunningHubRelativeOutputPath("../escape"));
    }

    [Fact]
    public void 输出MIME_按扩展名回填()
    {
        Assert.Equal("image/png",
            ProviderWorkflowValues.RunningHubOutputMimeType("https://x/a.png", "application/octet-stream"));
        Assert.Equal("video/mp4",
            ProviderWorkflowValues.RunningHubOutputMimeType("https://x/a.mp4", ""));
        Assert.Equal("image/jpeg",
            ProviderWorkflowValues.RunningHubOutputMimeType("https://x/download", "image/jpeg"));
    }

    [Fact]
    public void 输出值_携带DataUrl与字节数()
    {
        byte[] payload = [0x01, 0x02];
        Dictionary<string, object?>? item =
            ProviderWorkflowValues.WorkflowOutputValue("image/png", payload);
        Assert.NotNull(item);
        Assert.Equal("image/png", item!["mimeType"]);
        Assert.Equal(2L, item["bytes"]);
        Assert.Equal("data:image/png;base64,AQI=", item["dataUrl"]);
        Assert.Null(ProviderWorkflowValues.WorkflowOutputValue("image/png", []));
    }

    // ------------------------------------------------------------ 纯函数辅助

    [Fact]
    public void 辅助函数_DataURL与链接值与上传401()
    {
        (string mimeType, byte[] data) = ProviderWorkflowTask.DecodeDataURL("data:image/png;base64,AQI=");
        Assert.Equal("image/png", mimeType);
        Assert.Equal([0x01, 0x02], data);
        Assert.Throws<InvalidOperationException>(
            () => ProviderWorkflowTask.DecodeDataURL("https://x/a.png"));

        Assert.True(ProviderWorkflowTask.IsLinkValue(
            Parse("""{"v": ["4", 1]}""")?["v"]));
        Assert.False(ProviderWorkflowTask.IsLinkValue("text"));

        Assert.StartsWith("RunningHub 参考素材上传接口认证失败",
            ProviderWorkflowTask.UploadAuthFailureMessage(new ProviderHttpException(
                401, "Unauthorized", "apikey verification failed", TimeSpan.Zero)));
        Assert.Equal("", ProviderWorkflowTask.UploadAuthFailureMessage(new ProviderHttpException(
            403, "Forbidden", "apikey verification failed", TimeSpan.Zero)));
    }

    [Fact]
    public void 失败文案_可行动提示()
    {
        Dictionary<string, object?>? payload = Parse("""{"msg": "node_info_mismatch"}""");
        Assert.Contains("重新选择该 App", ProviderWorkflowValues.RunningHubFailureMessage(payload));

        Dictionary<string, object?>? balance = Parse("""{"msg": "企业版余额不足"}""");
        Assert.Contains("积分 API Key", ProviderWorkflowValues.RunningHubWorkflowFailureMessage(balance));

        Assert.Equal("t1", ProviderWorkflowValues.RunningHubTaskID(
            Parse("""{"data": {"taskId": "t1"}}""")));
        Assert.Equal("input/x.png", ProviderWorkflowValues.RunningHubFileName(
            Parse("""{"data": {"fileName": "input/x.png"}}""")));
    }

    // ------------------------------------------------------------ 端到端：提交 → 轮询 → 下载

    private static string UploadResponse() => """{"code": 0, "data": {"fileName": "input/x.png"}}""";

    private static StubHandler FullChainHandler(
        List<string> outputsResponses,
        string? createResponse = null,
        bool withUpload = true)
    {
        int pollCount = 0;
        return new StubHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/openapi/getJsonApiFormat" => Json(WorkflowEnvelope()),
                "/task/openapi/upload" => withUpload
                    ? Json(UploadResponse())
                    : throw new InvalidOperationException("不应上传参考素材"),
                "/task/openapi/create" => Json(createResponse
                    ?? """{"code": 0, "data": {"taskId": "t1"}}"""),
                "/task/openapi/outputs" => Json(outputsResponses[
                    Math.Min(pollCount++, outputsResponses.Count - 1)]),
                _ when request.Method == HttpMethod.Get => Binary(),
                _ => throw new InvalidOperationException("意外请求：" + path),
            };
        });
    }

    [Fact]
    public async Task 端到端_工作流JSON推断字段并提交轮询下载()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
            [
                """{"code": 804, "data": {"status": "running"}}""",
                """{"code": 0, "data": {"outputs": ["https://127.0.0.1/out/result.png"]}}""",
            ]);
            // LoadImage 首图必填，工作流推断路径同样需要参考图才能通过必填校验。
            TextTaskInput input = Input();
            input.ReferenceImages =
            [
                new ProviderMedia { ID = "m1", DataURL = "data:image/png;base64,AQI=" },
            ];
            Dictionary<string, object?>? result = await NewTask(handler).RunAsync(
                input,
                pollPolicy: Policy());

            Assert.Equal("image", result!["mode"]);
            List<Dictionary<string, object?>> images =
                Assert.IsType<List<Dictionary<string, object?>>>(result["images"]);
            Dictionary<string, object?> image = Assert.Single(images);
            Assert.Equal("image/png", image["mimeType"]);
            Assert.Equal((long)Png.Length, Assert.IsType<long>(image["bytes"]));
            Assert.StartsWith("data:image/png;base64,", (string)image["dataUrl"]!);

            // create：apiKey/workflowId/nodeInfoList，字段值全部字符串化。
            int createIndex = handler.Requests.FindIndex(
                request => request.RequestUri!.AbsolutePath == "/task/openapi/create");
            Dictionary<string, object?>? createBody = Parse(handler.Bodies[createIndex]);
            Assert.NotNull(createBody);
            Assert.Equal("points-key", createBody!["apiKey"]);
            Assert.Equal("wf-1", createBody["workflowId"]);
            List<Dictionary<string, object?>> nodeInfo =
                NodeInfoList(createBody["nodeInfoList"]);
            // prompt 回退 + LoadImage.image + 两个 seed（KSampler 默认值与工作流相同，不发送）。
            Assert.Equal(4, nodeInfo.Count);
            object? promptItem = FindItem(nodeInfo, "60", "text");
            Assert.NotNull(promptItem);
            Assert.Equal("一只猫", ProviderWorkflowValues.WorkflowScalarString(
                Assert.IsAssignableFrom<Dictionary<string, object?>>(promptItem)
                    .GetValueOrDefault("fieldValue")));
            object? imageItem = FindItem(nodeInfo, "88", "image");
            Assert.Equal("input/x.png", ProviderWorkflowValues.WorkflowScalarString(
                Assert.IsAssignableFrom<Dictionary<string, object?>>(imageItem)
                    .GetValueOrDefault("fieldValue")));
            Assert.All(nodeInfo, item => Assert.IsType<string>(
                Assert.IsAssignableFrom<Dictionary<string, object?>>(item)["fieldValue"]));
            // INT.value / ImageResize+ 宽高从不进入 nodeInfoList。
            Assert.DoesNotContain(nodeInfo, item =>
                ProviderWorkflowValues.WorkflowScalarString(
                    item.GetValueOrDefault("nodeId")) == "97");
            Assert.DoesNotContain(nodeInfo, item =>
                ProviderWorkflowValues.WorkflowScalarString(
                    item.GetValueOrDefault("nodeId")) == "95");

            // outputs 查询带着 taskId 与积分 Key。
            int outputsIndex = handler.Requests.FindIndex(
                request => request.RequestUri!.AbsolutePath == "/task/openapi/outputs");
            Dictionary<string, object?>? outputsBody = Parse(handler.Bodies[outputsIndex]);
            Assert.Equal("t1", outputsBody!["taskId"]);
            Assert.Equal("points-key", outputsBody["apiKey"]);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 端到端_显式字段映射跳过拉取()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            List<WorkflowField> fields =
            [
                new WorkflowField
                {
                    ID = "60::text",
                    NodeID = "60",
                    FieldName = "text",
                    Source = "prompt",
                    Required = true,
                },
            ];
            StubHandler handler = FullChainHandler(
                ["""{"code": 0, "data": {"outputs": ["https://127.0.0.1/out/a.png"]}}"""],
                withUpload: false);
            TextTaskInput input = Input();
            input.Config.WorkflowFields = fields;
            // 只有显式提供 WorkflowJSON 时才会跳过工作流拉取（Go 同款触发条件）。
            input.Config.WorkflowJSON = Parse(TestJSON);
            Dictionary<string, object?>? result = await NewTask(handler).RunAsync(
                input,
                pollPolicy: Policy());
            Assert.Equal("image", result!["mode"]);

            Assert.DoesNotContain(handler.Requests, request =>
                request.RequestUri!.AbsolutePath == "/api/openapi/getJsonApiFormat");
            int createIndex = handler.Requests.FindIndex(
                request => request.RequestUri!.AbsolutePath == "/task/openapi/create");
            Dictionary<string, object?>? createBody = Parse(handler.Bodies[createIndex]);
            List<Dictionary<string, object?>> nodeInfo =
                NodeInfoList(createBody!["nodeInfoList"]);
            Dictionary<string, object?> item = Assert.Single(nodeInfo);
            Assert.Equal("一只猫", item["fieldValue"]);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 端到端_video模式轮询状态机()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
            [
                """{"code": 0, "data": {"taskId": "t1", "status": "queued"}}""",
                """{"code": 0, "data": {"taskId": "t1", "status": "running"}}""",
                """{"code": 0, "data": {"taskId": "t1", "videos": [{"url": "https://127.0.0.1/out/v.mp4"}]}}""",
            ]);
            // video 模式下 LoadImage 首图同样必填。
            TextTaskInput input = Input("video");
            input.ReferenceImages =
            [
                new ProviderMedia { ID = "m1", DataURL = "data:image/png;base64,AQI=" },
            ];
            Dictionary<string, object?>? result = await NewTask(handler).RunAsync(
                input,
                pollPolicy: Policy());
            Assert.Equal("video", result!["mode"]);
            Dictionary<string, object?> video =
                Assert.IsType<Dictionary<string, object?>>(result["video"]);
            Assert.Equal("video/mp4", video["mimeType"]);
            Assert.Equal((long)Png.Length, Assert.IsType<long>(video["bytes"]));
            // 排队与运行中的暂态响应都被继续轮询。
            Assert.Equal(3, handler.Requests.Count(request =>
                request.RequestUri!.AbsolutePath == "/task/openapi/outputs"));
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 端到端_失败码向上抛错()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
                ["""{"code": 805, "data": {"msg": "GPU 节点崩溃"}}"""]);
            TextTaskInput input = Input();
            input.Config.WorkflowFields =
            [
                new WorkflowField { ID = "60::text", NodeID = "60", FieldName = "text", Source = "prompt", Required = true },
            ];
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewTask(handler).RunAsync(input, pollPolicy: Policy()));
            Assert.Contains("RunningHub 任务失败", error.Message);
            Assert.Contains("GPU 节点崩溃", error.Message);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 端到端_恢复模式直接从轮询开始()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
                ["""{"code": 0, "data": {"outputs": ["https://127.0.0.1/out/r.png"]}}"""]);
            Dictionary<string, object?>? result = await NewTask(handler).RunAsync(
                Input(),
            resumedProviderRequestId: "t1",
            pollPolicy: Policy());
            Assert.Equal("image", result!["mode"]);
            Assert.DoesNotContain(handler.Requests, request =>
                request.RequestUri!.AbsolutePath is "/task/openapi/create" or "/api/openapi/getJsonApiFormat");
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    // ------------------------------------------------------------ 端到端：校验失败

    private static async Task RunSlotCaseAsync(TextTaskInput input, string expectedFragment)
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler([], withUpload: false);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewTask(handler).RunAsync(input, pollPolicy: Policy()));
            Assert.Contains(expectedFragment, error.Message);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 校验_参考图超出槽位()
    {
        TextTaskInput input = Input();
        input.ReferenceImages =
        [
            new ProviderMedia { ID = "m1", DataURL = "data:image/png;base64,AQI=" },
            new ProviderMedia { ID = "m2", DataURL = "data:image/png;base64,AQI=" },
        ];
        // stub 现在返回真实工作流图：LoadImage 推断出 1 个图片槽位。
        await RunSlotCaseAsync(input, "工作流只配置了 1 个参考图片槽位，但画布传入了 2 个");
    }

    [Fact]
    public async Task 校验_蒙版没有映射()
    {
        TextTaskInput input = Input();
        input.Mask = new ProviderMedia { ID = "mask", DataURL = "data:image/png;base64,AQI=" };
        await RunSlotCaseAsync(input, "画布传入了蒙版，但工作流没有配置蒙版字段映射");
    }

    [Fact]
    public async Task 校验_必填字段缺值()
    {
        TextTaskInput input = Input(prompt: "");
        input.Config.WorkflowFields =
        [
            new WorkflowField { ID = "60::text", NodeID = "60", FieldName = "text", Source = "prompt", Required = true },
        ];
        await RunSlotCaseAsync(input, "工作流字段 60::text 缺少值");
    }

    [Fact]
    public async Task 提交失败_上游返回非零code()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
                [],
                createResponse: """{"code": 400, "msg": "参数不合法"}""",
                withUpload: false);
            TextTaskInput input = Input();
            input.Config.WorkflowFields =
            [
                new WorkflowField { ID = "60::text", NodeID = "60", FieldName = "text", Source = "prompt", Required = true },
            ];
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewTask(handler).RunAsync(input, pollPolicy: Policy()));
            Assert.Contains("RunningHub 工作流提交失败", error.Message);
            Assert.Contains("参数不合法", error.Message);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 轮询超时_抛TimeoutException()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = FullChainHandler(
                ["""{"code": 804, "data": {"status": "running"}}"""]);
            // video 模式轮询尊重注入策略（2 秒超时 + 空休眠）；image 模式遗留轮询固定 1 小时会挂住测试。
            TextTaskInput input = Input("video");
            input.Config.WorkflowFields =
            [
                new WorkflowField { ID = "60::text", NodeID = "60", FieldName = "text", Source = "prompt", Required = true },
            ];
            await Assert.ThrowsAsync<TimeoutException>(
                () => NewTask(handler).RunAsync(input, pollPolicy: Policy()));
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }

    [Fact]
    public async Task 上传成功_公开URL参考素材()
    {
        string? previous = Environment.GetEnvironmentVariable("CANVAS_ALLOWED_PRIVATE_UPSTREAM_HOSTS");
        AllowPrivateHosts();
        try
        {
            StubHandler handler = new((request, _) =>
            {
                string path = request.RequestUri!.AbsolutePath;
                return path switch
                {
                    "/api/openapi/getJsonApiFormat" => Json(WorkflowEnvelope()),
                    "/task/openapi/upload" => Json(UploadResponse()),
                    "/task/openapi/create" => Json("""{"code": 0, "data": {"taskId": "t1"}}"""),
                    "/task/openapi/outputs" => Json(
                        """{"code": 0, "data": {"outputs": ["https://127.0.0.1/out/a.png"]}}"""),
                    _ when request.Method == HttpMethod.Get => Binary(),
                    _ => throw new InvalidOperationException("意外请求：" + path),
                };
            });
            TextTaskInput input = Input();
            // 参考图只带公开 URL：上传前必须先下载字节，不把外部地址交给供应商。
            input.ReferenceImages = [new ProviderMedia { ID = "m1", URL = "https://127.0.0.1/ref/a.png" }];
            Dictionary<string, object?>? result = await NewTask(handler).RunAsync(
                input,
                pollPolicy: Policy());
            Assert.Equal("image", result!["mode"]);
            int uploadIndex = handler.Requests.FindIndex(
                request => request.RequestUri!.AbsolutePath == "/task/openapi/upload");
            Assert.Contains("name=\"apiKey\"", handler.Bodies[uploadIndex]);
            Assert.Contains("name=\"fileType\"", handler.Bodies[uploadIndex]);
            Assert.Contains("name=\"file\"", handler.Bodies[uploadIndex]);
        }
        finally
        {
            RestorePrivateHosts(previous);
        }
    }
}
