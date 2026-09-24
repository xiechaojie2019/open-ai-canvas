#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>分析查询参数。对应 Go: <c>app.AnalyticsQuery</c>。</summary>
public sealed class AnalyticsQueryDto
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string UserID { get; init; } = "";
    public string Model { get; init; } = "";
    public string ChannelID { get; init; } = "";
    public string Capability { get; init; } = "";
}

/// <summary>分析总览。对应 Go: <c>app.AnalyticsOverview</c>（字段顺序即输出顺序）。</summary>
public sealed class AnalyticsOverviewDto
{
    [JsonPropertyName("from")]
    public DateTime From { get; init; }

    [JsonPropertyName("to")]
    public DateTime To { get; init; }

    [JsonPropertyName("kpi")]
    public AnalyticsKpiDto KPI { get; init; } = new();

    [JsonPropertyName("trend")]
    public List<AnalyticsTrendPointDto> Trend { get; set; } = [];

    [JsonPropertyName("models")]
    public List<AnalyticsModelRowDto> Models { get; set; } = [];

    [JsonPropertyName("users")]
    public List<AnalyticsUserRowDto> Users { get; set; } = [];

    [JsonPropertyName("failures")]
    public List<AnalyticsFailureRowDto> Failures { get; set; } = [];
}

/// <summary>分析 KPI。对应 Go: <c>app.AnalyticsKPI</c>。</summary>
public sealed class AnalyticsKpiDto
{
    [JsonPropertyName("activeUsers")]
    public long ActiveUsers { get; set; }

    [JsonPropertyName("dau")]
    public long DAU { get; set; }

    [JsonPropertyName("wau")]
    public long WAU { get; set; }

    [JsonPropertyName("mau")]
    public long MAU { get; set; }

    [JsonPropertyName("generationTasks")]
    public long GenerationTasks { get; set; }

    [JsonPropertyName("upstreamRequests")]
    public long UpstreamRequests { get; set; }

    [JsonPropertyName("successRate")]
    public double SuccessRate { get; set; }

    [JsonPropertyName("p95DurationMs")]
    public long P95DurationMs { get; set; }

    [JsonPropertyName("currentQueuedTasks")]
    public long CurrentQueuedTasks { get; set; }

    [JsonPropertyName("estimatedCostMicros")]
    public long EstimatedCostMicros { get; set; }

    [JsonPropertyName("costAvailable")]
    public bool CostAvailable { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";
}

/// <summary>趋势点。对应 Go: <c>app.AnalyticsTrendPoint</c>。</summary>
public sealed class AnalyticsTrendPointDto
{
    [JsonPropertyName("day")]
    public string Day { get; set; } = "";

    [JsonPropertyName("tasks")]
    public long Tasks { get; set; }

    [JsonPropertyName("requests")]
    public long Requests { get; set; }

    [JsonPropertyName("activeUsers")]
    public long ActiveUsers { get; set; }

    [JsonPropertyName("requestSuccessRate")]
    public double RequestSuccessRate { get; set; }
}

/// <summary>模型维度行。对应 Go: <c>app.AnalyticsModelRow</c>。</summary>
public sealed class AnalyticsModelRowDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("tasks")]
    public long Tasks { get; set; }

    [JsonPropertyName("requests")]
    public long Requests { get; set; }

    [JsonPropertyName("uniqueUsers")]
    public long UniqueUsers { get; set; }

    [JsonPropertyName("taskSuccessRate")]
    public double TaskSuccessRate { get; set; }

    [JsonPropertyName("requestSuccessRate")]
    public double RequestSuccessRate { get; set; }

    [JsonPropertyName("p50DurationMs")]
    public long P50DurationMs { get; set; }

    [JsonPropertyName("p95DurationMs")]
    public long P95DurationMs { get; set; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("cachedTokens")]
    public long CachedTokens { get; set; }

    [JsonPropertyName("usageAvailable")]
    public bool UsageAvailable { get; set; }

    [JsonPropertyName("mediaCount")]
    public long MediaCount { get; set; }

    [JsonPropertyName("videoSeconds")]
    public long VideoSeconds { get; set; }

    [JsonPropertyName("estimatedCostMicros")]
    public long EstimatedCostMicros { get; set; }

    [JsonPropertyName("costAvailable")]
    public bool CostAvailable { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";
}

