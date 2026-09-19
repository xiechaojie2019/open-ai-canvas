#nullable enable
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>
/// 管理员 API 日志媒体读取。对应 Go: <c>app/analytics.go</c> 的
/// adminAPICallLogMediaResource / PrepareAdminAPICallLogMediaDelivery。
/// </summary>
/// <remarks>
/// 必须同时校验日志、任务和资源归属，不能绕过用户资源边界按资源 ID 任意读取。
/// </remarks>
public sealed class AdminLogMediaService
{
    private readonly Repository _repository;
    private readonly ResourceDomainService _resources;

    public AdminLogMediaService(Repository repository, ResourceDomainService resources)
    {
        _repository = repository;
        _resources = resources;
    }

    /// <summary>准备日志媒体下发。对应 Go: <c>PrepareAdminAPICallLogMediaDelivery</c>。</summary>
    public async Task<ResourceDelivery> PrepareMediaDeliveryAsync(
        User actor, string logId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (string ownerUserID, string resourceID) = await MediaResourceAsync(logId, cancellationToken).ConfigureAwait(false);
        ResourceDelivery delivery = await _resources
            .PrepareResourceDeliveryAsync(ownerUserID, resourceID, new ResourceDeliveryOptions(), cancellationToken)
            .ConfigureAwait(false);
        return delivery;
    }

    /// <summary>日志媒体资源定位。对应 Go: <c>adminAPICallLogMediaResource</c>。</summary>
    public async Task<(string OwnerUserID, string ResourceID)> MediaResourceAsync(
        string logId, CancellationToken cancellationToken = default)
    {
        ApiCallLog? log = await _repository
            .ApiCallLogAsync(logId.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (log.TaskID.Length == 0 || (log.Capability != "image" && log.Capability != "video"))
        {
            throw AppError.BadAuthRequest("该请求没有可预览媒体");
        }
        TaskEntity? task = await _repository
            .TaskAsync(log.TaskID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (task.UserID != log.UserID)
        {
            throw AppError.BadAuthRequest("请求与媒体归属不一致");
        }
        (string previewURL, string _) = AdminAnalyticsService.TaskMediaPreview(task.ResultJSON, task.Type);
        string resourceID = AdminAnalyticsService.CanvasResourceID(previewURL);
        if (resourceID.Length == 0)
        {
            throw AppError.BadAuthRequest("该请求没有已持久化媒体");
        }
        return (log.UserID, resourceID);
    }
}
