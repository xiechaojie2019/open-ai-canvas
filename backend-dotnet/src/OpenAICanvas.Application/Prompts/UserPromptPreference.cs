#nullable enable
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.Prompts;

/// <summary>用户提示词定制请求。对应 Go: <c>prompts.UserPromptCustomizationRequest</c>。</summary>
public sealed class UserPromptCustomizationRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>用户提示词偏好。对应 Go: <c>prompts.UserPromptPreference</c>。</summary>
/// <remarks><c>template</c> 在 Go 无 omitempty：无启用版本时输出 <c>null</c>。</remarks>
public sealed class UserPromptPreferenceDto
{
    [JsonPropertyName("definition")]
    public PromptOperationDefinition Definition { get; set; } = new();

    [JsonPropertyName("template")]
    public PromptTemplate? Template { get; set; }

    [JsonPropertyName("customization")]
    [GoOmitEmpty]
    public UserPromptCustomization? Customization { get; set; }

    [JsonPropertyName("outdated")]
    public bool Outdated { get; set; }
}

/// <summary>
/// 用户级提示词定制。对应 Go: <c>internal/prompts/prompt_template.go</c> 的偏好部分。
/// </summary>
public sealed partial class PromptTemplateService
{
    private const string CustomizationInherit = "inherit";
    private const string CustomizationAppend = "append";
    private const string CustomizationRewrite = "rewrite";
    private const int MaxCustomizationRunes = 12_000;

    /// <summary>用户偏好列表。对应 Go: <c>UserPromptPreferences</c>。</summary>
    public async Task<List<UserPromptPreferenceDto>> UserPromptPreferencesAsync(
        User user, CancellationToken cancellationToken = default)
    {
        if (user is null || user.ID.Length == 0)
        {
            throw AppError.BadAuthRequest("请先登录");
        }
        IReadOnlyList<UserPromptCustomization> customizations = await _repository
            .UserPromptCustomizationsAsync(user.ID, cancellationToken).ConfigureAwait(false);
        Dictionary<string, UserPromptCustomization> byOperation =
            new(StringComparer.Ordinal);
        foreach (UserPromptCustomization customization in customizations)
        {
            byOperation[customization.Operation] = customization;
        }
        List<UserPromptPreferenceDto> preferences = [];
        foreach (PromptOperationDefinition definition in PromptDefaults.Definitions())
        {
            PromptTemplate? template = await _repository
                .ActivePromptTemplateAsync(definition.Operation, cancellationToken).ConfigureAwait(false);
            byOperation.TryGetValue(definition.Operation, out UserPromptCustomization? customization);
            preferences.Add(new UserPromptPreferenceDto
            {
                Definition = definition,
                Template = template,
                Customization = customization,
                Outdated = customization is not null
                    && customization.Mode == CustomizationRewrite
                    && template is not null
                    && customization.BaseTemplateID != template.ID,
            });
        }
        return preferences;
    }

    /// <summary>更新用户定制。对应 Go: <c>UpdateUserPromptCustomization</c>。</summary>
    public async Task<UserPromptCustomization> UpdateUserPromptCustomizationAsync(
        User user,
        string operation,
        UserPromptCustomizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (user is null || user.ID.Length == 0)
        {
            throw AppError.BadAuthRequest("请先登录");
        }
        PromptOperationDefinition? definition = FindDefinition(operation.Trim());
        if (definition is null)
        {
            throw AppError.BadAuthRequest("不支持的提示词模板类型");
        }
        string mode = request.Mode.Trim();
        if (mode is not (CustomizationInherit or CustomizationAppend or CustomizationRewrite))
        {
            throw AppError.BadAuthRequest("不支持的提示词定制方式");
        }
        string content = request.Content.Trim();
        if (mode == CustomizationInherit)
        {
            content = "";
        }
        if (mode != CustomizationInherit && content.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写个人提示词要求");
        }
        if (content.EnumerateRunes().Count() > MaxCustomizationRunes)
        {
            throw AppError.BadAuthRequest($"个人提示词最多 {MaxCustomizationRunes} 个字符");
        }
        ValidatePlaceholders(definition, content);
        PromptTemplate? active = await _repository
            .ActivePromptTemplateAsync(definition.Operation, cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            throw new InvalidOperationException("当前模板类型没有启用版本");
        }
        UserPromptCustomization? customization = await _repository
            .UserPromptCustomizationAsync(user.ID, definition.Operation, cancellationToken).ConfigureAwait(false);
        if (customization is null)
        {
            customization = new UserPromptCustomization
            {
                ID = IdGenerator.NewId(),
                UserID = user.ID,
                Operation = definition.Operation,
            };
        }
        customization.Mode = mode;
        customization.Content = content;
        customization.BaseTemplateID = active.ID;
        customization.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveUserPromptCustomizationAsync(customization, cancellationToken).ConfigureAwait(false);
        return customization;
    }

    /// <summary>重置用户定制。对应 Go: <c>ResetUserPromptCustomization</c>。</summary>
    public async Task ResetUserPromptCustomizationAsync(
        User user, string operation, CancellationToken cancellationToken = default)
    {
        if (user is null || user.ID.Length == 0)
        {
            throw AppError.BadAuthRequest("请先登录");
        }
        if (FindDefinition(operation.Trim()) is null)
        {
            throw AppError.BadAuthRequest("不支持的提示词模板类型");
        }
        await _repository.DeleteUserPromptCustomizationAsync(
            user.ID, operation.Trim(), cancellationToken).ConfigureAwait(false);
    }
}
