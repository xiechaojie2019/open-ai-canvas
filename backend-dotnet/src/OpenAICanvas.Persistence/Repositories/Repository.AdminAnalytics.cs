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
    long ResourceCount,
    long ReadyCount,
    long LogicalBytes,
    long PhysicalBytes,
    long LocalBytes,
    long RemoteBytes);

public sealed record ResourceKindStat(
    string Kind,
    long Count,
    long LogicalBytes,
    long PhysicalBytes);

public sealed record ResourceProviderStat(
    string Provider,
    long Count,
    long LogicalBytes,
    long PhysicalBytes);

/// <summary>
/// 管理后台分析/日志/存储仓储方法。
/// 对应 Go: <c>repository/analytics.go</c> 与 <c>repository/admin_storage.go</c>。
/// </summary>
public sealed partial class Repository
{
    private const string ResourceProviderExpression = "COALESCE(NULLIF(\"provider\", ''), 'local')";

    /// <summary>写入一条 API 调用日志。代理路径只写已脱敏报文，不接收凭证字段。</summary>
    public async Task CreateApiCallLogAsync(
        ApiCallLog log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(log.ID))
        {
            log.ID = IdGenerator.NewId();
        }
        if (log.CreatedAt == default)
        {
            log.CreatedAt = DateTime.UtcNow;
        }
        if (log.StartedAt == default)
        {
            log.StartedAt = log.CreatedAt.AddMilliseconds(-Math.Max(log.DurationMs, 0));
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert(typeof(ApiCallLog)), log, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>整行更新一条调用日志（视频 poll 合并回根行）。对应 Go: <c>repo.Save(root)</c>。</summary>
    public async Task SaveApiCallLogAsync(ApiCallLog log, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Update(typeof(ApiCallLog)), log, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 查找视频调用的根日志（create 行）。任务优先，缺任务时退化为
    /// 用户+渠道+上游请求 ID 的匹配。对应 Go: <c>repo.VideoAPICallRoot</c>。
    /// </summary>
    public async Task<ApiCallLog?> VideoApiCallRootAsync(
        string taskId, string userId, string channelId, string providerRequestId,
        CancellationToken cancellationToken = default)
    {
        string where = taskId.Length > 0
            ? "\"capability\" = 'video' AND \"requestKind\" = 'create' AND \"taskId\" = @taskId"
            : "\"capability\" = 'video' AND \"requestKind\" = 'create' AND \"userId\" = @userId "
              + "AND \"channelId\" = @channelId AND \"providerRequestId\" = @providerRequestId";
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ApiCallLog>(
            connection,
            SqlBuilder.Select<ApiCallLog>(where, "\"createdAt\" DESC", " LIMIT 1"),
            new { taskId, userId, channelId, providerRequestId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务是否已有调用日志（失败兜底只在完全无日志时补一条）。</summary>
    public async Task<bool> HasApiCallLogForTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        if (taskId.Trim().Length == 0)
        {
            return false;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long count = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"apiCallLogs\" WHERE \"taskId\" = @taskId",
            new { taskId = taskId.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return count > 0;
    }

    public async Task<(IReadOnlyList<ApiCallLog> Logs, long Total)> QueryApiCallLogsAsync(
        ApiCallLogFilter filter, CancellationToken cancellationToken = default)
    {
        (string where, DynamicParameters parameters) = BuildApiCallLogFilter(filter);

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection, "SELECT COUNT(*) FROM \"apiCallLogs\" WHERE " + where, parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        parameters.Add("limit", filter.Limit);
        parameters.Add("offset", (filter.Page - 1) * filter.Limit);
        IReadOnlyList<ApiCallLog> logs = await QueryAsync<ApiCallLog>(
            connection,
            SqlBuilder.Select<ApiCallLog>(where, "\"createdAt\" DESC", Dialect.LimitOffset(filter.Limit, (filter.Page - 1) * filter.Limit)),
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
            SqlBuilder.Select<ApiCallLog>(where, "\"createdAt\" DESC", " LIMIT @limit"),
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private (string Where, DynamicParameters Parameters) BuildApiCallLogFilter(ApiCallLogFilter filter)
    {
        List<string> conditions = ["\"createdAt\" >= @from", "\"createdAt\" < @to"];
        DynamicParameters parameters = new();
        parameters.Add("from", filter.Analytics.From);
        parameters.Add("to", filter.Analytics.To);
        if (filter.Analytics.UserID.Length > 0)
        {
            conditions.Add("\"userId\" = @userId");
            parameters.Add("userId", filter.Analytics.UserID);
        }
        if (filter.Analytics.Model.Length > 0)
        {
            conditions.Add("\"model\" = @model");
            parameters.Add("model", filter.Analytics.Model);
        }
        if (filter.Analytics.ChannelID.Length > 0)
        {
            conditions.Add("\"channelId\" = @channelId");
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
                conditions.Add("\"requestKind\" = @requestKindDownload");
                parameters.Add("requestKindDownload", "download");
                break;
            case "all":
                break;
            default:
                conditions.Add("(\"requestKind\" IS NULL OR (\"requestKind\" <> @requestKindPoll AND \"requestKind\" <> @requestKindDownload))");
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
                ["ID", "Name", "Enabled"], "scope = @scope", "\"createdAt\" ASC"),
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
                $"""
                WITH "physical_resources" AS (
                  SELECT {ResourceProviderExpression} AS "Provider", MAX("size") AS "Size"
                  FROM "resources"
                  WHERE "status" = @ready
                  GROUP BY {ResourceProviderExpression}, "endpoint", "bucket", "objectKey"
                )
                SELECT
                  CAST(COUNT(*) AS BIGINT) AS "ResourceCount",
                  CAST(COALESCE(SUM(CASE WHEN "status" = @ready THEN 1 ELSE 0 END), 0) AS BIGINT) AS "ReadyCount",
                  CAST(COALESCE(SUM("size"), 0) AS BIGINT) AS "LogicalBytes",
                  CAST(COALESCE((SELECT SUM("Size") FROM "physical_resources"), 0) AS BIGINT) AS "PhysicalBytes",
                  CAST(COALESCE((SELECT SUM("Size") FROM "physical_resources" WHERE "Provider" = 'local'), 0) AS BIGINT) AS "LocalBytes",
                  CAST(COALESCE((SELECT SUM("Size") FROM "physical_resources" WHERE "Provider" <> 'local'), 0) AS BIGINT) AS "RemoteBytes"
                FROM "resources"
                """,
                new { ready = "ready" },
                cancellationToken: cancellationToken)).ConfigureAwait(false) ?? new ResourceStorageSummary(0, 0, 0, 0, 0, 0);
    }

    /// <summary>按 kind 分组统计。对应 Go: <c>ResourceKindStats</c>。</summary>
    public async Task<IReadOnlyList<ResourceKindStat>> ResourceKindStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ResourceKindStat>(
            connection,
            $"""
            WITH "logical_stats" AS (
              SELECT
                "kind" AS "Kind",
                CAST(COUNT(*) AS BIGINT) AS "Count",
                CAST(COALESCE(SUM("size"), 0) AS BIGINT) AS "LogicalBytes"
              FROM "resources"
              GROUP BY "kind"
            ), "physical_resources" AS (
              SELECT "kind" AS "Kind", MAX("size") AS "Size"
              FROM "resources"
              WHERE "status" = @ready
              GROUP BY "kind", {ResourceProviderExpression}, "endpoint", "bucket", "objectKey"
            ), "physical_stats" AS (
              SELECT "Kind", CAST(COALESCE(SUM("Size"), 0) AS BIGINT) AS "PhysicalBytes"
              FROM "physical_resources"
              GROUP BY "Kind"
            )
            SELECT
              "logical_stats"."Kind",
              "logical_stats"."Count",
              "logical_stats"."LogicalBytes",
              COALESCE("physical_stats"."PhysicalBytes", 0) AS "PhysicalBytes"
            FROM "logical_stats"
            LEFT JOIN "physical_stats" ON "physical_stats"."Kind" = "logical_stats"."Kind"
            ORDER BY "logical_stats"."LogicalBytes" DESC, "logical_stats"."Kind" ASC
            """,
            new { ready = "ready" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 provider 分组统计。对应 Go: <c>ResourceProviderStats</c>。</summary>
    public async Task<IReadOnlyList<ResourceProviderStat>> ResourceProviderStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ResourceProviderStat>(
            connection,
            $"""
            WITH "logical_stats" AS (
              SELECT
                {ResourceProviderExpression} AS "Provider",
                CAST(COUNT(*) AS BIGINT) AS "Count",
                CAST(COALESCE(SUM("size"), 0) AS BIGINT) AS "LogicalBytes"
              FROM "resources"
              GROUP BY {ResourceProviderExpression}
            ), "physical_resources" AS (
              SELECT {ResourceProviderExpression} AS "Provider", MAX("size") AS "Size"
              FROM "resources"
              WHERE "status" = @ready
              GROUP BY {ResourceProviderExpression}, "endpoint", "bucket", "objectKey"
            ), "physical_stats" AS (
              SELECT "Provider", CAST(COALESCE(SUM("Size"), 0) AS BIGINT) AS "PhysicalBytes"
              FROM "physical_resources"
              GROUP BY "Provider"
            )
            SELECT
              "logical_stats"."Provider",
              "logical_stats"."Count",
              "logical_stats"."LogicalBytes",
              COALESCE("physical_stats"."PhysicalBytes", 0) AS "PhysicalBytes"
            FROM "logical_stats"
            LEFT JOIN "physical_stats" ON "physical_stats"."Provider" = "logical_stats"."Provider"
            ORDER BY "logical_stats"."LogicalBytes" DESC, "logical_stats"."Provider" ASC
            """,
            new { ready = "ready" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
