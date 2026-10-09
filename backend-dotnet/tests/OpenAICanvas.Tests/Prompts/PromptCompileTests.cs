#nullable enable
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenAICanvas.Application;
using OpenAICanvas.Application.Prompts;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using Xunit;

namespace OpenAICanvas.Tests.Prompts;

/// <summary>
/// 提示词编译链路（模板 → 用户定制 → 变量渲染 → 受保护上下文）。
/// 对应 Go: <c>prompts.Service.CompilePrompt</c>。
/// </summary>
/// <remarks>
/// 为什么要单独测编译：这是「模板」唯一真正生效的地方。少了它，运营改的模板和用户存的偏好
/// 都只是数据库里的死数据 —— 模型只会收到节点提示词框里的那句话，并把它当普通对话回答，
/// 前端于是报出很难定位的「分镜任务没有返回镜头行」。
/// </remarks>
public sealed class PromptCompileTests : IDisposable
{
    private readonly string _dataDir;
    private readonly WebApplicationFactory<Program> _factory;

    public PromptCompileTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"canvas-prompt-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CANVAS_BACKEND_DATA_DIR", _dataDir);
            builder.UseSetting("CANVAS_DATABASE_DRIVER", "sqlite");
            builder.UseSetting("CANVAS_AUTO_MIGRATE", "true");
        });

        // 触发一次宿主构建，确保迁移与模板种子已跑完。
        _ = _factory.Services;
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataDir))
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_dataDir, recursive: true);
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>默认编译：启用模板正文被渲染，受保护上下文（剧情 + JSON Schema）追加在后。</summary>
    [Fact]
    public async Task 默认编译渲染模板并追加受保护上下文()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CanvasService canvas = scope.ServiceProvider.GetRequiredService<CanvasService>();

        CompiledPrompt compiled = await canvas.PromptTemplates.CompilePromptAsync(
            "user-1", "storyboard_plan", new Dictionary<string, string>
            {
                ["项目名称"] = "夜灯",
                ["项目画风"] = "水墨",
                ["用户要求"] = "共 6 秒",
                ["剧情"] = "小猫在溪边抓鱼",
            });

        // 模板正文（默认分镜模板首句）已被渲染并放在最前。
        Assert.StartsWith("你是影视分镜导演", compiled.Content, StringComparison.Ordinal);
        // 模板变量全部替换，不能把 {{...}} 原样发给模型。
        Assert.DoesNotContain("{{", compiled.Content, StringComparison.Ordinal);
        // 受保护上下文：剧情、执行契约、服务端 JSON Schema 三者缺一不可。
        Assert.Contains("小猫在溪边抓鱼", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("【受保护执行契约】", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("storyboard-plan/v3", compiled.Content, StringComparison.Ordinal);
        // 种子版本的追溯信息。
        Assert.Equal(1, compiled.TemplateVersion);
        Assert.NotEmpty(compiled.TemplateID);
    }

    /// <summary>
    /// 用户 rewrite 定制只替换创意部分，<b>不能</b>替换受保护上下文。
    /// 否则用户一句「只写一句话」就会把 JSON 契约挤掉，分镜必然校验失败。
    /// </summary>
    [Fact]
    public async Task 用户重写定制不能挤掉受保护上下文()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CanvasService canvas = scope.ServiceProvider.GetRequiredService<CanvasService>();

        await canvas.PromptTemplates.UpdateUserPromptCustomizationAsync(
            User("user-rewrite"), "storyboard_plan",
            new UserPromptCustomizationRequest { Mode = "rewrite", Content = "只用一句白话描述画面。" });

        CompiledPrompt compiled = await canvas.PromptTemplates.CompilePromptAsync(
            "user-rewrite", "storyboard_plan", new Dictionary<string, string>
            {
                ["剧情"] = "小猫在溪边抓鱼",
            });

        Assert.StartsWith("只用一句白话描述画面。", compiled.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("你是影视分镜导演", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("【受保护执行契约】", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("storyboard-plan/v3", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("小猫在溪边抓鱼", compiled.Content, StringComparison.Ordinal);
        Assert.NotEmpty(compiled.CustomizationID);
    }

    /// <summary>append 定制接在模板正文之后。对应 Go 的 <c>【用户个性化创作要求】</c> 段。</summary>
    [Fact]
    public async Task 用户追加定制接在模板之后()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CanvasService canvas = scope.ServiceProvider.GetRequiredService<CanvasService>();

        await canvas.PromptTemplates.UpdateUserPromptCustomizationAsync(
            User("user-append"), "short_drama_outline",
            new UserPromptCustomizationRequest { Mode = "append", Content = "每章结尾留一个钩子。" });

        CompiledPrompt compiled = await canvas.PromptTemplates.CompilePromptAsync(
            "user-append", "short_drama_outline", new Dictionary<string, string>
            {
                ["章节数量"] = "3",
                ["用户故事"] = "租客发现房东不是人",
            });

        Assert.Contains("你是短剧编剧", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("【用户个性化创作要求】", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("每章结尾留一个钩子。", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("3 个章节", compiled.Content, StringComparison.Ordinal);
        Assert.Contains("short-drama-outline/v1", compiled.Content, StringComparison.Ordinal);
    }

    /// <summary>不支持的操作必须报可读错误（对应 Go 的 <c>不支持的提示词模板类型：%s</c>）。</summary>
    [Fact]
    public async Task 未知操作报错()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CanvasService canvas = scope.ServiceProvider.GetRequiredService<CanvasService>();

        AppError error = await Assert.ThrowsAsync<AppError>(() =>
            canvas.PromptTemplates.CompilePromptAsync(
                "user-1", "not-an-operation", new Dictionary<string, string>()));

        Assert.Contains("不支持的提示词模板类型：not-an-operation", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 没有启用版本时回落到内置默认正文 —— 编译不会因为运维没配模板而失败。
    /// </summary>
    [Fact]
    public async Task 无启用版本时回落内置默认模板()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CanvasService canvas = scope.ServiceProvider.GetRequiredService<CanvasService>();
        Repository repository = scope.ServiceProvider.GetRequiredService<Repository>();

        // 直接删掉种子行（绕过「启用中不可删除」的业务约束），模拟「库里一条也没有」。
        PromptTemplate? seeded = await repository.ActivePromptTemplateAsync("skill_draft");
        Assert.NotNull(seeded);
        await repository.DeletePromptTemplateAsync(seeded!.ID);

        CompiledPrompt compiled = await canvas.PromptTemplates.CompilePromptAsync(
            "user-1", "skill_draft", new Dictionary<string, string> { ["用户想法"] = "做一个电商主图技能" });

        Assert.Contains("你是一位技能编写助手", compiled.Content, StringComparison.Ordinal);
        Assert.Equal("", compiled.TemplateID);
        Assert.Equal(1, compiled.TemplateVersion);
        Assert.Contains("skill-draft/v1", compiled.Content, StringComparison.Ordinal);
    }

    private static User User(string id) => new()
    {
        ID = id,
        Username = id,
        Email = $"{id}@example.com",
        DisplayName = id,
        Role = UserRole.UserRoleUser,
        Status = UserStatus.UserStatusActive,
    };
}
