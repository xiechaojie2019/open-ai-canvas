#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 分镜、镜头资产引用与资产候选。对应 Go: <c>repository/repository.go</c>
/// 的 shots/candidates 部分与 <c>repository/project_workbench_read.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>项目内可引用的资产版本（JOIN 链接表）。对应 Go: <c>AssetVersionForProject</c>。</summary>
    public async Task<AssetVersion?> AssetVersionForProjectAsync(
        string projectId, string versionId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<AssetVersion>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<AssetVersion>("asset_versions")}
            FROM asset_versions
            JOIN project_asset_links ON project_asset_links.asset_id = asset_versions.asset_id
            WHERE project_asset_links.project_id = @projectId AND asset_versions.id = @versionId
            LIMIT 1
            """,
            new { projectId, versionId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目内的镜头。对应 Go: <c>ShotForProject</c>。</summary>
    public async Task<Shot?> ShotForProjectAsync(
        string projectId, string shotId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("id = @shotId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { shotId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按 ID 查项目资产候选。对应 Go: <c>ProjectAssetCandidate</c>。</summary>
    public async Task<ProjectAssetCandidate?> ProjectAssetCandidateAsync(
        string projectId, string candidateId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<ProjectAssetCandidate>(
            connection,
            SqlBuilder.Select<ProjectAssetCandidate>(
                "id = @candidateId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { candidateId, projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>项目全部候选（创建时间升序）。对应 Go: <c>ProjectAssetCandidates</c>。</summary>
    public async Task<IReadOnlyList<ProjectAssetCandidate>> ProjectAssetCandidatesAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectAssetCandidate>(
            connection,
            SqlBuilder.Select<ProjectAssetCandidate>("project_id = @projectId", "created_at ASC"),
            new { projectId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建候选（任意冲突跳过）。对应 Go: <c>CreateProjectAssetCandidate</c>。</summary>
    /// <returns>是否真的插入了新行。</returns>
    public async Task<bool> CreateProjectAssetCandidateAsync(
        ProjectAssetCandidate candidate, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int inserted = await ExecuteAsync(
            connection,
            SqlBuilder.Insert<ProjectAssetCandidate>() + Dialect.OnConflictDoNothing(""),
            candidate,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    /// <summary>候选分页（unitId/status/category/名称包含过滤）。对应 Go: <c>ProjectAssetCandidatesPage</c>。</summary>
    public async Task<(IReadOnlyList<ProjectAssetCandidate> Candidates, long Total)> ProjectAssetCandidatesPageAsync(
        string projectId, long page, long pageSize, string unitId, string status, string category, string query,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string> { "project_id = @projectId" };
        if (!string.IsNullOrWhiteSpace(unitId))
        {
            conditions.Add("unit_id = @unitId");
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            conditions.Add("status = @status");
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            conditions.Add("category = @category");
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add("LOWER(name) LIKE @queryPattern");
        }

        string where = string.Join(" AND ", conditions);
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            $"SELECT COUNT(*) FROM \"project_asset_candidates\" WHERE {where}",
            new
            {
                projectId,
                unitId = unitId.Trim(),
                status = status.Trim(),
                category = category.Trim(),
                queryPattern = "%" + query.Trim().ToLowerInvariant() + "%",
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetCandidate> candidates = await QueryAsync<ProjectAssetCandidate>(
            connection,
            SqlBuilder.Select<ProjectAssetCandidate>(where, "created_at DESC")
            + " LIMIT @limit OFFSET @offset",
            new
            {
                projectId,
                unitId = unitId.Trim(),
                status = status.Trim(),
                category = category.Trim(),
                queryPattern = "%" + query.Trim().ToLowerInvariant() + "%",
                offset = (page - 1) * pageSize,
                limit = pageSize,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (candidates, total);
    }

    /// <summary>
    /// 原子保存镜头当前值与新版本，并失效下游工件/工作流步骤。对应 Go: <c>SaveShotWithRevision</c>。
    /// </summary>
    /// <remarks>revision.Version 在事务内取 MAX(version)+1 后回写。</remarks>
    public async Task SaveShotWithRevisionAsync(
        Shot shot, ShotRevision revision, bool create, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (create)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<Shot>(), shot, transaction, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int updated = await ExecuteAsync(
                connection,
                """
                UPDATE shots SET
                    unit_id = @UnitID, title = @Title, description = @Description, position = @Position,
                    duration_ms = @DurationMs, status = @Status, updated_at = @UpdatedAt
                WHERE id = @ID AND project_id = @ProjectID
                """,
                shot,
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (updated != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("record not found");
            }
        }

        long currentVersion = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(\"version\"), 0) FROM \"shot_revisions\" WHERE \"shot_id\" = @shotId",
            new { shotId = shot.ID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        revision.Version = currentVersion + 1;
        await ExecuteAsync(
            connection, SqlBuilder.Insert<ShotRevision>(), revision, transaction, cancellationToken).ConfigureAwait(false);

        shot.CurrentRevisionID = revision.ID;
        await ExecuteAsync(
            connection,
            """
            UPDATE shots SET current_revision_id = @CurrentRevisionID, description = @Description,
              duration_ms = @DurationMs, status = @Status, updated_at = @UpdatedAt
            WHERE id = @ID AND project_id = @ProjectID
            """,
            shot,
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (!create)
        {
            await MarkShotArtifactsStaleAsync(
                connection, transaction, shot.ID, shot.UpdatedAt, cancellationToken).ConfigureAwait(false);
        }

        await InvalidateUnitWorkflowAsync(
            connection, transaction, shot.ProjectID, shot.UnitID, "storyboard", shot.UpdatedAt,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt = shot.UpdatedAt, projectId = shot.ProjectID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 章节级整体替换镜头：可选乐观锁校验后级联清空并重建。对应 Go: <c>ReplaceProjectUnitShots</c>。
    /// </summary>
    /// <remarks>expectedShotIDs 非空且与当前不一致时抛 400（对应 ErrProjectUnitShotsChanged 的投影）。</remarks>
    public async Task ReplaceProjectUnitShotsAsync(
        string projectId,
        string unitId,
        IReadOnlyList<Shot> shots,
        IReadOnlyList<ShotRevision> revisions,
        IReadOnlyList<ShotAssetReference> references,
        IReadOnlyList<string>? expectedShotIds,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (expectedShotIds is not null)
        {
            IReadOnlyList<string> currentShotIds = await QueryAsync<string>(
                connection,
                "SELECT \"id\" FROM \"shots\" WHERE \"project_id\" = @projectId AND \"unit_id\" = @unitId ORDER BY \"id\" ASC",
                new { projectId, unitId },
                transaction,
                cancellationToken).ConfigureAwait(false);
            List<string> expected = [.. expectedShotIds];
            expected.Sort(StringComparer.Ordinal);
            if (!currentShotIds.SequenceEqual(expected, StringComparer.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw AppError.BadAuthRequest("本章分镜已发生变化，请刷新后重新确认");
            }
        }

        await ExecuteAsync(
            connection,
            "DELETE FROM shot_artifacts WHERE project_id = @projectId AND unit_id = @unitId",
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            DELETE FROM shot_revisions WHERE shot_id IN (
              SELECT id FROM shots WHERE project_id = @projectId AND unit_id = @unitId)
            """,
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            DELETE FROM shot_asset_references WHERE shot_id IN (
              SELECT id FROM shots WHERE project_id = @projectId AND unit_id = @unitId)
            """,
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            DELETE FROM project_asset_candidates WHERE project_id = @projectId AND shot_id IN (
              SELECT id FROM shots WHERE project_id = @projectId AND unit_id = @unitId)
            """,
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM shots WHERE project_id = @projectId AND unit_id = @unitId",
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        foreach (Shot shot in shots)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<Shot>(), shot, transaction, cancellationToken).ConfigureAwait(false);
        }
        foreach (ShotRevision revision in revisions)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<ShotRevision>(), revision, transaction, cancellationToken).ConfigureAwait(false);
        }
        foreach (ShotAssetReference reference in references)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<ShotAssetReference>(), reference, transaction, cancellationToken).ConfigureAwait(false);
        }

        await InvalidateUnitWorkflowAsync(
            connection, transaction, projectId, unitId, "storyboard", DateTime.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @now WHERE id = @projectId",
            new { now = DateTime.UtcNow, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除镜头及全部领域关联，并压紧同章节顺序。对应 Go: <c>DeleteProjectShot</c>。</summary>
    public async Task DeleteProjectShotAsync(
        string projectId, string shotId, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Shot? shot = await FirstOrDefaultAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("id = @shotId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { shotId, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (shot is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await ExecuteAsync(
            connection,
            "DELETE FROM production_task_links WHERE project_id = @projectId AND shot_id = @shotId",
            new { projectId, shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM shot_artifacts WHERE project_id = @projectId AND shot_id = @shotId",
            new { projectId, shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM shot_revisions WHERE shot_id = @shotId",
            new { shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM shot_asset_references WHERE shot_id = @shotId",
            new { shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM project_asset_candidates WHERE project_id = @projectId AND shot_id = @shotId",
            new { projectId, shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        int deleted = await ExecuteAsync(
            connection,
            "DELETE FROM shots WHERE id = @shotId AND project_id = @projectId",
            new { shotId, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (deleted != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        IReadOnlyList<Shot> remaining = await QueryAsync<Shot>(
            connection,
            "SELECT \"id\", \"position\" FROM \"shots\" WHERE \"project_id\" = @projectId AND \"unit_id\" = @unitId ORDER BY \"position\" ASC, \"created_at\" ASC, \"id\" ASC",
            new { projectId, unitId = shot.UnitID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        for (int position = 0; position < remaining.Count; position++)
        {
            if (remaining[position].Position == position)
            {
                continue;
            }

            await ExecuteAsync(
                connection,
                "UPDATE shots SET position = @position WHERE id = @id AND project_id = @projectId",
                new { position, id = remaining[position].ID, projectId },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await InvalidateUnitWorkflowAsync(
            connection, transaction, projectId, shot.UnitID, "storyboard", updatedAt, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>镜头资产引用 upsert + 失效。对应 Go: <c>UpsertShotAssetReferenceAndInvalidate</c>。</summary>
    public async Task UpsertShotAssetReferenceAndInvalidateAsync(
        string projectId, ShotAssetReference reference, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int updated = await ExecuteAsync(
            connection,
            """
            UPDATE shot_asset_references SET status = @Status
            WHERE shot_id = @ShotID AND asset_version_id = @AssetVersionID AND role = @Role
            """,
            reference,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<ShotAssetReference>(), reference, transaction, cancellationToken).ConfigureAwait(false);
        }

        await MarkShotArtifactsStaleAsync(
            connection, transaction, reference.ShotID, updatedAt, cancellationToken).ConfigureAwait(false);

        Shot? shot = await FirstOrDefaultAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("id = @shotId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { shotId = reference.ShotID, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (shot is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await InvalidateUnitWorkflowAsync(
            connection, transaction, projectId, shot.UnitID, "storyboard", updatedAt, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除镜头资产引用 + 失效。对应 Go: <c>DeleteShotAssetReferenceAndInvalidate</c>。</summary>
    public async Task<bool> DeleteShotAssetReferenceAndInvalidateAsync(
        string projectId, string shotId, string referenceId, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int deleted = await ExecuteAsync(
            connection,
            "DELETE FROM shot_asset_references WHERE id = @referenceId AND shot_id = @shotId",
            new { referenceId, shotId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await MarkShotArtifactsStaleAsync(connection, transaction, shotId, updatedAt, cancellationToken).ConfigureAwait(false);

        Shot? shot = await FirstOrDefaultAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("id = @shotId AND project_id = @projectId", limitOffset: " LIMIT 1"),
            new { shotId, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (shot is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await InvalidateUnitWorkflowAsync(
            connection, transaction, projectId, shot.UnitID, "storyboard", updatedAt, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt, projectId },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 普通候选确认事务：正式资产身份、首版本、项目引用与候选状态同事务生效。
    /// 对应 Go: <c>ConfirmProjectAssetCandidate</c>（repository 层）。
    /// </summary>
    public async Task ConfirmProjectAssetCandidateAsync(
        ProjectAssetCandidate candidate,
        Asset asset,
        AssetVersion version,
        ProjectAssetLink link,
        bool createAsset,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (createAsset)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<Asset>(), asset, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection, SqlBuilder.Insert<AssetVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            long existing = await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM assets WHERE id = @id AND user_id = @userId",
                new { id = asset.ID, userId = asset.UserID },
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (existing != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("record not found");
            }
        }

        long linkExisting = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM project_asset_links WHERE project_id = @projectId AND asset_id = @assetId",
            new { projectId = link.ProjectID, assetId = link.AssetID },
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (linkExisting == 0)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<ProjectAssetLink>(), link, transaction, cancellationToken).ConfigureAwait(false);
        }

        await UpdateCandidateConfirmedAsync(
            connection, transaction, candidate, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt = candidate.UpdatedAt, projectId = candidate.ProjectID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 角色候选确认事务：角色版本替换与候选状态同事务生效。对应 Go: <c>ConfirmProjectCharacterCandidate</c>。
    /// </summary>
    public async Task ConfirmProjectCharacterCandidateAsync(
        ProjectAssetCandidate candidate,
        Asset asset,
        AssetVersion version,
        IReadOnlyList<AssetRepresentation> representations,
        CharacterVoiceBinding? voice,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection, SqlBuilder.Insert<AssetVersion>(), version, transaction, cancellationToken).ConfigureAwait(false);
        foreach (AssetRepresentation representation in representations)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<AssetRepresentation>(), representation, transaction, cancellationToken).ConfigureAwait(false);
        }
        if (voice is not null)
        {
            await ExecuteAsync(
                connection, SqlBuilder.Insert<CharacterVoiceBinding>(), voice, transaction, cancellationToken).ConfigureAwait(false);
        }

        int assetAffected = await ExecuteAsync(
            connection,
            """
            UPDATE assets SET
                kind = @Kind, category = @Category, status = @Status,
                primary_version_id = @PrimaryVersionID, title = @Title,
                payload_json = @PayloadJSON, updated_at = @UpdatedAt
            WHERE id = @ID AND user_id = @UserID
            """,
            asset,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (assetAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("record not found");
        }

        await UpdateCandidateConfirmedAsync(
            connection, transaction, candidate, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
            new { updatedAt = candidate.UpdatedAt, projectId = candidate.ProjectID },
            transaction,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>候选状态推进（仅 pending_confirmation 行，要求恰好命中 1 行）。对应 Go 的乐观更新。</summary>
    private async Task UpdateCandidateConfirmedAsync(
        DbConnection connection, DbTransaction transaction, ProjectAssetCandidate candidate, CancellationToken cancellationToken)
    {
        int affected = await ExecuteAsync(
            connection,
            """
            UPDATE project_asset_candidates SET status = @Status, resolved_asset_id = @ResolvedAssetID,
              updated_at = @UpdatedAt
            WHERE id = @ID AND project_id = @ProjectID AND status = 'pending_confirmation'
            """,
            candidate,
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("invalid data");
        }
    }

    /// <summary>把镜头未失败的产物标记为 stale。Go 各事务内的共享片段。</summary>
    private async Task MarkShotArtifactsStaleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string shotId,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            """
            UPDATE shot_artifacts SET status = 'stale', selected = @selected, updated_at = @updatedAt
            WHERE shot_id = @shotId AND status NOT IN ('failed', 'stale')
            """,
            new { shotId, updatedAt, selected = Dialect.Boolean(false) },
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 工作流失效：把章节实例中 storyboard 起的步骤重置（storyboard 置 running、后续置 pending）。
    /// 对应 Go: <c>invalidateUnitWorkflowTx</c>。
    /// </summary>
    private async Task InvalidateUnitWorkflowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string projectId,
        string unitId,
        string fromStepKey,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(unitId))
        {
            return;
        }

        IReadOnlyList<WorkflowInstance> instances = await QueryAsync<WorkflowInstance>(
            connection,
            SqlBuilder.Select<WorkflowInstance>("project_id = @projectId AND unit_id = @unitId"),
            new { projectId, unitId },
            transaction,
            cancellationToken).ConfigureAwait(false);
        foreach (WorkflowInstance instance in instances)
        {
            IReadOnlyList<WorkflowStepInstance> steps = await QueryAsync<WorkflowStepInstance>(
                connection,
                SqlBuilder.Select<WorkflowStepInstance>(
                    "workflow_instance_id = @instanceId", "position ASC"),
                new { instanceId = instance.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);

            long fromPosition = -1;
            foreach (WorkflowStepInstance step in steps)
            {
                if (step.StepKey == fromStepKey)
                {
                    fromPosition = step.Position;
                    break;
                }
            }
            if (fromPosition < 0)
            {
                continue;
            }

            foreach (WorkflowStepInstance step in steps)
            {
                if (step.Position < fromPosition)
                {
                    continue;
                }

                string status = step.Position == fromPosition
                    ? WorkflowStepStatus.WorkflowStepStatusRunning
                    : WorkflowStepStatus.WorkflowStepStatusPending;
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE workflow_step_instances SET status = @status, error = '', completed_at = NULL,
                      updated_at = @updatedAt
                    WHERE id = @id AND workflow_instance_id = @instanceId
                    """,
                    new { status, updatedAt, id = step.ID, instanceId = instance.ID },
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE workflow_instances SET status = 'active', revision = revision + 1, updated_at = @updatedAt
                WHERE id = @instanceId
                """,
                new { updatedAt, instanceId = instance.ID },
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
