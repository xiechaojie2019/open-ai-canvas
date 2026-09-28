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
            "9f4199f9d89d5ce4ba08142f78c17cdd6c8f888c46069ea562786e0bfd8cf980",
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
        Assert.Equal("cloud-agent-system", system.Id);
        Assert.Equal(2, system.Version);
        Assert.Equal("82fc03c3b8a5b484acc4119569eaf0f3ffdffabd459839b6ce89e0598cb40d43", system.Hash);
        Assert.Equal("cloud-agent-media", media.Id);
        Assert.Equal(2, media.Version);
        Assert.Equal("abcd5762760a551595e9e5f972d4f6e7d1d616c2f1323a949669e7cce9846cf4", media.Hash);
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
