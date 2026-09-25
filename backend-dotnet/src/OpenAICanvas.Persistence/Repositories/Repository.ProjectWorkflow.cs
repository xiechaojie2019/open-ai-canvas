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
                "project_id = @projectId AND unit_id = @unitId", "position ASC, created_at ASC"),
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
            FROM shot_revisions sr
            JOIN shots s ON s.id = sr.shot_id
            WHERE s.project_id = @projectId AND s.unit_id = @unitId
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
                "project_id = @projectId AND unit_id = @unitId", "shot_id ASC, type ASC, version ASC"),
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
            FROM shot_asset_references sar
            JOIN shots s ON s.id = sar.shot_id
            WHERE s.project_id = @projectId AND s.unit_id = @unitId
            ORDER BY sar.created_at ASC
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
            JOIN project_asset_links pal ON pal.asset_id = a.id AND pal.project_id = @projectId
            JOIN asset_versions av ON av.asset_id = a.id
            JOIN shot_asset_references sar ON sar.asset_version_id = av.id
            JOIN shots s ON s.id = sar.shot_id AND s.project_id = @projectId AND s.unit_id = @unitId
            WHERE a.user_id = @userId
            ORDER BY a.updated_at DESC
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
                "project_id = @projectId AND (unit_id = @unitId OR unit_id = '')", "created_at ASC"),
            new { projectId, unitId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Shot>> ProjectShotsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<Shot>(
            connection,
            SqlBuilder.Select<Shot>("project_id = @projectId", "position ASC, created_at ASC"),
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
            FROM shot_revisions sr
            JOIN shots s ON s.id = sr.shot_id
            WHERE s.project_id = @projectId
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
            SqlBuilder.Select<ShotArtifact>("project_id = @projectId", "shot_id ASC, type ASC, version ASC"),
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
            FROM shot_asset_references sar
            JOIN shots s ON s.id = sar.shot_id
            WHERE s.project_id = @projectId
            ORDER BY sar.created_at ASC
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
            "SELECT COUNT(*) FROM canvas_projects WHERE user_id = @userId AND project_id = @projectId",
            new { userId, projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasProject> canvases = await QueryAsync<CanvasProject>(
            connection,
            SqlBuilder.SelectColumns<CanvasProject>(
                ["ID", "UserID", "ProjectID", "Title", "CreatedAt", "UpdatedAt"],
                "user_id = @userId AND project_id = @projectId", "updated_at DESC",
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
                "project_id = @projectId AND canvas_id IN @canvasIds", "created_at ASC"),
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
            FROM asset_versions av
            JOIN project_asset_links pal ON pal.asset_id = av.asset_id
            WHERE pal.project_id = @projectId AND av.id IN @versionIds
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
                "asset_version_id IN @versionIds", "asset_version_id ASC, role ASC, created_at ASC"),
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
                "template_key = @templateKey AND version = @version", limitOffset: " LIMIT 1"),
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
            connection, SqlBuilder.Select<WorkflowInstance>("project_id = @projectId", "created_at ASC"),
            new { projectId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowInstance>> ProjectWorkflowInstancesForUnitAsync(
        string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<WorkflowInstance>(
            connection,
            SqlBuilder.Select<WorkflowInstance>(
                "project_id = @projectId AND unit_id = @unitId", "created_at ASC"),
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
                "project_id = @projectId AND unit_id = @unitId AND template_version_id = @templateVersionId",
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
                "workflow_instance_id = @instanceId", "position ASC"),
            new { instanceId }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkflowStepInstance?> NextWorkflowStepAsync(
        string instanceId, long position, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await FirstOrDefaultAsync<WorkflowStepInstance>(
            connection,
            SqlBuilder.Select<WorkflowStepInstance>(
                "workflow_instance_id = @instanceId AND position > @position", "position ASC", " LIMIT 1"),
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
            FROM workflow_step_instances wsi
            JOIN workflow_instances wi ON wi.id = wsi.workflow_instance_id
            WHERE wi.project_id = @projectId AND wsi.id = @stepId
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
                "user_id = @userId AND project_id = @projectId AND status = @status",
                "completed_at ASC, created_at ASC"),
            new { userId, projectId, status = TaskStatus.TaskStatusSucceeded },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssetRepresentation>> AssetRepresentationsForTaskAsync(
        string taskId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>("task_id = @taskId", "created_at ASC"),
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
                UPDATE workflow_step_instances SET status = @Status, output_json = @OutputJSON,
                  error = @Error, started_at = @StartedAt, completed_at = @CompletedAt,
                  updated_at = @UpdatedAt
                WHERE id = @ID AND workflow_instance_id = @WorkflowInstanceID
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
                    "UPDATE workflow_step_instances SET status = @Status, updated_at = @UpdatedAt WHERE id = @ID AND workflow_instance_id = @WorkflowInstanceID",
                    next, transaction, cancellationToken).ConfigureAwait(false);
                if (nextAffected != 1)
                {
                    throw new InvalidOperationException("invalid data");
                }
            }

            int instanceAffected = await ExecuteAsync(
                connection,
                "UPDATE workflow_instances SET status = @Status, revision = @Revision, updated_at = @UpdatedAt WHERE id = @ID AND project_id = @ProjectID",
                instance, transaction, cancellationToken).ConfigureAwait(false);
            if (instanceAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }

            int projectAffected = await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
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
                    "workflow_step_id = @workflowStepId AND task_id = @taskId", limitOffset: " LIMIT 1"),
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
                        "task_id = @taskId AND role = @role", limitOffset: " LIMIT 1"),
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
                        "task_id = @taskId AND shot_id = @shotId AND artifact_type = @artifactType",
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
                        "UPDATE production_task_links SET project_id = @ProjectID, canvas_id = @CanvasID, unit_id = @UnitID, workflow_step_id = @WorkflowStepID, updated_at = @UpdatedAt WHERE id = @ID",
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
                        "task_id = @taskId AND shot_id = @shotId AND type = @type", limitOffset: " LIMIT 1"),
                    new { taskId = artifact.TaskID, shotId = artifact.ShotID, type = artifact.Type },
                    transaction, cancellationToken).ConfigureAwait(false);
                if (existingArtifact is null)
                {
                    long currentVersion = await ScalarAsync<long>(
                        connection,
                        "SELECT COALESCE(MAX(version), 0) FROM shot_artifacts WHERE shot_id = @shotId AND type = @type",
                        new { shotId = artifact.ShotID, type = artifact.Type }, transaction,
                        cancellationToken).ConfigureAwait(false);
                    artifact.Version = currentVersion + 1;
                    if (artifact.Selected)
                    {
                        await ExecuteAsync(
                            connection,
                            "UPDATE shot_artifacts SET selected = @selected, updated_at = @updatedAt WHERE shot_id = @shotId AND type = @type",
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
                UPDATE workflow_step_instances SET status = @Status, output_json = @OutputJSON,
                  error = @Error, started_at = @StartedAt, completed_at = @CompletedAt,
                  updated_at = @UpdatedAt
                WHERE id = @ID AND workflow_instance_id = @WorkflowInstanceID
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
                    "UPDATE workflow_step_instances SET status = @Status, updated_at = @UpdatedAt WHERE id = @ID AND workflow_instance_id = @WorkflowInstanceID",
                    next, transaction, cancellationToken).ConfigureAwait(false);
                if (nextAffected != 1)
                {
                    throw new InvalidOperationException("invalid data");
                }
            }

            int instanceAffected = await ExecuteAsync(
                connection,
                "UPDATE workflow_instances SET status = @Status, revision = @Revision, updated_at = @UpdatedAt WHERE id = @ID AND project_id = @ProjectID",
                instance, transaction, cancellationToken).ConfigureAwait(false);
            if (instanceAffected != 1)
            {
                throw new InvalidOperationException("invalid data");
            }
            int projectAffected = await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
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
                SqlBuilder.Insert<ProjectAssetLink>() + Dialect.OnConflictDoNothing("\"project_id\", \"asset_id\""),
                link, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                "UPDATE projects SET revision = revision + 1, updated_at = @updatedAt WHERE id = @projectId",
                new { projectId = link.ProjectID, updatedAt = asset.UpdatedAt }, transaction,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
