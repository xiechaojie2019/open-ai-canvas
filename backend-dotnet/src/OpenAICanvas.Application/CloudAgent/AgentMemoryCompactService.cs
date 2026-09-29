#nullable enable
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>手动压缩请求。对应 Go: <c>app.AgentMemoryCompactRequest</c>。</summary>
public sealed class AgentMemoryCompactRequest
{
    [JsonPropertyName("logicalModelId")]
    public string LogicalModelID { get; set; } = "";

    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
}

/// <summary>设置更新请求。对应 Go: <c>app.AgentMemorySettingRequest</c>。</summary>
public sealed class AgentMemorySettingRequest
{
    [JsonPropertyName("compactInterval")]
    public string CompactInterval { get; set; } = "";

    [JsonPropertyName("logicalModelId")]
    public string LogicalModelID { get; set; } = "";

    [JsonPropertyName("channelId")]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("channelModelKey")]
    public string ChannelModelKey { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
}

/// <summary>压缩状态视图。对应 Go: <c>app.AgentMemoryCompactView</c>（struct 字段序）。</summary>
public sealed class AgentMemoryCompactView
{
    [JsonPropertyName("compactInterval")]
    public string CompactInterval { get; set; } = "";

    [JsonPropertyName("logicalModelId")]
    [Domain.Serialization.GoOmitEmpty]
    public string LogicalModelID { get; set; } = "";

    [JsonPropertyName("channelId")]
    [Domain.Serialization.GoOmitEmpty]
    public string ChannelID { get; set; } = "";

    [JsonPropertyName("channelModelKey")]
    [Domain.Serialization.GoOmitEmpty]
    public string ChannelModelKey { get; set; } = "";

    [JsonPropertyName("model")]
    [Domain.Serialization.GoOmitEmpty]
    public string Model { get; set; } = "";

    [JsonPropertyName("lastCompactAt")]
    [Domain.Serialization.GoOmitEmpty]
    public DateTime? LastCompactAt { get; set; }

    [JsonPropertyName("lastStatus")]
    public string LastStatus { get; set; } = "";

    [JsonPropertyName("lastError")]
    [Domain.Serialization.GoOmitEmpty]
    public string LastError { get; set; } = "";

    [JsonPropertyName("taskId")]
    [Domain.Serialization.GoOmitEmpty]
    public string TaskID { get; set; } = "";

    [JsonPropertyName("summary")]
    [Domain.Serialization.GoOmitEmpty]
    public AgentMemoryCompactSummary? Summary { get; set; }
}

/// <summary>对应 Go: <c>app.AgentMemoryCompactSummary</c>。</summary>
public sealed class AgentMemoryCompactSummary
{
    [JsonPropertyName("rewritten")]
    public int Rewritten { get; set; }

    [JsonPropertyName("merged")]
    public int Merged { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }
}

/// <summary>
/// 个人记忆压缩：设置、手动/定时触发与压缩任务结果回写。
/// 对应 Go: <c>app/cloud_agent_memory_compact.go</c>。
/// </summary>
public sealed class AgentMemoryCompactService
{
    /// <summary>对应 Go: <c>cloudAgentMemoryCompactOp</c>。</summary>
    public const string CompactOperation = "agent_memory_compact";

    private const int CompactMaxItems = 80;
    private static readonly TimeSpan CompactStaleAfter = TimeSpan.FromMinutes(30);
    private const int CompactPerTick = 3;

    private readonly Repository _repository;
    private readonly TaskCreationService? _taskCreation;
    private readonly ILogger<AgentMemoryCompactService>? _logger;

    public AgentMemoryCompactService(
        Repository repository,
        TaskCreationService? taskCreation = null,
        ILogger<AgentMemoryCompactService>? logger = null)
    {
        _repository = repository;
        _taskCreation = taskCreation;
        _logger = logger;
    }

    // ------------------------------------------------------------- 设置

    /// <summary>对应 Go: <c>UserAgentMemoryCompact</c>。</summary>
    public async Task<AgentMemoryCompactView> GetAsync(
        string userID, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        AgentMemorySetting setting = await EnsureSettingAsync(userID, cancellationToken).ConfigureAwait(false);
        return ViewOf(setting);
    }

