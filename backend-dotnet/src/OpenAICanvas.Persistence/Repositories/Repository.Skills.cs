#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 技能库查询与用户状态。对应 Go: <c>repository/skills.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>
    /// 内置技能幂等落库（按 id 冲突更新正文字段；用户关系独立表不受影响）。
    /// 对应 Go: <c>UpsertBuiltinSkills</c> 的 OnConflict DoUpdates 语义。
    /// </summary>
    public async Task UpsertBuiltinSkillsAsync(IReadOnlyList<Skill> skills, CancellationToken cancellationToken = default)
    {
        if (skills.Count == 0)
        {
            throw new InvalidOperationException("builtin skills are empty");
        }
        // 这里是逗号分隔的裸列名列表，引号由下方 $"\"{c}\"" 拼装时统一添加，切勿在此处预先加引号。
        const string columns = "ownerId, authorName, authorAvatarUrl, name, description, instruction, "
            + "status, source, tag, sortWeight, isPrivate, markdownUrl, showcaseMediaJson, extraInfo, "
            + "initialLikeCount, initialAddedCount, createdAt, updatedAt";
        string[] columnNames = columns.Split(',').Select(c => c.Trim()).ToArray();
        await InTransactionAsync(async (connection, transaction) =>
        {
            string insert = SqlBuilder.Insert(typeof(Skill));
            string updateClause = " ON CONFLICT (\"id\") DO UPDATE SET "
                + string.Join(", ", columnNames.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));
            foreach (Skill skill in skills)
            {
                DynamicParameters parameters = SqlBuilder.Parameters(skill);
                await connection.ExecuteAsync(new CommandDefinition(
                    insert + updateClause,
                    parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>技能列表过滤条件。对应 Go: <c>repository.SkillListFilter</c>。</summary>
    public sealed class SkillListFilter
    {
        public string UserID { get; init; } = "";
        public string Scope { get; init; } = "";
        public string Search { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Sort { get; init; } = "";

        /// <summary>小于 0 表示不分页（Go 用 -1 取全部）。</summary>
        public int Limit { get; init; }
        public int Offset { get; init; }
    }

    /// <summary>技能聚合指标。对应 Go: <c>repository.SkillMetrics</c>。</summary>
    public sealed class SkillMetricRow
    {
        public string SkillID { get; set; } = "";
        public long AddedCount { get; set; }
        public long LikeCount { get; set; }
    }

    /// <summary>
    /// 技能列表。按作用域（全部 / 我的 / 我创建的 / 我收藏的）过滤，支持搜索与排序。
    /// 对应 Go: <c>Skills</c>。
    /// </summary>
    /// <remarks>
    /// 用户状态表按 <c>(user_id, skill_id)</c> 唯一，所以 JOIN 不会产生重复行；
    /// <b>刻意不加 DISTINCT</b>——Go 的注释说明它会被带进 PostgreSQL 的热门排序查询里。
    /// </remarks>
    public async Task<(IReadOnlyList<Skill> Skills, long Total)> SkillsAsync(
        SkillListFilter filter, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        List<string> joins = [];
        List<string> conditions = ["skills.status = @enabledStatus"];
        DynamicParameters parameters = new();
        parameters.Add("enabledStatus", 1L);

        switch (filter.Scope)
        {
            case "mine":
                joins.Add("LEFT JOIN \"userSkillStates\" ON \"userSkillStates\".\"skillId\" = skills.id AND \"userSkillStates\".\"userId\" = @scopeUserId");
                conditions.Add("(skills.\"ownerId\" = @scopeUserId OR (\"userSkillStates\".added = @addedTrue AND skills.\"isPrivate\" = @privateFalse))");
                parameters.Add("scopeUserId", filter.UserID);
                parameters.Add("addedTrue", true);
                parameters.Add("privateFalse", false);
                break;

            case "created":
                conditions.Add("skills.\"ownerId\" = @scopeUserId");
                parameters.Add("scopeUserId", filter.UserID);
                break;

            case "favorites":
                joins.Add("JOIN \"userSkillStates\" ON \"userSkillStates\".\"skillId\" = skills.id AND \"userSkillStates\".\"userId\" = @scopeUserId AND \"userSkillStates\".liked = @likedTrue");
                conditions.Add("(skills.\"ownerId\" = @scopeUserId OR skills.\"isPrivate\" = @privateFalse)");
                parameters.Add("scopeUserId", filter.UserID);
                parameters.Add("likedTrue", true);
                parameters.Add("privateFalse", false);
                break;

            default:
                conditions.Add("skills.\"isPrivate\" = @privateFalse");
                parameters.Add("privateFalse", false);
                break;
        }

        if (filter.Search.Length > 0)
        {
            joins.Add("LEFT JOIN users skill_owners ON skill_owners.id = skills.\"ownerId\"");
            conditions.Add("""
                (lower(skills.name) LIKE @pattern
                 OR lower(skills.description) LIKE @pattern
                 OR lower(skills."authorName") LIKE @pattern
                 OR lower(skill_owners."displayName") LIKE @pattern
                 OR lower(skill_owners.username) LIKE @pattern)
                """);
            parameters.Add("pattern", "%" + filter.Search.ToLowerInvariant() + "%");
        }

        if (filter.Tag.Length > 0)
        {
            conditions.Add("skills.tag = @tag");
            parameters.Add("tag", filter.Tag);
        }

        string joinSql = joins.Count == 0 ? "" : " " + string.Join(" ", joins);
        string whereSql = " WHERE " + string.Join(" AND ", conditions);

        long total = await ScalarAsync<long>(
            connection, $"SELECT COUNT(*) FROM skills{joinSql}{whereSql}", parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 热门排序用「内置初始值 + 实时加入数」，与 Go 的子查询写法一致。
        string order = filter.Sort switch
        {
            "new" => "skills.\"createdAt\" DESC",
            "popular" => """
                (skills."initialAddedCount"
                 + (SELECT COUNT(*) FROM "userSkillStates" metric_states
                    WHERE metric_states."skillId" = skills.id AND metric_states.added = true)) DESC,
                skills."updatedAt" DESC
                """,
            _ => "skills.\"sortWeight\" DESC, skills.\"updatedAt\" DESC",
        };

        string limitSql = filter.Limit < 0
            ? ""
            : Dialect.LimitOffset(filter.Limit, filter.Offset);

        IReadOnlyList<Skill> skills = await QueryAsync<Skill>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<Skill>("skills")}
            FROM skills{joinSql}{whereSql}
            ORDER BY {order}
            {limitSql}
            """,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (skills, total);
    }

    /// <summary>按 ID 查启用中的技能。对应 Go: <c>Skill</c>。</summary>
    public async Task<Skill?> SkillAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Skill>(
            connection,
            SqlBuilder.Select<Skill>("id = @id AND status = @status", limitOffset: " LIMIT 1"),
            new { id, status = 1L },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 用户状态

    /// <summary>查用户对某技能的状态；不存在返回 null。对应 Go: <c>UserSkillState</c>。</summary>
    public async Task<UserSkillState?> UserSkillStateAsync(
        string userId, string skillId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<UserSkillState>(
            connection,
            SqlBuilder.Select<UserSkillState>("\"userId\" = @userId AND \"skillId\" = @skillId", limitOffset: " LIMIT 1"),
            new { userId, skillId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>批量查用户状态。对应 Go: <c>UserSkillStatesBySkillIDs</c>。</summary>
    public async Task<IReadOnlyList<UserSkillState>> UserSkillStatesBySkillIDsAsync(
        string userId, IReadOnlyList<string> skillIds, CancellationToken cancellationToken = default)
    {
        if (skillIds.Count == 0)
        {
            return [];
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<UserSkillState>(
            connection,
            SqlBuilder.Select<UserSkillState>("\"userId\" = @userId AND \"skillId\" IN @skillIds"),
            new { userId, skillIds },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入「加入」状态（upsert，只覆盖加入相关列）。
    /// 对应 Go: <c>SetUserSkillAdded</c>。
    /// </summary>
    public async Task SetUserSkillAddedAsync(UserSkillState state, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            {SqlBuilder.Insert<UserSkillState>()}
            ON CONFLICT ("userId", "skillId") DO UPDATE SET
                "added" = excluded."added",
                "installedVersionId" = excluded."installedVersionId",
                "autoUpdate" = excluded."autoUpdate",
                "updatedAt" = excluded."updatedAt"
            """,
            state,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入「收藏」状态（upsert，只覆盖收藏列）。
    /// 对应 Go: <c>SetUserSkillLiked</c>。
    /// </summary>
    public async Task SetUserSkillLikedAsync(UserSkillState state, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            {SqlBuilder.Insert<UserSkillState>()}
            ON CONFLICT ("userId", "skillId") DO UPDATE SET
                "liked" = excluded."liked",
                "updatedAt" = excluded."updatedAt"
            """,
            state,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 聚合

    /// <summary>按技能统计加入数与收藏数。对应 Go: <c>SkillMetrics</c>。</summary>
    public async Task<Dictionary<string, SkillMetricRow>> SkillMetricsAsync(
        IReadOnlyList<string> skillIds, CancellationToken cancellationToken = default)
    {
        Dictionary<string, SkillMetricRow> metrics = new(StringComparer.Ordinal);
        if (skillIds.Count == 0)
        {
            return metrics;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SkillMetricRow> rows = await QueryAsync<SkillMetricRow>(
            connection,
            """
            SELECT
                "skillId" AS "SkillID",
                SUM(CASE WHEN added THEN 1 ELSE 0 END) AS "AddedCount",
                SUM(CASE WHEN liked THEN 1 ELSE 0 END) AS "LikeCount"
            FROM "userSkillStates"
            WHERE "skillId" IN @skillIds
            GROUP BY "skillId"
            """,
            new { skillIds },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (SkillMetricRow row in rows)
        {
            metrics[row.SkillID] = row;
        }
        return metrics;
    }

    /// <summary>批量取技能作者。对应 Go: <c>SkillOwners</c>。</summary>
    public async Task<Dictionary<string, User>> SkillOwnersAsync(
        IReadOnlyList<string> ownerIds, CancellationToken cancellationToken = default)
    {
        Dictionary<string, User> owners = new(StringComparer.Ordinal);
        if (ownerIds.Count == 0)
        {
            return owners;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (User user in await QueryAsync<User>(
            connection,
            SqlBuilder.Select<User>("id IN @ownerIds"),
            new { ownerIds },
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            owners[user.ID] = user;
        }
        return owners;
    }

    /// <summary>
    /// 批量取作者头像（取最近更新的第三方身份头像）。
    /// 对应 Go: <c>SkillOwnerAvatars</c>。
    /// </summary>
    public async Task<Dictionary<string, string>> SkillOwnerAvatarsAsync(
        IReadOnlyList<string> ownerIds, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> avatars = new(StringComparer.Ordinal);
        if (ownerIds.Count == 0)
        {
            return avatars;
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UserIdentity> identities = await QueryAsync<UserIdentity>(
            connection,
            SqlBuilder.Select<UserIdentity>(
                "\"userId\" IN @ownerIds AND \"avatarUrl\" <> ''", orderBy: "\"updatedAt\" DESC"),
            new { ownerIds },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (UserIdentity identity in identities)
        {
            // 按 updated_at 降序，首次出现即最新头像。
            if (!avatars.ContainsKey(identity.UserID))
            {
                avatars[identity.UserID] = identity.AvatarURL;
            }
        }
        return avatars;
    }

    // ---------------------------------------------------------------- 删除

    /// <summary>
    /// 删除技能及其状态、版本、文件。对应 Go: <c>DeleteSkill</c>。
    /// </summary>
    public async Task DeleteSkillAsync(string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, "DELETE FROM \"userSkillStates\" WHERE \"skillId\" = @id", new { id }, transaction, cancellationToken)
            .ConfigureAwait(false);

        // 先删文件再删版本，避免留下孤儿文件行。
        await ExecuteAsync(
            connection,
            """
            DELETE FROM "skillFiles"
            WHERE "skillVersionId" IN (SELECT id FROM "skillVersions" WHERE "skillId" = @id)
            """,
            new { id },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, "DELETE FROM \"skillVersions\" WHERE \"skillId\" = @id", new { id }, transaction, cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection, "DELETE FROM skills WHERE id = @id", new { id }, transaction, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建技能与包（四写同事务）。对应 Go: <c>CreateSkillWithPackage</c>。</summary>
    public async Task CreateSkillWithPackageAsync(
        Skill skill,
        SkillVersion version,
        IReadOnlyList<SkillFile> files,
        UserSkillState ownerState,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlBuilder.Insert<Skill>(), skill, transaction, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlBuilder.Insert<SkillVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        foreach (SkillFile file in files)
        {
            await ExecuteAsync(connection, SqlBuilder.Insert<SkillFile>(), file, transaction, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, SqlBuilder.Insert<UserSkillState>(), ownerState, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>追加技能版本（版本+文件+技能保存同事务）。对应 Go: <c>AddSkillVersion</c>。</summary>
    public async Task AddSkillVersionAsync(
        Skill skill,
        SkillVersion version,
        IReadOnlyList<SkillFile> files,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqlBuilder.Insert<SkillVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        foreach (SkillFile file in files)
        {
            await ExecuteAsync(connection, SqlBuilder.Insert<SkillFile>(), file, transaction, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteAsync(
            connection,
            """
            UPDATE skills SET
              name = @Name, description = @Description, instruction = @Instruction,
              tag = @Tag, "markdownUrl" = @MarkdownURL,
              "showcaseMediaJson" = @ShowcaseMediaJSON, "extraInfo" = @ExtraInfo,
              "currentVersionId" = @CurrentVersionID, "versionLabel" = @VersionLabel,
              "contentHash" = @ContentHash, "fileCount" = @FileCount, "totalBytes" = @TotalBytes,
              "sourceType" = @SourceType, "sourceUrl" = @SourceURL, "sourceRef" = @SourceRef,
              "sourceSubdir" = @SourceSubdir, "sourceCommit" = @SourceCommit,
              "syncStatus" = @SyncStatus, "syncError" = @SyncError, "autoUpdate" = @AutoUpdate,
              "lastCheckedAt" = @LastCheckedAt, "lastSyncedAt" = @LastSyncedAt,
              "updatedAt" = @UpdatedAt
            WHERE id = @ID
            """,
            skill,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保存技能全量字段。对应 Go: <c>SaveSkill</c>（GORM Save）。</summary>
    public async Task SaveSkillAsync(Skill skill, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE skills SET
              name = @Name, description = @Description, instruction = @Instruction,
              tag = @Tag, "markdownUrl" = @MarkdownURL,
              "showcaseMediaJson" = @ShowcaseMediaJSON, "extraInfo" = @ExtraInfo,
              "currentVersionId" = @CurrentVersionID, "versionLabel" = @VersionLabel,
              "contentHash" = @ContentHash, "fileCount" = @FileCount, "totalBytes" = @TotalBytes,
              "sourceType" = @SourceType, "sourceUrl" = @SourceURL, "sourceRef" = @SourceRef,
              "sourceSubdir" = @SourceSubdir, "sourceCommit" = @SourceCommit,
              "syncStatus" = @SyncStatus, "syncError" = @SyncError, "autoUpdate" = @AutoUpdate,
              "lastCheckedAt" = @LastCheckedAt, "lastSyncedAt" = @LastSyncedAt,
              status = @Status, source = @Source, "isPrivate" = @IsPrivate,
              "updatedAt" = @UpdatedAt
            WHERE id = @ID
            """,
            skill,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert<Skill>(), skill, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

}
