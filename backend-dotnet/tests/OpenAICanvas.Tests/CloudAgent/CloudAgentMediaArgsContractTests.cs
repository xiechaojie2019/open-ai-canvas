#nullable enable
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// <c>generate_media</c> 参数解码契约：工具 schema 下发给模型的字段名
/// 必须与 <see cref="CloudAgentMediaArgs"/> 的 JSON 绑定完全一致。
/// 对应 Go: <c>internal/app/cloud_agent_media.go</c> 的 <c>cloudAgentMediaArgs</c> struct tag。
/// </summary>
/// <remarks>
/// 2026-10-10 线上事故：schema 里声明的是 <c>durationSeconds</c>（Go 风格），
/// 但 .NET 的 <c>CloudAgentMediaArgs.Duration</c> 靠 camelCase 策略绑成 <c>duration</c>。
/// <see cref="CloudAgentContracts.DecodeObject{T}"/> 用
/// <c>UnmappedMemberHandling.Disallow</c>，于是模型按 schema 传
/// <c>"durationSeconds": 3</c> 直接被拒 ⇒ 报「生成参数必须是只含支持字段的单个JSON对象」。
/// 视频必带 duration，所以生视频 100% 失败；生图不带 duration，侥幸能过。
/// </remarks>
public sealed class CloudAgentMediaArgsContractTests
{
    /// <summary>线上真实失败参数（run agd917dcdc286a36b54aae169967ddc32e 的 seq 19）。</summary>
    private const string RealVideoArguments = """
        {
          "mode": "video",
          "nodeId": "video-1791595112000-catfi",
          "title": "小猫抓鱼视频",
          "prompt": "小猫抓鱼",
          "referenceNodeIds": ["image-1791594820000-kx7q2"],
          "snapshotHash": "be76c4a149db30cead7d21ddaf73e58e8b0fc2535d49e618fb1ef43ba019847d",
          "durationSeconds": 3,
          "size": "1:1",
          "logicalModelId": "doubao-seedance-2-0-260128"
        }
        """;

    [Fact]
    public void 线上真实视频参数可解码()
    {
        CloudAgentMediaArgs args = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(RealVideoArguments);

        Assert.Equal("video", args.Mode);
        Assert.Equal(3, args.Duration);
        Assert.Equal("小猫抓鱼视频", args.Title);
        Assert.Equal("video-1791595112000-catfi", args.NodeID);
        Assert.Equal(["image-1791594820000-kx7q2"], args.ReferenceNodeIDs);
    }

    [Fact]
    public void 工具schema声明的每个字段名都能解码()
    {
        // schema 里出现过的名字，一个都不能被 Disallow 拒掉。
        string[] schemaFields =
        [
            "mode", "prompt", "logicalModelId", "channelId", "channelModelKey",
            "durationSeconds", "size", "quality", "videoGenerateAudio",
            "snapshotHash", "nodeId", "title", "sourceNodeId", "referenceNodeIds", "count",
        ];
        foreach (string field in schemaFields)
        {
            string json = $$"""
                {
                  "mode": "video",
                  "prompt": "p",
                  "snapshotHash": "h",
                  "nodeId": "n",
                  "title": "t",
                  "referenceNodeIds": [],
                  "{{field}}": {{SampleFor(field)}}
                }
                """;

            CloudAgentMediaArgs args = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(json);

            Assert.NotNull(args);
        }
    }

    [Theory]
    // Go 的 json tag 就是权威契约，缺一个都算漂移
    [InlineData("durationSeconds")]
    [InlineData("logicalModelId")]
    [InlineData("channelId")]
    [InlineData("channelModelKey")]
    [InlineData("nodeId")]
    [InlineData("sourceNodeId")]
    [InlineData("referenceNodeIds")]
    [InlineData("videoGenerateAudio")]
    [InlineData("snapshotHash")]
    [InlineData("count")]
    public void Go的json_tag必须被接受(string field)
    {
        string json = $$"""
            {
              "mode": "video", "prompt": "p", "snapshotHash": "h",
              "nodeId": "n", "title": "t", "referenceNodeIds": [],
              "{{field}}": {{SampleFor(field)}}
            }
            """;

        CloudAgentMediaArgs args = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(json);

        Assert.NotNull(args);
    }

    [Fact]
    public void 未知字段仍然必须被拒()
    {
        // 不能为了容错就把 Disallow 放开——那是把契约校验整个关掉。
        Assert.ThrowsAny<Exception>(() => CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(
            """{"mode":"video","prompt":"p","snapshotHash":"h","nodeId":"n","title":"t","referenceNodeIds":[],"duratioSeconds":3}"""));
    }

    /// <summary>
    /// 批量出图（count&gt;1，本 fork 扩展）的 schema 契约：字段必须真的下发给模型，
    /// 且上限与 <see cref="CloudAgentMediaService.MaxBatchCount"/> 同源——两处各写一个数字
    /// 迟早会漂移成「schema 允许 12、服务端只放 8」。
    /// </summary>
    [Fact]
    public void 工具schema必须声明count且上限与服务端一致()
    {
        Dictionary<string, System.Text.Json.JsonElement> tool = CloudAgentTools.BuildTools(null)
            .Single(item => item["function"].GetProperty("name").GetString() == "generate_media");
        System.Text.Json.JsonElement parameters = tool["function"].GetProperty("parameters");
        System.Text.Json.JsonElement properties = parameters.GetProperty("properties");

        Assert.True(properties.TryGetProperty("count", out System.Text.Json.JsonElement count),
            "generate_media schema 必须声明 count，否则模型无从表达「一次出几张」");
        Assert.Equal("integer", count.GetProperty("type").GetString());
        Assert.Equal(1, count.GetProperty("minimum").GetInt32());
        Assert.Equal(CloudAgentMediaService.MaxBatchCount, count.GetProperty("maximum").GetInt32());
        Assert.Contains("image", count.GetProperty("description").GetString()!);

        // count 不是必填：不传等价于 1，单产物路径与 Go 保持同形。
        Assert.DoesNotContain("count", parameters.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>批量参数必须能解码出张数，且单张时默认 1。</summary>
    [Fact]
    public void 批量张数可解码()
    {
        CloudAgentMediaArgs batch = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(
            """{"mode":"image","prompt":"p","snapshotHash":"h","nodeId":"n","title":"t","referenceNodeIds":[],"count":3}""");
        Assert.Equal(3, batch.Count);

        CloudAgentMediaArgs single = CloudAgentContracts.DecodeObject<CloudAgentMediaArgs>(
            """{"mode":"image","prompt":"p","snapshotHash":"h","nodeId":"n","title":"t","referenceNodeIds":[]}""");
        Assert.Equal(0, single.Count); // 0 = 未传；PrepareCoreAsync 会归一成 1。
    }

    private static string SampleFor(string field) => field switch
    {
        "mode" => "\"video\"",
        "prompt" => "\"p\"",
        "logicalModelId" => "\"model-a\"",
        "channelId" => "\"CHANNEL_000001\"",
        "channelModelKey" => "\"gpt-video-1\"",
        "durationSeconds" => "3",
        "size" => "\"1:1\"",
        "quality" => "\"720p\"",
        "videoGenerateAudio" => "true",
        "snapshotHash" => "\"h\"",
        "nodeId" => "\"n\"",
        "title" => "\"t\"",
        "sourceNodeId" => "\"s\"",
        "referenceNodeIds" => "[\"img-1\"]",
        "count" => "3",
        _ => "null",
    };
}
