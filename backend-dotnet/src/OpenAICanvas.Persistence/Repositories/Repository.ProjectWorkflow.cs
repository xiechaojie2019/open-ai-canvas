#nullable enable
using System.Data.Common;
using Dapper;
using OpenAICanvas.Domain.Entities;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 工作流实例、工作台聚合与工作流产物回填仓储。
/// 对应 Go: <c>repository/project_workflow.go</c> 与
/// <c>repository/project_workbench_read.go</c>。
/// </summary>
public sealed partial class Repository
{
    // ------------------------------------------------------------ 工作台读视图

    public async Task<IReadOnlyList<Shot>> ProjectUnitShotsAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>(
                "\"projectId\" = @projectId AND \"unitId\" = @unitId", "position ASC, \"createdAt\" ASC"),
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotRevision>> ProjectUnitShotRevisionsAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotRevision>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<ShotRevision>("sr")}
            FROM "shotRevisions" sr
            JOIN shots s ON s.id = sr."shotId"
            WHERE s."projectId" = @projectId AND s."unitId" = @unitId
            ORDER BY s.position ASC, sr.version ASC
            """,
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotArtifact>> ProjectUnitShotArtifactsAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotArtifact>(
            connection,
            SqlBuilder.Select<ShotArtifact>(
                "\"projectId\" = @projectId AND \"unitId\" = @unitId", "\"shotId\" ASC, type ASC, version ASC"),
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotAssetReference>> ProjectUnitShotAssetReferencesAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotAssetReference>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<ShotAssetReference>("sar")}
            FROM "shotAssetReferences" sar
            JOIN shots s ON s.id = sar."shotId"
            WHERE s."projectId" = @projectId AND s."unitId" = @unitId
            ORDER BY sar."createdAt" ASC
            """,
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Asset>> ProjectUnitAssetsAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Asset>(
            connection,
            $"""
            SELECT DISTINCT {SqlBuilder.Projection<Asset>("a")}
            FROM assets a
            JOIN "projectAssetLinks" pal ON pal."assetId" = a.id AND pal."projectId" = @projectId
            JOIN "assetVersions" av ON av."assetId" = a.id
            JOIN "shotAssetReferences" sar ON sar."assetVersionId" = av.id
            JOIN shots s ON s.id = sar."shotId" AND s."projectId" = @projectId AND s."unitId" = @unitId
            WHERE a."userId" = @userId
            ORDER BY a."updatedAt" DESC
            """,
            new { userId, projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectAssetCandidate>> ProjectUnitAssetCandidatesAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ProjectAssetCandidate>(
            connection,
            SqlBuilder.Select<ProjectAssetCandidate>(
                "\"projectId\" = @projectId AND (\"unitId\" = @unitId OR \"unitId\" = '')", "\"createdAt\" ASC"),
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Shot>> ProjectShotsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("\"projectId\" = @projectId", "position ASC, \"createdAt\" ASC"),
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotRevision>> ProjectShotRevisionsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotRevision>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<ShotRevision>("sr")}
            FROM "shotRevisions" sr
            JOIN shots s ON s.id = sr."shotId"
            WHERE s."projectId" = @projectId
            ORDER BY s.position ASC, sr.version ASC
            """,
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotArtifact>> ProjectShotArtifactsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotArtifact>(
            connection,
            SqlBuilder.Select<ShotArtifact>("\"projectId\" = @projectId", "\"shotId\" ASC, type ASC, version ASC"),
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShotAssetReference>> ProjectShotAssetReferencesAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ShotAssetReference>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<ShotAssetReference>("sar")}
            FROM "shotAssetReferences" sar
            JOIN shots s ON s.id = sar."shotId"
            WHERE s."projectId" = @projectId
            ORDER BY sar."createdAt" ASC
            """,
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<CanvasProject> Canvases, long Total)> ProjectCanvasSummariesPageAsync(
        string userId, string projectId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        long total = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"canvasProjects\" WHERE \"userId\" = @userId AND \"projectId\" = @projectId",
            new { userId, projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasProject> canvases = await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.SelectColumns<CanvasProject>(
                ["ID", "UserID", "ProjectID", "Title", "CreatedAt", "UpdatedAt"],
                "\"userId\" = @userId AND \"projectId\" = @projectId", "\"updatedAt\" DESC",
                Dialect.LimitOffset(pageSize, (page - 1) * pageSize)),
            new { userId, projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (canvases, total);
    }

    public async Task<IReadOnlyList<CanvasUnitLink>> ProjectCanvasUnitLinksForCanvasesAsync(
        string projectId, IReadOnlyList<string> canvasIds, CancellationToken cancellationToken = default)
    {
        if (canvasIds.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<CanvasUnitLink>(
            connection,
            SqlBuilder.Select<CanvasUnitLink>(
                "\"projectId\" = @projectId AND \"canvasId\" IN @canvasIds", "\"createdAt\" ASC"),
            new { projectId, canvasIds }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssetVersion>> ProjectAssetVersionsByIDsAsync(
        string projectId, IReadOnlyList<string> versionIds, CancellationToken cancellationToken = default)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetVersion>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<AssetVersion>("av")}
            FROM "assetVersions" av
            JOIN "projectAssetLinks" pal ON pal."assetId" = av."assetId"
            WHERE pal."projectId" = @projectId AND av.id IN @versionIds
            """,
            new { projectId, versionIds }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssetRepresentation>> AssetRepresentationsByVersionIDsAsync(
        IReadOnlyList<string> versionIds, CancellationToken cancellationToken = default)
    {
        if (versionIds.Count == 0)
        {
            return [];
        }
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>(
                "\"assetVersionId\" IN @versionIds", "\"assetVersionId\" ASC, role ASC, \"createdAt\" ASC"),
            new { versionIds }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 工作流读写

    public async Task<WorkflowTemplateVersion?> WorkflowTemplateVersionAsync(
        string templateKey, long version, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowTemplateVersion>(
            connection,
            SqlBuilder.Select<WorkflowTemplateVersion>(
                "\"templateKey\" = @templateKey AND version = @version", limitOffset: " LIMIT 1"),
            new { templateKey, version }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateWorkflowTemplateVersionAsync(
        WorkflowTemplateVersion template, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlBuilder.Insert<WorkflowTemplateVersion>(), template, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowInstance>> ProjectWorkflowInstancesAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<WorkflowInstance>(
            connection, SqlBuilder.Select<WorkflowInstance>("\"projectId\" = @projectId", "\"createdAt\" ASC"),
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowInstance>> ProjectWorkflowInstancesForUnitAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<WorkflowInstance>(
            connection,
            SqlBuilder.Select<WorkflowInstance>(
                "\"projectId\" = @projectId AND \"unitId\" = @unitId", "\"createdAt\" ASC"),
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowInstance?> WorkflowInstanceForScopeAsync(
        string projectId, string unitId, string templateVersionId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowInstance>(
            connection,
            SqlBuilder.Select<WorkflowInstance>(
                "\"projectId\" = @projectId AND \"unitId\" = @unitId AND \"templateVersionId\" = @templateVersionId",
                limitOffset: " LIMIT 1"),
            new { projectId, unitId, templateVersionId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowInstance?> WorkflowInstanceAsync(
        string id, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowInstance>(
            connection, SqlBuilder.Select<WorkflowInstance>("id = @id", limitOffset: " LIMIT 1"),
            new { id }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowStepInstance>> WorkflowStepsAsync(
        string instanceId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<WorkflowStepInstance>(
            connection, SqlBuilder.Select<WorkflowStepInstance>(
                "\"workflowInstanceId\" = @instanceId", "position ASC"),
            new { instanceId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowStepInstance?> NextWorkflowStepAsync(
        string instanceId, long position, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowStepInstance>(
            connection,
            SqlBuilder.Select<WorkflowStepInstance>(
                "\"workflowInstanceId\" = @instanceId AND position > @position", "position ASC", " LIMIT 1"),
            new { instanceId, position }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateWorkflowInstanceAsync(
        WorkflowInstance instance, IReadOnlyList<WorkflowStepInstance> steps,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                SqlBuilder.Insert<WorkflowInstance>(), instance, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            foreach (WorkflowStepInstance step in steps)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert<WorkflowStepInstance>(), step, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowStepInstance?> WorkflowStepForProjectAsync(
        string projectId, string stepId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowStepInstance>(
            connection,
            $"""
            SELECT {SqlBuilder.Projection<WorkflowStepInstance>("wsi")}
            FROM "workflowStepInstances" wsi
            JOIN "workflowInstances" wi ON wi.id = wsi."workflowInstanceId"
            WHERE wi."projectId" = @projectId AND wsi.id = @stepId
            LIMIT 1
            """,
            new { projectId, stepId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskEntity>> SuccessfulWorkflowTasksForProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<TaskEntity>(
            connection,
            SqlBuilder.Select<TaskEntity>(
                "\"userId\" = @userId AND \"projectId\" = @projectId AND status = @status",
                "\"completedAt\" ASC, \"createdAt\" ASC"),
            new { userId, projectId, status = TaskStatus.TaskStatusSucceeded },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssetRepresentation>> AssetRepresentationsForTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>("\"taskId\" = @taskId", "\"createdAt\" ASC"),
            new { taskId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateWorkflowProgressAsync(
        WorkflowStepInstance step, WorkflowStepInstance? next, WorkflowInstance instance,
        string projectId, CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            int stepAffected = await ExecuteAsync(
                connection,
                """
                UPDATE "workflowStepInstances" SET status = @Status, "outputJson" = @OutputJSON,
                  error = @Error, "startedAt" = @StartedAt, "completedAt" = @CompletedAt,
                  "updatedAt" = @UpdatedAt
                WHERE id = @ID AND "workflowInstanceId" = @WorkflowInstanceID
                """,
                step, transaction, cancellationToken).ConfigureAwait(false);
            if (stepAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }

            if (next is not null)
            {
                int nextAffected = await ExecuteAsync(
                    connection,
                    "UPDATE \"workflowStepInstances\" SET status = @Status, \"updatedAt\" = @UpdatedAt WHERE id = @ID AND \"workflowInstanceId\" = @WorkflowInstanceID",
                    next, transaction, cancellationToken).ConfigureAwait(false);
                if (nextAffected != 1)
                {
                    throw new InvalidOperationException("invalid data");
                }
            }

            int instanceAffected = await ExecuteAsync(
                connection,
                "UPDATE \"workflowInstances\" SET status = @Status, revision = @Revision, \"updatedAt\" = @UpdatedAt WHERE id = @ID AND \"projectId\" = @ProjectID",
                instance, transaction, cancellationToken).ConfigureAwait(false);
            if (instanceAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }

            int projectAffected = await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, \"updatedAt\" = @updatedAt WHERE id = @projectId",
                new { projectId, updatedAt = step.UpdatedAt }, transaction, cancellationToken).ConfigureAwait(false);
            if (projectAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterWorkflowTaskOutputAsync(
        WorkflowStepInstance step, WorkflowStepInstance? next, WorkflowInstance instance,
        string projectId, WorkflowStepTask link, AssetRepresentation? representation,
        ProductionTaskLink? productionLink, ShotArtifact? artifact,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            WorkflowStepTask? existingLink = await FirstOrDefaultAsync<WorkflowStepTask>(
                connection,
                SqlBuilder.Select<WorkflowStepTask>(
                    "\"workflowStepId\" = @workflowStepId AND \"taskId\" = @taskId", limitOffset: " LIMIT 1"),
                new { workflowStepId = link.WorkflowStepID, taskId = link.TaskID }, transaction,
                cancellationToken).ConfigureAwait(false);
            if (existingLink is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert<WorkflowStepTask>(), link, transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (representation is not null)
            {
                AssetRepresentation? existingRepresentation = await FirstOrDefaultAsync<AssetRepresentation>(
                    connection,
                    SqlBuilder.Select<AssetRepresentation>(
                        "\"taskId\" = @taskId AND role = @role", limitOffset: " LIMIT 1"),
                    new { taskId = representation.TaskID, role = representation.Role }, transaction,
                    cancellationToken).ConfigureAwait(false);
                if (existingRepresentation is null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        SqlBuilder.Insert<AssetRepresentation>(), representation, transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }

            if (productionLink is not null)
            {
                ProductionTaskLink? existingProductionLink = await FirstOrDefaultAsync<ProductionTaskLink>(
                    connection,
                    SqlBuilder.Select<ProductionTaskLink>(
                        "\"taskId\" = @taskId AND \"shotId\" = @shotId AND \"artifactType\" = @artifactType",
                        limitOffset: " LIMIT 1"),
                    new
                    {
                        taskId = productionLink.TaskID,
                        shotId = productionLink.ShotID,
                        artifactType = productionLink.ArtifactType,
                    }, transaction, cancellationToken).ConfigureAwait(false);
                if (existingProductionLink is null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        SqlBuilder.Insert<ProductionTaskLink>(), productionLink, transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteAsync(
                        connection,
                        "UPDATE \"productionTaskLinks\" SET \"projectId\" = @ProjectID, \"canvasId\" = @CanvasID, \"unitId\" = @UnitID, \"workflowStepId\" = @WorkflowStepID, \"updatedAt\" = @UpdatedAt WHERE id = @ID",
                        new
                        {
                            existingProductionLink.ID,
                            productionLink.ProjectID,
                            productionLink.CanvasID,
                            productionLink.UnitID,
                            productionLink.WorkflowStepID,
                            productionLink.UpdatedAt,
                        }, transaction, cancellationToken).ConfigureAwait(false);
                }
            }

            if (artifact is not null)
            {
                ShotArtifact? existingArtifact = await FirstOrDefaultAsync<ShotArtifact>(
                    connection,
                    SqlBuilder.Select<ShotArtifact>(
                        "\"taskId\" = @taskId AND \"shotId\" = @shotId AND type = @type", limitOffset: " LIMIT 1"),
                    new { taskId = artifact.TaskID, shotId = artifact.ShotID, type = artifact.Type },
                    transaction, cancellationToken).ConfigureAwait(false);
                if (existingArtifact is null)
                {
                    long currentVersion = await ScalarAsync<long>(
                        connection,
                        "SELECT COALESCE(MAX(version), 0) FROM \"shotArtifacts\" WHERE \"shotId\" = @shotId AND type = @type",
                        new { shotId = artifact.ShotID, type = artifact.Type }, transaction,
                        cancellationToken).ConfigureAwait(false);
                    artifact.Version = currentVersion + 1;
                    if (artifact.Selected)
                    {
                        await ExecuteAsync(
                            connection,
                            "UPDATE \"shotArtifacts\" SET selected = @selected, \"updatedAt\" = @updatedAt WHERE \"shotId\" = @shotId AND type = @type",
                            new
                            {
                                selected = Dialect.Boolean(false),
                                updatedAt = artifact.UpdatedAt,
                                shotId = artifact.ShotID,
                                type = artifact.Type,
                            }, transaction, cancellationToken).ConfigureAwait(false);
                    }
                    await connection.ExecuteAsync(new CommandDefinition(
                        SqlBuilder.Insert<ShotArtifact>(), artifact, transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }

            int stepAffected = await ExecuteAsync(
                connection,
                """
                UPDATE "workflowStepInstances" SET status = @Status, "outputJson" = @OutputJSON,
                  error = @Error, "startedAt" = @StartedAt, "completedAt" = @CompletedAt,
                  "updatedAt" = @UpdatedAt
                WHERE id = @ID AND "workflowInstanceId" = @WorkflowInstanceID
                """,
                step, transaction, cancellationToken).ConfigureAwait(false);
            if (stepAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }
            if (next is not null)
            {
                int nextAffected = await ExecuteAsync(
                    connection,
                    "UPDATE \"workflowStepInstances\" SET status = @Status, \"updatedAt\" = @UpdatedAt WHERE id = @ID AND \"workflowInstanceId\" = @WorkflowInstanceID",
                    next, transaction, cancellationToken).ConfigureAwait(false);
                if (nextAffected != 1)
                {
                    throw new InvalidOperationException("invalid data");
                }
            }

            int instanceAffected = await ExecuteAsync(
                connection,
                "UPDATE \"workflowInstances\" SET status = @Status, revision = @Revision, \"updatedAt\" = @UpdatedAt WHERE id = @ID AND \"projectId\" = @ProjectID",
                instance, transaction, cancellationToken).ConfigureAwait(false);
            if (instanceAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }
            int projectAffected = await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, \"updatedAt\" = @updatedAt WHERE id = @projectId",
                new { projectId, updatedAt = step.UpdatedAt }, transaction, cancellationToken).ConfigureAwait(false);
            if (projectAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>补偿任务产物时原子创建资产、版本与项目链接。</summary>
    public async Task CreateGeneratedProjectAssetAsync(
        Asset asset, AssetVersion version, ProjectAssetLink link,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert<Asset>() + Dialect.OnConflictDoNothing("\"id\""),
                asset, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert<AssetVersion>() + Dialect.OnConflictDoNothing("\"id\""),
                version, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                SqlBuilder.Insert<ProjectAssetLink>() + Dialect.OnConflictDoNothing("\"projectId\", \"assetId\""),
                link, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, \"updatedAt\" = @updatedAt WHERE id = @projectId",
                new { projectId = link.ProjectID, updatedAt = asset.UpdatedAt }, transaction,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