/// <summary>用户维度行。对应 Go: <c>app.AnalyticsUserRow</c>。</summary>
public sealed class AnalyticsUserRowDto
{
    [JsonPropertyName("userId")]
    public string UserID { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("activeDays")]
    public long ActiveDays { get; set; }

    [JsonPropertyName("tasks")]
    public long Tasks { get; set; }

    [JsonPropertyName("agentMessages")]
    public long AgentMessages { get; set; }

    [JsonPropertyName("canvasDays")]
    public long CanvasDays { get; set; }

    [JsonPropertyName("assets")]
    public long Assets { get; set; }

    [JsonPropertyName("resources")]
    public long Resources { get; set; }

    [JsonPropertyName("commonModel")]
    public string CommonModel { get; set; } = "";
}

/// <summary>失败维度行。对应 Go: <c>app.AnalyticsFailureRow</c>。</summary>
public sealed class AnalyticsFailureRowDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonPropertyName("lastError")]
    public string LastError { get; set; } = "";

    [JsonPropertyName("lastSeenAt")]
    public DateTime LastSeenAt { get; set; }
}

/// <summary>模型价格请求。对应 Go: <c>app.ModelPricingRequest</c>。</summary>
public sealed class ModelPricingRequestDto
{
    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("capability")]
    public string Capability { get; set; } = "";

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("inputPerMillionMicros")]
    public long InputPerMillionMicros { get; set; }

    [JsonPropertyName("outputPerMillionMicros")]
    public long OutputPerMillionMicros { get; set; }

    [JsonPropertyName("cachedPerMillionMicros")]
    public long CachedPerMillionMicros { get; set; }

    [JsonPropertyName("perRequestMicros")]
    public long PerRequestMicros { get; set; }

    [JsonPropertyName("perMediaMicros")]
    public long PerMediaMicros { get; set; }

    [JsonPropertyName("perVideoSecondMicros")]
    public long PerVideoSecondMicros { get; set; }
}

/// <summary>
/// 管理端分析总览与模型价格。对应 Go: <c>internal/app/analytics.go</c>。
/// </summary>
public sealed partial class AdminAnalyticsService
{
    /// <summary>对应 Go: <c>contentModerationErrorCode</c>。</summary>
    private const string ContentModerationErrorCode = "sensitive_words_detected";

