using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>绘图工具设置值。对应 Go: <c>app.DrawingEngineSetting</c>。</summary>
public sealed class DrawingEngineSetting
{
    [JsonPropertyName("defaultEngine")]
    public string DefaultEngine { get; set; } = DrawingEngineSettingValue.Excalidraw;

    [JsonPropertyName("tldrawLicenseKey")]
    public string TldrawLicenseKey { get; set; } = string.Empty;
}

/// <summary>绘图工具标识。对应 Go: <c>DrawingEngineTldraw</c> / <c>DrawingEngineExcalidraw</c>。</summary>
public static class DrawingEngineSettingValue
{
    public const string Tldraw = "tldraw";
    public const string Excalidraw = "excalidraw";
}

/// <summary>
/// 公开绘图工具设置。对应 Go: <c>app.PublicDrawingEngineSetting</c>。
/// </summary>
/// <remarks>
/// Go 用结构体嵌入 <see cref="DrawingEngineSetting"/>，JSON 平铺到同一层；
/// 这里按声明顺序显式展开。
/// </remarks>
public sealed class PublicDrawingEngineSetting
{
    [JsonPropertyName("defaultEngine")]
    public string DefaultEngine { get; init; } = DrawingEngineSettingValue.Excalidraw;

    [JsonPropertyName("tldrawLicenseKey")]
    public string TldrawLicenseKey { get; init; } = string.Empty;

    /// <summary>是否已由运维显式配置过。</summary>
    [JsonPropertyName("configured")]
    public bool Configured { get; init; }

    [JsonPropertyName("updatedBy")]
    [GoOmitEmpty]
    public string UpdatedBy { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>
/// 绘图工具设置的读写。对应 Go: <c>internal/app/drawing_engine.go</c>。
/// </summary>
public sealed class DrawingEngineService
{
    private const string SettingKey = "drawing_engine";
    private const string LicenseKeySettingKey = "tldraw_license_key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Repository _repository;

    public DrawingEngineService(Repository repository) => _repository = repository;

    /// <summary>新部署默认使用开源编辑器。对应 Go: <c>defaultDrawingEngineSetting</c>。</summary>
    public static DrawingEngineSetting Default() => new();

    /// <summary>读取设置。对应 Go: <c>DrawingEngineSetting()</c>。</summary>
    public async Task<PublicDrawingEngineSetting> GetAsync(CancellationToken cancellationToken = default)
    {
        (SystemSetting? setting, DrawingEngineSetting value) =
            await ReadAsync(cancellationToken).ConfigureAwait(false);

        return new PublicDrawingEngineSetting
        {
            DefaultEngine = value.DefaultEngine,
            TldrawLicenseKey = value.TldrawLicenseKey,
            Configured = setting is not null,
            UpdatedBy = setting?.UpdatedBy ?? string.Empty,
            CreatedAt = setting is null || setting.CreatedAt == default ? null : setting.CreatedAt,
            UpdatedAt = setting is null || setting.UpdatedAt == default ? null : setting.UpdatedAt,
        };
    }

    /// <summary>校验设置值。对应 Go: <c>validateDrawingEngineSetting</c>。</summary>
    public static void Validate(DrawingEngineSetting value)
    {
        if (value.DefaultEngine is not (DrawingEngineSettingValue.Tldraw or DrawingEngineSettingValue.Excalidraw))
        {
            throw AppError.BadAuthRequest("默认绘图工具必须是 tldraw 或 Excalidraw");
        }
    }

    /// <summary>写入设置。对应 Go: <c>UpdateDrawingEngineSetting</c>（审计写入另行接入）。</summary>
    public async Task<PublicDrawingEngineSetting> UpdateAsync(
        DrawingEngineSetting value,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        Validate(value);
        value.TldrawLicenseKey = value.TldrawLicenseKey.Trim();

        (SystemSetting? current, _) = await ReadAsync(cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;

        // 引擎与 License Key 分开存两个设置键，与 Go 一致。
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = SettingKey,
            ValueJSON = JsonSerializer.Serialize(new { defaultEngine = value.DefaultEngine }),
            UpdatedBy = actorUserId,
            CreatedAt = current?.CreatedAt ?? default,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        (SystemSetting? licenseCurrent, _) = await ReadLicenseKeyAsync(cancellationToken).ConfigureAwait(false);
        await _repository.SaveSystemSettingAsync(new SystemSetting
        {
            Key = LicenseKeySettingKey,
            ValueJSON = JsonSerializer.Serialize(value.TldrawLicenseKey),
            UpdatedBy = actorUserId,
            CreatedAt = licenseCurrent?.CreatedAt ?? default,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        return await GetAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SystemSetting? Setting, DrawingEngineSetting Value)> ReadAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(SettingKey, cancellationToken)
            .ConfigureAwait(false);

        DrawingEngineSetting value = Default();
        if (setting is not null)
        {
            if (string.IsNullOrWhiteSpace(setting.ValueJSON))
            {
                throw new InvalidOperationException("绘图工具配置格式无效");
            }

            try
            {
                DrawingEngineSetting? parsed = JsonSerializer.Deserialize<DrawingEngineSetting>(
                    setting.ValueJSON, JsonOptions);
                if (parsed is null)
                {
                    throw new InvalidOperationException("绘图工具配置格式无效");
                }

                value = parsed;
                Validate(value);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("绘图工具配置格式无效");
            }
        }

        (_, string licenseKey) = await ReadLicenseKeyAsync(cancellationToken).ConfigureAwait(false);
        value.TldrawLicenseKey = licenseKey;
        return (setting, value);
    }

    private async Task<(SystemSetting? Setting, string Value)> ReadLicenseKeyAsync(
        CancellationToken cancellationToken)
    {
        SystemSetting? setting = await _repository.SystemSettingAsync(LicenseKeySettingKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return (null, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(setting.ValueJSON))
        {
            throw new InvalidOperationException("tldraw License Key 配置格式无效");
        }

        try
        {
            string? value = JsonSerializer.Deserialize<string>(setting.ValueJSON, JsonOptions);
            return value is null
                ? throw new InvalidOperationException("tldraw License Key 配置格式无效")
                : (setting, value.Trim());
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("tldraw License Key 配置格式无效");
        }
    }
}
