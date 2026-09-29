#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application.CloudAgent;

/// <summary>记忆编辑请求。对应 Go: <c>app.AgentMemoryRequest</c>。</summary>
public sealed class AgentMemoryRequest
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("situation")]
    public string Situation { get; set; } = "";

    [JsonPropertyName("lesson")]
    public string Lesson { get; set; } = "";

    [JsonPropertyName("steps")]
    public AgentLessonStepDto[]? Steps { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
}

/// <summary>导出条目。对应 Go: <c>app.AgentMemoryExportItem</c>。</summary>
public sealed class AgentMemoryExportItem
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("situation")]
    public string Situation { get; set; } = "";

    [JsonPropertyName("lesson")]
    [Domain.Serialization.GoOmitEmpty]
    public string Lesson { get; set; } = "";

    [JsonPropertyName("steps")]
    [Domain.Serialization.GoOmitEmpty]
    public List<AgentLessonStepDto>? Steps { get; set; }

    [JsonPropertyName("source")]
    [Domain.Serialization.GoOmitEmpty]
    public string Source { get; set; } = "";

    [JsonPropertyName("status")]
    [Domain.Serialization.GoOmitEmpty]
    public string Status { get; set; } = "";
}

/// <summary>导出捆绑包。对应 Go: <c>app.AgentMemoryBundle</c>（struct 字段序）。</summary>
public sealed class AgentMemoryBundle
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("exportedAt")]
    public DateTime ExportedAt { get; set; }

    [JsonPropertyName("memories")]
    public List<AgentMemoryExportItem> Memories { get; set; } = [];
}

/// <summary>导入结果。对应 Go: <c>app.AgentMemoryImportResult</c>。</summary>
public sealed class AgentMemoryImportResult
{
    [JsonPropertyName("imported")]
    public int Imported { get; set; }

    [JsonPropertyName("merged")]
    public int Merged { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }
}

/// <summary>
/// 用户与管理的个人记忆 CRUD。对应 Go: <c>app/cloud_agent_lessons.go</c> 的
/// Service 方法与 <c>handler/agent_lesson.go</c> 路由的服务层。
/// </summary>
public sealed class AgentLessonService
{
    private readonly Repository _repository;

    public AgentLessonService(Repository repository) => _repository = repository;

    /// <summary>对应 Go: <c>UserAgentMemories</c>。</summary>
    public async Task<List<CloudAgentLessons.AgentLessonView>> UserAgentMemoriesAsync(
        string userID, string status, int limit, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        List<AgentLesson> lessons = await _repository.UserAgentLessonsAsync(userID, status, limit, cancellationToken)
            .ConfigureAwait(false);
        return lessons.Select(CloudAgentLessons.ViewOf).ToList();
    }

    /// <summary>对应 Go: <c>CreateUserAgentMemory</c>。</summary>
    public async Task<CloudAgentLessons.AgentLessonView> CreateUserAgentMemoryAsync(
        string userID, AgentMemoryRequest request, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        long approved = await _repository.CountAgentLessonsByAuthorAsync(
            userID, CloudAgentLessons.StatusApproved, cancellationToken).ConfigureAwait(false);
        if (approved >= CloudAgentLessons.ApprovedMemoryMax)
        {
            throw AppError.BadAuthRequest($"已批准记忆最多 {CloudAgentLessons.ApprovedMemoryMax} 条，请先清理后再添加");
        }
        AgentLesson entry = CloudAgentLessons.BuildMemory(userID, request, CloudAgentLessons.StatusApproved);
        DateTime now = DateTime.UtcNow;
        entry.ID = IdGenerator.NewId();
        entry.CreatedAt = now;
        AgentLesson? duplicate = await CloudAgentLessons.FindDuplicateAsync(
            _repository, userID, entry, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            if (duplicate.Status != CloudAgentLessons.StatusApproved)
            {
                await _repository.SetAgentLessonStatusAsync(
                    userID, duplicate.ID, CloudAgentLessons.StatusApproved, cancellationToken).ConfigureAwait(false);
            }
            await _repository.TouchAgentLessonAsync(userID, duplicate.ID, now, cancellationToken).ConfigureAwait(false);
            AgentLesson? existing = await _repository.AgentLessonForUserAsync(userID, duplicate.ID, cancellationToken)
                .ConfigureAwait(false)
                ?? throw AppError.NotFound("记忆不存在");
            return CloudAgentLessons.ViewOf(existing);
        }
        await _repository.CreateAgentLessonAsync(entry, cancellationToken).ConfigureAwait(false);
        return CloudAgentLessons.ViewOf(entry);
    }

