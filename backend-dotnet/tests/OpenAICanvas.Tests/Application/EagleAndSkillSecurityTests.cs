#nullable enable

using OpenAICanvas.Application;
using OpenAICanvas.Domain.Kernel;
using Xunit;

namespace OpenAICanvas.Tests.Application;

public sealed class EagleAndSkillSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:41594")]
    [InlineData("https://127.0.0.1:41595")]
    [InlineData("http://192.168.1.2:41595")]
    [InlineData("http://127.0.0.1:41595/api")]
    [InlineData("http://user:pass@127.0.0.1:41595")]
    public void Eagle地址拒绝非固定本机端点(string? value)
    {
        if (value is null)
        {
            Assert.Equal("http://127.0.0.1:41595/", EagleService.ValidateBaseURL(value).ToString());
            return;
        }

        Assert.Throws<AppError>(() => EagleService.ValidateBaseURL(value));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "owner", "repo", "", "")]
    [InlineData("https://github.com/owner/repo/tree/main/skills/demo", "owner", "repo", "main", "skills/demo")]
    public void GitHub地址只解析仓库或tree子目录(
        string value, string owner, string repo, string branch, string subdir)
    {
        GitHubSkillSpecAssert(value, owner, repo, branch, subdir);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://api.github.com/repos/owner/repo")]
    [InlineData("https://github.com/owner/repo/blob/main/SKILL.md")]
    [InlineData("https://github.com/owner/repo?x=1")]
    public void GitHub地址拒绝非允许形式(string value)
    {
        Assert.Throws<AppError>(() => SkillsService.ParseGitHubSkillURL(value, "", ""));
    }

    private static void GitHubSkillSpecAssert(
        string value, string owner, string repo, string branch, string subdir)
    {
        SkillsService.GitHubSkillSpec spec = SkillsService.ParseGitHubSkillURL(value, "", "");
        Assert.Equal(owner, spec.Owner);
        Assert.Equal(repo, spec.Repo);
        Assert.Equal(branch, spec.Ref);
        Assert.Equal(subdir, spec.Subdir);
    }
}
