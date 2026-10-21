#nullable enable
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Protocol;
using OpenAICanvas.Providers;

namespace OpenAICanvas.Application;

/// <summary>
/// 方舟私域素材库：把参考图转成 <c>asset://&lt;assetId&gt;</c> 后再提交视频任务。
/// 对应 Go: <c>internal/app/provider_ark_private_assets.go</c>。
/// </summary>
/// <remarks>
/// 只有 Seedance 视频协议接受可信素材 ID；其他视频模型（尤其 OpenAI 风格）仍使用原 URL/data URL。
/// 控制面请求使用管理员配置的 IAM AK/SK 做 V4 签名，不进入模型调用的审计与计费上下文。
/// </remarks>
public sealed class ArkPrivateAssetService
{
    private const string ApiVersion = "2024-01-01";
    private const string GroupType = "AIGC";
    private const string StatusCreating = "creating";
    private const string StatusProcessing = "processing";
    private const string StatusActive = "active";
    private const string StatusFailed = "failed";

    private readonly Repository _repository;
    private readonly PlatformSettingsService _settings;
    private readonly Func<Resource, DateTime, CancellationToken, Task<string>> _resourceUrl;
    private readonly HttpClient _http;
    private readonly string? _controlPlaneBaseUrl;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;

    public ArkPrivateAssetService(
        Repository repository,
        PlatformSettingsService settings,
        Func<Resource, DateTime, CancellationToken, Task<string>> resourceUrl,
        HttpClient? httpClient = null,
        string? controlPlaneBaseUrl = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null)
    {
        _repository = repository;
        _settings = settings;
        _resourceUrl = resourceUrl;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _controlPlaneBaseUrl = controlPlaneBaseUrl;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollTimeout = pollTimeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// 视频任务提交前改写 Seedance 参考图。必须在资源水合之后调用，
    /// 否则水合生成的签名 URL 会覆盖这里的 <c>asset://</c>。
    /// </summary>
    public async Task PrepareVideoReferencesAsync(
        string userId, TextTaskInput input, CancellationToken cancellationToken = default)
    {
        if (input is null
            || !string.Equals(input.Mode, "video", StringComparison.Ordinal)
            || !IsArkPrivateAssetVideoConfig(input.Config)
            || !ProviderVideoOptions.ParseBool(input.Config.ArkPrivateAssetUpload, true))
        {
            return;
        }

        bool hasOwnedReference = input.ReferenceImages.Any(
            reference => ArkPrivateAssetResourceId(reference).Length > 0);
        if (!hasOwnedReference)
        {
            return;
        }

        ArkPrivateAssetConnection setting = await _settings
            .ReadArkPrivateAssetConnectionAsync(cancellationToken).ConfigureAwait(false);
        // 未启用是明确的可选增强关闭：保留原始参考图，继续常规视频请求。
        if (!setting.Enabled)
        {
            return;
        }
        EnsureCredentials(setting);

        foreach (ProviderMedia reference in input.ReferenceImages)
        {
            if ((reference.URL ?? "").Trim().StartsWith("asset://", StringComparison.Ordinal))
            {
                continue;
            }
            string resourceId = ArkPrivateAssetResourceId(reference);
            if (resourceId.Length == 0)
            {
                continue;
            }

            Resource? resource = await _repository
                .ResourceForUserAsync(userId, resourceId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                throw AppError.BadAuthRequest("读取方舟参考素材失败：资源不存在或无权访问");
            }
            if (resource.Status != ResourceStatus.ResourceStatusReady)
            {
                throw AppError.BadAuthRequest("方舟参考素材尚未上传完成");
            }
            if (!resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw AppError.BadAuthRequest("方舟可信素材库当前只支持上传图片参考素材");
            }

            string assetId = await EnsureAssetAsync(userId, resource, setting, cancellationToken)
                .ConfigureAwait(false);
            reference.URL = "asset://" + assetId;
            reference.DataURL = "";
        }
    }

    /// <summary>用户显式触发的参考图预同步入口。对应 Go: <c>SyncResourceToArkPrivateAsset</c>。</summary>
    public async Task<ArkPrivateAssetSyncResult> SyncResourceAsync(
        string userId, string resourceId, CancellationToken cancellationToken = default)
    {
        resourceId = (resourceId ?? "").Trim();
        if (resourceId.Length == 0)
        {
            throw AppError.BadAuthRequest("请选择要同步的图片素材");
        }
        Resource? resource = await _repository
            .ResourceForUserAsync(userId, resourceId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            throw AppError.NotFound("图片素材不存在或无权访问");
        }
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("图片素材尚未上传完成");
        }
        if (!resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw AppError.BadAuthRequest("方舟可信素材库当前只支持上传图片素材");
        }

        ArkPrivateAssetConnection setting = await _settings
            .ReadArkPrivateAssetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!setting.Enabled)
        {
            throw AppError.BadAuthRequest(
                "方舟可信素材库尚未配置，请由管理员在方舟素材库设置中填写启用的 IAM AK/SK");
        }
        EnsureCredentials(setting);

