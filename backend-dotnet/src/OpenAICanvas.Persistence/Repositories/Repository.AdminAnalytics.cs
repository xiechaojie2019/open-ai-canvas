#nullable enable
using System.Data.Common;
using System.Globalization;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>分析过滤条件。对应 Go: <c>repository.AnalyticsFilter</c>。</summary>
public sealed record AnalyticsFilter(
    DateTime From, DateTime To, string UserID, string Model, string ChannelID, string Capability);

/// <summary>API 日志过滤条件。对应 Go: <c>repository.APICallLogFilter</c>。</summary>
public sealed record ApiCallLogFilter(
    AnalyticsFilter Analytics, string RecordType, string Keyword, string Status, long Page, long Limit);

/// <summary>资源存储汇总。对应 Go: <c>repository.ResourceStorageSummary</c>。</summary>
public sealed record ResourceStorageSummary(
    long ResourceCount, long ReadyCount, long TotalBytes, long PhysicalBytes);

public sealed record ResourceKindStat(string Key, long Count, long Bytes);

public sealed record ResourceProviderStat(string Key, long Count, long Bytes);

/// <summary>
/// 管理后台分析/日志/存储仓储方法。
/// 对应 Go: <c>repository/analytics.go</c> 与 <c>repository/admin_storage.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>API 日志分页。对应 Go: <c>QueryAPICallLogs</c>。</summary>
    public async Task<(IReadOnlyList<ApiCallLog> Logs, long Total)> QueryApiCallLogsAsync(
        ApiCallLogFilter filter, CancellationToken cancellationToken = default)
    {
        (string where, DynamicParameters parameters) = BuildApiCallLogFilter(filter);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM \"api_call_logs\" WHERE " + where, parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", filter.Limit);
        parameters.Add("offset", (filter.Page - 1) * filter.Limit);
        IReadOnlyList<ApiCallLog> logs = await QueryAsync<ApiCallLog>(
            connection,
            SqlBuilder.Select<ApiCallLog>(where, "created_at DESC", Dialect.LimitOffset(filter.Limit, (filter.Page - 1) * filter.Limit)),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (logs, total);
    }

    /// <summary>导出用日志（不分页，上限 5000）。对应 Go 的导出路径。</summary>
    public async Task<IReadOnlyList<ApiCallLog>> ApiCallLogsForExportAsync(
        ApiCallLogFilter filter, CancellationToken cancellationToken = default)
    {
        (string where, DynamicParameters parameters) = BuildApiCallLogFilter(filter);
        parameters.Add("limit", 5000);
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ApiCallLog>(
            connection,
            SqlBuilder.Select<ApiCallLog>(where, "created_at DESC", " LIMIT @limit"),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private (string Where, DynamicParameters Parameters) BuildApiCallLogFilter(ApiCallLogFilter filter)
    {
        List<string> conditions = ["\"created_at\" >= @from", "\"created_at\" < @to"];
        DynamicParameters parameters = new();
        parameters.Add("from", filter.Analytics.From);
        parameters.Add("to", filter.Analytics.To);
        if (filter.Analytics.UserID.Length > 0)
        {
            conditions.Add("\"user_id\" = @userId");
            parameters.Add("userId", filter.Analytics.UserID);
        }
        if (filter.Analytics.Model.Length > 0)
        {
            conditions.Add("\"model\" = @model");
            parameters.Add("model", filter.Analytics.Model);
        }
        if (filter.Analytics.ChannelID.Length > 0)
        {
            conditions.Add("\"channel_id\" = @channelId");
            parameters.Add("channelId", filter.Analytics.ChannelID);
        }
        if (filter.Analytics.Capability.Length > 0)
        {
            conditions.Add("\"capability\" = @capability");
            parameters.Add("capability", filter.Analytics.Capability);
        }
        // 对应 Go filteredAPICallLogQuery 的 RecordType 分支：
        // download → 仅 request_kind='download'；all → 不过滤；
        // 默认 → 排除轮询（poll）与下载记录。
        switch (filter.RecordType)
        {
            case "download":
                conditions.Add("\"request_kind\" = @requestKindDownload");
                parameters.Add("requestKindDownload", "download");
                break;
            case "all":
                break;
            default:
                conditions.Add("(\"request_kind\" IS NULL OR (\"request_kind\" <> @requestKindPoll AND \"request_kind\" <> @requestKindDownload))");
                parameters.Add("requestKindPoll", "poll");
                parameters.Add("requestKindDownload", "download");
                break;
        }
        if (filter.Status.Length > 0)
        {
            conditions.Add("\"status\" = @status");
            parameters.Add("status", filter.Status);
        }
        string keyword = filter.Keyword.Trim();
        if (keyword.Length > 0)
        {
            conditions.Add("(lower(\"path\") LIKE @pattern OR lower(\"model\") LIKE @pattern OR lower(\"error\") LIKE @pattern)");
            parameters.Add("pattern", "%" + keyword.ToLowerInvariant() + "%");
        }
        return (string.Join(" AND ", conditions), parameters);
    }

    /// <summary>按 ID 查日志。对应 Go: <c>APICallLog</c>。</summary>
    public async Task<ApiCallLog?> ApiCallLogAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ApiCallLog>(
            connection,
            SqlBuilder.Select<ApiCallLog>("id = @id", limitOffset: " LIMIT 1"),
            new { id },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>日志关联任务。对应 Go: <c>APICallLogTasks</c>。</summary>
    public async Task<IReadOnlyList<TaskEntity>> ApiCallLogTasksAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>("id IN @ids"),
            new { ids },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 批量取账单。对应 Go: <c>BillingOrdersByIDs</c>。</summary>
    public async Task<Dictionary<string, BillingOrder>> BillingOrdersByIDsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Dictionary<string, BillingOrder> result = new(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (BillingOrder order in await QueryAsync<BillingOrder>(
                     connection,
                     SqlBuilder.Select<BillingOrder>("id IN @ids"),
                     new { ids },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result[order.ID] = order;
        }
        return result;
    }

    /// <summary>历史系统渠道引用（含已删除）。对应 Go: <c>HistoricalSystemChannelReferences</c>。</summary>
    public async Task<IReadOnlyList<ModelChannel>> HistoricalSystemChannelReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ModelChannel>(
            connection,
            SqlBuilder.SelectColumns<ModelChannel>(
                ["ID", "Name", "Enabled"], "scope = @scope", "created_at ASC"),
            new { scope = "system" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 批量取用户。对应 Go: <c>UsersByIDs</c>。</summary>
    public async Task<Dictionary<string, User>> UsersByIDsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Dictionary<string, User> result = new(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (User user in await QueryAsync<User>(
                     connection,
                     SqlBuilder.Select<User>("id IN @ids"),
                     new { ids },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result[user.ID] = user;
        }
        return result;
    }

    /// <summary>存储汇总统计。对应 Go: <c>ResourceStorageSummary</c>。</summary>
    public async Task<ResourceStorageSummary> ResourceStorageSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<ResourceStorageSummary>(
            new CommandDefinition(
                """
                SELECT
                  COUNT(*) AS "ResourceCount",
                  COALESCE(SUM(CASE WHEN "status" = @ready THEN 1 ELSE 0 END), 0) AS "ReadyCount",
                  COALESCE(SUM("size"), 0) AS "TotalBytes",
                  COALESCE(SUM(CASE WHEN "status" = @ready THEN "size" ELSE 0 END), 0) AS "PhysicalBytes"
                FROM "resources"
                """,
                new { ready = "ready" },
                cancellationToken: cancellationToken)).ConfigureAwait(false) ?? new ResourceStorageSummary(0, 0, 0, 0);
    }

    /// <summary>按 kind 分组统计。对应 Go: <c>ResourceKindStats</c>。</summary>
    public async Task<IReadOnlyList<ResourceKindStat>> ResourceKindStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ResourceKindStat>(
            connection,
            """SELECT "kind" AS "Key", COUNT(*) AS "Count", COALESCE(SUM("size"), 0) AS "Bytes" FROM "resources" GROUP BY "kind" ORDER BY "kind" ASC""",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 provider 分组统计。对应 Go: <c>ResourceProviderStats</c>。</summary>
    public async Task<IReadOnlyList<ResourceProviderStat>> ResourceProviderStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ResourceProviderStat>(
            connection,
            """SELECT COALESCE(NULLIF("provider", ''), 'local') AS "Key", COUNT(*) AS "Count", COALESCE(SUM("size"), 0) AS "Bytes" FROM "resources" GROUP BY COALESCE(NULLIF("provider", ''), 'local') ORDER BY "Key" ASC""",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}