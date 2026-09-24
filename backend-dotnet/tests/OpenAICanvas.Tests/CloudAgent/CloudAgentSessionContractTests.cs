using System.Text.Json;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 阶段 11.2 会话层契约：工具清单/Canonical 缓存键/续聊历史恢复与 Go 行为一致。
/// </summary>
public class CloudAgentSessionContractTests
{
    [Fact]
    public void SupportedToolNames_MatchGoBaseline()
    {
        string[] expected =
        [
            "agent_profile_read", "canvas_list_node_types", "canvas_get_state", "canvas_read_batch_table",
            "canvas_read_storyboard", "skill_read_file", "task_get", "model_list", "canvas_create_storyboard",
            "canvas_edit_storyboard", "canvas_edit_batch_table", "canvas_apply_ops", "generate_media",
        ];
        Assert.Equal(expected, CloudAgentTools.SupportedToolNames());
    }

    [Fact]
    public void Canonical_UsesCanvasScopedCacheKey()
    {
        CloudAgentCanonicalRequestDto canonical = CloudAgentSessionService.Canonical(
            "system-prompt", [], "prompt", "canvas-1");
        string expectedKey = "cloud-agent:" + CloudAgentContracts.Sha256Hex("canvas-1\x00system-prompt")[..48];
        Assert.Equal(expectedKey, canonical.PromptCacheKey);
        Assert.Equal("auto", canonical.ToolChoice.GetString());
        Assert.Single(canonical.Messages);
        Assert.Equal("user", canonical.Messages[0]["role"].GetString());
        Assert.Equal("prompt", canonical.Messages[0]["content"].GetString());
    }

    [Fact]
    public void LegacyHistory_RestoresStrictAlternatingPrefix()
    {
        static Dictionary<string, JsonElement> Message(string role, string content) => new(StringComparer.Ordinal)
        {
            ["role"] = JsonSerializer.SerializeToElement(role),
            ["content"] = JsonSerializer.SerializeToElement(content),
        };
        List<Dictionary<string, JsonElement>> messages =
        [
            Message("user", "第一问"),
            Message("assistant", "第一答"),
            Message("user", "第二问"),
            Message("assistant", "第二答"),
        ];
        List<CloudAgentTextMessageDto> history =
            CloudAgentSessionService.LegacyHistory(messages, "第二问");
        Assert.Equal(2, history.Count);
        Assert.Equal(("user", "第一问"), (history[0].Role, history[0].Content));
        Assert.Equal(("assistant", "第一答"), (history[1].Role, history[1].Content));

        // 当前消息是 assistant 序号或找不到时，一律放弃恢复。
        Assert.Empty(CloudAgentSessionService.LegacyHistory(messages, "不存在"));
        List<Dictionary<string, JsonElement>> oddCount =
        [
            Message("user", "第一问"),
            Message("assistant", "第一答"),
            Message("user", "第二问"),
        ];
        // 当前消息位于偶数下标时，交替前缀有效，返回其前缀。
        List<CloudAgentTextMessageDto> prefixHistory =
            CloudAgentSessionService.LegacyHistory(oddCount, "第二问");
        Assert.Equal(2, prefixHistory.Count);

        // 角色不交替：恢复失败。
        List<Dictionary<string, JsonElement>> broken =
        [
            Message("user", "第一问"),
            Message("user", "不是助手"),
            Message("user", "第二问"),
        ];
        Assert.Empty(CloudAgentSessionService.LegacyHistory(broken, "第二问"));
    }

    [Fact]
    public void ValidateRequest_EnforcesGoContract()
    {
        // read_only 不能带生成预算。
        CloudAgentRequestDto readOnly = ValidRequest();
        readOnly.PermissionMode = "read_only";
        readOnly.Budget.MaxGenerationTasks = 2;
        Assert.Throws<AppError>(() => CloudAgentContracts.ValidateRequest(readOnly));

        // 逻辑模型与系统渠道不能混用。
        CloudAgentRequestDto mixed = ValidRequest();
        mixed.LogicalModelID = "lm-1";
        Assert.Throws<AppError>(() => CloudAgentContracts.ValidateRequest(mixed));

        // 幂等键过短。
        CloudAgentRequestDto shortKey = ValidRequest();
        shortKey.IdempotencyKey = "short";
        Assert.Throws<AppError>(() => CloudAgentContracts.ValidateRequest(shortKey));

        // 合法请求通过且 ContextScope 归一。
        CloudAgentRequestDto ok = ValidRequest();
        CloudAgentContracts.ValidateRequest(ok);
        Assert.Equal("canvas", ok.ContextScope[0]);
    }

    [Fact]
    public void AgentId_IsDeterministicPerUserAndKey()
    {
        Assert.Equal(CloudAgentContracts.AgentID("u", "k"), CloudAgentContracts.AgentID("u", "k"));
        Assert.NotEqual(CloudAgentContracts.AgentID("u", "k"), CloudAgentContracts.AgentID("u2", "k"));
        Assert.StartsWith("ag", CloudAgentContracts.AgentID("u", "k"));
    }

    private static CloudAgentRequestDto ValidRequest() => new()
    {
        CanvasID = "canvas-1",
        Prompt = "帮我生成分镜",
        PermissionMode = "auto",
        ChannelID = "ch-1",
        ChannelModelKey = "model-key",
        Budget = new CloudAgentBudgetDto { MaxCredits = 5 },
        ContextScope = ["canvas"],
        IdempotencyKey = "idempotency-key-1",
    };
}