    /// <summary>对应 Go: <c>UpdateUserAgentMemorySetting</c>。</summary>
    public async Task<AgentMemoryCompactView> UpdateSettingAsync(
        string userID, AgentMemorySettingRequest request, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        string interval = NormalizeInterval(request.CompactInterval);
        AgentMemorySetting setting = await EnsureSettingAsync(userID, cancellationToken).ConfigureAwait(false);
        setting.CompactInterval = interval;
        ApplyCompactModel(setting, new AgentMemoryCompactRequest
        {
            LogicalModelID = request.LogicalModelID,
            ChannelID = request.ChannelID,
            ChannelModelKey = request.ChannelModelKey,
            Model = request.Model,
        });
        setting.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
        return ViewOf(setting);
    }

    /// <summary>手动触发压缩。对应 Go: <c>CompactUserAgentMemories</c>。</summary>
    public async Task<AgentMemoryCompactView> CompactAsync(
        string userID, AgentMemoryCompactRequest request, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        AgentMemorySetting setting = await EnsureSettingAsync(userID, cancellationToken).ConfigureAwait(false);
        ApplyCompactModel(setting, request);
        setting.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
        await StartCompactAsync(setting, scheduled: false, cancellationToken).ConfigureAwait(false);
        return ViewOf(setting);
    }

    // ------------------------------------------------------------- 触发

