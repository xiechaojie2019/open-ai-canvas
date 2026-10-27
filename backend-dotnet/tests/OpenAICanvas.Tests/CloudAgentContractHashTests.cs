using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Canvas.Capability;
using OpenAICanvas.Prompts;

using Xunit;

namespace OpenAICanvas.Tests;

/// <summary>
/// 契约红线：能力集哈希与 Agent 策略文档哈希必须与 Go 逐字节一致。
/// 基线值由 <c>go run</c> capability/prompts 包计算得出。
/// </summary>
public class CloudAgentContractHashTests
{
    [Fact]
    public void CapabilitySetHash_MatchesGoBaseline()
    {
        Assert.Equal(
            "910aa1fc3959f67d9228d3f006b2558631b242423931ce5b6547ff7e1dab65bd",
            BuiltinCanvasCapabilities.BuiltinRegistry().Hash());
    }

    [Fact]
    public void CapabilitySetVersion_MatchesGo()
    {
        Assert.Equal("canvas-capabilities/v4", CapabilityRegistry.SetVersion);
    }

    [Fact]
    public async Task AgentPolicyDocuments_MatchGoBaselines()
    {
        (AgentPolicy system, AgentPolicy media) = await AgentPolicyDocuments.LoadAgentPoliciesAsync();
        // 系统策略 v3：新增「图片产出文本时建参考连线」规则，解析 + 哈希必须与 Go 逐字节一致。
        Assert.Equal("cloud-agent-system", system.Id);
        Assert.Equal(3, system.Version);
        Assert.Equal("ee441a9879ec5a5b40237ac6403342919ea45143a4e0e4ca8eb6cc158e98794b", system.Hash);
        Assert.Equal("cloud-agent-media", media.Id);
        // ⚠️ 媒体策略**有意偏离 Go 基线**（Go 为 version 2 / abcd5762…）：
        // 本 fork 按产品要求放开了两处规则 —— auto 模式不再对 generate_media 强制人工审批、
        // 且允许一次出多张（count）。两处都必须写进策略正文，否则模型仍会逐张调用并等审批。
        // 策略正文是 MediaPolicyHash 的输入 ⇒ 改文必然改哈希，在途旧运行会被
        // ValidatePolicyVersions 判 409（预期行为：规则变了就不让旧运行带着旧规则跑完）。
        Assert.Equal(3, media.Version);
        Assert.Equal("6808c76bb401ca7be521bc06381a66c9636413ae2cc93bbf99b4df87ffc9ff75", media.Hash);
        // 放宽范围必须是「有界」的：策略里仍要明确收费授权与预算兜底的边界。
        Assert.Contains("自动执行模式不做独立审批", media.Text);
        Assert.Contains("count", media.Text);
    }

    [Fact]
    public void AgentID_MatchesGoDerivation()
    {
        // cloudAgentID("user", "key") = "ag" + hex(sha256("user\x00key")[:16])
        Assert.Equal("ag" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(new byte[] { 117, 115, 101, 114, 0, 107, 101, 121 }),
            0, 16).ToLowerInvariant(), CloudAgentContracts.AgentID("user", "key"));
    }

    [Fact]
    public void CanvasHash_IgnoresViewportAndUpdateTime()
    {
        string doc1 = """{"nodes":[],"updatedAt":"2026-01-01T00:00:00Z","viewport":{"x":1}}""";
        string doc2 = """{"nodes":[],"updatedAt":"2027-02-02T00:00:00Z"}""";
        JsonObject a = JsonNode.Parse(doc1)!.AsObject();
        JsonObject b = JsonNode.Parse(doc2)!.AsObject();
        Assert.Equal(CloudAgentContracts.CanvasHash(a), CloudAgentContracts.CanvasHash(b));
    }


    [Fact]
    public void CapabilityHashInput_MatchesGoFixture()
    {
        string go = File.ReadAllText("Fixtures/capability-hash-input.json").Trim();
        string net = BuiltinCanvasCapabilities.BuiltinRegistry().HashInputJsonForTest();
        Assert.Equal(go, net);
    }
}
