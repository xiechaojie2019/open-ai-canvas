#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.CloudAgent;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;
using TaskStatus = OpenAICanvas.Domain.Entities.TaskStatus;

namespace OpenAICanvas.Application;

public sealed class ProjectWorkflowDetailDto
{
    [JsonPropertyName("instance")]
    public WorkflowInstance Instance { get; init; } = new();

    [JsonPropertyName("steps")]
    public IReadOnlyList<WorkflowStepInstance> Steps { get; init; } = [];
}

public sealed class CreateUnitWorkflowRequest
{
    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";
}

public sealed class UpdateWorkflowStepRequest
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("outputJson")]
    public string OutputJSON { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

public sealed class RegisterTaskOutputRequest
{
    [JsonPropertyName("taskId")]
    public string TaskID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("unitId")]
    public string UnitID { get; set; } = "";

    [JsonPropertyName("shotId")]
    public string ShotID { get; set; } = "";

    [JsonPropertyName("shotRevisionId")]
    public string ShotRevisionID { get; set; } = "";

    [JsonPropertyName("artifactType")]
    public string ArtifactType { get; set; } = "";

    [JsonPropertyName("assetVersionId")]
    public string AssetVersionID { get; set; } = "";

    [JsonPropertyName("resourceId")]
    public string ResourceID { get; set; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("metadataJson")]
    public string MetadataJSON { get; set; } = "";

    [JsonPropertyName("outputJson")]
    public string OutputJSON { get; set; } = "";
}

public sealed class ProjectDetailDto
{
    [JsonPropertyName("project")]
    public Project Project { get; init; } = new();

    [JsonPropertyName("units")]
    public IReadOnlyList<ProjectUnit> Units { get; init; } = [];

    [JsonPropertyName("canvases")]
    public IReadOnlyList<CanvasProject> Canvases { get; init; } = [];

    [JsonPropertyName("canvasUnitLinks")]
    public IReadOnlyList<CanvasUnitLink> CanvasUnitLinks { get; init; } = [];

    [JsonPropertyName("assets")]
    public IReadOnlyList<ProjectAssetSummaryDto> Assets { get; init; } = [];

    [JsonPropertyName("assetFolders")]
    public IReadOnlyList<ProjectAssetFolder> AssetFolders { get; init; } = [];

    [JsonPropertyName("workflows")]
    public IReadOnlyList<ProjectWorkflowDetailDto> Workflows { get; init; } = [];

    [JsonPropertyName("shots")]
    public IReadOnlyList<Shot> Shots { get; init; } = [];

    [JsonPropertyName("shotRevisions")]
    public IReadOnlyList<ShotRevision> ShotRevisions { get; init; } = [];

    [JsonPropertyName("shotArtifacts")]
    public IReadOnlyList<ShotArtifact> ShotArtifacts { get; init; } = [];

    [JsonPropertyName("shotReferences")]
    public IReadOnlyList<ShotAssetReference> ShotReferences { get; init; } = [];

    [JsonPropertyName("assetCandidates")]
    public IReadOnlyList<ProjectAssetCandidate> AssetCandidates { get; init; } = [];

    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskSummaryDto> Tasks { get; init; } = [];
}

public sealed class ProjectShotAssetReferenceVersionDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("assetId")]
    public string AssetID { get; init; } = "";

    [JsonPropertyName("version")]
    public long Version { get; init; }

    [JsonPropertyName("representations")]
    public IReadOnlyList<CharacterRepresentationSummaryDto> Representations { get; init; } = [];
}

public sealed class ProjectShotAssetReferenceDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("shotId")]
    public string ShotID { get; init; } = "";

    [JsonPropertyName("assetVersionId")]
    public string AssetVersionID { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("asset")]
    public ProjectAssetSummaryDto Asset { get; init; } = new();

    [JsonPropertyName("referencedVersion")]
    public ProjectShotAssetReferenceVersionDto ReferencedVersion { get; init; } = new();
}

public sealed class ProjectUnitWorkspaceDto
{
    [JsonPropertyName("unit")]
    public ProjectUnit Unit { get; init; } = new();

    [JsonPropertyName("workflows")]
    public IReadOnlyList<ProjectWorkflowDetailDto> Workflows { get; init; } = [];

    [JsonPropertyName("shots")]
    public IReadOnlyList<Shot> Shots { get; init; } = [];

    [JsonPropertyName("shotRevisions")]
    public IReadOnlyList<ShotRevision> ShotRevisions { get; init; } = [];

    [JsonPropertyName("shotArtifacts")]
    public IReadOnlyList<ShotArtifact> ShotArtifacts { get; init; } = [];

    [JsonPropertyName("shotReferences")]
    public IReadOnlyList<ProjectShotAssetReferenceDto> ShotReferences { get; init; } = [];

    [JsonPropertyName("assetCandidates")]
    public IReadOnlyList<ProjectAssetCandidate> AssetCandidates { get; init; } = [];

    [JsonPropertyName("assets")]
    public IReadOnlyList<ProjectAssetSummaryDto> Assets { get; init; } = [];

    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskSummaryDto> Tasks { get; init; } = [];
}

public sealed class ProjectCanvasPageDto
{
    [JsonPropertyName("canvases")]
    public IReadOnlyList<CanvasProject> Canvases { get; init; } = [];

