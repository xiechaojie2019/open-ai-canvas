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
            "snapshotHash", "nodeId", "title", "sourceNodeId", "referenceNodeIds",
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
        _ => "null",
    };
}
