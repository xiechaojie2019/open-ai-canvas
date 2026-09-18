#nullable enable
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.Prompts;

/// <summary>
/// 提示词模板版本管理。对应 Go: <c>internal/prompts/prompt_template.go</c>。
/// </summary>
/// <remarks>
/// <para>
/// 模板是<b>版本化</b>的：每次创建产生新版本，启用中的版本不允许直接改内容或停用，
/// 必须新建版本再切换。这样历史生成结果永远能追溯到当时用的模板。
/// </para>
/// <para>
/// 本轮覆盖管理端 4 条路由（列表 / 创建 / 更新 / 删除）与默认模板种子。
/// 用户级定制（<c>UserPromptCustomization</c>）与模板渲染另行实现。
/// </para>
/// </remarks>
public sealed partial class PromptTemplateService
{
    /// <summary>占位符形态：<c>{{变量名}}</c>。对应 Go: <c>promptPlaceholderPattern</c>。</summary>
    [GeneratedRegex(@"\{\{[^{}]+\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>模板正文上限 30000 字符。对应 Go 的 <c>30_000</c>。</summary>
    private const int MaxContentRunes = 30_000;

    private readonly Repository _repository;

    public PromptTemplateService(Repository repository)
    {
        _repository = repository;
    }

    // ------------------------------------------------------------ 管理端

    /// <summary>模板列表与操作定义。对应 Go: <c>AdminPromptTemplates</c>。</summary>
    public async Task<(IReadOnlyList<PromptTemplate> Templates, List<PromptOperationDefinition> Definitions)>
        AdminPromptTemplatesAsync(User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        IReadOnlyList<PromptTemplate> templates = await _repository
            .PromptTemplatesAsync(cancellationToken).ConfigureAwait(false);

        return (templates, PromptDefaults.Definitions());
    }

    /// <summary>
    /// 创建新版本。对应 Go: <c>CreatePromptTemplate</c>。
    /// </summary>
    public async Task<PromptTemplate> CreatePromptTemplateAsync(
        User? actor, PromptTemplateRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        PromptOperationDefinition definition = FindDefinition(request.Operation.Trim())
            ?? throw AppError.BadAuthRequest("不支持的提示词模板类型");

        (string name, string content) = ValidateContent(definition, request.Name, request.Content);
        long version = await _repository
            .NextPromptTemplateVersionAsync(definition.Operation, cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        PromptTemplate template = new()
        {
            ID = IdGenerator.NewId(),
            Operation = definition.Operation,
            Name = name,
            Version = version,
            Content = content,
            OutputType = definition.OutputType,
            // Go 用指针语义：未提交视为不启用。
            Enabled = request.Enabled is true,
            CreatedBy = actor!.ID,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.SavePromptTemplateAsync(template, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor, "prompt_template.create", template.ID, "创建提示词模板版本",
            new { operation = template.Operation, version = template.Version, enabled = template.Enabled },
            cancellationToken).ConfigureAwait(false);

        return template;
    }

    /// <summary>
    /// 更新版本。对应 Go: <c>UpdatePromptTemplate</c>。
    /// </summary>
    /// <remarks>
    /// 启用中的版本有两条硬约束：不能改内容/名称（要新建版本），
    /// 也不能直接停用（要先启用同类型的其他版本）。
    /// </remarks>
    public async Task<PromptTemplate> UpdatePromptTemplateAsync(
        User? actor, string id, PromptTemplateRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        PromptTemplate template = await _repository.PromptTemplateAsync(id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("提示词模板不存在");

        PromptOperationDefinition definition = FindDefinition(template.Operation)
            ?? throw AppError.BadAuthRequest("提示词模板类型已经失效");

        bool contentChanged = request.Content.Trim() != template.Content.Trim();
        bool nameChanged = request.Name.Trim() != template.Name;
        if (template.Enabled && (contentChanged || nameChanged))
        {
            throw AppError.BadAuthRequest("启用中的版本不可直接修改，请基于它新建版本");
        }

        if (request.Enabled is false && template.Enabled)
        {
            throw AppError.BadAuthRequest("启用中的版本不能直接停用，请先启用同类型的其他版本");
        }

        (string name, string content) = ValidateContent(definition, request.Name, request.Content);

        template.Name = name;
        template.Content = content;
        if (request.Enabled is not null)
        {
            template.Enabled = request.Enabled.Value;
        }
        template.UpdatedAt = DateTime.UtcNow;

        await _repository.SavePromptTemplateAsync(template, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor!, "prompt_template.update", template.ID, "更新提示词模板版本",
            new { operation = template.Operation, version = template.Version, enabled = template.Enabled },
            cancellationToken).ConfigureAwait(false);

        return template;
    }

    /// <summary>
    /// 删除版本。对应 Go: <c>DeletePromptTemplate</c>。启用中的版本不允许删除。
    /// </summary>
    public async Task DeletePromptTemplateAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        PromptTemplate template = await _repository.PromptTemplateAsync(id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("提示词模板不存在");

        if (template.Enabled)
        {
            throw AppError.BadAuthRequest("启用中的版本不能删除，请先启用同类型的其他版本");
        }

        await _repository.DeletePromptTemplateAsync(id, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor!, "prompt_template.delete", template.ID, "删除提示词模板版本",
            new { operation = template.Operation, version = template.Version },
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 种子

    /// <summary>
    /// 为缺少模板的操作种入默认版本。对应 Go: <c>EnsureDefaultPromptTemplates</c>。
    /// </summary>
    /// <remarks>
    /// 幂等：已存在任何版本的操作用户不动。这是启动期调用，失败应中断启动
    /// （否则后续生成会因为找不到模板而报错）。
    /// </remarks>
    public async Task EnsureDefaultPromptTemplatesAsync(CancellationToken cancellationToken = default)
    {
        foreach (PromptOperationDefinition definition in PromptDefaults.Definitions())
        {
            long count = await _repository
                .PromptTemplateCountAsync(definition.Operation, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                await MigrateLegacyStoryboardVideoAsync(definition, cancellationToken).ConfigureAwait(false);
                continue;
            }

            DateTime now = DateTime.UtcNow;
            await _repository.SavePromptTemplateAsync(new PromptTemplate
            {
                ID = IdGenerator.NewId(),
                Operation = definition.Operation,
                Name = "默认" + definition.Label + "模板",
                Version = 1,
                Content = definition.DefaultContent,
                OutputType = definition.OutputType,
                Enabled = true,
                CreatedBy = "",
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 剥离分镜视频模板里历史遗留的引导语。
    /// 对应 Go 在 <c>EnsureDefaultPromptTemplates</c> 里的 <c>legacyStoryboardVideoPromptPreamble</c> 分支。
    /// </summary>
    /// <remarks>
    /// 只处理「系统种下的（<c>created_by</c> 为空）且仍以旧前缀开头」的启用版本，
    /// 避免动到运营手工编辑过的内容。
    /// </remarks>
    private async Task MigrateLegacyStoryboardVideoAsync(
        PromptOperationDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.Operation != "storyboard_video")
        {
            return;
        }

        PromptTemplate? active = await _repository
            .ActivePromptTemplateAsync(definition.Operation, cancellationToken).ConfigureAwait(false);
        if (active is null
            || active.CreatedBy.Length > 0
            || !active.Content.StartsWith(PromptDefaults.LegacyStoryboardVideoPromptPreamble, StringComparison.Ordinal))
        {
            return;
        }

        active.Content = active.Content[PromptDefaults.LegacyStoryboardVideoPromptPreamble.Length..];
        active.UpdatedAt = DateTime.UtcNow;
        await _repository.SavePromptTemplateAsync(active, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 内部

    private static PromptOperationDefinition? FindDefinition(string operation) =>
        PromptDefaults.Definitions().FirstOrDefault(definition =>
            string.Equals(definition.Operation, operation, StringComparison.Ordinal));

    /// <summary>
    /// 校验并归一化模板内容。对应 Go: <c>validatePromptTemplateContent</c>。
    /// </summary>
    private static (string Name, string Content) ValidateContent(
        PromptOperationDefinition definition, string name, string content)
    {
        name = (name ?? "").Trim();
        content = (content ?? "").Trim();

        if (name.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写版本名称");
        }
        if (content.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写提示词模板");
        }
        if (content.EnumerateRunes().Count() > MaxContentRunes)
        {
            throw AppError.BadAuthRequest("提示词模板最多 30000 个字符");
        }

        ValidatePlaceholders(definition, content);
        return (name, content);
    }

    /// <summary>
    /// 模板里的每个 <c>{{变量}}</c> 都必须是该操作声明过的变量。
    /// 对应 Go: <c>validatePromptPlaceholders</c>。
    /// </summary>
    /// <remarks>
    /// 未知变量会在渲染时被原样留下（或渲染成空），等于静默产生错误的提示词，
    /// 所以必须在保存时就拒绝。报错时把未知变量排序去重后一并列出，方便定位。
    /// </remarks>
    private static void ValidatePlaceholders(PromptOperationDefinition definition, string content)
    {
        HashSet<string> allowed = definition.Variables
            .Select(variable => variable.Placeholder)
            .ToHashSet(StringComparer.Ordinal);

        List<string> unknown = PlaceholderPattern()
            .Matches(content)
            .Select(match => match.Value)
            .Where(placeholder => !allowed.Contains(placeholder))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        throw AppError.BadAuthRequest("模板包含不支持的变量：" + string.Join("、", unknown));
    }

    private async Task AppendAuditAsync(
        User actor, string action, string targetId, string summary,
        object metadata, CancellationToken cancellationToken)
    {
        await _repository.AppendAdminAuditAsync(new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = action,
            TargetType = "prompt_template",
            TargetID = targetId,
            Summary = summary,
            MetadataJSON = JsonSerializer.Serialize(metadata),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }
}
