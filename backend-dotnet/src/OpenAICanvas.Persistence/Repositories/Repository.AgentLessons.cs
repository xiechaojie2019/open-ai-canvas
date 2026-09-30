using System.Data.Common;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// Agent 个人记忆（经验教训）仓储。对应 Go: <c>repository/agent_lesson.go</c>。
/// 记忆是用户私有的；只有 approved 状态会注入会话。
/// InTx 变体供云 Agent 检查点事务内的工具执行使用。
/// </summary>
public sealed partial class Repository
{
    private const string AgentLessonApproved = "approved";

    // ------------------------------------------------------------- 普通入口

    /// <summary>按分类统计已批准记忆。对应 Go: <c>ApprovedAgentLessonCategoryCounts</c>。</summary>
    public Task<List<AgentLessonCategoryCountRow>> ApprovedAgentLessonCategoryCountsAsync(
        string userID, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            ApprovedAgentLessonCategoryCountsCoreAsync(connection, transaction, userID, cancellationToken));

    /// <summary>用户记忆列表（可按状态过滤）。对应 Go: <c>UserAgentLessons</c>。</summary>
    public Task<List<AgentLesson>> UserAgentLessonsAsync(
        string userID, string status, int limit, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            UserAgentLessonsCoreAsync(connection, transaction, userID, status, limit, cancellationToken));

    /// <summary>按 ID 取用户记忆（不限状态）。对应 Go: <c>AgentLessonForUser</c>。</summary>
    public Task<AgentLesson?> AgentLessonForUserAsync(
        string userID, string id, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            AgentLessonForUserCoreAsync(connection, transaction, userID, id, cancellationToken));

    /// <summary>已批准记忆（hits 优先）。对应 Go: <c>ApprovedAgentLessons</c>。</summary>
    public Task<List<AgentLesson>> ApprovedAgentLessonsAsync(
        string userID, int limit, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            ApprovedAgentLessonsCoreAsync(connection, transaction, userID, limit, cancellationToken));

    /// <summary>按主题取已批准记忆。对应 Go: <c>AgentLessonByTopic</c>。</summary>
    public Task<AgentLesson?> AgentLessonByTopicAsync(
        string userID, string topic, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            AgentLessonByTopicCoreAsync(connection, transaction, userID, topic, cancellationToken));

    /// <summary>按分类取已批准记忆。对应 Go: <c>AgentLessonsByCategory</c>。</summary>
    public Task<List<AgentLesson>> AgentLessonsByCategoryAsync(
        string userID, string category, int limit, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            AgentLessonsByCategoryCoreAsync(connection, transaction, userID, category, limit, cancellationToken));

    /// <summary>按作者与状态计数。对应 Go: <c>CountAgentLessonsByAuthor</c>。</summary>
    public Task<long> CountAgentLessonsByAuthorAsync(
        string userID, string status, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            CountAgentLessonsByAuthorCoreAsync(connection, transaction, userID, status, cancellationToken));

    /// <summary>管理端记忆列表（可按状态/作者/关键词过滤）。对应 Go: <c>AdminAgentLessons</c>。</summary>
    public Task<List<AgentLesson>> AdminAgentLessonsAsync(
        string status, string userID, string keyword, int limit, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            AdminAgentLessonsCoreAsync(connection, transaction, status, userID, keyword, limit, cancellationToken));