    /// <summary>对应 Go: <c>UpdateUserAgentMemory</c>。</summary>
    public async Task<CloudAgentLessons.AgentLessonView> UpdateUserAgentMemoryAsync(
        string userID, string id, AgentMemoryRequest request, CancellationToken cancellationToken = default)
    {
        AgentLesson? current = await _repository.AgentLessonForUserAsync(userID, id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("记忆不存在");
        AgentLesson next = CloudAgentLessons.BuildMemory(userID, request, current.Status);
        current.Topic = next.Topic;
        current.Category = next.Category;
        current.Situation = next.Situation;
        current.Lesson = next.Lesson;
        current.Source = next.Source;
        current.StepsJSON = next.StepsJSON;
        current.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveAgentLessonAsync(current, cancellationToken).ConfigureAwait(false);
        return CloudAgentLessons.ViewOf(current);
    }

    /// <summary>对应 Go: <c>DecideUserAgentMemory</c>。</summary>
    public async Task DecideUserAgentMemoryAsync(
        string userID, string id, string decision, CancellationToken cancellationToken = default)
    {
        string status = decision switch
        {
            "approve" => CloudAgentLessons.StatusApproved,
            "reject" => CloudAgentLessons.StatusRejected,
            _ => throw AppError.BadAuthRequest("无效裁决"),
        };
        if (status == CloudAgentLessons.StatusApproved)
        {
            long approved = await _repository.CountAgentLessonsByAuthorAsync(
                userID, CloudAgentLessons.StatusApproved, cancellationToken).ConfigureAwait(false);
            if (approved >= CloudAgentLessons.ApprovedMemoryMax)
            {
                throw AppError.BadAuthRequest($"已批准记忆最多 {CloudAgentLessons.ApprovedMemoryMax} 条，请先清理后再批准");
            }
        }
        try
        {
            await _repository.SetAgentLessonStatusAsync(userID, id.Trim(), status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw AppError.NotFound("记忆不存在");
        }
    }

    /// <summary>对应 Go: <c>DeleteUserAgentMemory</c>。</summary>
    public async Task DeleteUserAgentMemoryAsync(
        string userID, string id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _repository.DeleteAgentLessonAsync(userID, id.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw AppError.NotFound("记忆不存在");
        }
    }

    /// <summary>对应 Go: <c>ExportUserAgentMemories</c>。</summary>
    public async Task<AgentMemoryBundle> ExportUserAgentMemoriesAsync(
        string userID, CancellationToken cancellationToken = default)
    {
        List<AgentLesson> lessons = await _repository.UserAgentLessonsAsync(userID, "", 500, cancellationToken)
            .ConfigureAwait(false);
        List<AgentMemoryExportItem> items = [];
        foreach (AgentLesson lesson in lessons)
        {
            CloudAgentLessons.AgentLessonView view = CloudAgentLessons.ViewOf(lesson);
            items.Add(new AgentMemoryExportItem
            {
                Topic = view.Topic,
                Category = view.Category,
                Situation = view.Situation,
                Lesson = view.Lesson,
                Steps = view.Steps,
                Source = view.Source,
                Status = view.Status,
            });
        }
        return new AgentMemoryBundle
        {
            Version = 1,
            Kind = "agent-memories",
            ExportedAt = DateTime.UtcNow,
            Memories = items,
        };
    }

    /// <summary>对应 Go: <c>ImportUserAgentMemories</c>。</summary>
    public async Task<AgentMemoryImportResult> ImportUserAgentMemoriesAsync(
        string userID, AgentMemoryBundle bundle, CancellationToken cancellationToken = default)
    {
        if (userID.Trim().Length == 0)
        {
            throw AppError.Unauthorized("请先登录");
        }
        if (bundle.Kind.Length > 0 && bundle.Kind != "agent-memories")
        {
            throw AppError.BadAuthRequest("不是 Agent 记忆导出文件");
        }
        if (bundle.Memories.Count > CloudAgentLessons.ImportMemoryMax)
        {
            throw AppError.BadAuthRequest($"一次最多导入 {CloudAgentLessons.ImportMemoryMax} 条");
        }
        AgentMemoryImportResult result = new();
        foreach (AgentMemoryExportItem item in bundle.Memories)
        {
            string status = item.Status.Trim();
            if (status is not (CloudAgentLessons.StatusApproved or CloudAgentLessons.StatusRejected))
            {
                status = CloudAgentLessons.StatusApproved;
            }
            if (status == CloudAgentLessons.StatusApproved)
            {
                long approved = await _repository.CountAgentLessonsByAuthorAsync(
                    userID, CloudAgentLessons.StatusApproved, cancellationToken).ConfigureAwait(false);
                if (approved >= CloudAgentLessons.ApprovedMemoryMax)
                {
                    result.Skipped++;
                    continue;
                }
            }
            AgentLesson entry;
            try
            {
                entry = CloudAgentLessons.BuildMemory(userID, new AgentMemoryRequest
                {
                    Topic = item.Topic,
                    Category = item.Category,
                    Situation = item.Situation,
                    Lesson = item.Lesson,
                    Steps = [.. item.Steps ?? []],
                    Source = item.Source,
                }, status);
            }
            catch (AppError)
            {
                result.Skipped++;
                continue;
            }
            DateTime now = DateTime.UtcNow;
            AgentLesson? duplicate = await CloudAgentLessons.FindDuplicateAsync(
                _repository, userID, entry, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                if (status == CloudAgentLessons.StatusApproved
                    && duplicate.Status != CloudAgentLessons.StatusApproved)
                {
                    await _repository.SetAgentLessonStatusAsync(
                        userID, duplicate.ID, CloudAgentLessons.StatusApproved, cancellationToken)
                        .ConfigureAwait(false);
                }
                await _repository.TouchAgentLessonAsync(userID, duplicate.ID, now, cancellationToken)
                    .ConfigureAwait(false);
                result.Merged++;
                continue;
            }
            entry.ID = IdGenerator.NewId();
            entry.CreatedAt = now;
            await _repository.CreateAgentLessonAsync(entry, cancellationToken).ConfigureAwait(false);
            result.Imported++;
        }
        return result;
    }

    /// <summary>对应 Go: <c>AdminAgentLessons</c>。</summary>
    public async Task<List<CloudAgentLessons.AgentLessonAdminView>> AdminAgentLessonsAsync(
        string status, string userID, string keyword, int limit, CancellationToken cancellationToken = default)
    {
        List<AgentLesson> lessons = await _repository.AdminAgentLessonsAsync(
            status, userID, keyword, limit, cancellationToken).ConfigureAwait(false);
        List<string> ids = [];
        foreach (AgentLesson lesson in lessons)
        {
            if (lesson.AuthorUserID.Length > 0)
            {
                ids.Add(lesson.AuthorUserID);
            }
        }
        Dictionary<string, User> users = await _repository.UsersByIDsAsync(ids, cancellationToken).ConfigureAwait(false);
        List<CloudAgentLessons.AgentLessonAdminView> views = [];
        foreach (AgentLesson lesson in lessons)
        {
            CloudAgentLessons.AgentLessonAdminView view = CloudAgentLessons.AdminViewOf(lesson);
            if (users.TryGetValue(lesson.AuthorUserID, out User? user))
            {
                view.AuthorUsername = user.Username;
                view.AuthorDisplayName = user.DisplayName;
            }
            views.Add(view);
        }
        return views;
    }

    /// <summary>对应 Go: <c>AdminDeleteAgentLesson</c>。</summary>
    public async Task AdminDeleteAgentLessonAsync(string id, CancellationToken cancellationToken = default)
    {
        id = id.Trim();
        if (id.Length == 0)
        {
            throw AppError.BadAuthRequest("记忆 ID 无效");
        }
        try
        {
            await _repository.DeleteAgentLessonAsync("", id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            throw AppError.NotFound("记忆不存在");
        }
    }
}
