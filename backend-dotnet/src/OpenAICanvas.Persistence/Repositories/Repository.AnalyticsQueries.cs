#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 分析读查询。对应 Go: <c>repository/analytics.go</c> 的 Analytics 部分。
/// </summary>
public sealed partial class Repository
{
    /// <summary>分析窗口内的任务（精简列）。对应 Go: <c>AnalyticsTasks</c>。</summary>
    public async Task<IReadOnlyList<TaskEntity>> AnalyticsTasksAsync(
        AnalyticsFilter filter, CancellationToken cancellationToken = default)
    {
        var conditions = new List<string> { "created_at >= @From", "created_at < @To" };
        if (filter.UserID.Length > 0)
        {
            conditions.Add("user_id = @UserID");
        }
        if (filter.Model.Length > 0)
        {
            conditions.Add("model = @Model");
        }
        if (filter.Capability.Length > 0)
        {
            conditions.Add(filter.Capability == "text"
                ? "(type LIKE '%text%' OR type LIKE '%storyboard%' OR type LIKE '%agent%')"
                : "type LIKE @CapabilityPattern");
        }

        string where = string.Join(" AND ", conditions);
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<TaskEntity>("tasks")}
            FROM tasks
            WHERE {where}
            """,
            new
            {
                filter.From,
                filter.To,
                filter.UserID,
                filter.Model,
                CapabilityPattern = "%" + filter.Capability + "%",
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>分析窗口内的 API 调用日志（不含原始报文）。对应 Go: <c>AnalyticsAPICallLogs</c>。</summary>
    public async Task<IReadOnlyList<ApiCallLog>> AnalyticsApiCallLogsAsync(
        AnalyticsFilter filter, CancellationToken cancellationToken = default)
    {
        var conditions = new List<string> { "created_at >= @From", "created_at < @To" };
        if (filter.UserID.Length > 0)
        {
            conditions.Add("user_id = @UserID");
        }
        if (filter.Model.Length > 0)
        {
            conditions.Add("model = @Model");
        }
        if (filter.ChannelID.Length > 0)
        {
            conditions.Add("channel_id = @ChannelID");
        }
        if (filter.Capability.Length > 0)
        {
            conditions.Add("capability = @CapabilityExact");
        }

        // 原始报文不进分析聚合（对应 GORM Omit("RequestBody","ResponseBody")）。
        string columns = """
            id, user_id, trace_id, request_id, channel_id, task_id, billing_order_id,
            source, capability, operation, request_kind, billable, api_format, method, path, status,
            status_code, error, error_code, model, usage_available, input_tokens, output_tokens,
            cached_tokens, media_count, video_seconds, estimated_cost_micros, cost_available, currency,
            provider_request_id, provider_status, poll_count, concurrency_limit, upstream_url,
            request_content_type, started_at, duration_ms, created_at
            """;
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ApiCallLog>(
            connection,
            $"SELECT {columns} FROM api_call_logs WHERE {string.Join(" AND ", conditions)}",
            new
            {
                filter.From,
                filter.To,
                filter.UserID,
                filter.Model,
                filter.ChannelID,
                CapabilityExact = filter.Capability,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>分析窗口内的用户日活。对应 Go: <c>AnalyticsActivities</c>。</summary>
    public async Task<IReadOnlyList<UserDailyActivity>> AnalyticsActivitiesAsync(
        AnalyticsFilter filter, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<UserDailyActivity>(
            connection,
            SqlBuilder.Select<UserDailyActivity>("day >= @From AND day < @To"
                + (filter.UserID.Length > 0 ? " AND user_id = @UserID" : "")),
            new { filter.From, filter.To, filter.UserID },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>当前排队中的任务数。对应 Go: <c>CurrentQueuedTaskCount</c>。</summary>
    public async Task<long> CurrentQueuedTaskCountAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM tasks WHERE status = 'queued'",
            new { },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全部模型价格（模型、能力排序）。对应 Go: <c>ModelPricings</c>。</summary>
    public async Task<IReadOnlyList<ModelPricing>> ModelPricingsAsync(CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ModelPricing>(
            connection,
            SqlBuilder.Select<ModelPricing>(orderBy: "model ASC, capability ASC"),
            new { },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按模型 + 能力 + 渠道查价格。对应 Go: <c>ModelPricing</c>。</summary>
    public async Task<ModelPricing?> ModelPricingAsync(
        string channelID, string model, string capability, CancellationToken cancellationToken = default)
    {
        string where = "model = @model AND capability = @capability";
        string orderBy = "";
        if (channelID.Length > 0)
        {
            where += " AND channel_id IN @channelIds";
            orderBy = " ORDER BY channel_id DESC LIMIT 1";
        }
        else
        {
            where += " AND channel_id = '' LIMIT 1";
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelPricing>(
            connection,
            SqlBuilder.Select<ModelPricing>(where) + orderBy,
            new { model, capability, channelIds = new[] { channelID, "" } },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查价格。对应 Go: <c>ModelPricingByID</c>。</summary>
    public async Task<ModelPricing?> ModelPricingByIDAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ModelPricing>(
            connection,
            SqlBuilder.Select<ModelPricing>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存模型价格（GORM Save 的 update-or-insert 语义）。对应 Go: <c>Save</c>。</summary>
    public async Task SaveModelPricingAsync(
        ModelPricing pricing, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE model_pricings SET
              channel_id = @ChannelID, model = @Model, capability = @Capability, currency = @Currency,
              input_per_million_micros = @InputPerMillionMicros, output_per_million_micros = @OutputPerMillionMicros,
              cached_per_million_micros = @CachedPerMillionMicros, per_request_micros = @PerRequestMicros,
              per_media_micros = @PerMediaMicros, per_video_second_micros = @PerVideoSecondMicros,
              updated_at = @UpdatedAt
            WHERE id = @ID
            """,
            pricing,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<ModelPricing>(), pricing, transaction, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除模型价格。对应 Go: <c>DeleteModelPricing</c>。</summary>
    public async Task DeleteModelPricingAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM model_pricings WHERE id = @id",
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