    private async Task StartCompactAsync(
        AgentMemorySetting setting, bool scheduled, CancellationToken cancellationToken)
    {
        if (setting.UserID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        DateTime now = DateTime.UtcNow;
        if (IsBusy(setting, now))
        {
            if (scheduled)
            {
                return;
            }
            throw AppError.BadAuthRequest("已有压缩任务在进行，请稍后再试");
        }
        List<AgentLesson> lessons = await _repository.ApprovedAgentLessonsAsync(
            setting.UserID, CompactMaxItems, cancellationToken).ConfigureAwait(false);
        if (lessons.Count == 0)
        {
            if (scheduled)
            {
                return;
            }
            throw AppError.BadAuthRequest("没有可压缩的已批准记忆");
        }
        if (setting.LogicalModelID.Trim().Length == 0 && setting.ChannelID.Trim().Length == 0
            && setting.Model.Trim().Length == 0 && setting.ChannelModelKey.Trim().Length == 0)
        {
            throw AppError.BadAuthRequest("请先选择用于压缩的文本模型");
        }
        if (_taskCreation is null)
        {
            throw new InvalidOperationException("任务创建服务不可用");
        }
        string modelKey = CloudAgentContracts.FirstNonEmpty(setting.ChannelModelKey, setting.Model);
        Dictionary<string, JsonElement> config = new(StringComparer.Ordinal);
        if (setting.ChannelID.Length > 0)
        {
            config["channelId"] = JsonSerializer.SerializeToElement(setting.ChannelID);
        }
        if (modelKey.Length > 0)
        {
            config["model"] = JsonSerializer.SerializeToElement(modelKey);
            config["channelModelKey"] = JsonSerializer.SerializeToElement(modelKey);
        }
        config["systemPrompt"] = JsonSerializer.SerializeToElement(SystemPrompt());

        Dictionary<string, JsonElement> textOptions = new(StringComparer.Ordinal)
        {
            ["stream"] = JsonSerializer.SerializeToElement(false),
        };
        Dictionary<string, JsonElement> metadata = new(StringComparer.Ordinal)
        {
            ["source"] = JsonSerializer.SerializeToElement(CompactOperation),
        };
        Dictionary<string, JsonElement> input = new(StringComparer.Ordinal)
        {
            ["mode"] = JsonSerializer.SerializeToElement("text"),
            ["prompt"] = JsonSerializer.SerializeToElement(UserPrompt(lessons)),
            ["textOptions"] = JsonSerializer.SerializeToElement(textOptions, GoJson.WriteOptions),
            ["config"] = JsonSerializer.SerializeToElement(config, GoJson.WriteOptions),
            ["metadata"] = JsonSerializer.SerializeToElement(metadata, GoJson.WriteOptions),
        };

        TaskEntity task = await _taskCreation.CreateAsync(setting.UserID, new CreateTaskRequestDto
        {
            Type = "canvas_text",
            Operation = CompactOperation,
            Prompt = "压缩优化个人 Agent 记忆",
            Model = modelKey,
            LogicalModelID = setting.LogicalModelID,
            Input = input,
        }, "", "", cancellationToken).ConfigureAwait(false);

        setting.CompactStartedAt = now;
        setting.CompactTaskID = task.ID;
        setting.LastStatus = "queued";
        setting.LastError = "";
        setting.UpdatedAt = now;
        await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>定时调度一跳。对应 Go: <c>dispatchDueAgentMemoryCompacts</c>。</summary>
    public async Task DispatchDueAsync(CancellationToken cancellationToken = default)
    {
        List<AgentMemorySetting> rows = await _repository.ScheduledAgentMemorySettingsAsync(50, cancellationToken)
            .ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int started = 0;
        foreach (AgentMemorySetting setting in rows)
        {
            if (!IsDue(setting, now) || IsBusy(setting, now))
            {
                continue;
            }
            try
            {
                await StartCompactAsync(setting, scheduled: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is AppError or InvalidOperationException
                or System.Data.Common.DbException)
            {
                _logger?.LogWarning(error, "agent memory compact schedule user={UserID}", setting.UserID);
                setting.LastStatus = "failed";
                setting.LastError = CloudAgentContracts.TruncateRunes(error.Message, 500);
                setting.UpdatedAt = now;
                await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
                continue;
            }
            started++;
            if (started >= CompactPerTick)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------- 任务挂点

    /// <summary>任务开始执行时置 running。对应 Go: <c>markAgentMemoryCompactRunning</c>。</summary>
    public async Task MarkRunningAsync(TaskEntity task, CancellationToken cancellationToken = default)
    {
        if (task.Operation.Trim() != CompactOperation)
        {
            return;
        }
        AgentMemorySetting? setting = await _repository.AgentMemorySettingAsync(task.UserID, cancellationToken)
            .ConfigureAwait(false);
        if (setting is null
            || (setting.CompactTaskID.Length > 0 && setting.CompactTaskID != task.ID))
        {
            return;
        }
        setting.LastStatus = "running";
        setting.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务终态回写。对应 Go: <c>noteAgentMemoryCompactTask</c>。</summary>
    public async Task NoteTaskAsync(
        TaskEntity task, Dictionary<string, object?>? result, Exception? error,
        CancellationToken cancellationToken = default)
    {
        if (task.Operation.Trim() != CompactOperation)
        {
            return;
        }
        AgentMemorySetting? setting = await _repository.AgentMemorySettingAsync(task.UserID, cancellationToken)
            .ConfigureAwait(false);
        if (setting is null
            || (setting.CompactTaskID.Length > 0 && setting.CompactTaskID != task.ID))
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        setting.UpdatedAt = now;
        if (error is not null)
        {
            setting.LastStatus = "failed";
            setting.LastError = CloudAgentContracts.TruncateRunes(error.Message, 500);
            await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
            return;
        }
        string text = ResultText(result);
        AgentMemoryCompactSummary summary;
        try
        {
            summary = await ApplyCompactTextAsync(setting.UserID, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyError) when (applyError is AppError or InvalidOperationException
            or System.Data.Common.DbException or JsonException)
        {
            setting.LastStatus = "failed";
            setting.LastError = CloudAgentContracts.TruncateRunes(applyError.Message, 500);
            await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
            return;
        }
        setting.LastSummaryJSON = JsonSerializer.Serialize(summary, GoJson.WriteOptions);
        setting.LastStatus = "succeeded";
        setting.LastError = "";
        setting.LastCompactAt = now;
        await _repository.SaveAgentMemorySettingAsync(setting, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- 计划应用

    /// <summary>解析模型输出并应用到记忆。对应 Go: <c>applyAgentMemoryCompactText</c>。</summary>
    public async Task<AgentMemoryCompactSummary> ApplyCompactTextAsync(
        string userID, string text, CancellationToken cancellationToken = default)
    {
        (List<CompactRewrite> rewrites, List<CompactMerge> merges) = ParsePlan(text);
        return await ApplyPlanAsync(userID, rewrites, merges, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentMemoryCompactSummary> ApplyPlanAsync(
        string userID, List<CompactRewrite> rewrites, List<CompactMerge> merges,
        CancellationToken cancellationToken)
    {
        List<AgentLesson> owned = await _repository.ApprovedAgentLessonsAsync(userID, 0, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, AgentLesson> byID = new(StringComparer.Ordinal);
        foreach (AgentLesson lesson in owned)
        {
            byID[lesson.ID] = lesson;
        }
        DateTime now = DateTime.UtcNow;
        List<AgentLesson> updates = [];
        List<string> deleteIDs = [];
        Dictionary<string, bool> used = new(StringComparer.Ordinal);
        AgentMemoryCompactSummary summary = new();

        foreach (CompactMerge merge in merges)
        {
            List<string> ids = [];
            Dictionary<string, bool> seen = new(StringComparer.Ordinal);
            foreach (string raw in merge.IDs)
            {
                string id = raw.Trim();
                if (id.Length == 0 || seen.ContainsKey(id))
                {
                    continue;
                }
                seen[id] = true;
                ids.Add(id);
            }
            if (ids.Count < 2)
            {
                summary.Skipped++;
                continue;
            }
            List<AgentLesson> sources = new(ids.Count);
            bool valid = true;
            foreach (string id in ids)
            {
                if (used.ContainsKey(id) || !byID.TryGetValue(id, out AgentLesson? lesson))
                {
                    valid = false;
                    break;
                }
                sources.Add(lesson);
            }
            if (!valid)
            {
                summary.Skipped++;
                continue;
            }
            AgentLesson next;
            try
            {
                next = CloudAgentLessons.BuildMemory(userID, new AgentMemoryRequest
                {
                    Topic = merge.Topic,
                    Category = merge.Category,
                    Situation = merge.Situation,
                    Lesson = merge.Lesson,
                    Steps = [.. merge.Steps ?? []],
                    Source = CloudAgentContracts.FirstNonEmpty(merge.Source, "compact"),
                }, CloudAgentLessons.StatusApproved);
            }
            catch (AppError)
            {
                summary.Skipped++;
                continue;
            }
            foreach (string id in ids)
            {
                used[id] = true;
            }
            AgentLesson current = sources[0];
            current.Topic = next.Topic;
            current.Category = next.Category;
            current.Situation = next.Situation;
            current.Lesson = next.Lesson;
            current.Source = next.Source;
            current.StepsJSON = next.StepsJSON;
            current.LastVerifiedAt = now;
            current.UpdatedAt = now;
            updates.Add(current);
            byID[current.ID] = current;
            deleteIDs.AddRange(ids[1..]);
            summary.Merged++;
            summary.Removed += ids.Count - 1;
        }

        foreach (CompactRewrite rewrite in rewrites)
        {
            string id = rewrite.ID.Trim();
            if (id.Length == 0 || used.ContainsKey(id) || !byID.TryGetValue(id, out AgentLesson? current))
            {
                summary.Skipped++;
                continue;
            }
            AgentLesson next;
            try
            {
                next = CloudAgentLessons.BuildMemory(userID, new AgentMemoryRequest
                {
                    Topic = rewrite.Topic,
                    Category = rewrite.Category,
                    Situation = rewrite.Situation,
                    Lesson = rewrite.Lesson,
                    Steps = [.. rewrite.Steps ?? []],
                    Source = rewrite.Source,
                }, current.Status);
            }
            catch (AppError)
            {
                summary.Skipped++;
                continue;
            }
            current.Topic = next.Topic;
            current.Category = next.Category;
            current.Situation = next.Situation;
            current.Lesson = next.Lesson;
            current.Source = next.Source;
            current.StepsJSON = next.StepsJSON;
            current.LastVerifiedAt = now;
            current.UpdatedAt = now;
            updates.Add(current);
            used[id] = true;
            summary.Rewritten++;
        }

        if (updates.Count == 0 && deleteIDs.Count == 0)
        {
            return summary;
        }
        await _repository.ApplyUserAgentMemoryCompactAsync(userID, updates, deleteIDs, cancellationToken)
            .ConfigureAwait(false);
        return summary;
    }

    private static (List<CompactRewrite>, List<CompactMerge>) ParsePlan(string text)
    {
        string raw = text.Trim();
        if (raw.Length == 0)
        {
            throw AppError.BadAuthRequest("模型没有返回可应用的压缩结果");
        }
        int fence = raw.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            raw = raw[(fence + 3)..];
            if (raw.StartsWith("json", StringComparison.Ordinal))
            {
                raw = raw[4..];
            }
            int end = raw.IndexOf("```", StringComparison.Ordinal);
            if (end >= 0)
            {
                raw = raw[..end];
            }
        }
        int start = raw.IndexOf('{');
        int last = raw.LastIndexOf('}');
        if (start < 0 || last <= start)
        {
            throw AppError.BadAuthRequest("模型返回不是压缩 JSON");
        }
        try
        {
            CompactPlan? plan = JsonSerializer.Deserialize<CompactPlan>(raw[start..(last + 1)], GoJson.ReadOptions);
            return (plan?.Rewrites ?? [], plan?.Merges ?? []);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("模型返回的压缩 JSON 无法解析");
        }
    }

    private static string ResultText(Dictionary<string, object?>? result)
    {
        if (result is null)
        {
            return "";
        }
        if (result.TryGetValue("text", out object? text) && text is string textValue
            && textValue.Trim().Length > 0)
        {
            return textValue;
        }
        if (result.TryGetValue("content", out object? content) && content is string contentValue
            && contentValue.Trim().Length > 0)
        {
            return contentValue;
        }
        return "";
    }

    // ------------------------------------------------------------- 辅助

    private async Task<AgentMemorySetting> EnsureSettingAsync(
        string userID, CancellationToken cancellationToken)
    {
        AgentMemorySetting? setting = await _repository.AgentMemorySettingAsync(userID, cancellationToken)
            .ConfigureAwait(false);
        if (setting is not null)
        {
            return setting;
        }
        DateTime now = DateTime.UtcNow;
        AgentMemorySetting created = new()
        {
            UserID = userID,
            CompactInterval = "off",
            LastStatus = "idle",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _repository.SaveAgentMemorySettingAsync(created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static void ApplyCompactModel(AgentMemorySetting setting, AgentMemoryCompactRequest request)
    {
        if (request.LogicalModelID.Trim().Length > 0)
        {
            setting.LogicalModelID = request.LogicalModelID.Trim();
        }
        if (request.ChannelID.Trim().Length > 0)
        {
            setting.ChannelID = request.ChannelID.Trim();
        }
        if (request.ChannelModelKey.Trim().Length > 0)
        {
            setting.ChannelModelKey = request.ChannelModelKey.Trim();
        }
        if (request.Model.Trim().Length > 0)
        {
            setting.Model = request.Model.Trim();
        }
    }

    private static string NormalizeInterval(string value) => value.Trim() switch
    {
        "" or "off" => "off",
        "daily" or "weekly" or "monthly" => value.Trim(),
        _ => throw AppError.BadAuthRequest("压缩周期必须是 off / daily / weekly / monthly"),
    };

    private static bool IsBusy(AgentMemorySetting setting, DateTime now)
    {
        if (setting.LastStatus is not ("queued" or "running"))
        {
            return false;
        }
        return setting.CompactStartedAt is null
            || now - setting.CompactStartedAt.Value < CompactStaleAfter;
    }

    private static bool IsDue(AgentMemorySetting setting, DateTime now)
    {
        TimeSpan interval = setting.CompactInterval switch
        {
            "daily" => TimeSpan.FromHours(24),
            "weekly" => TimeSpan.FromDays(7),
            "monthly" => TimeSpan.FromDays(30),
            _ => TimeSpan.Zero,
        };
        if (interval == TimeSpan.Zero)
        {
            return false;
        }
        if (setting.LastCompactAt is null)
        {
            return true;
        }
        return setting.LastCompactAt.Value.Add(interval) <= now;
    }

    private static AgentMemoryCompactView ViewOf(AgentMemorySetting setting)
    {
        AgentMemoryCompactView view = new()
        {
            CompactInterval = CloudAgentContracts.FirstNonEmpty(setting.CompactInterval, "off"),
            LogicalModelID = setting.LogicalModelID,
            ChannelID = setting.ChannelID,
            ChannelModelKey = setting.ChannelModelKey,
            Model = setting.Model,
            LastCompactAt = setting.LastCompactAt,
            LastStatus = CloudAgentContracts.FirstNonEmpty(setting.LastStatus, "idle"),
            LastError = setting.LastError,
            TaskID = setting.CompactTaskID,
        };
        if (setting.LastSummaryJSON.Trim().Length > 0)
        {
            try
            {
                view.Summary = JsonSerializer.Deserialize<AgentMemoryCompactSummary>(
                    setting.LastSummaryJSON, GoJson.ReadOptions);
            }
            catch (JsonException)
            {
            }
        }
        return view;
    }

    private static string SystemPrompt() => string.Join("\n",
    [
        "你在整理用户自己的 Agent 个人记忆。只输出一个 JSON 对象，不要解释。",
        """格式：{"rewrites":[{"id":"...","topic":"...","category":"...","situation":"...","lesson":"...","steps":[{"tool":"...","action":"..."}],"source":"..."}],"merges":[{"ids":["id1","id2"],"topic":"...","category":"...","situation":"...","lesson":"...","steps":[],"source":"compact"}]}""",
        "规则：",
        "1. 只使用输入里出现过的 id，禁止编造。",
        "2. 意思接近或重复的条目放进 merges，ids 至少 2 个；保留通用做法，去掉具体对象名。",
        "3. 表述含糊、过长或夹带一次性细节的条目放进 rewrites，改成脱离当前画布也成立的短句。",
        "4. category 只能是 storyboard/video/image/canvas/asset/model/workflow/billing/other。",
        "5. 不要删除没有被 merges 覆盖的条目；没有改动就返回空数组。",
        "6. 不要写链接、资源键、UUID、任务/节点 ID。",
    ]);

    private static string UserPrompt(List<AgentLesson> lessons)
    {
        JsonArray items = [];
        foreach (AgentLesson lesson in lessons)
        {
            CloudAgentLessons.AgentLessonView view = CloudAgentLessons.ViewOf(lesson);
            // Go 的 item struct 字段序：id, topic, category, situation, lesson?, steps?, source?。
            JsonObject item = new()
            {
                ["id"] = view.ID,
                ["topic"] = view.Topic,
                ["category"] = view.Category,
                ["situation"] = view.Situation,
            };
            if (view.Lesson.Length > 0)
            {
                item["lesson"] = view.Lesson;
            }
            if (view.Steps is { Count: > 0 })
            {
                item["steps"] = JsonSerializer.SerializeToNode(view.Steps, GoJson.WriteOptions);
            }
            if (view.Source.Length > 0)
            {
                item["source"] = view.Source;
            }
            items.Add(item);
        }
        return "请压缩并优化下面这些已批准记忆：\n" + JsonSerializer.Serialize(items, GoJson.WriteOptions);
    }

    private sealed class CompactPlan
    {
        [JsonPropertyName("rewrites")]
        public List<CompactRewrite> Rewrites { get; set; } = [];

        [JsonPropertyName("merges")]
        public List<CompactMerge> Merges { get; set; } = [];
    }

    private sealed class CompactRewrite
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("topic")]
        public string Topic { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("situation")]
        public string Situation { get; set; } = "";

        [JsonPropertyName("lesson")]
        public string Lesson { get; set; } = "";

        [JsonPropertyName("steps")]
        public List<AgentLessonStepDto>? Steps { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";
    }

    private sealed class CompactMerge
    {
        [JsonPropertyName("ids")]
        public List<string> IDs { get; set; } = [];

        [JsonPropertyName("topic")]
        public string Topic { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("situation")]
        public string Situation { get; set; } = "";

        [JsonPropertyName("lesson")]
        public string Lesson { get; set; } = "";

        [JsonPropertyName("steps")]
        public List<AgentLessonStepDto>? Steps { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";
    }
}