    [JsonPropertyName("canvasUnitLinks")]
    public IReadOnlyList<CanvasUnitLink> CanvasUnitLinks { get; init; } = [];

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}

/// <summary>
/// 项目工作流 v2 与工作台聚合服务。
/// 对应 Go: <c>app/project.go</c>、<c>project_workflow.go</c> 与
/// <c>project_workbench_read.go</c>。
/// </summary>
public sealed class ProjectWorkflowService
{
    private const string BuiltinTemplateKey = "short-drama-production";
    private const long BuiltinTemplateVersion = 2;

    private static readonly (string Key, string Name)[] BuiltinSteps =
    [
        ("story", "剧情与章节"),
        ("assets", "资产拆分"),
        ("storyboard", "分镜脚本"),
        ("previz", "黑白动作预演"),
        ("video", "视频生成"),
        ("delivery", "交付与打包"),
    ];

    private readonly Repository _repository;
    private readonly ProjectAssetService _assets;
    private readonly ProjectAssetFolderService _assetFolders;
    private readonly TaskService _tasks;
    private readonly string _dataDir;

    public ProjectWorkflowService(
        Repository repository,
        ProjectAssetService assets,
        ProjectAssetFolderService assetFolders,
        TaskService tasks,
        string? dataDir = null)
    {
        _repository = repository;
        _assets = assets;
        _assetFolders = assetFolders;
        _tasks = tasks;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir;
    }

