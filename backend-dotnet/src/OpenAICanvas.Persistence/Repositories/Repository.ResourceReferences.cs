#nullable enable
using System.Data.Common;
using System.Text.Json;
using Dapper;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Persistence.Repositories;

/// <summary>
/// 资源删除校验使用的只读业务文档快照。
/// 对应 Go: <c>repository.ResourceReferenceSnapshot</c> 系列。
/// </summary>
public sealed record ResourceReferenceDocument(
    string Kind, string ID, string Title, string PrimaryJSON, string SecondaryJSON);

public sealed record ResourceDirectReference(
    string Kind, string ID, string Title, string ResourceID);

public sealed record ResourceReferenceSnapshot(
    IReadOnlyList<ResourceReferenceDocument> Documents,
    IReadOnlyList<ResourceDirectReference> Direct);

/// <summary>
/// 资源引用快照与删除级联仓储。
/// 对应 Go: <c>repository/resource_reference.go</c>。
/// </summary>
public sealed partial class Repository
{
    /// <summary>素材版本与表现记录。对应 Go: <c>AssetResourceRecords</c>。</summary>
    public async Task<(IReadOnlyList<AssetVersion> Versions, IReadOnlyList<AssetRepresentation> Representations)>
        AssetResourceRecordsAsync(string assetId, CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<AssetVersion> versions = (await QueryAsync<AssetVersion>(
            connection,
            SqlBuilder.Select<AssetVersion>("asset_id = @assetId"),
            new { assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        if (versions.Count == 0)
        {
            return (versions, []);
        }
        List<AssetRepresentation> representations = (await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>("asset_version_id IN @versionIds"),
            new { versionIds = versions.Select(version => version.ID).ToList() },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return (versions, representations);
    }

    /// <summary>
    /// 未排除的资源记录中指向同一物理对象的记录数（历史数据复用对象路径的兼容）。
    /// 对应 Go: <c>ResourceStorageReferenceCount</c>。
    /// </summary>
    public async Task<long> ResourceStorageReferenceCountAsync(
        Resource resource,
        IReadOnlyList<string> excludedResourceIds,
        CancellationToken cancellationToken = default)
    {
        string providerCondition =
            string.IsNullOrWhiteSpace(resource.Provider) ||
            string.Equals(resource.Provider.Trim(), "local", StringComparison.OrdinalIgnoreCase)
                ? "\"provider\" IN @providers"
                : "\"provider\" = @provider";
        DynamicParameters parameters = new();
        if (providerCondition.Contains("IN"))
        {
            parameters.Add("providers", new[] { "", "local" });
        }
        else
        {
            parameters.Add("provider", resource.Provider.Trim());
        }
        parameters.Add("endpoint", resource.Endpoint);
        parameters.Add("bucket", resource.Bucket);
        parameters.Add("objectKey", resource.ObjectKey);
        string excludeCondition = "";
        if (excludedResourceIds.Count > 0)
        {
            excludeCondition = " AND \"id\" NOT IN @excluded";
            parameters.Add("excluded", excludedResourceIds);
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"resources\" WHERE \"endpoint\" = @endpoint AND \"bucket\" = @bucket AND \"object_key\" = @objectKey AND " +
            providerCondition + excludeCondition,
            parameters,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 全业务面资源引用快照（素材/画布/任务/创作/日志/结果/项目/风格/工作流/镜头产物/声音/公告）。
    /// 对应 Go: <c>ResourceReferenceSnapshot</c>。
    /// </summary>
    public async Task<ResourceReferenceSnapshot> ResourceReferenceSnapshotAsync(
        string userId,
        string excludingAssetId,
        IReadOnlyList<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        List<ResourceReferenceDocument> documents = [];
        List<ResourceDirectReference> direct = [];
        if (resourceIds.Count == 0)
        {
            return new ResourceReferenceSnapshot(documents, direct);
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (Asset asset in await QueryAsync<Asset>(
                     connection,
                     SqlBuilder.Select<Asset>("user_id = @userId AND id <> @excludingAssetId"),
                     new { userId, excludingAssetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("素材", asset.ID, asset.Title, asset.PayloadJSON, ""));
        }

        foreach (CanvasProject canvas in await QueryAsync<CanvasProject>(
                     connection,
                     SqlBuilder.Select<CanvasProject>("user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("画布", canvas.ID, canvas.Title, canvas.PayloadJSON, ""));
        }

        foreach (TaskEntity task in await QueryAsync<TaskEntity>(
                     connection,
                     SqlBuilder.SelectColumns<TaskEntity>(
                         ["ID", "Prompt", "InputJSON", "ResultJSON"], "user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("任务", task.ID, task.Prompt, task.InputJSON, task.ResultJSON));
        }

        foreach (CreationRun run in await QueryAsync<CreationRun>(
                     connection,
                     SqlBuilder.Select<CreationRun>("user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("创作会话", run.ID, "智能创作", run.StateJSON, run.ApprovedOperationsJSON));
        }

        foreach (CreationSubmission submission in await QueryAsync<CreationSubmission>(
                     connection,
                     SqlBuilder.Select<CreationSubmission>("user_id = @userId AND revoked_at IS NULL"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("创作执行项", submission.ID, submission.ItemKey, submission.RequestJSON, ""));
        }

        foreach (TaskLog taskLog in await QueryAsync<TaskLog>(
                     connection,
                     SqlBuilder.SelectColumns<TaskLog>(["ID", "Message", "Payload"], "user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("任务日志", taskLog.ID, taskLog.Message, taskLog.Payload, ""));
        }

        foreach (Result result in await QueryAsync<Result>(
                     connection,
                     SqlBuilder.SelectColumns<Result>(["ID", "Kind", "URL", "Payload"], "user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("任务结果", result.ID, result.Kind, result.URL, result.Payload));
        }

        foreach (Project project in await QueryAsync<Project>(
                     connection,
                     SqlBuilder.Select<Project>("user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("项目", project.ID, project.Name, project.StyleProfileJSON, ""));
            if (project.CoverResourceID.Length > 0 && resourceIds.Contains(project.CoverResourceID))
            {
                direct.Add(new ResourceDirectReference("项目主图", project.ID, project.Name, project.CoverResourceID));
            }
        }

        foreach (StyleProfile style in await QueryAsync<StyleProfile>(
                     connection,
                     SqlBuilder.Select<StyleProfile>("user_id = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("风格", style.ID, style.Name, style.CoverURL, style.ProfileJSON));
        }

        // 其他素材的版本定义（按 asset_versions JOIN assets）。
        foreach (var version in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "asset_versions"."id" AS "ID", "assets"."title" AS "Title",
                            "asset_versions"."definition_json" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "asset_versions" JOIN "assets" ON "assets"."id" = "asset_versions"."asset_id"
                     WHERE "assets"."user_id" = @userId AND "assets"."id" <> @excludingAssetId
                     """,
                     new { userId, excludingAssetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("素材", version.ID, version.Title, version.PrimaryJSON, version.SecondaryJSON));
        }

        foreach (var candidate in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "project_asset_candidates"."id" AS "ID", "projects"."name" AS "Title",
                            "project_asset_candidates"."details_json" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "project_asset_candidates" JOIN "projects" ON "projects"."id" = "project_asset_candidates"."project_id"
                     WHERE "projects"."user_id" = @userId
                     """,
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("项目", candidate.ID, candidate.Title, candidate.PrimaryJSON, candidate.SecondaryJSON));
        }

        foreach (var step in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "workflow_step_instances"."id" AS "ID", "workflow_step_instances"."name" AS "Title",
                            "workflow_step_instances"."input_json" AS "PrimaryJSON",
                            "workflow_step_instances"."output_json" AS "SecondaryJSON"
                     FROM "workflow_step_instances"
                     JOIN "workflow_instances" ON "workflow_instances"."id" = "workflow_step_instances"."workflow_instance_id"
                     JOIN "projects" ON "projects"."id" = "workflow_instances"."project_id"
                     WHERE "projects"."user_id" = @userId
                     """,
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("工作流", step.ID, step.Title, step.PrimaryJSON, step.SecondaryJSON));
        }

        foreach (var artifact in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "shot_artifacts"."id" AS "ID", "shots"."title" AS "Title",
                            "shot_artifacts"."metadata_json" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "shot_artifacts"
                     JOIN "shots" ON "shots"."id" = "shot_artifacts"."shot_id"
                     JOIN "projects" ON "projects"."id" = "shots"."project_id"
                     WHERE "projects"."user_id" = @userId
                     """,
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("镜头产物", artifact.ID, artifact.Title, artifact.PrimaryJSON, artifact.SecondaryJSON));
        }

        // 直接引用（asset_representations / voices / shot_artifacts.resource_id / 公告）。
        foreach (var representation in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "asset_representations"."id" AS "ID", "assets"."title" AS "Title",
                            "asset_representations"."resource_id" AS "ResourceID",
                            '' AS "SecondaryJSON"
                     FROM "asset_representations"
                     JOIN "asset_versions" ON "asset_versions"."id" = "asset_representations"."asset_version_id"
                     JOIN "assets" ON "assets"."id" = "asset_versions"."asset_id"
                     WHERE "assets"."user_id" = @userId AND "assets"."id" <> @excludingAssetId
                       AND "asset_representations"."resource_id" IN @resourceIds
                     """,
                     new { userId, excludingAssetId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("素材", representation.ID, representation.Title, representation.ResourceID));
        }

        foreach (VoiceProfile voice in await QueryAsync<VoiceProfile>(
                     connection,
                     SqlBuilder.Select<VoiceProfile>("user_id = @userId AND sample_resource_id IN @resourceIds"),
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("声音", voice.ID, voice.Name, voice.SampleResourceID));
        }

        foreach (var artifact in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "shot_artifacts"."id" AS "ID", "shots"."title" AS "Title",
                            "shot_artifacts"."resource_id" AS "ResourceID",
                            '' AS "SecondaryJSON"
                     FROM "shot_artifacts"
                     JOIN "shots" ON "shots"."id" = "shot_artifacts"."shot_id"
                     JOIN "projects" ON "projects"."id" = "shots"."project_id"
                     WHERE "projects"."user_id" = @userId AND "shot_artifacts"."resource_id" IN @resourceIds
                     """,
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("镜头产物", artifact.ID, artifact.Title, artifact.ResourceID));
        }

        foreach (Announcement announcement in await QueryAsync<Announcement>(
                     connection,
                     SqlBuilder.Select<Announcement>("created_by = @userId AND image_resource_id IN @resourceIds"),
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("公告", announcement.ID, announcement.Title, announcement.ImageResourceID));
        }

        foreach (AnnouncementImageDraft draft in await QueryAsync<AnnouncementImageDraft>(
                     connection,
                     SqlBuilder.Select<AnnouncementImageDraft>("user_id = @userId AND resource_id IN @resourceIds"),
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("公告草稿", draft.ResourceID, "", draft.ResourceID));
        }

        return new ResourceReferenceSnapshot(documents, direct);
    }

    /// <summary>素材的业务引用（项目/镜头/候选）。对应 Go: <c>AssetBusinessReferences</c>。</summary>
    public async Task<IReadOnlyList<ResourceDirectReference>> AssetBusinessReferencesAsync(
        string userId,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<ResourceDirectReference> result = [];

        foreach (var project in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT DISTINCT "projects"."id" AS "ID", "projects"."name" AS "Title",
                            '' AS "PrimaryJSON", '' AS "SecondaryJSON"
                     FROM "project_asset_links" JOIN "projects" ON "projects"."id" = "project_asset_links"."project_id"
                     WHERE "projects"."user_id" = @userId AND "project_asset_links"."asset_id" = @assetId
                     """,
                     new { userId, assetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResourceDirectReference("项目", project.ID, project.Title, ""));
        }

        foreach (var project in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT DISTINCT "projects"."id" AS "ID", "projects"."name" AS "Title",
                            '' AS "PrimaryJSON", '' AS "SecondaryJSON"
                     FROM "shot_asset_references"
                     JOIN "asset_versions" ON "asset_versions"."id" = "shot_asset_references"."asset_version_id"
                     JOIN "shots" ON "shots"."id" = "shot_asset_references"."shot_id"
                     JOIN "projects" ON "projects"."id" = "shots"."project_id"
                     WHERE "projects"."user_id" = @userId AND "asset_versions"."asset_id" = @assetId
                     """,
                     new { userId, assetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResourceDirectReference("项目", project.ID, project.Title, ""));
        }

        foreach (var project in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT DISTINCT "projects"."id" AS "ID", "projects"."name" AS "Title",
                            '' AS "PrimaryJSON", '' AS "SecondaryJSON"
                     FROM "project_asset_candidates" JOIN "projects" ON "projects"."id" = "project_asset_candidates"."project_id"
                     WHERE "projects"."user_id" = @userId AND "project_asset_candidates"."resolved_asset_id" = @assetId
                     """,
                     new { userId, assetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ResourceDirectReference("项目", project.ID, project.Title, ""));
        }

        return result;
    }

    /// <summary>
    /// 删除素材及关联资源：版本引用/绑定/表现/项目链接/候选/版本/素材本体/删除任务/私有绑定/资源，同事务。
    /// 对应 Go: <c>DeleteAssetAndResources</c>。
    /// </summary>
    public async Task DeleteAssetAndResourcesAsync(
        string userId,
        string assetId,
        IReadOnlyList<string> resourceIds,
        IReadOnlyList<ResourceDeletionJob> deletionJobs,
        CancellationToken cancellationToken = default)
    {
        await InTransactionAsync(async (connection, transaction) =>
        {
            List<string> versionIds = (await QueryAsync<string>(
                connection,
                "SELECT \"id\" FROM \"asset_versions\" WHERE \"asset_id\" = @assetId",
                new { assetId },
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();

            if (versionIds.Count > 0)
            {
                await ExecuteAsync(connection,
                    "DELETE FROM \"shot_asset_references\" WHERE \"asset_version_id\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "DELETE FROM \"character_voice_bindings\" WHERE \"asset_version_id\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "DELETE FROM \"asset_representations\" WHERE \"asset_version_id\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"project_asset_links\" WHERE \"asset_id\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"project_asset_candidates\" WHERE \"resolved_asset_id\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"asset_versions\" WHERE \"asset_id\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"assets\" WHERE \"id\" = @assetId AND \"user_id\" = @userId",
                new { assetId, userId }, transaction, cancellationToken).ConfigureAwait(false);

            foreach (ResourceDeletionJob job in deletionJobs)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    SqlBuilder.Insert(typeof(ResourceDeletionJob)),
                    job,
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (resourceIds.Count == 0)
            {
                return;
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"ark_private_asset_bindings\" WHERE \"resource_id\" IN @resourceIds",
                new { resourceIds }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"resources\" WHERE \"user_id\" = @userId AND \"id\" IN @resourceIds",
                new { userId, resourceIds }, transaction, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>待删除任务。对应 Go: <c>ResourceDeletionStatusPending</c> / 完成/失败状态机由 worker 推进。</summary>
    public async Task<IReadOnlyList<ResourceDeletionJob>> PendingResourceDeletionJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await QueryAsync<ResourceDeletionJob>(
            connection,
            SqlBuilder.Select<ResourceDeletionJob>("status = @status", "next_attempt_at ASC"),
            new { status = "pending" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新删除任务状态。对应 Go: <c>drainResourceDeletionJobs</c> 的收尾写入。</summary>
    public async Task UpdateResourceDeletionJobAsync(
        string id, string status, string lastError, DateTime nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "UPDATE \"resource_deletion_jobs\" SET \"status\" = @status, \"last_error\" = @lastError, \"attempts\" = \"attempts\" + 1, \"next_attempt_at\" = @nextAttemptAt WHERE \"id\" = @id",
            new { id, status, lastError, nextAttemptAt },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed class JoinedDoc
    {
        public string ID { get; set; } = "";
        public string Title { get; set; } = "";
        public string PrimaryJSON { get; set; } = "";
        public string SecondaryJSON { get; set; } = "";
        public string ResourceID { get; set; } = "";
    }
}