    /// <summary>分析总览。对应 Go: <c>AdminAnalytics</c>。</summary>
    public async Task<AnalyticsOverviewDto> OverviewAsync(
        User actor, AnalyticsQueryDto query, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        AnalyticsFilter filter = NormalizeAnalyticsFilter(query);
        IReadOnlyList<TaskEntity> tasks = await _repository
            .AnalyticsTasksAsync(filter, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ApiCallLog> logs = await _repository
            .AnalyticsApiCallLogsAsync(filter, cancellationToken).ConfigureAwait(false);
        if (filter.ChannelID.Length > 0)
        {
            tasks = TasksWithLoggedRequests(tasks, logs);
        }

        AnalyticsFilter activityFilter = filter;
        DateTime rollingFrom = filter.To.AddDays(-30);
        if (rollingFrom < activityFilter.From)
        {
            activityFilter = activityFilter with { From = rollingFrom };
        }
        IReadOnlyList<UserDailyActivity> activities = await _repository
            .AnalyticsActivitiesAsync(activityFilter, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TaskEntity> rollingTasks = tasks;
        IReadOnlyList<ApiCallLog> rollingLogs = logs;
        if (activityFilter.From < filter.From)
        {
            rollingTasks = await _repository
                .AnalyticsTasksAsync(activityFilter, cancellationToken).ConfigureAwait(false);
            if (HasCreationDimensionFilter(filter))
            {
                rollingLogs = await _repository
                    .AnalyticsApiCallLogsAsync(activityFilter, cancellationToken).ConfigureAwait(false);
            }
            if (filter.ChannelID.Length > 0)
            {
                rollingTasks = TasksWithLoggedRequests(rollingTasks, rollingLogs);
            }
        }
        IReadOnlyList<User> users = await _repository.UsersAsync(cancellationToken).ConfigureAwait(false);
        long queued = await _repository.CurrentQueuedTaskCountAsync(cancellationToken).ConfigureAwait(false);

        AnalyticsOverviewDto result = BuildAnalyticsOverview(
            filter, tasks, rollingTasks, rollingLogs, logs, activities, users);
        result.KPI.CurrentQueuedTasks = queued;
        return result;
    }

    /// <summary>分析导出 CSV（usage-*.csv，UTF-8 BOM）。对应 Go: <c>AdminAnalyticsCSV</c>。</summary>
    public async Task<byte[]> AnalyticsCsvAsync(
        User actor, AnalyticsQueryDto query, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        AnalyticsFilter filter = NormalizeAnalyticsFilter(query);
        IReadOnlyList<ApiCallLog> logs = await _repository
            .AnalyticsApiCallLogsAsync(filter, cancellationToken).ConfigureAwait(false);

        StringBuilder writer = new();
        writer.Append("\uFEFF");
        writer.Append("\uFEFF");
        writer.AppendLine("时间,用户ID,渠道ID,任务ID,能力,请求阶段,模型,状态,状态码,耗时毫秒,输入Token,输出Token,缓存Token,媒体数量,视频秒数,估算费用(微单位),币种,错误类型");
        foreach (ApiCallLog log in logs)
        {
            string cost = log.CostAvailable ? log.EstimatedCostMicros.ToString(CultureInfo.InvariantCulture) : "";
            string inputTokens = "", outputTokens = "", cachedTokens = "";
            if (log.UsageAvailable)
            {
                inputTokens = log.InputTokens.ToString(CultureInfo.InvariantCulture);
                outputTokens = log.OutputTokens.ToString(CultureInfo.InvariantCulture);
                cachedTokens = log.CachedTokens.ToString(CultureInfo.InvariantCulture);
            }
            writer.AppendLine(string.Join(",",
                log.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                log.UserID,
                log.ChannelID,
                log.TaskID,
                log.Capability,
                log.RequestKind,
                log.Model,
                log.Status,
                log.StatusCode.ToString(CultureInfo.InvariantCulture),
                log.DurationMs.ToString(CultureInfo.InvariantCulture),
                inputTokens,
                outputTokens,
                cachedTokens,
                log.MediaCount.ToString(CultureInfo.InvariantCulture),
                log.VideoSeconds.ToString(CultureInfo.InvariantCulture),
                cost,
                log.Currency,
                ClassifyApiCallError(log)));
        }
        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    /// <summary>模型价格列表。对应 Go: <c>AdminModelPricings</c>。</summary>
    public async Task<IReadOnlyList<ModelPricing>> ModelPricingsAsync(
        User actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await _repository.ModelPricingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存模型价格。对应 Go: <c>SaveModelPricing</c>。</summary>
    public async Task<ModelPricing> SaveModelPricingAsync(
        User actor, string id, ModelPricingRequestDto request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        string model = request.Model.Trim();
        string capability = NormalizeCapability(request.Capability);
        string currency = request.Currency.Trim().ToUpperInvariant();
        if (model.Length == 0 || capability.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写模型并选择能力类型");
        }
        if (currency.Length == 0)
        {
            currency = "USD";
        }
        if (currency.Length > 12 || HasNegativePricing(request))
        {
            throw AppError.BadAuthRequest("价格配置格式无效");
        }
        ModelPricing pricing = new()
        {
            ID = IdGenerator.NewId(),
            CreatedAt = DateTime.UtcNow,
        };
        if (id.Length > 0)
        {
            ModelPricing? current = await _repository
                .ModelPricingByIDAsync(id, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                // Go 返回原始 not-found → failInternal（500）。
                throw new InvalidOperationException("record not found");
            }
            pricing = current;
        }
        string channelID = request.ChannelID.Trim();
        ModelPricing? duplicate = await _repository.ModelPricingByKeyAsync(
            channelID, model, capability, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null && !string.Equals(duplicate.ID, pricing.ID, StringComparison.Ordinal))
        {
            throw AppError.New(
                409,
                "相同渠道、模型和能力的积分定价已存在，请直接编辑已有配置");
        }
        pricing.ChannelID = channelID;
        pricing.Model = model;
        pricing.Capability = capability;
        pricing.Currency = currency;
        pricing.InputPerMillionMicros = request.InputPerMillionMicros;
        pricing.OutputPerMillionMicros = request.OutputPerMillionMicros;
        pricing.CachedPerMillionMicros = request.CachedPerMillionMicros;
        pricing.PerRequestMicros = request.PerRequestMicros;
        pricing.PerMediaMicros = request.PerMediaMicros;
        pricing.PerVideoSecondMicros = request.PerVideoSecondMicros;
        pricing.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveModelPricingAsync(pricing, cancellationToken).ConfigureAwait(false);
        return pricing;
    }

    /// <summary>删除模型价格。对应 Go: <c>DeleteModelPricing</c>。</summary>
    public async Task DeleteModelPricingAsync(
        User actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        await _repository.DeleteModelPricingAsync(id, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 聚合内核

    /// <summary>构建总览。对应 Go: <c>buildAnalyticsOverview</c>。</summary>
    private static AnalyticsOverviewDto BuildAnalyticsOverview(
        AnalyticsFilter filter,
        IReadOnlyList<TaskEntity> tasks,
        IReadOnlyList<TaskEntity> rollingTasks,
        IReadOnlyList<ApiCallLog> rollingLogs,
        IReadOnlyList<ApiCallLog> logs,
        IReadOnlyList<UserDailyActivity> activities,
        IReadOnlyList<User> users)
    {
        AnalyticsOverviewDto result = new()
        {
            From = filter.From,
            To = filter.To,
            Trend = [],
            Models = [],
            Users = [],
            Failures = [],
        };
        result.KPI.GenerationTasks = tasks.Count;
        result.KPI.UpstreamRequests = logs.Count;
        result.KPI.SuccessRate = SuccessRateLogs(logs);
        List<long> durations = new(logs.Count);
        HashSet<string> activeUsers = new(StringComparer.Ordinal);
        if (!HasCreationDimensionFilter(filter))
        {
            foreach (UserDailyActivity activity in activities)
            {
                if (activity.Day < filter.From || activity.Day >= filter.To || !MeaningfulActivity(activity))
                {
                    continue;
                }
                activeUsers.Add(activity.UserID);
            }
        }
        foreach (TaskEntity task in tasks)
        {
            activeUsers.Add(task.UserID);
        }
        foreach (ApiCallLog log in logs)
        {
            activeUsers.Add(log.UserID);
        }
        result.KPI.ActiveUsers = activeUsers.Count;
        IReadOnlyList<UserDailyActivity> rollingActivities = activities;
        if (HasCreationDimensionFilter(filter))
        {
            rollingActivities = [];
        }
        result.KPI.DAU = RollingActiveUsers(rollingActivities, rollingTasks, rollingLogs, filter.To.AddDays(-1), filter.To);
        result.KPI.WAU = RollingActiveUsers(rollingActivities, rollingTasks, rollingLogs, filter.To.AddDays(-7), filter.To);
        result.KPI.MAU = RollingActiveUsers(rollingActivities, rollingTasks, rollingLogs, filter.To.AddDays(-30), filter.To);
        string currency = "";
        foreach (ApiCallLog log in logs)
        {
            durations.Add(log.DurationMs);
            if (log.CostAvailable)
            {
                result.KPI.CostAvailable = true;
                result.KPI.EstimatedCostMicros += log.EstimatedCostMicros;
                currency = MergeCurrency(currency, log.Currency);
            }
        }
        result.KPI.Currency = currency;
        result.KPI.P95DurationMs = Percentile(durations, 0.95);
        result.Trend = BuildAnalyticsTrend(filter, tasks, logs, activities);
        result.Models = BuildAnalyticsModels(tasks, logs);
        result.Users = BuildAnalyticsUsers(filter, tasks, logs, activities, users);
        result.Failures = BuildAnalyticsFailures(logs);
        return result;
    }

    /// <summary>对应 Go: <c>buildAnalyticsTrend</c>。</summary>
    private static List<AnalyticsTrendPointDto> BuildAnalyticsTrend(
        AnalyticsFilter filter,
        IReadOnlyList<TaskEntity> tasks,
        IReadOnlyList<ApiCallLog> logs,
        IReadOnlyList<UserDailyActivity> activities)
    {
        Dictionary<string, AnalyticsTrendPointDto> points = new(StringComparer.Ordinal);
        for (DateTime day = filter.From.Date; day < filter.To; day = day.AddDays(1))
        {
            string key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            points[key] = new AnalyticsTrendPointDto { Day = key };
        }
        Dictionary<string, long> requestTotals = new(StringComparer.Ordinal);
        Dictionary<string, long> requestSuccess = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> activeByDay = new(StringComparer.Ordinal);
        foreach (TaskEntity task in tasks)
        {
            string key = task.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (points.TryGetValue(key, out AnalyticsTrendPointDto? point))
            {
                point.Tasks++;
                if (!activeByDay.TryGetValue(key, out HashSet<string>? usersOfDay))
                {
                    usersOfDay = new HashSet<string>(StringComparer.Ordinal);
                    activeByDay[key] = usersOfDay;
                }
                usersOfDay.Add(task.UserID);
            }
        }
        foreach (ApiCallLog log in logs)
        {
            string key = log.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (points.TryGetValue(key, out AnalyticsTrendPointDto? point))
            {
                point.Requests++;
                requestTotals[key] = requestTotals.GetValueOrDefault(key) + 1;
                if (log.Status == "succeeded")
                {
                    requestSuccess[key] = requestSuccess.GetValueOrDefault(key) + 1;
                }
                if (!activeByDay.TryGetValue(key, out HashSet<string>? usersOfDay))
                {
                    usersOfDay = new HashSet<string>(StringComparer.Ordinal);
                    activeByDay[key] = usersOfDay;
                }
                usersOfDay.Add(log.UserID);
            }
        }
        if (!HasCreationDimensionFilter(filter))
        {
            foreach (UserDailyActivity activity in activities)
            {
                string key = activity.Day.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!points.ContainsKey(key) || !MeaningfulActivity(activity))
                {
                    continue;
                }
                if (!activeByDay.TryGetValue(key, out HashSet<string>? usersOfDay))
                {
                    usersOfDay = new HashSet<string>(StringComparer.Ordinal);
                    activeByDay[key] = usersOfDay;
                }
                usersOfDay.Add(activity.UserID);
            }
        }
        List<AnalyticsTrendPointDto> result = new(points.Count);
        foreach (string key in points.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            AnalyticsTrendPointDto point = points[key];
            point.ActiveUsers = activeByDay.TryGetValue(key, out HashSet<string>? usersOfDay) ? usersOfDay.Count : 0;
            point.RequestSuccessRate = Ratio(requestSuccess.GetValueOrDefault(key), requestTotals.GetValueOrDefault(key));
            result.Add(point);
        }
        return result;
    }

    /// <summary>对应 Go: <c>buildAnalyticsModels</c>。</summary>
    private static List<AnalyticsModelRowDto> BuildAnalyticsModels(
        IReadOnlyList<TaskEntity> tasks, IReadOnlyList<ApiCallLog> logs)
    {
        Dictionary<string, AnalyticsModelRowDto> items = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> usersByItem = new(StringComparer.Ordinal);
        Dictionary<string, (long Success, long Total)> taskStats = new(StringComparer.Ordinal);
        Dictionary<string, (long Success, List<long> Durations)> requestStats = new(StringComparer.Ordinal);
        string Key(string modelName, string capability) => modelName + "\x00" + capability;
        AnalyticsModelRowDto Get(string modelName, string capability)
        {
            if (modelName.Length == 0)
            {
                modelName = "未识别";
            }
            string key = Key(modelName, capability);
            if (!items.TryGetValue(key, out AnalyticsModelRowDto? row))
            {
                row = new AnalyticsModelRowDto { Model = modelName, Capability = capability };
                items[key] = row;
                usersByItem[key] = new HashSet<string>(StringComparer.Ordinal);
                taskStats[key] = (0, 0);
                requestStats[key] = (0, new List<long>());
            }
            return row;
        }
        foreach (TaskEntity task in tasks)
        {
            string capability = CapabilityFromTaskType(task.Type);
            AnalyticsModelRowDto item = Get(task.Model, capability);
            string key = Key(item.Model, item.Capability);
            item.Tasks++;
            usersByItem[key].Add(task.UserID);
            if (task.Status != "cancelled")
            {
                (long success, long total) = taskStats[key];
                total++;
                if (task.Status == "succeeded")
                {
                    success++;
                }
                taskStats[key] = (success, total);
            }
        }
        foreach (ApiCallLog log in logs)
        {
            AnalyticsModelRowDto item = Get(log.Model, log.Capability);
            string key = Key(item.Model, item.Capability);
            item.Requests++;
            usersByItem[key].Add(log.UserID);
            (long success, List<long> durations) = requestStats[key];
            durations.Add(log.DurationMs);
            requestStats[key] = (success, durations);
            if (log.Status == "succeeded")
            {
                requestStats[key] = (success + 1, durations);
            }
            if (log.UsageAvailable)
            {
                item.UsageAvailable = true;
                item.InputTokens += log.InputTokens;
                item.OutputTokens += log.OutputTokens;
                item.CachedTokens += log.CachedTokens;
            }
            item.MediaCount += log.MediaCount;
            item.VideoSeconds += log.VideoSeconds;
            if (log.CostAvailable)
            {
                item.CostAvailable = true;
                item.EstimatedCostMicros += log.EstimatedCostMicros;
                item.Currency = MergeCurrency(item.Currency, log.Currency);
            }
        }
        List<AnalyticsModelRowDto> result = new(items.Count);
        foreach (KeyValuePair<string, AnalyticsModelRowDto> pair in items)
        {
            AnalyticsModelRowDto row = pair.Value;
            row.UniqueUsers = usersByItem[pair.Key].Count;
            (long taskSuccess, long taskTotal) = taskStats[pair.Key];
            row.TaskSuccessRate = Ratio(taskSuccess, taskTotal);
            (long requestSuccess, List<long> durations) = requestStats[pair.Key];
            row.RequestSuccessRate = Ratio(requestSuccess, row.Requests);
            row.P50DurationMs = Percentile(durations, 0.5);
            row.P95DurationMs = Percentile(durations, 0.95);
            result.Add(row);
        }
        result.Sort((a, b) =>
        {
            int byTasks = b.Tasks.CompareTo(a.Tasks);
            return byTasks != 0 ? byTasks : b.Requests.CompareTo(a.Requests);
        });
        return result;
    }

    /// <summary>对应 Go: <c>buildAnalyticsUsers</c>。</summary>
    private static List<AnalyticsUserRowDto> BuildAnalyticsUsers(
        AnalyticsFilter filter,
        IReadOnlyList<TaskEntity> tasks,
        IReadOnlyList<ApiCallLog> logs,
        IReadOnlyList<UserDailyActivity> activities,
        IReadOnlyList<User> users)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (User user in users)
        {
            names[user.ID] = FirstNonEmpty(user.DisplayName, user.Username);
        }
        Dictionary<string, AnalyticsUserRowDto> rows = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, long>> models = new(StringComparer.Ordinal);
        AnalyticsUserRowDto Get(string userID)
        {
            if (!rows.TryGetValue(userID, out AnalyticsUserRowDto? row))
            {
                row = new AnalyticsUserRowDto
                {
                    UserID = userID,
                    Name = FirstNonEmpty(names.GetValueOrDefault(userID), userID),
                };
                rows[userID] = row;
            }
            return row;
        }
        if (!HasCreationDimensionFilter(filter))
        {
            foreach (UserDailyActivity activity in activities)
            {
                if (activity.Day < filter.From || activity.Day >= filter.To || !MeaningfulActivity(activity))
                {
                    continue;
                }
                AnalyticsUserRowDto row = Get(activity.UserID);
                row.ActiveDays++;
                row.AgentMessages += activity.AgentMessageCount;
                if (activity.CanvasActive)
                {
                    row.CanvasDays++;
                }
                row.Assets += activity.AssetCount;
                row.Resources += activity.ResourceCount;
            }
        }
        foreach (TaskEntity task in tasks)
        {
            if (!models.TryGetValue(task.UserID, out Dictionary<string, long>? byModel))
            {
                byModel = new Dictionary<string, long>(StringComparer.Ordinal);
                models[task.UserID] = byModel;
            }
            byModel[task.Model] = byModel.GetValueOrDefault(task.Model) + 1;
            Get(task.UserID).Tasks++;
        }
        if (HasCreationDimensionFilter(filter))
        {
            Dictionary<string, HashSet<string>> activeDays = new(StringComparer.Ordinal);
            foreach (ApiCallLog log in logs)
            {
                Get(log.UserID);
                if (!activeDays.TryGetValue(log.UserID, out HashSet<string>? days))
                {
                    days = new HashSet<string>(StringComparer.Ordinal);
                    activeDays[log.UserID] = days;
                }
                days.Add(log.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            foreach (KeyValuePair<string, HashSet<string>> pair in activeDays)
            {
                Get(pair.Key).ActiveDays = pair.Value.Count;
            }
        }
        List<AnalyticsUserRowDto> result = new(rows.Count);
        foreach (KeyValuePair<string, AnalyticsUserRowDto> pair in rows)
        {
            AnalyticsUserRowDto row = pair.Value;
            long bestCount = 0;
            if (models.TryGetValue(pair.Key, out Dictionary<string, long>? byModel))
            {
                foreach (KeyValuePair<string, long> modelPair in byModel)
                {
                    if (modelPair.Key.Length > 0 && modelPair.Value > bestCount)
                    {
                        row.CommonModel = modelPair.Key;
                        bestCount = modelPair.Value;
                    }
                }
            }
            result.Add(row);
        }
        result.Sort((a, b) =>
        {
            int byTasks = b.Tasks.CompareTo(a.Tasks);
            return byTasks != 0 ? byTasks : b.ActiveDays.CompareTo(a.ActiveDays);
        });
        return result;
    }

    /// <summary>对应 Go: <c>buildAnalyticsFailures</c>。</summary>
    private static List<AnalyticsFailureRowDto> BuildAnalyticsFailures(IReadOnlyList<ApiCallLog> logs)
    {
        Dictionary<string, AnalyticsFailureRowDto> items = new(StringComparer.Ordinal);
        foreach (ApiCallLog log in logs)
        {
            if (log.Status != "failed")
            {
                continue;
            }
            string typeName = ClassifyApiCallError(log);
            string modelName = FirstNonEmpty(log.Model, "未识别");
            string key = typeName + "\x00" + modelName;
            if (!items.TryGetValue(key, out AnalyticsFailureRowDto? item))
            {
                item = new AnalyticsFailureRowDto { Type = typeName, Model = modelName };
                items[key] = item;
            }
            item.Count++;
            if (log.CreatedAt > item.LastSeenAt)
            {
                item.LastSeenAt = log.CreatedAt;
                item.LastError = KernelUtil.TruncateRunes(log.Error, 180);
            }
        }
        List<AnalyticsFailureRowDto> result = new(items.Values);
        result.Sort((a, b) => b.Count.CompareTo(a.Count));
        return result;
    }

    /// <summary>对应 Go: <c>classifyAPICallError</c>。</summary>
    private static string ClassifyApiCallError(ApiCallLog log)
    {
        string value = log.Error.ToLowerInvariant();
        if (log.ErrorCode == ContentModerationErrorCode)
        {
            return "内容审核";
        }
        if (value.Contains("timeout") || value.Contains("超时")
            || log.StatusCode == 408 || log.StatusCode == 504 || log.StatusCode == 524)
        {
            return "超时";
        }
        if (log.StatusCode == 401 || log.StatusCode == 403 || value.Contains("unauthorized"))
        {
            return "鉴权失败";
        }
        if (log.StatusCode == 429 || value.Contains("rate limit"))
        {
            return "限流";
        }
        if (log.StatusCode >= 400 && log.StatusCode < 500)
        {
            return "请求参数";
        }
        if (log.StatusCode >= 500)
        {
            return "上游服务";
        }
        return value.Length > 0 ? "网络或客户端" : "未知错误";
    }

    /// <summary>对应 Go: <c>meaningfulActivity</c>。</summary>
    private static bool MeaningfulActivity(UserDailyActivity activity) =>
        activity.TaskCount > 0 || activity.AgentMessageCount > 0 || activity.CanvasActive
        || activity.AssetCount > 0 || activity.ResourceCount > 0;

    /// <summary>对应 Go: <c>rollingActiveUsers</c>。</summary>
    private static long RollingActiveUsers(
        IReadOnlyList<UserDailyActivity> activities,
        IReadOnlyList<TaskEntity> tasks,
        IReadOnlyList<ApiCallLog> logs,
        DateTime from,
        DateTime to)
    {
        HashSet<string> users = new(StringComparer.Ordinal);
        foreach (UserDailyActivity activity in activities)
        {
            if (activity.Day >= from && activity.Day < to && MeaningfulActivity(activity))
            {
                users.Add(activity.UserID);
            }
        }
        foreach (TaskEntity task in tasks)
        {
            if (task.CreatedAt >= from && task.CreatedAt < to)
            {
                users.Add(task.UserID);
            }
        }
        foreach (ApiCallLog log in logs)
        {
            if (log.CreatedAt >= from && log.CreatedAt < to)
            {
                users.Add(log.UserID);
            }
        }
        return users.Count;
    }

    private static double SuccessRateLogs(IReadOnlyList<ApiCallLog> logs)
    {
        long succeeded = 0, failed = 0;
        foreach (ApiCallLog log in logs)
        {
            if (log.Status == "succeeded")
            {
                succeeded++;
            }
            else if (log.Status == "failed")
            {
                failed++;
            }
        }
        return Ratio(succeeded, succeeded + failed);
    }

    private static double Ratio(long value, long total) =>
        total == 0 ? 0 : value * 100.0 / total;

    /// <summary>对应 Go: <c>percentile</c>（四舍五入索引取中位数/分位数）。</summary>
    private static long Percentile(IReadOnlyList<long> values, double quantile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        List<long> items = [.. values];
        items.Sort();
        int index = (int)((items.Count - 1) * quantile + 0.5);
        return items[index];
    }

    private static string MergeCurrency(string current, string next)
    {
        if (next.Length == 0)
        {
            return current;
        }
        if (current.Length == 0 || current == next)
        {
            return next;
        }
        return "MIXED";
    }

    /// <summary>对应 Go: <c>capabilityFromTaskType</c>。</summary>
    private static string CapabilityFromTaskType(string taskType)
    {
        string value = taskType.ToLowerInvariant();
        foreach (string capability in new[] { "video", "image", "audio", "text" })
        {
            if (value.Contains(capability, StringComparison.Ordinal))
            {
                return capability;
            }
        }
        if (value.Contains("storyboard", StringComparison.Ordinal)
            || value.Contains("agent", StringComparison.Ordinal))
        {
            return "text";
        }
        return "";
    }

    private static bool HasCreationDimensionFilter(AnalyticsFilter filter) =>
        filter.Model.Length > 0 || filter.ChannelID.Length > 0 || filter.Capability.Length > 0;

    private static IReadOnlyList<TaskEntity> TasksWithLoggedRequests(
        IReadOnlyList<TaskEntity> tasks, IReadOnlyList<ApiCallLog> logs)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ApiCallLog log in logs)
        {
            if (log.TaskID.Length > 0)
            {
                ids.Add(log.TaskID);
            }
        }
        List<TaskEntity> result = new(tasks.Count);
        foreach (TaskEntity task in tasks)
        {
            if (ids.Contains(task.ID))
            {
                result.Add(task);
            }
        }
        return result;
    }

    /// <summary>对应 Go: <c>normalizeAnalyticsFilter</c>。默认窗口 [今天-29, 明天)，按日粒度对齐 UTC。</summary>
    private static AnalyticsFilter NormalizeAnalyticsFilter(AnalyticsQueryDto query)
    {
        DateTime now = DateTime.UtcNow;
        DateTime to = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        DateTime from = to.AddDays(-30);
        if (ParseAnalyticsTime(query.From) is (DateTime parsedFrom, true))
        {
            from = parsedFrom;
        }
        if (ParseAnalyticsTime(query.To) is (DateTime parsedTo, true))
        {
            to = parsedTo;
            if (query.To.Trim().Length == "yyyy-MM-dd".Length)
            {
                to = to.AddDays(1);
            }
        }
        if (!(to > from))
        {
            to = from.AddDays(1);
        }
        if (to - from > TimeSpan.FromDays(366))
        {
            from = to.AddYears(-1);
        }
        return new AnalyticsFilter(from, to, query.UserID.Trim(), query.Model.Trim(), query.ChannelID.Trim(),
            NormalizeCapability(query.Capability));
    }

    private static (DateTime Value, bool Ok) ParseAnalyticsTime(string value)
    {
        value = value.Trim();
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime byDay))
        {
            return (byDay, true);
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            return (parsed, true);
        }
        return (default, false);
    }

    private static string NormalizeCapability(string value)
    {
        string trimmed = value.Trim().ToLowerInvariant();
        return trimmed is "text" or "image" or "video" or "audio" ? trimmed : "";
    }

    private static bool HasNegativePricing(ModelPricingRequestDto request) =>
        request.InputPerMillionMicros < 0 || request.OutputPerMillionMicros < 0
        || request.CachedPerMillionMicros < 0 || request.PerRequestMicros < 0
        || request.PerMediaMicros < 0 || request.PerVideoSecondMicros < 0;

    private static string FirstNonEmpty(string first, string second) =>
        first.Length > 0 ? first : second;

    /// <summary>按 rune 截断（对应 Go: <c>kernel.TruncateRunes</c>）。</summary>
    private static string KernelTruncateRunes(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }
        // UTF-16 码元可能把代理对截半：按 rune 数截。
        List<char> chars = new(value.Length);
        int runeCount = 0;
        for (int i = 0; i < value.Length && runeCount < limit; i++)
        {
            chars.Add(value[i]);
            if (!char.IsHighSurrogate(value[i]) || i + 1 >= value.Length)
            {
                runeCount++;
                continue;
            }
            chars.Add(value[i + 1]);
            i++;
            runeCount++;
        }
        return new string(chars.ToArray());
    }
}
