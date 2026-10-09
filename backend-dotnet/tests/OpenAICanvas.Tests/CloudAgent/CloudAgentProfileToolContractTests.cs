#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAICanvas.Application;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.CloudAgent;

/// <summary>
/// 偏好读取工具的下发契约。对应 Go:
/// <c>TestCloudAgentOmitsProfileReadWhenNoLayers</c> + <c>TestCloudAgentProfileReadListsAvailableLayers</c>。
///
/// 为什么需要它：单测环境与线上一样，<c>agentProfiles</c> 常常是空的（用户从没保存过偏好文档）。
/// 此时系统提示会正确写明「本轮没有用户/项目偏好文档。」，但只要 <c>agent_profile_read</c> 还留在
/// 工具清单里，模型就会去读 —— 而本轮一个层都没有，于是把 <c>user</c>、<c>project</c>、<c>canvas</c>
/// 挨个试一遍，三次全失败。
///
/// 线上实例（2026-10-09，run <c>ag967165184e50c55a565d68bba03d2ae2</c>）：
/// seq1/2/3 三次 <c>tool_failed</c>，arguments 依次是 <c>{"scope":"user"}</c>、
/// <c>{"scope":"project"}</c>、<c>{"scope":"canvas"}</c>，报错全是同一句
/// 「本轮固定快照中不存在该长期偏好层；请只读取系统清单列出的层」—— 这句不给任何可自纠信息，
/// 模型无从退出枚举循环。Go 侧既不在空快照时下发该工具，报错也会列出可读层。
/// </summary>
public sealed class CloudAgentProfileToolContractTests
{
    private const string ProfileTool = "agent_profile_read";

    /// <summary>空清单快照不得把工具下发给模型（本缺陷的主因）。</summary>
    [Fact]
    public void 清单为空时不下发偏好读取工具()
    {
        Assert.DoesNotContain(ProfileTool, ToolNames(CloudAgentTools.BuildTools(Request(), includeProfileTool: false)));
        Assert.Contains(ProfileTool, ToolNames(CloudAgentTools.BuildTools(Request(), includeProfileTool: true)));
    }

    /// <summary>规范形（真正下发的那份）同样由调用方按有无偏好层决定。</summary>
    [Fact]
    public void 规范形按偏好层数决定是否下发偏好读取工具()
    {
        CloudAgentCanonicalRequestDto empty = CloudAgentSessionService.Canonical(
            "system", [], "把正文写进节点", "canvas-1", Request(), includeProfileTool: false);
        Assert.DoesNotContain(ProfileTool, ToolNames(empty.Tools));

        CloudAgentCanonicalRequestDto filled = CloudAgentSessionService.Canonical(
            "system", [], "把正文写进节点", "canvas-1", Request(), includeProfileTool: true);
        Assert.Contains(ProfileTool, ToolNames(filled.Tools));
    }

    /// <summary>
    /// 运行时判定仍按全量走（对应 Go 的 <c>cloudAgentToolAllowed</c>）。
    /// 空快照时工具只是"不下发"，不是"不认识"：模型硬调要能走到 ProfileRead 拿到明确指引，
    /// 而不是一句无用的「未知工具」。
    /// </summary>
    [Fact]
    public void 运行时仍放行偏好工具以便给出明确报错()
    {
        Assert.True(CloudAgentTools.Allowed(Request(), ProfileTool));
    }

    /// <summary>空清单时明说「不要调用」，与 Go 的空清单分支逐字一致。</summary>
    [Fact]
    public void 空清单快照拒绝偏好读取并给出明确指引()
    {
        CloudAgentRuntimeDto state = new() { Profile = new CloudAgentProfileSnapshotDto { Layers = [] } };

        AppError error = Assert.Throws<AppError>(() => CloudAgentRuntimeService.ProfileRead(state, Call("user")));

        Assert.Equal("本轮没有长期偏好层，不要调用 agent_profile_read", error.Message);
    }

    /// <summary>清单非空时必须列出可读层，模型才能自纠。</summary>
    [Fact]
    public void 清单非空时读取不存在的层会列出可读层()
    {
        CloudAgentRuntimeDto state = new()
        {
            Profile = new CloudAgentProfileSnapshotDto
            {
                Layers = [new AgentProfileLayerDto { Scope = "user", Content = "only-user" }],
            },
        };

        AppError error = Assert.Throws<AppError>(() => CloudAgentRuntimeService.ProfileRead(state, Call("canvas")));

        Assert.Contains("user", error.Message);
        Assert.Contains("不要再尝试其它层", error.Message);
        // Go 的 TestCloudAgentProfileReadListsAvailableLayers 明确断言这句话不得出现。
        Assert.DoesNotContain("请只读取系统清单列出的层", error.Message);
    }

    /// <summary>命中存在的层时返回正文并标记已读。</summary>
    [Fact]
    public void 读取存在的层会返回正文并标记已读()
    {
        CloudAgentRuntimeDto state = new()
        {
            Profile = new CloudAgentProfileSnapshotDto
            {
                Layers =
                [
                    new AgentProfileLayerDto { Scope = "project", Revision = 3, Hash = "h", Content = "固定偏好" },
                ],
            },
        };

        JsonObject result = CloudAgentRuntimeService.ProfileRead(state, Call("project"));

        Assert.Equal("project", result["scope"]!.GetValue<string>());
        Assert.Equal("固定偏好", result["content"]!.GetValue<string>());
        Assert.True(state.ProfileReads!["project"]);
    }

    /// <summary>同一层重复读取仍按原契约拒绝（本轮已读过就用历史结果）。</summary>
    [Fact]
    public void 重复读取同一层会被拒绝()
    {
        CloudAgentRuntimeDto state = new()
        {
            Profile = new CloudAgentProfileSnapshotDto
            {
                Layers = [new AgentProfileLayerDto { Scope = "user", Content = "x" }],
            },
            ProfileReads = new Dictionary<string, bool>(StringComparer.Ordinal) { ["user"] = true },
        };

        AppError error = Assert.Throws<AppError>(() => CloudAgentRuntimeService.ProfileRead(state, Call("user")));

        Assert.Equal("本轮已读取该长期偏好层，请使用历史工具结果，不要重复读取", error.Message);
    }

    // ---------- helpers ----------

    private static CloudAgentCallDto Call(string scope) => new()
    {
        ID = "call-profile-1",
        Function = new CloudAgentCallFunctionDto
        {
            Name = ProfileTool,
            Arguments = $"{{\"scope\":\"{scope}\"}}",
        },
    };

    private static CloudAgentRequestDto Request() => new()
    {
        CanvasID = "canvas-1",
        Prompt = "把正文写进节点",
        PermissionMode = "auto",
        IdempotencyKey = "idem-0000000003",
        Model = "glm-5.3-flash",
        ChannelID = "CHANNEL_000001",
        ChannelModelKey = "glm-5.3-flash",
        ReasoningMode = "off",
        ContextScope = ["canvas"],
        SkillIDs = [],
        Budget = new CloudAgentBudgetDto { MaxCredits = 100, MaxGenerationTasks = 1 },
    };

    private static List<string> ToolNames(List<Dictionary<string, JsonElement>> tools)
    {
        List<string> names = [];
        foreach (Dictionary<string, JsonElement> tool in tools)
        {
            if (tool["function"].TryGetProperty("name", out JsonElement name))
            {
                names.Add(name.GetString() ?? "");
            }
        }

        return names;
    }
}