    public async Task EnsureBuiltinProjectWorkflowTemplateAsync(
        CancellationToken cancellationToken = default)
    {
        if (await _repository.WorkflowTemplateVersionAsync(
                BuiltinTemplateKey, BuiltinTemplateVersion, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        string definition = JsonSerializer.Serialize(new
        {
            scope = new[] { "project", "unit" },
            steps = BuiltinSteps.Select(step => new { key = step.Key, name = step.Name }),
        });
        try
        {
            await _repository.CreateWorkflowTemplateVersionAsync(new WorkflowTemplateVersion
            {
                ID = IdGenerator.NewId(),
                TemplateKey = BuiltinTemplateKey,
                Name = "短剧分镜工作流",
                Version = BuiltinTemplateVersion,
                DefinitionJSON = definition,
                CreatedAt = DateTime.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (await _repository.WorkflowTemplateVersionAsync(
                    BuiltinTemplateKey, BuiltinTemplateVersion, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }
        }
    }

    public async Task<ProjectWorkflowDetailDto> CreateProjectWorkflowAsync(
        string projectId, string unitId, string scope,
        CancellationToken cancellationToken = default)
    {
        await EnsureBuiltinProjectWorkflowTemplateAsync(cancellationToken).ConfigureAwait(false);
        WorkflowTemplateVersion template = await _repository.WorkflowTemplateVersionAsync(
            BuiltinTemplateKey, BuiltinTemplateVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        WorkflowInstance? existing = await _repository.WorkflowInstanceForScopeAsync(
            projectId, unitId, template.ID, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return await DetailAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        DateTime now = DateTime.UtcNow;
        WorkflowInstance instance = new()
        {
            ID = IdGenerator.NewId(),
            ProjectID = projectId,
            UnitID = unitId,
            TemplateVersionID = template.ID,
            Scope = scope,
            Status = WorkflowStatus.WorkflowStatusActive,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        List<WorkflowStepInstance> steps = [];
        for (int index = 0; index < BuiltinSteps.Length; index++)
        {
            (string key, string name) = BuiltinSteps[index];
            steps.Add(new WorkflowStepInstance
            {
                ID = IdGenerator.NewId(),
                WorkflowInstanceID = instance.ID,
                StepKey = key,
                Name = name,
                Position = index,
                Status = index == 0
                    ? WorkflowStepStatus.WorkflowStepStatusReady
                    : WorkflowStepStatus.WorkflowStepStatusPending,
                InputJSON = "{}",
                OutputJSON = "{}",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await _repository.CreateWorkflowInstanceAsync(instance, steps, cancellationToken).ConfigureAwait(false);
        await _repository.BumpProjectRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        return new ProjectWorkflowDetailDto { Instance = instance, Steps = steps };
    }

    public async Task<ProjectWorkflowDetailDto> CreateUnitWorkflowAsync(
        string userId, string projectId, string unitId,
        CancellationToken cancellationToken = default)
    {
        await RequireActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (await _repository.ProjectUnitAsync(projectId, unitId.Trim(), cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        return await CreateProjectWorkflowAsync(projectId, unitId.Trim(), "unit", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectWorkflowDetailDto>> ProjectWorkflowsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkflowInstance> instances = await _repository
            .ProjectWorkflowInstancesAsync(projectId, cancellationToken).ConfigureAwait(false);
        List<ProjectWorkflowDetailDto> result = [];
        foreach (WorkflowInstance instance in instances)
        {
            result.Add(await DetailAsync(instance, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<WorkflowStepInstance> UpdateWorkflowStepAsync(
        string userId, string projectId, string stepId, UpdateWorkflowStepRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        WorkflowStepInstance step = await _repository.WorkflowStepForProjectAsync(
            projectId, stepId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        string status = request.Status.Trim();
        if (!ValidWorkflowStepStatus(status))
        {
            throw AppError.BadAuthRequest("不支持的工作流步骤状态");
        }
        if (!CanTransitionWorkflowStep(step.Status, status))
        {
            throw AppError.BadAuthRequest("当前工作流步骤不能直接切换到目标状态");
        }

        DateTime now = DateTime.UtcNow;
        step.Status = status;
        step.OutputJSON = request.OutputJSON.Trim();
        if (step.OutputJSON.Length == 0)
        {
            step.OutputJSON = "{}";
        }
        step.Error = request.Error.Trim();
        if (status == WorkflowStepStatus.WorkflowStepStatusRunning && step.StartedAt is null)
        {
            step.StartedAt = now;
        }
        step.CompletedAt = status is WorkflowStepStatus.WorkflowStepStatusCompleted
            or WorkflowStepStatus.WorkflowStepStatusSkipped ? now : null;
        step.UpdatedAt = now;

        WorkflowInstance instance = await _repository.WorkflowInstanceAsync(step.WorkflowInstanceID,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("record not found");
        if (status == WorkflowStepStatus.WorkflowStepStatusCompleted)
        {
            await ValidateWorkflowStepCompletionAsync(projectId, instance, step, cancellationToken)
                .ConfigureAwait(false);
        }
        instance.Status = status == WorkflowStepStatus.WorkflowStepStatusFailed
            ? WorkflowStatus.WorkflowStatusFailed
            : WorkflowStatus.WorkflowStatusActive;
        instance.Revision++;
        instance.UpdatedAt = now;

        WorkflowStepInstance? next = null;
        if (status is WorkflowStepStatus.WorkflowStepStatusCompleted
            or WorkflowStepStatus.WorkflowStepStatusSkipped)
        {
            next = await _repository.NextWorkflowStepAsync(
                step.WorkflowInstanceID, step.Position, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                instance.Status = WorkflowStatus.WorkflowStatusCompleted;
            }
            else if (next.Status == WorkflowStepStatus.WorkflowStepStatusPending)
            {
                next.Status = WorkflowStepStatus.WorkflowStepStatusReady;
                next.UpdatedAt = now;
            }
        }
        await _repository.UpdateWorkflowProgressAsync(step, next, instance, projectId, cancellationToken)
            .ConfigureAwait(false);
        return step;
    }

    public async Task<WorkflowStepInstance> RegisterTaskOutputAsync(
        string userId, string projectId, string stepId, RegisterTaskOutputRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireActiveProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        TaskEntity task = await _repository.TaskForUserAsync(
            userId, request.TaskID.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");

        string canvasId = request.CanvasID.Trim();
        if (task.ProjectID != projectId)
        {
            CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(
                userId, task.ProjectID, cancellationToken).ConfigureAwait(false);
            if (canvas is null || canvas.ProjectID != projectId)
            {
                throw AppError.BadAuthRequest("任务不属于当前项目");
            }
            if (canvasId.Length > 0 && canvasId != canvas.ID)
            {
                throw AppError.BadAuthRequest("任务画布与产物画布不一致");
            }
            canvasId = canvas.ID;
        }
        if (canvasId.Length > 0)
        {
            CanvasProject? canvas = await _repository.CanvasProjectForUserAsync(
                userId, canvasId, cancellationToken).ConfigureAwait(false);
            if (canvas is null || canvas.ProjectID != projectId)
            {
                throw AppError.BadAuthRequest("画布不属于当前项目");
            }
        }
        if (task.Status != TaskStatus.TaskStatusSucceeded)
        {
            throw AppError.BadAuthRequest("只有成功任务才能登记产物");
        }

        WorkflowStepInstance step = await _repository.WorkflowStepForProjectAsync(
            projectId, stepId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (step.Status == WorkflowStepStatus.WorkflowStepStatusFailed)
        {
            throw AppError.BadAuthRequest("失败步骤不能登记成功产物");
        }

        string unitId = request.UnitID.Trim();
        string shotId = request.ShotID.Trim();
        Shot? shot = null;
        if (shotId.Length > 0)
        {
            shot = await _repository.ShotForProjectAsync(projectId, shotId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            if (unitId.Length == 0)
            {
                unitId = shot.UnitID;
            }
            else if (unitId != shot.UnitID)
            {
                throw AppError.BadAuthRequest("镜头不属于指定章节");
            }
        }
        else if (unitId.Length > 0 && await _repository.ProjectUnitAsync(
                     projectId, unitId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }

        string shotRevisionId = request.ShotRevisionID.Trim();
        if (shot is not null)
        {
            if (shotRevisionId.Length == 0)
            {
                shotRevisionId = shot.CurrentRevisionID;
            }
            else if (await _repository.ShotRevisionForShotAsync(
                         shot.ID, shotRevisionId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("record not found");
            }
        }
        else if (shotRevisionId.Length > 0)
        {
            throw AppError.BadAuthRequest("镜头版本缺少所属镜头");
        }

        string assetVersionId = request.AssetVersionID.Trim();
        if (assetVersionId.Length > 0 && await _repository.AssetVersionForProjectAsync(
                projectId, assetVersionId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        string resourceId = request.ResourceID.Trim();
        if (resourceId.Length > 0 && await _repository.ResourceForUserAsync(
                userId, resourceId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("record not found");
        }
        string metadata = request.MetadataJSON.Trim();
        if (metadata.Length == 0)
        {
            metadata = "{}";
        }
        try
        {
            using JsonDocument _ = JsonDocument.Parse(metadata);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("产物元数据必须是有效 JSON");
        }

        DateTime now = DateTime.UtcNow;
        step.Status = WorkflowStepStatus.WorkflowStepStatusCompleted;
        step.OutputJSON = request.OutputJSON.Trim();
        if (step.OutputJSON.Length == 0)
        {
            step.OutputJSON = task.ResultJSON;
        }
        if (step.OutputJSON.Trim().Length == 0)
        {
            step.OutputJSON = "{}";
        }
        step.Error = "";
        step.CompletedAt = now;
        step.UpdatedAt = now;
        WorkflowInstance instance = await _repository.WorkflowInstanceAsync(
            step.WorkflowInstanceID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        instance.Revision++;
        instance.Status = WorkflowStatus.WorkflowStatusActive;
        instance.UpdatedAt = now;

        WorkflowStepInstance? next = null;
        if (shot is null)
        {
            next = await _repository.NextWorkflowStepAsync(
                step.WorkflowInstanceID, step.Position, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                instance.Status = WorkflowStatus.WorkflowStatusCompleted;
            }
            else if (next.Status == WorkflowStepStatus.WorkflowStepStatusPending)
            {
                next.Status = WorkflowStepStatus.WorkflowStepStatusReady;
                next.UpdatedAt = now;
            }
        }
        else
        {
            step.Status = WorkflowStepStatus.WorkflowStepStatusRunning;
            step.CompletedAt = null;
        }

        AssetRepresentation? representation = null;
        if (assetVersionId.Length > 0)
        {
            string role = request.Role.Trim();
            if (role.Length == 0)
            {
                role = "output";
            }
            if (!ValidShotAssetRole(role))
            {
                throw AppError.BadAuthRequest("不支持的产物用途");
            }
            representation = new AssetRepresentation
            {
                ID = IdGenerator.NewId(),
                TaskID = task.ID,
                AssetVersionID = assetVersionId,
                ResourceID = resourceId,
                MediaType = request.MediaType.Trim(),
                Role = role,
                MetadataJSON = metadata,
                CreatedAt = now,
            };
        }

        string artifactType = request.ArtifactType.Trim();
        if (artifactType.Length == 0 && shot is not null && resourceId.Length > 0)
        {
            artifactType = WorkflowArtifactType(step.StepKey);
        }
        ProductionTaskLink productionLink = new()
        {
            ID = IdGenerator.NewId(),
            TaskID = task.ID,
            ProjectID = projectId,
            CanvasID = canvasId,
            UnitID = unitId,
            ShotID = shotId,
            WorkflowStepID = step.ID,
            ArtifactType = artifactType,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ShotArtifact? artifact = null;
        if (shot is not null && resourceId.Length > 0 && artifactType.Length > 0)
        {
            artifact = new ShotArtifact
            {
                ID = IdGenerator.NewId(),
                ProjectID = projectId,
                UnitID = shot.UnitID,
                ShotID = shot.ID,
                RevisionID = shotRevisionId,
                TaskID = task.ID,
                Type = artifactType,
                ResourceID = resourceId,
                Status = "ready",
                Selected = true,
                MetadataJSON = metadata,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        await _repository.RegisterWorkflowTaskOutputAsync(
            step, next, instance, projectId,
            new WorkflowStepTask
            {
                ID = IdGenerator.NewId(), WorkflowStepID = step.ID, TaskID = task.ID, CreatedAt = now,
            }, representation, productionLink, artifact, cancellationToken).ConfigureAwait(false);
        return step;
    }

    public async Task<ProjectDetailDto> ProjectDetailAsync(
        string userId, string projectId, CancellationToken cancellationToken = default)
    {
        Project project = await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TaskEntity> successful = await _repository
            .SuccessfulWorkflowTasksForProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        foreach (TaskEntity task in successful)
        {
            try
            {
                await RegisterTaskOutputFromTaskAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Read-side compensation is best effort; one stale task must not hide the project.
            }
        }

        IReadOnlyList<ProjectUnit> units = await _repository.ProjectUnitSummariesAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasProject> canvases = await _repository.ProjectCanvasSummariesAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasUnitLink> canvasUnitLinks = await _repository.ProjectCanvasUnitLinksAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetSummaryDto> assets = await _assets.ProjectAssetsAsync(userId, projectId, cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetFolder> folders = await _assetFolders.ProjectAssetFoldersAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectWorkflowDetailDto> workflows = await ProjectWorkflowsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Shot> shots = await _repository.ProjectShotsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotRevision> revisions = await _repository.ProjectShotRevisionsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotArtifact> artifacts = await _repository.ProjectShotArtifactsAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotAssetReference> references = await _repository.ProjectShotAssetReferencesAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetCandidate> candidates = await _repository.ProjectAssetCandidatesAsync(projectId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TaskSummaryDto> tasks = await _tasks.TasksWithOptionsAsync(
            userId, new TaskListOptions { Limit = 100, ProjectID = projectId }, cancellationToken).ConfigureAwait(false);
        return new ProjectDetailDto
        {
            Project = project,
            Units = units,
            Canvases = canvases,
            CanvasUnitLinks = canvasUnitLinks,
            Assets = assets,
            AssetFolders = folders,
            Workflows = workflows,
            Shots = shots,
            ShotRevisions = revisions,
            ShotArtifacts = artifacts,
            ShotReferences = references,
            AssetCandidates = candidates,
            Tasks = tasks,
        };
    }

    public async Task<ProjectUnitWorkspaceDto> ProjectUnitWorkspaceAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        ProjectUnit unit = await _repository.ProjectUnitAsync(projectId, unitId.Trim(), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("record not found");
        IReadOnlyList<Shot> shots = await _repository.ProjectUnitShotsAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotRevision> revisions = await _repository.ProjectUnitShotRevisionsAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotArtifact> artifacts = await _repository.ProjectUnitShotArtifactsAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ShotAssetReference> references = await _repository.ProjectUnitShotAssetReferencesAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectAssetCandidate> candidates = await _repository.ProjectUnitAssetCandidatesAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Asset> referencedAssets = await _repository.ProjectUnitAssetsAsync(userId, projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        List<ProjectAssetSummaryDto> assetSummaries = [];
        foreach (Asset asset in referencedAssets)
        {
            assetSummaries.Add(await _assets.BuildSummaryAsync(userId, projectId, asset, cancellationToken).ConfigureAwait(false));
        }
        IReadOnlyList<ProjectWorkflowDetailDto> workflows = [];
        IReadOnlyList<WorkflowInstance> instances = await _repository
            .ProjectWorkflowInstancesForUnitAsync(projectId, unit.ID, cancellationToken).ConfigureAwait(false);
        List<ProjectWorkflowDetailDto> workflowList = [];
        foreach (WorkflowInstance instance in instances)
        {
            workflowList.Add(await DetailAsync(instance, cancellationToken).ConfigureAwait(false));
        }
        workflows = workflowList;

        List<ProjectShotAssetReferenceDto> enrichedReferences = await EnrichReferencesAsync(
            userId, projectId, references, assetSummaries, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TaskSummaryDto> recent = await _tasks.TasksWithOptionsAsync(
            userId, new TaskListOptions { Limit = 100, ProjectID = projectId }, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TaskSummaryDto> active = await _tasks.TasksWithOptionsAsync(
            userId, new TaskListOptions { Limit = 100, ProjectID = projectId, ActiveOnly = true }, cancellationToken).ConfigureAwait(false);
        HashSet<string> shotIds = shots.Select(shot => shot.ID).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, TaskSummaryDto> taskById = new(StringComparer.Ordinal);
        foreach (TaskSummaryDto task in active.Concat(recent))
        {
            taskById.TryAdd(task.ID, task);
        }
        List<TaskSummaryDto> tasks = [];
        foreach (TaskSummaryDto task in taskById.Values)
        {
            TaskClientContextDto? context = task.ClientContext;
            if (context is null)
            {
                continue;
            }
            if (context.ChapterID == unit.ID || (context.ShotID.Length > 0 && shotIds.Contains(context.ShotID)))
            {
                tasks.Add(task);
            }
        }
        return new ProjectUnitWorkspaceDto
        {
            Unit = unit,
            Workflows = workflows,
            Shots = shots,
            ShotRevisions = revisions,
            ShotArtifacts = artifacts,
            ShotReferences = enrichedReferences,
            AssetCandidates = candidates,
            Assets = assetSummaries,
            Tasks = tasks,
        };
    }

    public async Task<ProjectCanvasPageDto> ProjectCanvasesPageAsync(
        string userId, string projectId, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        (page, pageSize) = NormalizePage(page, pageSize, 100);
        (IReadOnlyList<CanvasProject> canvases, long total) = await _repository
            .ProjectCanvasSummariesPageAsync(userId, projectId, page, pageSize, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CanvasUnitLink> links = await _repository.ProjectCanvasUnitLinksForCanvasesAsync(
            projectId, canvases.Select(canvas => canvas.ID).ToList(), cancellationToken).ConfigureAwait(false);
        return new ProjectCanvasPageDto
        {
            Canvases = canvases,
            CanvasUnitLinks = links,
            Page = page,
            PageSize = pageSize,
            Total = total,
            HasMore = (long)page * pageSize < total,
        };
    }

    private async Task<ProjectWorkflowDetailDto> DetailAsync(
        WorkflowInstance instance, CancellationToken cancellationToken)
    {
        return new ProjectWorkflowDetailDto
        {
            Instance = instance,
            Steps = await _repository.WorkflowStepsAsync(instance.ID, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<List<ProjectShotAssetReferenceDto>> EnrichReferencesAsync(
        string userId, string projectId, IReadOnlyList<ShotAssetReference> references,
        IReadOnlyList<ProjectAssetSummaryDto> summaries, CancellationToken cancellationToken)
    {
        List<string> versionIds = references.Select(reference => reference.AssetVersionID).ToList();
        IReadOnlyList<AssetVersion> versions = await _repository.ProjectAssetVersionsByIDsAsync(
            projectId, versionIds, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AssetRepresentation> stored = await _repository.AssetRepresentationsByVersionIDsAsync(
            versionIds, cancellationToken).ConfigureAwait(false);
        Dictionary<string, AssetVersion> versionById = versions.ToDictionary(version => version.ID, StringComparer.Ordinal);
        Dictionary<string, ProjectAssetSummaryDto> assetById = summaries.ToDictionary(asset => asset.ID, StringComparer.Ordinal);
        Dictionary<string, List<CharacterRepresentationSummaryDto>> representationByVersion = new(StringComparer.Ordinal);
        foreach (AssetRepresentation representation in stored)
        {
            if (!representationByVersion.TryGetValue(representation.AssetVersionID, out List<CharacterRepresentationSummaryDto>? list))
            {
                list = [];
                representationByVersion[representation.AssetVersionID] = list;
            }
            list.Add(new CharacterRepresentationSummaryDto
            {
                ID = representation.ID,
                ResourceID = representation.ResourceID,
                MediaType = representation.MediaType,
                Role = representation.Role,
            });
        }
        List<ProjectShotAssetReferenceDto> result = [];
        foreach (ShotAssetReference reference in references)
        {
            if (!versionById.TryGetValue(reference.AssetVersionID, out AssetVersion? version))
            {
                throw AppError.BadAuthRequest("镜头引用的资产版本不可用");
            }
            if (!assetById.TryGetValue(version.AssetID, out ProjectAssetSummaryDto? asset))
            {
                throw AppError.BadAuthRequest("镜头引用的项目资产不可用");
            }
            result.Add(new ProjectShotAssetReferenceDto
            {
                ID = reference.ID,
                ShotID = reference.ShotID,
                AssetVersionID = reference.AssetVersionID,
                Role = reference.Role,
                Status = reference.Status,
                CreatedAt = reference.CreatedAt,
                Asset = asset,
                ReferencedVersion = new ProjectShotAssetReferenceVersionDto
                {
                    ID = version.ID,
                    AssetID = version.AssetID,
                    Version = version.Version,
                    Representations = representationByVersion.GetValueOrDefault(version.ID) ?? [],
                },
            });
        }
        return result;
    }

    private async Task<Project> RequireProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken)
    {
        return await _repository.ProjectForUserAsync(userId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
    }

    private async Task RequireActiveProjectAsync(
        string userId, string projectId, CancellationToken cancellationToken)
    {
        Project project = await RequireProjectAsync(userId, projectId, cancellationToken).ConfigureAwait(false);
        if (project.Status == ProjectStatus.ProjectStatusArchived)
        {
            throw AppError.BadAuthRequest("项目已归档，不能修改短剧生产数据");
        }
    }

    private async Task ValidateWorkflowStepCompletionAsync(
        string projectId, WorkflowInstance instance, WorkflowStepInstance step,
        CancellationToken cancellationToken)
    {
        if (instance.UnitID.Trim().Length == 0)
        {
            return;
        }
        ProjectUnit unit = await _repository.ProjectUnitAsync(
            projectId, instance.UnitID, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        switch (step.StepKey)
        {
            case "story" when unit.SourceText.Trim().Length == 0:
                throw AppError.BadAuthRequest("章节正文为空，不能完成剧情阶段");
            case "assets":
            {
                IReadOnlyList<ProjectAssetCandidate> candidates = await _repository.ProjectAssetCandidatesAsync(
                    projectId, cancellationToken).ConfigureAwait(false);
                if (candidates.Any(candidate => candidate.UnitID == instance.UnitID
                    && candidate.Status == "pending_confirmation"))
                {
                    throw AppError.BadAuthRequest("仍有待确认资产，不能完成资产拆分阶段");
                }
                break;
            }
            case "storyboard":
            case "previz":
            case "video":
            case "delivery":
            {
                IReadOnlyList<Shot> shots = await _repository.ProjectShotsAsync(projectId, cancellationToken).ConfigureAwait(false);
                List<Shot> unitShots = shots.Where(shot => shot.UnitID == instance.UnitID).ToList();
                if (unitShots.Count == 0)
                {
                    throw AppError.BadAuthRequest("当前章节还没有分镜，不能完成本阶段");
                }
                if (step.StepKey == "storyboard")
                {
                    if (unitShots.Any(shot => shot.CurrentRevisionID.Trim().Length == 0))
                    {
                        throw AppError.BadAuthRequest("存在没有分镜版本的镜头，不能完成分镜阶段");
                    }
                    break;
                }
                string type = step.StepKey is "video" or "delivery" ? "video" : "action_board";
                IReadOnlyList<ShotArtifact> artifacts = await _repository.ProjectShotArtifactsAsync(projectId, cancellationToken).ConfigureAwait(false);
                HashSet<string> ready = artifacts.Where(artifact => artifact.UnitID == instance.UnitID
                    && artifact.Type == type && artifact.Selected && artifact.Status == "ready")
                    .Select(artifact => artifact.ShotID).ToHashSet(StringComparer.Ordinal);
                if (ready.Count != unitShots.Count)
                {
                    throw AppError.BadAuthRequest(type == "action_board"
                        ? "仍有镜头缺少已通过的动作预演，不能完成本阶段"
                        : "仍有镜头缺少可交付视频，不能完成本阶段");
                }
                break;
            }
        }
    }

    private async Task RegisterTaskOutputFromTaskAsync(
        TaskEntity task, CancellationToken cancellationToken)
    {
        if (task.ProjectID.Trim().Length == 0 || task.Status != TaskStatus.TaskStatusSucceeded
            || task.InputJSON.Trim().Length == 0)
        {
            return;
        }
        JsonElement input = DecryptTaskInput(task.InputJSON);
        if (input.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        string workflowStepId = ReadString(input, "workflowStepId");
        JsonElement metadata = input.TryGetProperty("metadata", out JsonElement metadataValue)
            && metadataValue.ValueKind == JsonValueKind.Object ? metadataValue : default;
        workflowStepId = FirstNonEmpty(workflowStepId, ReadString(metadata, "workflowStepId"));
        if (workflowStepId.Length == 0)
        {
            return;
        }
        string projectId = FirstNonEmpty(ReadString(input, "domainProjectId"), ReadString(metadata, "domainProjectId"));
        if (projectId.Length == 0 && await _repository.ProjectForUserAsync(
                task.UserID, task.ProjectID, cancellationToken).ConfigureAwait(false) is not null)
        {
            projectId = task.ProjectID;
        }
        if (projectId.Length == 0)
        {
            return;
        }
        string resourceId = FirstNonEmpty(ReadString(input, "resourceId"), ReadString(metadata, "resourceId"));
        string mediaType = FirstNonEmpty(ReadString(input, "mediaType"), ReadString(metadata, "mediaType"));
        if (resourceId.Length == 0)
        {
            (resourceId, mediaType) = CloudAgentMediaService.TaskOutputResource(task.ResultJSON, task.Type);
        }
        if (mediaType.Length == 0 && resourceId.Length > 0)
        {
            mediaType = task.Type.Contains("video", StringComparison.OrdinalIgnoreCase) ? "video" : "image";
        }
        string shotId = FirstNonEmpty(ReadString(input, "shotId"), ReadString(metadata, "shotId"));
        string assetVersionId = FirstNonEmpty(ReadString(input, "assetVersionId"), ReadString(metadata, "assetVersionId"));
        if (resourceId.Length > 0 && assetVersionId.Length == 0 && shotId.Length > 0)
        {
            assetVersionId = await EnsureGeneratedProjectAssetAsync(
                task, projectId, shotId, resourceId, mediaType, cancellationToken).ConfigureAwait(false);
        }
        string metadataJson = ReadString(input, "metadataJson");
        if (metadataJson.Length == 0 && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("artifactMetadata", out JsonElement artifactMetadata))
        {
            metadataJson = artifactMetadata.GetRawText();
        }
        await RegisterTaskOutputAsync(task.UserID, projectId, workflowStepId, new RegisterTaskOutputRequest
        {
            TaskID = task.ID,
            CanvasID = FirstNonEmpty(ReadString(input, "canvasId"), ReadString(metadata, "canvasId")),
            UnitID = FirstNonEmpty(ReadString(input, "unitId"), ReadString(metadata, "unitId")),
            ShotID = shotId,
            ShotRevisionID = FirstNonEmpty(ReadString(input, "shotRevisionId"), ReadString(metadata, "shotRevisionId")),
            ArtifactType = FirstNonEmpty(ReadString(input, "artifactType"), ReadString(metadata, "artifactType")),
            AssetVersionID = assetVersionId,
            ResourceID = resourceId,
            MediaType = mediaType,
            Role = FirstNonEmpty(ReadString(input, "role"), ReadString(metadata, "role")),
            MetadataJSON = metadataJson,
            OutputJSON = task.ResultJSON,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> EnsureGeneratedProjectAssetAsync(
        TaskEntity task, string projectId, string shotId, string resourceId, string mediaType,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetRepresentation> existing = await _repository.AssetRepresentationsForTaskAsync(
            task.ID, cancellationToken).ConfigureAwait(false);
        AssetRepresentation? prior = existing.FirstOrDefault(item => item.Role == "output"
            && item.ResourceID == resourceId && item.AssetVersionID.Length > 0);
        if (prior is not null)
        {
            return prior.AssetVersionID;
        }
        Resource resource = await _repository.ResourceForUserAsync(
            task.UserID, resourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw AppError.BadAuthRequest("生成资源尚未就绪，无法登记项目素材");
        }
        Shot shot = await _repository.ShotForProjectAsync(
            projectId, shotId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        DateTime now = DateTime.UtcNow;
        string assetId = WorkflowGeneratedEntityID("asset", task.ID);
        string versionId = WorkflowGeneratedEntityID("version", task.ID);
        string label = mediaType == "video" ? "视频" : "图片";
        string title = shot.Title.Trim();
        if (title.Length == 0)
        {
            title = "镜头产物";
        }
        title += " · " + label;
        long width = resource.Width > 0 ? resource.Width : 1;
        long height = resource.Height > 0 ? resource.Height : 1;
        string resourceUrl = "/api/resources/" + resourceId + "/file";
        Dictionary<string, object?> data = new()
        {
            ["storageKey"] = "resource:" + resourceId,
            ["mimeType"] = resource.MimeType,
            ["bytes"] = resource.Size,
            ["width"] = width,
            ["height"] = height,
        };
        if (mediaType == "image")
        {
            data["dataUrl"] = resourceUrl;
        }
        else
        {
            data["url"] = resourceUrl;
            if (resource.DurationMs > 0)
            {
                data["durationMs"] = resource.DurationMs;
            }
        }
        string payload = JsonSerializer.Serialize(new
        {
            id = assetId,
            kind = mediaType,
            category = AssetCategory.AssetCategoryMaterial,
            status = AssetVersionStatus.AssetVersionStatusConfirmed,
            primaryVersionId = versionId,
            title,
            coverUrl = resourceUrl,
            tags = Array.Empty<string>(),
            createdAt = ProjectService.FormatRfc3339Nano(now),
            updatedAt = ProjectService.FormatRfc3339Nano(now),
            data,
            metadata = new { source = "short-drama-workflow", taskId = task.ID, shotId },
        }, ProjectCharacterService.GoPayloadOptions);
        await _repository.CreateGeneratedProjectAssetAsync(
            new Asset
            {
                ID = assetId,
                UserID = task.UserID,
                Kind = mediaType,
                Category = AssetCategory.AssetCategoryMaterial,
                Status = AssetVersionStatus.AssetVersionStatusConfirmed,
                PrimaryVersionID = versionId,
                Title = title,
                PayloadJSON = payload,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new AssetVersion
            {
                ID = versionId,
                AssetID = assetId,
                Version = 1,
                Status = AssetVersionStatus.AssetVersionStatusConfirmed,
                DefinitionJSON = "{}",
                Prompt = task.Prompt,
                Note = "工作流生成产物",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new ProjectAssetLink
            {
                ID = IdGenerator.NewId(),
                ProjectID = projectId,
                AssetID = assetId,
                FolderID = "",
                Position = await _repository.NextProjectAssetPositionAsync(projectId, "", cancellationToken).ConfigureAwait(false),
                CreatedAt = now,
            }, cancellationToken).ConfigureAwait(false);
        return versionId;
    }

    private JsonElement DecryptTaskInput(string raw)
    {
        try
        {
            JsonElement value = JsonSerializer.Deserialize<JsonElement>(raw);
            return DecryptSecrets(value);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private JsonElement DecryptSecrets(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (TaskCreationService.IsTaskSecretFieldPublic(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = JsonSerializer.SerializeToElement(
                        SettingsCrypto.DecryptSecret(property.Value.GetString() ?? "", _dataDir));
                }
                else
                {
                    result[property.Name] = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        ? DecryptSecrets(property.Value)
                        : property.Value.Clone();
                }
            }
            return JsonSerializer.SerializeToElement(result);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(DecryptSecrets).ToList());
        }
        return value.Clone();
    }

    private static string ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement element)
        && element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? "" : "";

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? "";

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize, int maximum) =>
        (Math.Max(page, 1), Math.Clamp(pageSize < 1 ? 40 : pageSize, 1, maximum));

    private static bool ValidWorkflowStepStatus(string status) => status is
        WorkflowStepStatus.WorkflowStepStatusPending or WorkflowStepStatus.WorkflowStepStatusReady
        or WorkflowStepStatus.WorkflowStepStatusRunning or WorkflowStepStatus.WorkflowStepStatusReview
        or WorkflowStepStatus.WorkflowStepStatusCompleted or WorkflowStepStatus.WorkflowStepStatusFailed
        or WorkflowStepStatus.WorkflowStepStatusSkipped;

    private static bool CanTransitionWorkflowStep(string current, string next)
    {
        if (current == next)
        {
            return true;
        }
        return current switch
        {
            WorkflowStepStatus.WorkflowStepStatusPending => next is WorkflowStepStatus.WorkflowStepStatusReady or WorkflowStepStatus.WorkflowStepStatusSkipped,
            WorkflowStepStatus.WorkflowStepStatusReady => next is WorkflowStepStatus.WorkflowStepStatusRunning or WorkflowStepStatus.WorkflowStepStatusSkipped,
            WorkflowStepStatus.WorkflowStepStatusRunning => next is WorkflowStepStatus.WorkflowStepStatusReview or WorkflowStepStatus.WorkflowStepStatusCompleted or WorkflowStepStatus.WorkflowStepStatusFailed,
            WorkflowStepStatus.WorkflowStepStatusReview => next is WorkflowStepStatus.WorkflowStepStatusRunning or WorkflowStepStatus.WorkflowStepStatusCompleted or WorkflowStepStatus.WorkflowStepStatusFailed,
            WorkflowStepStatus.WorkflowStepStatusFailed => next is WorkflowStepStatus.WorkflowStepStatusReady or WorkflowStepStatus.WorkflowStepStatusRunning,
            WorkflowStepStatus.WorkflowStepStatusCompleted => next == WorkflowStepStatus.WorkflowStepStatusRunning,
            WorkflowStepStatus.WorkflowStepStatusSkipped => next == WorkflowStepStatus.WorkflowStepStatusReady,
            _ => false,
        };
    }

    private static bool ValidShotAssetRole(string role) => role is
        "reference" or "start_frame" or "end_frame" or "keyframe" or "storyboard" or "output";

    private static string WorkflowArtifactType(string stepKey) => stepKey switch
    {
        "storyboard" => "storyboard",
        "previz" => "action_board",
        "video" => "video",
        "delivery" => "delivery",
        _ => "",
    };

    private static string WorkflowGeneratedEntityID(string namespaceValue, string taskId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(namespaceValue.Trim() + ":" + taskId.Trim()));
        return Convert.ToHexString(digest[..16]).ToLowerInvariant();
    }
}
