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
    string Kind, string ID, string Title, string PrimaryJSON, string SecondaryJSON, string TaskStatus = "");

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
            SqlBuilder.Select<AssetVersion>("\"assetId\" = @assetId"),
            new { assetId },
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        if (versions.Count == 0)
        {
            return (versions, []);
        }
        List<AssetRepresentation> representations = (await QueryAsync<AssetRepresentation>(
            connection,
            SqlBuilder.Select<AssetRepresentation>("\"assetVersionId\" IN @versionIds"),
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
            // DynamicParameters 通道只传标量：集合用 Placeholders 手写展开。
            providerCondition = providerCondition.Replace(
                "IN @providers",
                "IN (@prov0, @prov1)");
            parameters.Add("prov0", "");
            parameters.Add("prov1", "local");
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
            excludeCondition = " AND \"id\" NOT IN (" + Placeholders(excludedResourceIds.Count) + ")";
            for (int index = 0; index < excludedResourceIds.Count; index++)
            {
                parameters.Add("p" + index, excludedResourceIds[index]);
            }
        }

        await using DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM \"resources\" WHERE \"endpoint\" = @endpoint AND \"bucket\" = @bucket AND \"objectKey\" = @objectKey AND " +
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
                     SqlBuilder.Select<Asset>("\"userId\" = @userId AND id <> @excludingAssetId"),
                     new { userId, excludingAssetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("素材", asset.ID, asset.Title, asset.PayloadJSON, ""));
        }

        foreach (CanvasProject canvas in await QueryAsync<CanvasProject>(
                     connection,
                     SqlBuilder.Select<CanvasProject>("\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("画布", canvas.ID, canvas.Title, canvas.PayloadJSON, ""));
        }

        foreach (TaskEntity task in await QueryAsync<TaskEntity>(
                     connection,
                     SqlBuilder.SelectColumns<TaskEntity>(
                         ["ID", "Prompt", "Status", "InputJSON", "ResultJSON"], "\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument(
                "任务", task.ID, task.Prompt, task.InputJSON, task.ResultJSON, task.Status));
        }

        Dictionary<string, string> taskStatuses = documents
            .Where(document => document.Kind == "任务")
            .ToDictionary(document => document.ID, document => document.TaskStatus, StringComparer.Ordinal);

        foreach (TaskLog taskLog in await QueryAsync<TaskLog>(
                     connection,
                     SqlBuilder.SelectColumns<TaskLog>(["ID", "TaskID", "Message", "Payload"], "\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument(
                "任务日志", taskLog.ID, taskLog.Message, taskLog.Payload, "",
                taskStatuses.GetValueOrDefault(taskLog.TaskID, "")));
        }

        foreach (Result result in await QueryAsync<Result>(
                     connection,
                     SqlBuilder.SelectColumns<Result>(["ID", "TaskID", "Kind", "URL", "Payload"], "\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument(
                "任务结果", result.ID, result.Kind, result.URL, result.Payload,
                taskStatuses.GetValueOrDefault(result.TaskID, "")));
        }
        foreach (CreationRun run in await QueryAsync<CreationRun>(
                     connection,
                     SqlBuilder.Select<CreationRun>("\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("创作会话", run.ID, "智能创作", run.StateJSON, run.ApprovedOperationsJSON));
        }

        foreach (CreationSubmission submission in await QueryAsync<CreationSubmission>(
                     connection,
                     SqlBuilder.Select<CreationSubmission>("\"userId\" = @userId AND \"revokedAt\" IS NULL"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("创作执行项", submission.ID, submission.ItemKey, submission.RequestJSON, ""));
        }

        foreach (Project project in await QueryAsync<Project>(
                     connection,
                     SqlBuilder.Select<Project>("\"userId\" = @userId"),
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
                     SqlBuilder.Select<StyleProfile>("\"userId\" = @userId"),
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("风格", style.ID, style.Name, style.CoverURL, style.ProfileJSON));
        }

        // 其他素材的版本定义（按 asset_versions JOIN assets）。
        foreach (var version in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "assetVersions"."id" AS "ID", "assets"."title" AS "Title",
                            "assetVersions"."definitionJson" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "assetVersions" JOIN "assets" ON "assets"."id" = "assetVersions"."assetId"
                     WHERE "assets"."userId" = @userId AND "assets"."id" <> @excludingAssetId
                     """,
                     new { userId, excludingAssetId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("素材", version.ID, version.Title, version.PrimaryJSON, version.SecondaryJSON));
        }

        foreach (var candidate in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "projectAssetCandidates"."id" AS "ID", "projects"."name" AS "Title",
                            "projectAssetCandidates"."detailsJson" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "projectAssetCandidates" JOIN "projects" ON "projects"."id" = "projectAssetCandidates"."projectId"
                     WHERE "projects"."userId" = @userId
                     """,
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("项目", candidate.ID, candidate.Title, candidate.PrimaryJSON, candidate.SecondaryJSON));
        }

        foreach (var step in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "workflowStepInstances"."id" AS "ID", "workflowStepInstances"."name" AS "Title",
                            "workflowStepInstances"."inputJson" AS "PrimaryJSON",
                            "workflowStepInstances"."outputJson" AS "SecondaryJSON"
                     FROM "workflowStepInstances"
                     JOIN "workflowInstances" ON "workflowInstances"."id" = "workflowStepInstances"."workflowInstanceId"
                     JOIN "projects" ON "projects"."id" = "workflowInstances"."projectId"
                     WHERE "projects"."userId" = @userId
                     """,
                     new { userId },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new ResourceReferenceDocument("工作流", step.ID, step.Title, step.PrimaryJSON, step.SecondaryJSON));
        }

        foreach (var artifact in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "shotArtifacts"."id" AS "ID", "shots"."title" AS "Title",
                            "shotArtifacts"."metadataJson" AS "PrimaryJSON",
                            '' AS "SecondaryJSON"
                     FROM "shotArtifacts"
                     JOIN "shots" ON "shots"."id" = "shotArtifacts"."shotId"
                     JOIN "projects" ON "projects"."id" = "shots"."projectId"
                     WHERE "projects"."userId" = @userId
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
                     SELECT "assetRepresentations"."id" AS "ID", "assets"."title" AS "Title",
                            "assetRepresentations"."resourceId" AS "ResourceID",
                            '' AS "SecondaryJSON"
                     FROM "assetRepresentations"
                     JOIN "assetVersions" ON "assetVersions"."id" = "assetRepresentations"."assetVersionId"
                     JOIN "assets" ON "assets"."id" = "assetVersions"."assetId"
                     WHERE "assets"."userId" = @userId AND "assets"."id" <> @excludingAssetId
                       AND "assetRepresentations"."resourceId" IN @resourceIds
                     """,
                     new { userId, excludingAssetId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("素材", representation.ID, representation.Title, representation.ResourceID));
        }

        foreach (VoiceProfile voice in await QueryAsync<VoiceProfile>(
                     connection,
                     SqlBuilder.Select<VoiceProfile>("\"userId\" = @userId AND \"sampleResourceId\" IN @resourceIds"),
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("声音", voice.ID, voice.Name, voice.SampleResourceID));
        }

        foreach (var artifact in await QueryAsync<JoinedDoc>(
                     connection,
                     """
                     SELECT "shotArtifacts"."id" AS "ID", "shots"."title" AS "Title",
                            "shotArtifacts"."resourceId" AS "ResourceID",
                            '' AS "SecondaryJSON"
                     FROM "shotArtifacts"
                     JOIN "shots" ON "shots"."id" = "shotArtifacts"."shotId"
                     JOIN "projects" ON "projects"."id" = "shots"."projectId"
                     WHERE "projects"."userId" = @userId AND "shotArtifacts"."resourceId" IN @resourceIds
                     """,
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("镜头产物", artifact.ID, artifact.Title, artifact.ResourceID));
        }

        foreach (Announcement announcement in await QueryAsync<Announcement>(
                     connection,
                     SqlBuilder.Select<Announcement>("\"createdBy\" = @userId AND \"imageResourceId\" IN @resourceIds"),
                     new { userId, resourceIds },
                     cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            direct.Add(new ResourceDirectReference("公告", announcement.ID, announcement.Title, announcement.ImageResourceID));
        }

        foreach (AnnouncementImageDraft draft in await QueryAsync<AnnouncementImageDraft>(
                     connection,
                     SqlBuilder.Select<AnnouncementImageDraft>("\"userId\" = @userId AND \"resourceId\" IN @resourceIds"),
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
                     FROM "projectAssetLinks" JOIN "projects" ON "projects"."id" = "projectAssetLinks"."projectId"
                     WHERE "projects"."userId" = @userId AND "projectAssetLinks"."assetId" = @assetId
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
                     FROM "shotAssetReferences"
                     JOIN "assetVersions" ON "assetVersions"."id" = "shotAssetReferences"."assetVersionId"
                     JOIN "shots" ON "shots"."id" = "shotAssetReferences"."shotId"
                     JOIN "projects" ON "projects"."id" = "shots"."projectId"
                     WHERE "projects"."userId" = @userId AND "assetVersions"."assetId" = @assetId
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
                     FROM "projectAssetCandidates" JOIN "projects" ON "projects"."id" = "projectAssetCandidates"."projectId"
                     WHERE "projects"."userId" = @userId AND "projectAssetCandidates"."resolvedAssetId" = @assetId
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
                "SELECT \"id\" FROM \"assetVersions\" WHERE \"assetId\" = @assetId",
                new { assetId },
                transaction,
                cancellationToken).ConfigureAwait(false)).ToList();

            if (versionIds.Count > 0)
            {
                await ExecuteAsync(connection,
                    "DELETE FROM \"shotAssetReferences\" WHERE \"assetVersionId\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "DELETE FROM \"characterVoiceBindings\" WHERE \"assetVersionId\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "DELETE FROM \"assetRepresentations\" WHERE \"assetVersionId\" IN @versionIds",
                    new { versionIds }, transaction, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync(connection,
                "DELETE FROM \"projectAssetLinks\" WHERE \"assetId\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"projectAssetCandidates\" WHERE \"resolvedAssetId\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"assetVersions\" WHERE \"assetId\" = @assetId",
                new { assetId }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"assets\" WHERE \"id\" = @assetId AND \"userId\" = @userId",
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
                "DELETE FROM \"arkPrivateAssetBindings\" WHERE \"resourceId\" IN @resourceIds",
                new { resourceIds }, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "DELETE FROM \"resources\" WHERE \"userId\" = @userId AND \"id\" IN @resourceIds",
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
            SqlBuilder.Select<ResourceDeletionJob>("status = @status", "\"nextAttemptAt\" ASC"),
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
            "UPDATE \"resourceDeletionJobs\" SET \"status\" = @status, \"lastError\" = @lastError, \"attempts\" = \"attempts\" + 1, \"nextAttemptAt\" = @nextAttemptAt WHERE \"id\" = @id",
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