    /// <summary>更新状态；未命中报 record not found。对应 Go: <c>SetAgentLessonStatus</c>。</summary>
    public Task SetAgentLessonStatusAsync(
        string userID, string id, string status, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            int affected = await SetAgentLessonStatusCoreAsync(
                connection, transaction, userID, id, status, cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("record not found");
            }
            return true;
        });

    /// <summary>删除记忆；未命中报 record not found。对应 Go: <c>DeleteAgentLesson</c>。</summary>
    public Task DeleteAgentLessonAsync(
        string userID, string id, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            string sql = $"DELETE FROM {Quote("agentLessons")} WHERE {Quote("id")} = @id";
            Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["id"] = id.Trim() };
            if (userID.Trim().Length > 0)
            {
                sql += $" AND {Quote("authorUserId")} = @userID";
                parameters["userID"] = userID.Trim();
            }
            int affected = await ExecuteAsync(connection, sql, parameters, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("record not found");
            }
            return true;
        });

    /// <summary>GORM Save 语义：按主键 UPDATE，未命中转 INSERT。对应 Go: <c>SaveAgentLesson</c>。</summary>
    public Task SaveAgentLessonAsync(AgentLesson lesson, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            int affected = await ExecuteAsync(
                connection,
                SqlBuilder.Update(typeof(AgentLesson)),
                SqlBuilder.Parameters(lesson),
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                await ExecuteAsync(
                    connection,
                    SqlBuilder.Insert(typeof(AgentLesson)),
                    SqlBuilder.Parameters(lesson),
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            return true;
        });

    /// <summary>创建记忆。对应 Go: <c>repo.Create(entry)</c>。</summary>
    public Task CreateAgentLessonAsync(AgentLesson lesson, CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert<AgentLesson>(),
                SqlBuilder.Parameters(lesson),
                transaction,
                cancellationToken).ConfigureAwait(false);
            return true;
        });

    /// <summary>命中计数 +1。对应 Go: <c>BumpAgentLessonHits</c>。</summary>
    public Task BumpAgentLessonHitsAsync(
        string userID, IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            BumpAgentLessonHitsCoreAsync(connection, transaction, userID, ids, cancellationToken));

    /// <summary>更新验证时间。对应 Go: <c>TouchAgentLesson</c>。</summary>
    public Task TouchAgentLessonAsync(
        string userID, string id, DateTime verifiedAt, CancellationToken cancellationToken = default) =>
        InTransactionAsync((connection, transaction) =>
            TouchAgentLessonCoreAsync(connection, transaction, userID, id, verifiedAt, cancellationToken));

    /// <summary>事务内批量应用压缩计划（更新 + 删除）。对应 Go: <c>ApplyUserAgentMemoryCompact</c>。</summary>
    public Task ApplyUserAgentMemoryCompactAsync(
        string userID, IReadOnlyList<AgentLesson> updates, IReadOnlyList<string> deleteIDs,
        CancellationToken cancellationToken = default) =>
        InTransactionAsync(async (connection, transaction) =>
        {
            userID = userID.Trim();
            if (userID.Length == 0)
            {
                throw new InvalidOperationException("record not found");
            }
            foreach (AgentLesson lesson in updates)
            {
                if (lesson.AuthorUserID.Trim() != userID)
                {
                    throw new InvalidOperationException("record not found");
                }
                int affected = await ExecuteAsync(
                    connection,
                    SqlBuilder.Update(typeof(AgentLesson)),
                    SqlBuilder.Parameters(lesson),
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    await ExecuteAsync(
                        connection,
                        SqlBuilder.Insert(typeof(AgentLesson)),
                        SqlBuilder.Parameters(lesson),
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            if (deleteIDs.Count > 0)
            {
                await ExecuteAsync(
                    connection,
                    $"DELETE FROM {Quote("agentLessons")} WHERE {Quote("authorUserId")} = @userID AND {Quote("id")} IN @ids",
                    new { userID, ids = deleteIDs },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            return true;
        });

    // ------------------------------------------------------- 记忆压缩设置

    /// <summary>取用户压缩设置。对应 Go: <c>AgentMemorySetting</c>（First，找不到报错）。</summary>
    public async Task<AgentMemorySetting?> AgentMemorySettingAsync(
        string userID, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AgentMemorySetting>(
            connection,
            SqlBuilder.Select<AgentMemorySetting>($"{Quote("userId")} = @userID", limitOffset: " LIMIT 1"),
            new { userID = userID.Trim() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GORM Save 语义的设置保存。对应 Go: <c>SaveAgentMemorySetting</c>。</summary>
    public async Task SaveAgentMemorySettingAsync(
        AgentMemorySetting setting, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await ExecuteAsync(
            connection,
            SqlBuilder.Update(typeof(AgentMemorySetting)),
            SqlBuilder.Parameters(setting),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert(typeof(AgentMemorySetting)),
                SqlBuilder.Parameters(setting),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>到期的定时压缩设置。对应 Go: <c>ScheduledAgentMemorySettings</c>。</summary>
    public async Task<List<AgentMemorySetting>> ScheduledAgentMemorySettingsAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || limit > 200)
        {
            limit = 50;
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<AgentMemorySetting> rows = (await QueryAsync<AgentMemorySetting>(
            connection,
            SqlBuilder.Select<AgentMemorySetting>(
                $"{Quote("compactInterval")} IN @intervals",
                orderBy: Quote("lastCompactAt") + " ASC",
                limitOffset: $" LIMIT {limit}"),
            new
            {
                intervals = new[] { "daily", "weekly", "monthly" },
            },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return rows;
    }

    // ---------------------------------------------------------- 事务内入口
    // 云 Agent 检查点事务内的工具执行（recall/remember）使用。

    public Task<List<AgentLessonCategoryCountRow>> ApprovedAgentLessonCategoryCountsInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID,
        CancellationToken cancellationToken = default) =>
        ApprovedAgentLessonCategoryCountsCoreAsync(connection, transaction, userID, cancellationToken);

    public Task<List<AgentLesson>> UserAgentLessonsInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string status, int limit,
        CancellationToken cancellationToken = default) =>
        UserAgentLessonsCoreAsync(connection, transaction, userID, status, limit, cancellationToken);

    public Task<AgentLesson?> AgentLessonByTopicInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string topic,
        CancellationToken cancellationToken = default) =>
        AgentLessonByTopicCoreAsync(connection, transaction, userID, topic, cancellationToken);

    public Task<List<AgentLesson>> ApprovedAgentLessonsInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, int limit,
        CancellationToken cancellationToken = default) =>
        ApprovedAgentLessonsCoreAsync(connection, transaction, userID, limit, cancellationToken);

    public Task<List<AgentLesson>> AgentLessonsByCategoryInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string category, int limit,
        CancellationToken cancellationToken = default) =>
        AgentLessonsByCategoryCoreAsync(connection, transaction, userID, category, limit, cancellationToken);

    public Task<long> CountAgentLessonsByAuthorInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string status,
        CancellationToken cancellationToken = default) =>
        CountAgentLessonsByAuthorCoreAsync(connection, transaction, userID, status, cancellationToken);

    public Task BumpAgentLessonHitsInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        BumpAgentLessonHitsCoreAsync(connection, transaction, userID, ids, cancellationToken);

    public Task TouchAgentLessonInTxAsync(
        DbConnection connection, DbTransaction transaction, string userID, string id, DateTime verifiedAt,
        CancellationToken cancellationToken = default) =>
        TouchAgentLessonCoreAsync(connection, transaction, userID, id, verifiedAt, cancellationToken);

    public Task CreateAgentLessonInTxAsync(
        DbConnection connection, DbTransaction transaction, AgentLesson lesson,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            connection,
            SqlBuilder.Insert<AgentLesson>(),
            SqlBuilder.Parameters(lesson),
            transaction,
            cancellationToken);

    // ------------------------------------------------------------- 核心实现

    private async Task<List<AgentLessonCategoryCountRow>> ApprovedAgentLessonCategoryCountsCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, CancellationToken cancellationToken)
    {
        string sql = $"SELECT {Quote("category")} AS {Quote("Category")}, COUNT(1) AS {Quote("Count")} " +
                     $"FROM {Quote("agentLessons")} " +
                     $"WHERE {Quote("authorUserId")} = @userID AND {Quote("status")} = @status " +
                     $"GROUP BY {Quote("category")}";
        List<AgentLessonCategoryCountRow> rows = (await QueryAsync<AgentLessonCategoryCountRow>(
            connection, sql, new { userID = userID.Trim(), status = AgentLessonApproved },
            transaction, cancellationToken).ConfigureAwait(false)).ToList();
        return rows;
    }

    private async Task<List<AgentLesson>> UserAgentLessonsCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string status, int limit,
        CancellationToken cancellationToken)
    {
        string where = $"{Quote("authorUserId")} = @userID";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["userID"] = userID.Trim() };
        if (status.Trim().Length > 0)
        {
            where += $" AND {Quote("status")} = @status";
            parameters["status"] = status.Trim();
        }
        if (limit <= 0 || limit > 500)
        {
            limit = 100;
        }
        string sql = SqlBuilder.Select<AgentLesson>(where, orderBy: $"{Quote("updatedAt")} DESC",
            limitOffset: $" LIMIT {limit}");
        List<AgentLesson> lessons = (await QueryAsync<AgentLesson>(
            connection, sql, parameters, transaction, cancellationToken).ConfigureAwait(false)).ToList();
        return lessons;
    }

    private async Task<AgentLesson?> AgentLessonForUserCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string id,
        CancellationToken cancellationToken)
    {
        string where = $"{Quote("id")} = @id";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["id"] = id.Trim() };
        if (userID.Trim().Length > 0)
        {
            where += $" AND {Quote("authorUserId")} = @userID";
            parameters["userID"] = userID.Trim();
        }
        return await FirstOrDefaultAsync<AgentLesson>(
            connection,
            SqlBuilder.Select<AgentLesson>(where, limitOffset: " LIMIT 1"),
            parameters,
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AgentLesson>> ApprovedAgentLessonsCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0 || limit > 500)
        {
            limit = 500;
        }
        string where = $"{Quote("authorUserId")} = @userID AND {Quote("status")} = @status";
        string sql = SqlBuilder.Select<AgentLesson>(where, orderBy: $"{Quote("hits")} DESC, {Quote("updatedAt")} DESC",
            limitOffset: $" LIMIT {limit}");
        List<AgentLesson> lessons = (await QueryAsync<AgentLesson>(
            connection, sql, new { userID = userID.Trim(), status = AgentLessonApproved },
            transaction, cancellationToken).ConfigureAwait(false)).ToList();
        return lessons;
    }

    private async Task<AgentLesson?> AgentLessonByTopicCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string topic,
        CancellationToken cancellationToken)
    {
        string where = $"{Quote("authorUserId")} = @userID AND {Quote("status")} = @status AND {Quote("topic")} = @topic";
        return await FirstOrDefaultAsync<AgentLesson>(
            connection,
            SqlBuilder.Select<AgentLesson>(where, orderBy: $"{Quote("updatedAt")} DESC", limitOffset: " LIMIT 1"),
            new { userID = userID.Trim(), status = AgentLessonApproved, topic },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AgentLesson>> AgentLessonsByCategoryCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string category, int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0 || limit > 500)
        {
            limit = 500;
        }
        string where = $"{Quote("authorUserId")} = @userID AND {Quote("status")} = @status AND {Quote("category")} = @category";
        string sql = SqlBuilder.Select<AgentLesson>(where, orderBy: $"{Quote("hits")} DESC, {Quote("updatedAt")} DESC",
            limitOffset: $" LIMIT {limit}");
        List<AgentLesson> lessons = (await QueryAsync<AgentLesson>(
            connection, sql, new { userID = userID.Trim(), status = AgentLessonApproved, category },
            transaction, cancellationToken).ConfigureAwait(false)).ToList();
        return lessons;
    }

    private async Task<long> CountAgentLessonsByAuthorCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string status,
        CancellationToken cancellationToken)
    {
        string where = $"{Quote("authorUserId")} = @userID";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["userID"] = userID.Trim() };
        if (status.Trim().Length > 0)
        {
            where += $" AND {Quote("status")} = @status";
            parameters["status"] = status.Trim();
        }
        string sql = $"SELECT COUNT(*) FROM {Quote("agentLessons")} WHERE {where}";
        long? count = await ScalarAsync<long?>(
            connection, sql, parameters, transaction, cancellationToken).ConfigureAwait(false);
        return count ?? 0;
    }

    private async Task<List<AgentLesson>> AdminAgentLessonsCoreAsync(
        DbConnection connection, DbTransaction? transaction, string status, string userID, string keyword, int limit,
        CancellationToken cancellationToken)
    {
        string where = "1 = 1";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal);
        if (status.Trim().Length > 0)
        {
            where += $" AND {Quote("status")} = @status";
            parameters["status"] = status.Trim();
        }
        if (userID.Trim().Length > 0)
        {
            where += $" AND {Quote("authorUserId")} = @userID";
            parameters["userID"] = userID.Trim();
        }
        if (keyword.Trim().Length > 0)
        {
            string like = "%" + keyword.Trim() + "%";
            where += $" AND ({Quote("topic")} LIKE @like OR {Quote("situation")} LIKE @like OR {Quote("lesson")} LIKE @like " +
                     $"OR {Quote("authorUserId")} LIKE @like OR {Quote("authorUserId")} IN " +
                     $"(SELECT {Quote("id")} FROM {Quote("users")} WHERE {Quote("username")} LIKE @like OR {Quote("displayName")} LIKE @like))";
            parameters["like"] = like;
        }
        if (limit <= 0 || limit > 200)
        {
            limit = 100;
        }
        string sql = SqlBuilder.Select<AgentLesson>(where, orderBy: $"{Quote("updatedAt")} DESC",
            limitOffset: $" LIMIT {limit}");
        List<AgentLesson> lessons = (await QueryAsync<AgentLesson>(
            connection, sql, parameters, transaction, cancellationToken).ConfigureAwait(false)).ToList();
        return lessons;
    }

    private Task<int> SetAgentLessonStatusCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string id, string status,
        CancellationToken cancellationToken)
    {
        string sql = $"UPDATE {Quote("agentLessons")} SET {Quote("status")} = @status WHERE {Quote("id")} = @id";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
        {
            ["status"] = status,
            ["id"] = id.Trim(),
        };
        if (userID.Trim().Length > 0)
        {
            sql += $" AND {Quote("authorUserId")} = @userID";
            parameters["userID"] = userID.Trim();
        }
        return ExecuteAsync(connection, sql, parameters, transaction, cancellationToken);
    }

    private Task BumpAgentLessonHitsCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }
        string sql = $"UPDATE {Quote("agentLessons")} SET {Quote("hits")} = {Quote("hits")} + 1 WHERE {Quote("id")} IN @ids";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal) { ["ids"] = ids };
        if (userID.Trim().Length > 0)
        {
            sql += $" AND {Quote("authorUserId")} = @userID";
            parameters["userID"] = userID.Trim();
        }
        return ExecuteAsync(connection, sql, parameters, transaction, cancellationToken);
    }

    private Task TouchAgentLessonCoreAsync(
        DbConnection connection, DbTransaction? transaction, string userID, string id, DateTime verifiedAt,
        CancellationToken cancellationToken)
    {
        string sql = $"UPDATE {Quote("agentLessons")} SET {Quote("lastVerifiedAt")} = @verifiedAt, " +
                     $"{Quote("updatedAt")} = @verifiedAt WHERE {Quote("id")} = @id";
        Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
        {
            ["verifiedAt"] = verifiedAt,
            ["id"] = id.Trim(),
        };
        if (userID.Trim().Length > 0)
        {
            sql += $" AND {Quote("authorUserId")} = @userID";
            parameters["userID"] = userID.Trim();
        }
        return ExecuteAsync(connection, sql, parameters, transaction, cancellationToken);
    }
}

/// <summary>分类计数行。对应 Go: <c>repository.AgentLessonCategoryCount</c>。</summary>
public sealed record AgentLessonCategoryCountRow(string Category, long Count);