        await EnsureAssetAsync(userId, resource, setting, cancellationToken).ConfigureAwait(false);
        return new ArkPrivateAssetSyncResult(resource.ID, StatusActive);
    }

    /// <summary>对应 Go: <c>isArkPrivateAssetVideoConfig</c>（图片协议绝不进入素材同步）。</summary>
    public static bool IsArkPrivateAssetVideoConfig(ProviderConfig config)
    {
        string interfaceType = (config.InterfaceType ?? "").Trim();
        if (interfaceType is "volcengine-ark-image" or "volcengine-ark-agent-plan-image")
        {
            return false;
        }
        string model = (config.Model ?? "").Trim().ToLowerInvariant();
        // 只有 Seedance 接受可信素材 ID。Agent Plan 的图片/其他视频模型共用 /api/plan/v3，
        // 不能仅凭基址把它们纳入上传，否则非 Seedance 任务会拿到 asset:// 并被上游拒绝。
        if (!model.Contains("seedance", StringComparison.Ordinal))
        {
            return false;
        }
        return interfaceType is "volcengine-ark-video" or "volcengine-ark-agent-plan-video";
    }

    /// <summary>
    /// 是否需要在提交视频任务前准备方舟可信素材。只匹配 Seedance 视频与受管图片资源，
    /// 其他模型、非视频任务或用户显式关闭上传时保持原行为。
    /// </summary>
    public static bool RequiresPreparation(TextTaskInput input)
    {
        if (input?.Config is null
            || !string.Equals(input.Mode, "video", StringComparison.Ordinal)
            || !IsArkPrivateAssetVideoConfig(input.Config)
            || !ProviderVideoOptions.ParseBool(input.Config.ArkPrivateAssetUpload, true))
        {
            return false;
        }
        return input.ReferenceImages.Any(reference => ArkPrivateAssetResourceId(reference).Length > 0);
    }

    private static string ArkPrivateAssetResourceId(ProviderMedia reference)
    {
        string key = (reference.StorageKey ?? "").Trim();
        return key.StartsWith("resource:", StringComparison.Ordinal)
            ? key["resource:".Length..].Trim()
            : "";
    }

    private static void EnsureCredentials(ArkPrivateAssetConnection setting)
    {
        if (setting.AccessKeyID.Trim().Length == 0 || setting.AccessKeySecret.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest(
                "方舟可信素材库尚未配置，请由管理员在方舟素材库设置中填写启用的 IAM AK/SK");
        }
    }

    private async Task<string> EnsureAssetAsync(
        string userId, Resource resource, ArkPrivateAssetConnection setting,
        CancellationToken cancellationToken)
    {
        ArkPrivateAssetBinding? binding = await _repository
            .ArkPrivateAssetBindingAsync(resource.ID, setting.ProjectName, cancellationToken).ConfigureAwait(false);
        if (binding is not null)
        {
            if (ShouldResumePolling(binding))
            {
                binding.Status = StatusProcessing;
                binding.Error = "";
                await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken).ConfigureAwait(false);
                return await WaitForAssetAsync(binding, setting, cancellationToken).ConfigureAwait(false);
            }
            if (!ShouldRetry(binding))
            {
                return await WaitForAssetAsync(binding, setting, cancellationToken).ConfigureAwait(false);
            }
            binding.Status = StatusCreating;
            binding.Error = "";
            await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            binding = new ArkPrivateAssetBinding
            {
                ID = IdGenerator.NewId(),
                UserID = userId,
                ResourceID = resource.ID,
                ProjectName = setting.ProjectName,
                Status = StatusCreating,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            bool inserted = await _repository
                .CreateArkPrivateAssetBindingAsync(binding, cancellationToken).ConfigureAwait(false);
            if (!inserted)
            {
                // 并发任务抢先建绑：复用对方记录，绝不重复上传同一张图。
                binding = await _repository
                    .ArkPrivateAssetBindingAsync(resource.ID, setting.ProjectName, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("读取方舟可信素材绑定失败");
                return await WaitForAssetAsync(binding, setting, cancellationToken).ConfigureAwait(false);
            }
        }

        string groupId = (binding.AssetGroupID ?? "").Trim();
        if (groupId.Length == 0)
        {
            try
            {
                groupId = await EnsureGroupAsync(setting, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw await FailAsync(binding, error, cancellationToken).ConfigureAwait(false);
            }
        }
        binding.AssetGroupID = groupId;

        string resourceUrl;
        try
        {
            resourceUrl = await _resourceUrl(resource, DateTime.UtcNow.AddHours(1), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw await FailAsync(
                binding, new InvalidOperationException("生成方舟素材临时地址失败：" + error.Message, error),
                cancellationToken).ConfigureAwait(false);
        }

        JsonElement response;
        try
        {
            response = await CallAsync(setting, "CreateAsset", new Dictionary<string, object?>
            {
                ["GroupId"] = groupId,
                ["URL"] = resourceUrl,
                ["AssetType"] = "Image",
                ["Name"] = "ark-private-asset-" + resource.ID,
                ["ProjectName"] = setting.ProjectName,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw await FailAsync(
                binding, new InvalidOperationException("上传方舟可信素材失败：" + error.Message, error),
                cancellationToken).ConfigureAwait(false);
        }

        string assetId = ResponseField(response, "AssetId", "AssetID", "asset_id", "Id", "ID", "id");
        if (assetId.Length == 0)
        {
            throw await FailAsync(
                binding, new InvalidOperationException("方舟素材库没有返回素材 ID"),
                cancellationToken).ConfigureAwait(false);
        }

        binding.ArkAssetID = assetId;
        binding.Status = StatusProcessing;
        binding.Error = "";
        await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        return await WaitForAssetAsync(binding, setting, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> EnsureGroupAsync(
        ArkPrivateAssetConnection setting, CancellationToken cancellationToken)
    {
        if (setting.DefaultGroupID.Trim().Length > 0)
        {
            return setting.DefaultGroupID.Trim();
        }
        JsonElement response;
        try
        {
            response = await CallAsync(setting, "CreateAssetGroup", new Dictionary<string, object?>
            {
                ["Name"] = "ark-private-assets",
                ["Description"] = "自动导入的方舟可信素材",
                ["GroupType"] = GroupType,
                ["ProjectName"] = setting.ProjectName,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException("创建方舟素材组失败：" + error.Message, error);
        }
        string groupId = ResponseField(response, "GroupId", "GroupID", "group_id", "Id", "ID", "id");
        if (groupId.Length == 0)
        {
            throw new InvalidOperationException("方舟素材库没有返回素材组 ID");
        }
        setting.DefaultGroupID = groupId;
        await _settings.SaveArkPrivateAssetDefaultGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        return groupId;
    }

    private async Task<string> WaitForAssetAsync(
        ArkPrivateAssetBinding binding, ArkPrivateAssetConnection setting,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + _pollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            string status = (binding.Status ?? "").Trim().ToLowerInvariant();
            if (status == StatusActive)
            {
                return binding.ArkAssetID.Trim().Length > 0
                    ? binding.ArkAssetID
                    : throw new InvalidOperationException("方舟可信素材记录缺少素材 ID");
            }
            if (status == StatusFailed)
            {
                string reason = string.IsNullOrWhiteSpace(binding.Error)
                    ? "请更换拥有使用权的虚拟人物素材后重试"
                    : binding.Error;
                throw new InvalidOperationException("方舟可信素材审核失败：" + reason);
            }

            if (binding.ArkAssetID.Trim().Length > 0)
            {
                JsonElement response;
                try
                {
                    response = await CallAsync(setting, "GetAsset", new Dictionary<string, object?>
                    {
                        ["Id"] = binding.ArkAssetID,
                        ["ProjectName"] = setting.ProjectName,
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "查询方舟可信素材审核状态失败：" + error.Message, error);
                }

                string upstreamStatus = ResponseField(response, "Status", "status").ToLowerInvariant();
                switch (upstreamStatus)
                {
                    case StatusActive:
                        binding.Status = StatusActive;
                        binding.Error = "";
                        await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken)
                            .ConfigureAwait(false);
                        return binding.ArkAssetID;
                    case StatusFailed:
                        string message = ResponseField(response, "ErrorMessage", "Message", "message");
                        binding.Status = StatusFailed;
                        binding.Error = message.Length > 0 ? message : "方舟未通过素材审核";
                        await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken)
                            .ConfigureAwait(false);
                        throw new InvalidOperationException("方舟可信素材审核失败：" + binding.Error);
                    case "":
                        throw new InvalidOperationException("方舟可信素材状态缺失");
                }
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            binding = await _repository
                .ArkPrivateAssetBindingAsync(binding.ResourceID, binding.ProjectName, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("方舟可信素材记录不存在");
        }
        throw new InvalidOperationException("方舟可信素材审核超时，请稍后重新提交视频任务");
    }

    /// <summary>素材组尚未建成时任何失败都可重试；素材已建后只有创建阶段的失败可重试。</summary>
    private static bool ShouldRetry(ArkPrivateAssetBinding binding)
    {
        if (binding is null
            || !string.Equals(binding.Status, StatusFailed, StringComparison.OrdinalIgnoreCase)
            || binding.ArkAssetID.Trim().Length > 0)
        {
            return false;
        }
        if (binding.AssetGroupID.Trim().Length == 0)
        {
            return true;
        }
        return binding.Error.Contains("上传方舟可信素材失败", StringComparison.Ordinal)
            || binding.Error.Contains("方舟素材库没有返回素材 ID", StringComparison.Ordinal);
    }

    /// <summary>旧版 GetAsset 参数错误导致轮询失败时恢复轮询，不重新上传素材。</summary>
    private static bool ShouldResumePolling(ArkPrivateAssetBinding binding) =>
        string.Equals(binding.Status, StatusFailed, StringComparison.OrdinalIgnoreCase)
        && binding.AssetGroupID.Trim().Length > 0
        && binding.ArkAssetID.Trim().Length > 0
        && binding.Error.Contains("MissingParameter.Id", StringComparison.Ordinal);

    private async Task<Exception> FailAsync(
        ArkPrivateAssetBinding binding, Exception reason, CancellationToken cancellationToken)
    {
        binding.Status = StatusFailed;
        binding.Error = reason.Message;
        await _repository.SaveArkPrivateAssetBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        return reason;
    }

    private string ControlPlaneBaseUrl(string region)
    {
        if (!string.IsNullOrWhiteSpace(_controlPlaneBaseUrl))
        {
            return _controlPlaneBaseUrl!;
        }
        region = (region ?? "").Trim().ToLowerInvariant();
        if (region.Length == 0)
        {
            throw new InvalidOperationException("方舟素材库未配置 Region");
        }
        foreach (char character in region)
        {
            bool valid = character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-';
            if (!valid)
            {
                throw new InvalidOperationException("方舟素材库 Region 格式无效");
            }
        }
        return "https://ark." + region + ".volcengineapi.com";
    }

    private async Task<JsonElement> CallAsync(
        ArkPrivateAssetConnection setting, string action, object payload,
        CancellationToken cancellationToken)
    {
        string baseUrl = ControlPlaneBaseUrl(setting.Region);
        UriBuilder endpoint = new(baseUrl);
        // UriBuilder.Query 自带前导 "?"；不要再手工拼第二个分隔符，否则签名 URI 与
        // 实际发送的 RequestUri 可能出现 "??"，控制面会按未知 Action 拒绝。
        endpoint.Query = "Action=" + Uri.EscapeDataString(action)
            + "&Version=" + Uri.EscapeDataString(ApiVersion);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint.Uri);
        ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;
        ProtocolRequestBuilder.SignVolcV4(
            request, setting.AccessKeyID.Trim(), setting.AccessKeySecret.Trim(),
            service: "ark", region: (setting.Region ?? "").Trim().ToLowerInvariant(), payload: body);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonElement parsed = ParseJson(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            string detail = UpstreamDetail(parsed);
            throw new InvalidOperationException(detail.Length > 0
                ? $"方舟素材库请求失败（HTTP {(int)response.StatusCode}）：{detail}"
                : $"方舟素材库请求失败（HTTP {(int)response.StatusCode}）");
        }
        string upstream = UpstreamDetail(parsed);
        if (upstream.Length > 0)
        {
            throw new InvalidOperationException(upstream);
        }
        return parsed;
    }

    private static JsonElement ParseJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IEnumerable<JsonElement> ResponseMaps(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }
        yield return root;
        foreach (string key in new[] { "Result", "Asset", "Group", "Data" })
        {
            if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            yield return value;
            foreach (string nestedKey in new[] { "Asset", "Group", "Data" })
            {
                if (value.TryGetProperty(nestedKey, out JsonElement nested)
                    && nested.ValueKind == JsonValueKind.Object)
                {
                    yield return nested;
                }
            }
        }
    }

    private static string ResponseField(JsonElement root, params string[] keys)
    {
        foreach (JsonElement map in ResponseMaps(root))
        {
            foreach (string key in keys)
            {
                if (!map.TryGetProperty(key, out JsonElement value))
                {
                    continue;
                }
                if (value.ValueKind == JsonValueKind.String)
                {
                    string text = (value.GetString() ?? "").Trim();
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
                else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetRawText();
                }
            }
        }
        return "";
    }

    private static string UpstreamDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("ResponseMetadata", out JsonElement metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("Error", out JsonElement upstream)
            || upstream.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        string code = upstream.TryGetProperty("Code", out JsonElement codeValue)
            ? (codeValue.GetString() ?? "").Trim()
            : "";
        string message = upstream.TryGetProperty("Message", out JsonElement messageValue)
            ? (messageValue.GetString() ?? "").Trim()
            : "";
        string detail = string.Join(' ', new[] { code, message }.Where(value => value.Length > 0));
        return detail.Length > 0 ? detail : "方舟素材库请求失败";
    }
}

/// <summary>手工同步结果。对应 Go: <c>ArkPrivateAssetSyncResult</c>。</summary>
public sealed record ArkPrivateAssetSyncResult(string ResourceID, string Status);
