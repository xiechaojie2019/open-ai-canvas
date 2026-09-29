#nullable enable
using System.Text.Json;
using OpenAICanvas.Application.CloudAgent;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// Agent 上下文预算与收敛的契约测试。
/// 对应 Go: <c>cloud_agent_context_budget_test.go</c>。
/// </summary>
public sealed class CloudAgentContextBudgetTests
{
    [Fact]
    public void 预算_大窗口与输出上限分离()
    {
        CloudAgentContextBudget budget = CloudAgentContextBudgets.For(1_000_000, 64_000, "channel-model");
        Assert.Equal(1_000_000, budget.ContextWindowTokens);
        Assert.Equal(64_000, budget.MaxOutputTokens);
        Assert.True(budget.InputBudgetTokens > 900_000);
        Assert.True(budget.CompactAtTokens < budget.InputBudgetTokens);
        Assert.Equal("channel-model", budget.Source);
    }

    [Fact]
    public void 预算_非法值回退默认()
    {
        CloudAgentContextBudget budget = CloudAgentContextBudgets.For(1, 0, "default");
        Assert.Equal(CloudAgentContextBudgets.DefaultContextWindowTokens, budget.ContextWindowTokens);
        Assert.True(budget.MaxOutputTokens > 0);
        Assert.True(budget.InputBudgetTokens > 0);
    }

    [Fact]
    public void 估算_中文比英文保守()
    {
        string english = new string('x', 4 * 1000);
        string chinese = new string('中', 1000);
        Assert.True(
            CloudAgentContextBudgets.EstimatedTokens(chinese) > CloudAgentContextBudgets.EstimatedTokens(english));
    }

    [Fact]
    public void 收敛_超预算必须报错_宽松预算通过()
    {
        CloudAgentCanonicalRequestDto request = new()
        {
            Messages =
            [
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["role"] = JsonSerializer.SerializeToElement("user"),
                    ["content"] = JsonSerializer.SerializeToElement(new string('中', 5000)),
                },
            ],
        };
        Assert.ThrowsAny<Exception>(() => CloudAgentRuntimeContextProbe.Fit(request, 1_000));
        CloudAgentRuntimeContextProbe.Fit(request, 20_000);
    }

    [Fact]
    public void 收敛_只淘汰完整旧轮次_保留用户与新鲜轮次()
    {
        CloudAgentCanonicalRequestDto request = new()
        {
            Messages =
            [
                User("goal-one"),
                Assistant("old-reply-longer"),
                Tool("old-result-longer"),
                Assistant("fresh-reply"),
                Tool("fresh-result"),
                User("latest-instruction"),
            ],
        };
        // 预算设为“总量减一点”：只允许成组淘汰旧 assistant/tool 轮次即可达标，
        // 最新完整轮次与用户指令保留（预算值由请求本身推导，不依赖手工估算）。
        int total = CloudAgentContextBudgets.RequestEstimatedTokens(request);
        CloudAgentRuntimeContextProbe.Fit(request, total - 8);
        Assert.Equal("user", Role(request.Messages[0]));
        Assert.Contains(request.Messages, message => Role(message) == "assistant");
        Assert.Equal("user", Role(request.Messages[^1]));
        Assert.DoesNotContain(request.Messages, message =>
            Role(message) != "" && Content(message).Contains("old-reply-longer", StringComparison.Ordinal));
    }

    [Fact]
    public void 预算文案_含输入预算与来源()
    {
        CloudAgentContextBudget budget = CloudAgentContextBudgets.For(128_000, 16_384, "channel-model");
        string message = CloudAgentContextBudgets.BudgetMessage(budget);
        Assert.Contains("模型输入预算", message, StringComparison.Ordinal);
        Assert.Contains("channel-model", message, StringComparison.Ordinal);
    }

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, JsonElement> User(string content) => new(StringComparer.Ordinal)
    {
        ["role"] = Text("user"),
        ["content"] = Text(content),
    };

    private static Dictionary<string, JsonElement> Assistant(string content) => new(StringComparer.Ordinal)
    {
        ["role"] = Text("assistant"),
        ["content"] = Text(content),
    };

    private static Dictionary<string, JsonElement> Tool(string content) => new(StringComparer.Ordinal)
    {
        ["role"] = Text("tool"),
        ["tool_call_id"] = Text("call-1"),
        ["content"] = Text(content),
    };

    private static string Content(Dictionary<string, JsonElement> message) =>
        message.TryGetValue("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";

    private static string Role(Dictionary<string, JsonElement> message) =>
        message.TryGetValue("role", out JsonElement role) && role.ValueKind == JsonValueKind.String
            ? role.GetString() ?? ""
            : "";
}

/// <summary>访问 internal 收敛函数的探针。</summary>
public static class CloudAgentRuntimeContextProbe
{
    public static void Fit(CloudAgentCanonicalRequestDto request, int maxTokens) =>
        CloudAgentRuntimeService.FitCloudAgentModelContext(request, maxTokens);
}
