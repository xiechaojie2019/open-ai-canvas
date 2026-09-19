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
                joins.Add("LEFT JOIN user_skill_states ON user_skill_states.skill_id = skills.id AND user_skill_states.user_id = @scopeUserId");
                conditions.Add("(skills.owner_id = @scopeUserId OR (user_skill_states.added = @addedTrue AND skills.is_private = @privateFalse))");
                parameters.Add("scopeUserId", filter.UserID);
                parameters.Add("addedTrue", true);
                parameters.Add("privateFalse", false);
                break;

            case "created":
                conditions.Add("skills.owner_id = @scopeUserId");
                parameters.Add("scopeUserId", filter.UserID);
                break;

            case "favorites":
                joins.Add("JOIN user_skill_states ON user_skill_states.skill_id = skills.id AND user_skill_states.user_id = @scopeUserId AND user_skill_states.liked = @likedTrue");
                conditions.Add("(skills.owner_id = @scopeUserId OR skills.is_private = @privateFalse)");
                parameters.Add("scopeUserId", filter.UserID);
                parameters.Add("likedTrue", true);
                parameters.Add("privateFalse", false);
                break;

            default:
                conditions.Add("skills.is_private = @privateFalse");
                parameters.Add("privateFalse", false);
                break;
        }

        if (filter.Search.Length > 0)
        {
            joins.Add("LEFT JOIN users skill_owners ON skill_owners.id = skills.owner_id");
            conditions.Add("""
                (lower(skills.name) LIKE @pattern
                 OR lower(skills.description) LIKE @pattern
                 OR lower(skills.author_name) LIKE @pattern
                 OR lower(skill_owners.display_name) LIKE @pattern
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
            "new" => "skills.created_at DESC",
            "popular" => """
                (skills.initial_added_count
                 + (SELECT COUNT(*) FROM user_skill_states metric_states
                    WHERE metric_states.skill_id = skills.id AND metric_states.added = true)) DESC,
                skills.updated_at DESC
                """,
            _ => "skills.sort_weight DESC, skills.updated_at DESC",
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
            SqlBuilder.Select<UserSkillState>("user_id = @userId AND skill_id = @skillId", limitOffset: " LIMIT 1"),
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
            SqlBuilder.Select<UserSkillState>("user_id = @userId AND skill_id IN @skillIds"),
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
            ON CONFLICT ("user_id", "skill_id") DO UPDATE SET
                "added" = excluded."added",
                "installed_version_id" = excluded."installed_version_id",
                "auto_update" = excluded."auto_update",
                "updated_at" = excluded."updated_at"
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
            ON CONFLICT ("user_id", "skill_id") DO UPDATE SET
                "liked" = excluded."liked",
                "updated_at" = excluded."updated_at"
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
                skill_id AS "SkillID",
                SUM(CASE WHEN added THEN 1 ELSE 0 END) AS "AddedCount",
                SUM(CASE WHEN liked THEN 1 ELSE 0 END) AS "LikeCount"
            FROM user_skill_states
            WHERE skill_id IN @skillIds
            GROUP BY skill_id
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
                "user_id IN @ownerIds AND avatar_url <> ''", orderBy: "updated_at DESC"),
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
            connection, "DELETE FROM user_skill_states WHERE skill_id = @id", new { id }, transaction, cancellationToken)
            .ConfigureAwait(false);

        // 先删文件再删版本，避免留下孤儿文件行。
        await ExecuteAsync(
            connection,
            """
            DELETE FROM skill_files
            WHERE skill_version_id IN (SELECT id FROM skill_versions WHERE skill_id = @id)
            """,
            new { id },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, "DELETE FROM skill_versions WHERE skill_id = @id", new { id }, transaction, cancellationToken)
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
              instruction = @Instruction,
              current_version_id = @CurrentVersionID, version_label = @VersionLabel,
              content_hash = @ContentHash, file_count = @FileCount, total_bytes = @TotalBytes,
              source_type = @SourceType, source_url = @SourceURL, source_ref = @SourceRef,
              source_subdir = @SourceSubdir, source_commit = @SourceCommit,
              sync_status = @SyncStatus, sync_error = @SyncError, auto_update = @AutoUpdate,
              last_checked_at = @LastCheckedAt, last_synced_at = @LastSyncedAt,
              updated_at = @UpdatedAt
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
              tag = @Tag, is_private = @IsPrivate, markdown_url = @MarkdownURL,
              showcase_media_json = @ShowcaseMediaJSON, extra_info = @ExtraInfo,
              current_version_id = @CurrentVersionID, version_label = @VersionLabel,
              content_hash = @ContentHash, file_count = @FileCount, total_bytes = @TotalBytes,
              updated_at = @UpdatedAt
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
