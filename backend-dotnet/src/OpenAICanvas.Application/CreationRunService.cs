#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>创作执行控制权。对应 Go: <c>app.CreationGuard</c>。</summary>
public sealed class CreationGuardDto
{
    [JsonPropertyName("executionEpoch")]
    public long ExecutionEpoch { get; set; }

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";
}

/// <summary>画布操作。对应 Go: <c>app.CreationCanvasOp</c>。</summary>
public sealed class CreationCanvasOpDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("id")]
    [GoOmitEmpty]
    public string ID { get; set; } = "";

    [JsonPropertyName("nodeType")]
    [GoOmitEmpty]
    public string NodeType { get; set; } = "";

    [JsonPropertyName("metadata")]
    [GoOmitEmpty]
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    [JsonPropertyName("patch")]
    [GoOmitEmpty]
    public Dictionary<string, JsonElement>? Patch { get; set; }

    [JsonPropertyName("fromNodeId")]
    [GoOmitEmpty]
    public string FromNodeID { get; set; } = "";

    [JsonPropertyName("fromHandleId")]
    [GoOmitEmpty]
    public string FromHandleID { get; set; } = "";

    [JsonPropertyName("toNodeId")]
    [GoOmitEmpty]
    public string ToNodeID { get; set; } = "";

    [JsonPropertyName("toHandleId")]
    [GoOmitEmpty]
    public string ToHandleID { get; set; } = "";

    [JsonPropertyName("ids")]
    [GoOmitEmpty]
    public List<string>? IDs { get; set; }

    [JsonPropertyName("title")]
    [GoOmitEmpty]
    public string Title { get; set; } = "";

    [JsonPropertyName("position")]
    [GoOmitEmpty]
    public Dictionary<string, JsonElement>? Position { get; set; }

    [JsonPropertyName("x")]
    [GoOmitEmpty]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    [GoOmitEmpty]
    public double? Y { get; set; }

    [JsonPropertyName("width")]
    [GoOmitEmpty]
    public double? Width { get; set; }

    [JsonPropertyName("height")]
    [GoOmitEmpty]
    public double? Height { get; set; }
}

/// <summary>创作请求。对应 Go: <c>app.CreationRequest</c>。</summary>
public sealed class CreationRequestDto
{
    [JsonPropertyName("executionEpoch")]
    public long ExecutionEpoch { get; set; }

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    [JsonPropertyName("clientKey")]
    public string ClientKey { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("expectedEpoch")]
    public long ExpectedEpoch { get; set; }

    [JsonPropertyName("state")]
    public Dictionary<string, JsonElement>? State { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("proposalVersion")]
    public long ProposalVersion { get; set; }

    [JsonPropertyName("proposal")]
    public JsonElement? Proposal { get; set; }

    [JsonPropertyName("ops")]
    public List<CreationCanvasOpDto>? Ops { get; set; }

    [JsonPropertyName("itemKey")]
    public string ItemKey { get; set; } = "";

    [JsonPropertyName("request")]
    public CreateTaskRequestDto? Request { get; set; }

    [JsonPropertyName("submissionIds")]
    public List<string>? SubmissionIDs { get; set; }

    [JsonPropertyName("submissionId")]
    public string SubmissionID { get; set; } = "";

    [JsonPropertyName("expectedSnapshotHash")]
    public string ExpectedSnapshotHash { get; set; } = "";

    [JsonPropertyName("document")]
    public JsonElement? Document { get; set; }
}

/// <summary>运行输出（含展开 state）。对应 Go: <c>app.CreationRunOutput</c>。</summary>
public sealed class CreationRunOutputDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("canvasId")]
    public string CanvasID { get; set; } = "";

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("executionEpoch")]
    public long ExecutionEpoch { get; set; }

    [JsonPropertyName("executionOwner")]
    public string ExecutionOwner { get; set; } = "";

    [JsonPropertyName("leaseExpiresAt")]
    [GoOmitEmpty]
    public DateTime? LeaseExpiresAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("state")]
    public JsonElement State { get; init; }

    [JsonPropertyName("approvedProposalVersion")]
    public long ApprovedProposalVersion { get; set; }

    [JsonPropertyName("approvedAt")]
    [GoOmitEmpty]
    public DateTime? ApprovedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>报价。对应 Go: <c>app.CreationQuote</c>。</summary>
public sealed class CreationQuoteDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("billingMode")]
    public string BillingMode { get; set; } = "";

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; set; }

    [JsonPropertyName("estimated")]
    public bool Estimated { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("quoteHash")]
    public string QuoteHash { get; set; } = "";

    [JsonPropertyName("options")]
    [GoOmitEmpty]
    public Dictionary<string, JsonElement>? Options { get; set; }
}

/// <summary>提交输出。对应 Go: <c>app.CreationSubmissionOutput</c>。</summary>
public sealed class CreationSubmissionOutputDto
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = "";

    [JsonPropertyName("runId")]
    public string RunID { get; set; } = "";

    [JsonPropertyName("itemKey")]
    public string ItemKey { get; set; } = "";

    [JsonPropertyName("proposalVersion")]
    public long ProposalVersion { get; set; }

    [JsonPropertyName("requestHash")]
    public string RequestHash { get; set; } = "";

    [JsonPropertyName("quote")]
    public CreationQuoteDto Quote { get; set; } = new();

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("approvedAt")]
    [GoOmitEmpty]
    public DateTime? ApprovedAt { get; set; }

    [JsonPropertyName("revokedAt")]
    [GoOmitEmpty]
    public DateTime? RevokedAt { get; set; }

    [JsonPropertyName("taskId")]
    [GoOmitEmpty]
    public string? TaskID { get; set; }
}

/// <summary>详情。对应 Go: <c>app.CreationDetail</c>。</summary>
public sealed class CreationDetailDto
{
    [JsonPropertyName("run")]
    public CreationRunOutputDto Run { get; set; } = new();

    [JsonPropertyName("submissions")]
    public List<CreationSubmissionOutputDto> Submissions { get; set; } = [];
}

/// <summary>
/// 智能创作运行。对应 Go: <c>internal/app/creation.go</c>。
/// </summary>
/// <remarks>
/// 画布提交三路由（canvas / canvas-snapshot / canvas-commit）属
/// <c>creation_canvas.go</c>，随下一批接入（PENDING #63）。
/// </remarks>
public sealed partial class CreationRunService
{
    private const string CreationConflictMessage = "创作状态已变化，请重新读取后继续";

    private static readonly HashSet<string> RunStatuses = new(StringComparer.Ordinal)
    {
        "idle", "running", "waiting_answer", "waiting_proposal", "waiting_canvas",
        "waiting_payment", "waiting_task", "paused", "completed", "cancelled",
    };

    private static readonly HashSet<string> NodeTypes = new(StringComparer.Ordinal)
    {
        "text", "markdown", "frame", "batch-table", "script",
    };

    private readonly Repository _repository;
    private readonly TaskCreationService _creations;
    private readonly IRuntimePolicyProvider _runtimePolicy;
    internal UserDataService? UserData { get; set; }

    public CreationRunService(
        Repository repository,
        TaskCreationService creations,
        IRuntimePolicyProvider? runtimePolicy = null)
    {
        _repository = repository;
        _creations = creations;
        _runtimePolicy = runtimePolicy ?? new DefaultRuntimePolicyProvider();
    }

    // ------------------------------------------------------------ 读取

    /// <summary>创建/幂等创建运行。对应 Go: <c>CreateCreationRun</c>。</summary>
    public async Task<CreationDetailDto> CreateAsync(
        string userId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(request.ClientKey)
            || request.ClientKey.Length > 120)
        {
            throw AppError.BadAuthRequest("缺少稳定会话键");
        }
        ValidateCreationJson(SerializeForValidation(request.State));
        if (request.CanvasID.Length > 0
            && await _repository.CanvasProjectForUserAsync(userId, request.CanvasID, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            throw CreationNotFound();
        }
        string stateJson = JsonSerializer.Serialize(
            ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(request.State ?? new Dictionary<string, JsonElement>())),
            ProjectCharacterService.GoPayloadOptions);
        CreationRun run = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            ClientKey = request.ClientKey,
            CreateHash = CreationHash(new object?[] { request.CanvasID, ParseJson(stateJson) }),
            CanvasID = request.CanvasID,
            Revision = 1,
            Status = "idle",
            StateJSON = stateJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        CreationRun? old = await _repository
            .CreationRunByClientKeyAsync(userId, request.ClientKey, cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            if (old.CreateHash != run.CreateHash)
            {
                throw CreationConflict();
            }
            return await GetAsync(userId, old.ID, cancellationToken).ConfigureAwait(false);
        }
        await ValidateCreationStorageAsync(userId, creating: true, delta: stateJson.Length, cancellationToken)
            .ConfigureAwait(false);
        CreationRun? created = await _repository
            .CreateCreationRunAsync(run, cancellationToken).ConfigureAwait(false);
        if (created is null)
        {
            throw CreationConflict();
        }
        return await GetAsync(userId, created.ID, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取运行详情。对应 Go: <c>GetCreationRun</c>。</summary>
    public async Task<CreationDetailDto> GetAsync(
        string userId, string id, CancellationToken cancellationToken = default)
    {
        CreationRun? run = await _repository.CreationRunAsync(userId, id, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();
        IReadOnlyList<CreationSubmission> items = await _repository
            .CreationSubmissionsAsync(userId, id, cancellationToken).ConfigureAwait(false);
        return new CreationDetailDto
        {
            Run = RunOutput(run),
            Submissions = items.Select(SubmissionOutput).ToList(),
        };
    }

    /// <summary>运行列表。对应 Go: <c>ListCreationRuns</c>。</summary>
    public async Task<List<CreationRunOutputDto>> ListAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CreationRun> items = await _repository
            .CreationRunsAsync(userId, cancellationToken).ConfigureAwait(false);
        return items.Select(RunOutput).ToList();
    }

    // ------------------------------------------------------------ 变更

    /// <summary>运行互斥变更分发。对应 Go: <c>ChangeCreationRun</c>。</summary>
    public Task<object?> ChangeAsync(
        string userId, string id, string action, CreationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return _repository.MutateCreationRunAsync<object?>(
            userId, id,
            async (run, tx) =>
            {
                int previousBytes = run.StateJSON.Length + run.ApprovedOperationsJSON.Length
                    + run.ApprovedCanvasJSON.Length;
                DateTime now = DateTime.UtcNow;
                object? result = run;
                if (action == "claim")
                {
                    if (request.Owner.Length == 0 || request.Owner.Length > 120
                        || request.ExpectedEpoch != run.ExecutionEpoch)
                    {
                        throw CreationConflict();
                    }
                    run.ExecutionEpoch++;
                    run.ExecutionOwner = request.Owner;
                    DateTime until = now.AddSeconds(45);
                    run.LeaseExpiresAt = until;
                }
                else
                {
                    ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
                    switch (action)
                    {
                        case "heartbeat":
                            run.LeaseExpiresAt = now.AddSeconds(45);
                            return new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["leaseExpiresAt"] = run.LeaseExpiresAt,
                            };
                        case "release":
                            run.LeaseExpiresAt = null;
                            return new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["released"] = true,
                            };
                        case "save":
                            if (request.Revision != run.Revision)
                            {
                                throw CreationConflict();
                            }
                            ValidateCreationJson(SerializeForValidation(request.State));
                            if (request.Status.Length == 0 || !RunStatuses.Contains(request.Status))
                            {
                                throw AppError.BadAuthRequest("创作状态无效");
                            }
                            if (run.Status == "cancelled" && request.Status != "cancelled")
                            {
                                throw CreationConflict();
                            }
                            run.StateJSON = JsonSerializer.Serialize(
                                ProjectCharacterService.SortedElement(
                                    JsonSerializer.SerializeToElement(request.State ?? new Dictionary<string, JsonElement>())),
                                ProjectCharacterService.GoPayloadOptions);
                            run.Status = request.Status;
                            break;
                        case "proposal-approve":
                            if (request.Revision != run.Revision || request.ProposalVersion <= 0)
                            {
                                throw CreationConflict();
                            }
                            ValidateCreationOps(request.Ops);
                            ValidateCreationJson(SerializeForValidation(request.Proposal));
                            foreach (CreationCanvasOpDto op in request.Ops ?? [])
                            {
                                string storageKey = OpMetadataString(op, "storageKey");
                                if (storageKey.Length > 0)
                                {
                                    if (!storageKey.StartsWith("resource:", StringComparison.Ordinal))
                                    {
                                        throw AppError.BadAuthRequest("已有素材必须来自当前账号资源库");
                                    }
                                    Resource? resource = await tx.ResourceForUserAsync(
                                        userId, storageKey["resource:".Length..], cancellationToken).ConfigureAwait(false)
                                        ?? throw CreationNotFound();
                                    if (resource.Status != ResourceStatus.ResourceStatusReady)
                                    {
                                        throw AppError.BadAuthRequest("已有素材尚未就绪");
                                    }
                                }
                            }
                            string hash = CreationHash(new object?[] { ParseProposal(request.Proposal), request.Ops });
                            if (request.ProposalVersion <= run.ApprovedProposalVersion)
                            {
                                if (request.ProposalVersion == run.ApprovedProposalVersion
                                    && hash == run.ApprovedProposalHash)
                                {
                                    result = RunOutput(run);
                                    return result;
                                }
                                throw CreationConflict();
                            }
                            await tx.RevokeSubmissionsAsync(id, cancellationToken).ConfigureAwait(false);
                            run.ApprovedOperationsJSON = JsonSerializer.Serialize(
                                ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(request.Ops ?? [])),
                                ProjectCharacterService.GoPayloadOptions);
                            run.ApprovedProposalVersion = request.ProposalVersion;
                            run.ApprovedProposalHash = hash;
                            run.ApprovedAt = now;
                            run.ApprovedCanvasJSON = "";
                            break;
                        case "proposal-invalidate":
                            if (request.Revision != run.Revision)
                            {
                                throw CreationConflict();
                            }
                            await tx.RevokeSubmissionsAsync(id, cancellationToken).ConfigureAwait(false);
                            run.ApprovedAt = null;
                            run.ApprovedProposalHash = "";
                            run.ApprovedOperationsJSON = "";
                            run.ApprovedCanvasJSON = "";
                            break;
                        default:
                            throw AppError.BadAuthRequest("未知创作操作");
                    }
                }
                run.Revision++;
                if (action == "save" || action == "proposal-approve")
                {
                    long delta = run.StateJSON.Length + run.ApprovedOperationsJSON.Length
                        + run.ApprovedCanvasJSON.Length - previousBytes;
                    await ValidateCreationStorageInTxAsync(tx, userId, creating: false, delta, cancellationToken)
                        .ConfigureAwait(false);
                }
                result = RunOutput(run);
                return result;
            },
            cancellationToken);
    }

    // ------------------------------------------------------------ 报价与提交

    /// <summary>准备报价。对应 Go: <c>PrepareCreationSubmission</c>。</summary>
    public async Task<CreationSubmissionOutputDto> PrepareSubmissionAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        if (request.ItemKey.Length == 0 || request.ItemKey.Length > 160)
        {
            throw AppError.BadAuthRequest("缺少稳定执行项键");
        }
        if (request.ItemKey.StartsWith("requote:", StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("该执行项键由报价刷新接口保留");
        }
        CreationRun run = await LoadRunAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
        ValidateSubmissionScope(run, request.ProposalVersion, request.Request);
        (CreationSubmission item, CreateTaskRequestDto original, string signature) =
            await BuildSubmissionAsync(userId, run, request.ItemKey, request.ProposalVersion, request.Request, cancellationToken)
                .ConfigureAwait(false);

        return await _repository.MutateCreationRunAsync(
            userId, runId,
            async (current, tx) =>
            {
                ValidateCreationGuard(current, request.ExecutionEpoch, request.Owner);
                ValidateSubmissionScope(current, request.ProposalVersion, original);
                IReadOnlyList<CreationSubmission> items = await tx
                    .CreationSubmissionsAsync(userId, runId, cancellationToken).ConfigureAwait(false);
                foreach (CreationSubmission old in items)
                {
                    if (old.ItemKey == request.ItemKey)
                    {
                        if (old.RequestHash != item.RequestHash || old.ProposalVersion != item.ProposalVersion)
                        {
                            throw CreationConflict();
                        }
                        return SubmissionOutput(old);
                    }
                }
                await ValidateCreationStorageInTxAsync(
                    tx, userId, creating: false,
                    delta: item.RequestJSON.Length + item.QuoteJSON.Length + item.PriceSignature.Length,
                    cancellationToken).ConfigureAwait(false);
                await tx.SaveSubmissionAsync(item, cancellationToken).ConfigureAwait(false);
                return SubmissionOutput(item);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>刷新报价（生成未批准的继任项）。对应 Go: <c>RefreshCreationSubmission</c>。</summary>
    public async Task<CreationSubmissionOutputDto> RefreshSubmissionAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        CreationRun run = await LoadRunAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
        CreationSubmission? old = await _repository
            .CreationSubmissionAsync(userId, runId, request.SubmissionID, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();
        if (old.TaskID is not null)
        {
            throw CreationConflict("任务已提交，请查看原任务，不需要刷新报价");
        }
        string successorKey = "requote:" + old.ID;
        IReadOnlyList<CreationSubmission> items = await _repository
            .CreationSubmissionsAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        foreach (CreationSubmission existing in items)
        {
            if (existing.ItemKey == successorKey)
            {
                return SubmissionOutput(existing);
            }
        }
        if (old.RevokedAt is not null)
        {
            throw CreationConflict("原方案已撤销，请重新准备生成项");
        }
        CreateTaskRequestDto request0 = JsonSerializer.Deserialize<CreateTaskRequestDto>(
            old.RequestJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("任务输入解析失败");
        ValidateSubmissionScope(run, old.ProposalVersion, request0);
        if (old.ProposalVersion > 0 && old.ProposalHash != run.ApprovedProposalHash)
        {
            throw CreationConflict("原方案已变化，请重新准备生成项");
        }
        (CreationSubmission item, CreateTaskRequestDto normalized, string signature) =
            await BuildSubmissionAsync(userId, run, successorKey, old.ProposalVersion, request0, cancellationToken)
                .ConfigureAwait(false);

        return await _repository.MutateCreationRunAsync(
            userId, runId,
            async (current, tx) =>
            {
                ValidateCreationGuard(current, request.ExecutionEpoch, request.Owner);
                CreationSubmission fresh = await tx.CreationSubmissionAsync(
                    userId, runId, old.ID, cancellationToken).ConfigureAwait(false)
                    ?? throw CreationNotFound();
                if (fresh.TaskID is not null)
                {
                    throw CreationConflict("任务已经提交，不能刷新报价");
                }
                IReadOnlyList<CreationSubmission> all = await tx
                    .CreationSubmissionsAsync(userId, runId, cancellationToken).ConfigureAwait(false);
                foreach (CreationSubmission existing in all)
                {
                    if (existing.ItemKey == successorKey)
                    {
                        return SubmissionOutput(existing);
                    }
                }
                if (fresh.RevokedAt is not null)
                {
                    throw CreationConflict("原报价已撤销");
                }
                ValidateSubmissionScope(current, fresh.ProposalVersion, normalized);
                if (fresh.ProposalVersion > 0 && fresh.ProposalHash != current.ApprovedProposalHash)
                {
                    throw CreationConflict();
                }
                await ValidateCreationStorageInTxAsync(
                    tx, userId, creating: false,
                    delta: item.RequestJSON.Length + item.QuoteJSON.Length + item.PriceSignature.Length,
                    cancellationToken).ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                fresh.RevokedAt = now;
                await tx.SaveSubmissionAsync(fresh, cancellationToken).ConfigureAwait(false);
                await tx.SaveSubmissionAsync(item, cancellationToken).ConfigureAwait(false);
                return SubmissionOutput(item);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>批准提交。对应 Go: <c>ApproveCreationSubmissions</c>。</summary>
    public async Task<List<CreationSubmissionOutputDto>> ApproveSubmissionsAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        List<string> submissionIds = request.SubmissionIDs ?? [];
        if (submissionIds.Count == 0 || submissionIds.Count > 20)
        {
            throw AppError.BadAuthRequest("请选择 1 到 20 项生成任务");
        }
        Dictionary<string, TaskEntity> prepared = new(StringComparer.Ordinal);
        Dictionary<string, string> signatures = new(StringComparer.Ordinal);
        Dictionary<string, CreationSubmission> itemsById = new(StringComparer.Ordinal);
        foreach (string sid in submissionIds)
        {
            CreationSubmission item = await _repository
                .CreationSubmissionAsync(userId, runId, sid, cancellationToken).ConfigureAwait(false)
                ?? throw CreationNotFound();
            itemsById[sid] = item;
            CreateTaskRequestDto taskRequest = JsonSerializer.Deserialize<CreateTaskRequestDto>(
                item.RequestJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("任务输入解析失败");
            (TaskEntity task, BillingOrder? order, string sig) = await PrepareCreationTaskAsync(
                userId, taskRequest, cancellationToken).ConfigureAwait(false);
            CreationSubmissionOutputDto fresh = SubmissionOutput(item);
            if (CreationQuoteFor(task, order, sig, item.ExpiresAt).QuoteHash != fresh.Quote.QuoteHash)
            {
                throw CreationConflict("报价已变化，请重新准备并确认");
            }
            prepared[sid] = task;
            signatures[sid] = sig;
        }
        List<CreationSubmissionOutputDto> output = [];
        return await _repository.MutateCreationRunAsync(
            userId, runId,
            async (run, tx) =>
            {
                ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
                DateTime now = DateTime.UtcNow;
                List<CreationSubmissionOutputDto> approved = [];
                foreach (string sid in submissionIds)
                {
                    CreationSubmission item = await tx.CreationSubmissionAsync(
                        userId, runId, sid, cancellationToken).ConfigureAwait(false)
                        ?? throw CreationNotFound();
                    if (item.RevokedAt is not null || !(item.ExpiresAt > now))
                    {
                        throw CreationConflict("报价已过期或撤销");
                    }
                    CreateTaskRequestDto taskRequest = JsonSerializer.Deserialize<CreateTaskRequestDto>(
                        item.RequestJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidOperationException("任务输入解析失败");
                    ValidateSubmissionScope(run, item.ProposalVersion, taskRequest);
                    if (item.ProposalVersion > 0 && item.ProposalHash != run.ApprovedProposalHash)
                    {
                        throw CreationConflict();
                    }
                    string signature = await tx.CreationPriceSignatureAsync(
                        prepared[sid], ChannelOf(prepared[sid]), ModelOf(prepared[sid]), cancellationToken)
                        .ConfigureAwait(false);
                    if (signature != signatures[sid])
                    {
                        throw CreationConflict("模型或价格配置已更新，请重新确认");
                    }
                    if (item.ApprovedAt is null)
                    {
                        item.ApprovedAt = now;
                        await tx.SaveSubmissionAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    approved.Add(SubmissionOutput(item));
                }
                output = approved;
                return output;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>执行提交（创建任务）。对应 Go: <c>ExecuteCreationSubmission</c>。</summary>
    public async Task<TaskEntity> ExecuteSubmissionAsync(
        string userId, string runId, CreationRequestDto request, CancellationToken cancellationToken = default)
    {
        CreationRun run = await LoadRunAsync(userId, runId, cancellationToken).ConfigureAwait(false);
        ValidateCreationGuard(run, request.ExecutionEpoch, request.Owner);
        CreationSubmission item = await _repository
            .CreationSubmissionAsync(userId, runId, request.SubmissionID, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();
        if (item.TaskID is not null)
        {
            TaskEntity? existing = await _repository
                .TaskForUserAsync(userId, item.TaskID, cancellationToken).ConfigureAwait(false)
                ?? throw CreationNotFound();
            return TaskCreationService.TaskForOutputPublic(existing);
        }
        CreateTaskRequestDto taskRequest = JsonSerializer.Deserialize<CreateTaskRequestDto>(
            item.RequestJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("任务输入解析失败");
        ValidateSubmissionScope(run, item.ProposalVersion, taskRequest);
        (TaskEntity task, BillingOrder? order, string signature) = await PrepareCreationTaskAsync(
            userId, taskRequest, cancellationToken).ConfigureAwait(false);
        if (CreationQuoteFor(task, order, signature, item.ExpiresAt).QuoteHash
            != SubmissionOutput(item).Quote.QuoteHash)
        {
            throw CreationConflict("报价已变化，请重新确认");
        }
        task.CreationSubmissionID = item.ID;
        if (order is not null)
        {
            task.BillingOrderID = order.ID;
        }
        Platform.RuntimePolicySetting policy = _runtimePolicy.Current();

        TaskEntity result = await _repository.MutateCreationRunAsync(
            userId, runId,
            async (current, tx) =>
            {
                ValidateCreationGuard(current, request.ExecutionEpoch, request.Owner);
                CreationSubmission fresh = await tx.CreationSubmissionAsync(
                    userId, runId, item.ID, cancellationToken).ConfigureAwait(false)
                    ?? throw CreationNotFound();
                if (fresh.TaskID is not null)
                {
                    TaskEntity existing = await _repository
                        .TaskForUserAsync(userId, fresh.TaskID, cancellationToken).ConfigureAwait(false)
                        ?? throw CreationNotFound();
                    return TaskCreationService.TaskForOutputPublic(existing);
                }
                if (fresh.ApprovedAt is null || fresh.RevokedAt is not null || !(fresh.ExpiresAt > DateTime.UtcNow))
                {
                    throw CreationConflict("任务尚未批准或报价已过期");
                }
                ValidateSubmissionScope(current, fresh.ProposalVersion, taskRequest);
                if (fresh.ProposalVersion > 0 && fresh.ProposalHash != current.ApprovedProposalHash)
                {
                    throw CreationConflict();
                }
                string signatureNow = await tx.CreationPriceSignatureAsync(
                    task, ChannelOf(task), ModelOf(task), cancellationToken).ConfigureAwait(false);
                if (signatureNow != signature)
                {
                    throw CreationConflict("模型或价格配置已更新，请重新确认");
                }
                await tx.CreateTaskWithQuotaAsync(
                    task, order, policy.Task.ActiveTaskLimit, cancellationToken).ConfigureAwait(false);
                fresh.TaskID = task.ID;
                await tx.SaveSubmissionAsync(fresh, cancellationToken).ConfigureAwait(false);
                return TaskCreationService.TaskForOutputPublic(task);
            },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ------------------------------------------------------------ 内部

    /// <summary>创作任务准备（校验 + admission + 后置一致性检查）。对应 Go: <c>prepareCreationTask</c>。</summary>
    private async Task<(TaskEntity Task, BillingOrder? Order, string Signature)> PrepareCreationTaskAsync(
        string userId, CreateTaskRequestDto request, CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement> input = TaskCreationService.NormalizeTaskInput(request.Input);
        Dictionary<string, JsonElement> config = InputConfigOf(input);
        if (request.LogicalModelID.Length == 0 && ConfigString(config, "channelId").Length == 0)
        {
            throw AppError.BadAuthRequest("智能创作目前仅支持后端受管模型，请在原入口使用其他渠道");
        }
        if (TaskCreationService.CreationUsesWorkflowOrReplay(input))
        {
            throw AppError.BadAuthRequest("智能创作不支持本机、工作流或文本回放任务");
        }
        Dictionary<string, JsonElement> safeConfig = new(StringComparer.Ordinal);
        foreach (string key in new[]
                 {
                     "channelId", "channelModelKey", "model", "priceTierId", "apiFormat", "interfaceType",
                     "size", "quality", "transparentBackground", "count", "videoSeconds", "vquality",
                     "videoGenerateAudio", "videoWatermark", "videoArkPrivateAssetUpload", "systemPrompt",
                 })
        {
            if (config.TryGetValue(key, out JsonElement value))
            {
                safeConfig[key] = value.Clone();
            }
        }
        input["config"] = JsonSerializer.SerializeToElement(safeConfig);
        request.Input = input;
        ValidateCreationJson(SerializeForValidation(request));
        if (request.Type is not ("canvas_text" or "text" or "canvas_image" or "canvas_video"))
        {
            throw AppError.BadAuthRequest("智能创作任务类型不受支持");
        }
        string expectedMode = request.Type switch
        {
            "text" => "text",
            "canvas_text" => "text",
            "canvas_image" => "image",
            _ => "video",
        };
        if (ConfigString(config, "mode") != expectedMode
            || InputString(input, "prompt").Trim() != request.Prompt.Trim())
        {
            throw AppError.BadAuthRequest("任务类型、模式和实际提示词必须一致");
        }
        if (expectedMode != "text" && input.ContainsKey("agentRequests"))
        {
            throw AppError.BadAuthRequest("媒体任务不允许携带独立模型协议请求");
        }
        string count = ConfigString(config, "count");
        if (count.Length > 0 && count != "1")
        {
            throw AppError.BadAuthRequest("每个获批执行项只能生成一个产物");
        }
        if (input.ContainsKey("mask") && input["mask"].ValueKind != JsonValueKind.Null)
        {
            throw AppError.BadAuthRequest("本期智能创作暂不支持蒙版任务");
        }
        if (expectedMode != "text")
        {
            Domain.Entities.CanvasProject? canvas = await _repository
                .CanvasProjectForUserAsync(userId, request.ProjectID, cancellationToken).ConfigureAwait(false)
                ?? throw CreationNotFound();
            Dictionary<string, Dictionary<string, JsonElement>> nodes = CanvasNodes(canvas.PayloadJSON);
            if (input.TryGetValue("referenceImages", out JsonElement refs)
                && refs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement raw in refs.EnumerateArray())
                {
                    string refId = raw.ValueKind == JsonValueKind.Object
                        && raw.TryGetProperty("id", out JsonElement idEl)
                        && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? ""
                            : "";
                    string refKey = raw.ValueKind == JsonValueKind.Object
                        && raw.TryGetProperty("storageKey", out JsonElement keyEl)
                        && keyEl.ValueKind == JsonValueKind.String
                            ? keyEl.GetString() ?? ""
                            : "";
                    nodes.TryGetValue(refId, out Dictionary<string, JsonElement>? node);
                    string nodeType = node is null ? "" : node.TryGetValue("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() ?? "" : "";
                    JsonElement meta = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());
                    if (node is not null && node.TryGetValue("metadata", out JsonElement metaElement)
                        && metaElement.ValueKind == JsonValueKind.Object)
                    {
                        meta = metaElement.Clone();
                    }
                    string nodeStatus = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("status", out JsonElement status)
                        && status.ValueKind == JsonValueKind.String
                            ? status.GetString() ?? ""
                            : "";
                    string nodeKey = meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("storageKey", out JsonElement storage)
                        && storage.ValueKind == JsonValueKind.String
                            ? storage.GetString() ?? ""
                            : "";
                    if (node is null || nodeType != "image" || nodeStatus != "success" || refKey != nodeKey)
                    {
                        throw CreationConflict("参考素材已变化或尚未就绪");
                    }
                }
            }
        }
        foreach (string name in new[] { "referenceImages", "referenceVideos", "referenceAudios" })
        {
            if (!input.TryGetValue(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            List<JsonElement> hydrated = [];
            foreach (JsonElement raw in list.EnumerateArray())
            {
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    throw AppError.BadAuthRequest("素材引用格式无效");
                }
                string key = ObjectString(raw, "storageKey");
                if (!key.StartsWith("resource:", StringComparison.Ordinal))
                {
                    throw AppError.BadAuthRequest("请先将参考素材保存到当前账号资源库");
                }
                Resource? resource = await _repository
                    .ResourceForUserAsync(userId, key["resource:".Length..], cancellationToken).ConfigureAwait(false)
                    ?? throw CreationNotFound();
                if (resource.Status != "ready")
                {
                    throw AppError.BadAuthRequest("参考素材尚未就绪");
                }
                Dictionary<string, JsonElement> media = new(StringComparer.Ordinal);
                foreach (JsonProperty property in raw.EnumerateObject())
                {
                    if (property.Name is "url" or "dataUrl")
                    {
                        continue;
                    }
                    media[property.Name] = property.Value.Clone();
                }
                media["bytes"] = JsonSerializer.SerializeToElement(resource.Size);
                media["durationMs"] = JsonSerializer.SerializeToElement(resource.DurationMs);
                hydrated.Add(JsonSerializer.SerializeToElement(media));
            }
            input[name] = JsonSerializer.SerializeToElement(hydrated);
        }

        // Admission：路由 + 计费（不落库）。
        (TaskEntity task, BillingOrder? order) = await _creations.AdmitQueuedAsync(
            userId, request, input, request.Type, request.Prompt, "", "", cancellationToken)
            .ConfigureAwait(false);

        // 后置一致性：解析后的规格必须与请求一致。
        Dictionary<string, JsonElement> resolved = InputConfigOf(ParseInput(task.InputJSON));
        foreach (string key in new[] { "size", "videoSeconds", "vquality", "quality", "count" })
        {
            string requested = ConfigString(config, key);
            if (requested.Length > 0
                && !string.Equals(requested, ConfigString(resolved, key), StringComparison.OrdinalIgnoreCase))
            {
                throw CreationConflict("模型解析后的生成规格与请求不同，请调整方案后重新报价");
            }
        }
        string channelId = ConfigString(resolved, "channelId");
        ModelChannel? channel = await _repository
            .SystemChannelByIDAsync(channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("record not found");
        if (expectedMode == "text"
            && input.TryGetValue("referenceImages", out JsonElement textRefs)
            && textRefs.ValueKind == JsonValueKind.Array
            && textRefs.GetArrayLength() > 0)
        {
            ChannelModel? cm = await _repository.ChannelModelByKeyAsync(
                channel.ID, ConfigString(resolved, "model"), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("record not found");
            Capabilities.ModelCapabilityConfig? profile =
                Capabilities.ModelCapabilityConfigOps.DecodeModelCapabilityConfig(cm.CapabilityConfigJSON);
            if (profile?.Text?.References.MaxImages < textRefs.GetArrayLength())
            {
                throw AppError.BadAuthRequest("当前文本模型未配置足够的图片理解能力");
            }
        }
        string signature = await _repository.CreationPriceSignatureAsync(
            task, channelId, ConfigString(resolved, "model"), cancellationToken).ConfigureAwait(false);
        return (task, order, signature);
    }

    /// <summary>构建提交（报价 + 请求哈希）。对应 Go: <c>buildCreationSubmission</c>。</summary>
    private async Task<(CreationSubmission Item, CreateTaskRequestDto Normalized, string Signature)> BuildSubmissionAsync(
        string userId,
        CreationRun run,
        string itemKey,
        long proposalVersion,
        CreateTaskRequestDto? request,
        CancellationToken cancellationToken)
    {
        request = CloneViaJson(request);
        (TaskEntity task, BillingOrder? order, string signature) = await PrepareCreationTaskAsync(
            userId, request, cancellationToken).ConfigureAwait(false);
        DateTime expires = DateTime.UtcNow.AddMinutes(5);
        CreationQuoteDto quote = CreationQuoteFor(task, order, signature, expires);
        CreationSubmission item = new()
        {
            ID = IdGenerator.NewId(),
            UserID = userId,
            RunID = run.ID,
            ItemKey = itemKey,
            ProposalVersion = proposalVersion,
            ProposalHash = run.ApprovedProposalHash,
            RequestJSON = JsonSerializer.Serialize(request, ProjectCharacterService.GoPayloadOptions),
            RequestHash = CreationHash(ParseJson(JsonSerializer.Serialize(request))),
            QuoteJSON = JsonSerializer.Serialize(quote, ProjectCharacterService.GoPayloadOptions),
            PriceSignature = signature,
            ExpiresAt = expires,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        return (item, request, signature);
    }

    /// <summary>报价投影。对应 Go: <c>creationQuoteFor</c>。</summary>
    private static CreationQuoteDto CreationQuoteFor(
        TaskEntity task, BillingOrder? order, string signature, DateTime expires)
    {
        CreationQuoteDto quote = new()
        {
            Model = task.Model,
            BillingMode = "free",
            Quantity = 1,
            ExpiresAt = expires,
        };
        if (order is not null)
        {
            quote.BillingMode = order.BillingMode;
            quote.Quantity = order.Quantity;
            quote.AmountMicrocredits = order.AmountMicrocredits;
            quote.Estimated = order.BillingMode == "token";
        }
        Dictionary<string, JsonElement> config = ParseInput(task.InputJSON) is Dictionary<string, JsonElement> d
            ? d.TryGetValue("config", out JsonElement c) && c.ValueKind == JsonValueKind.Object
                ? ConfigMap(c)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Dictionary<string, JsonElement> options = new(StringComparer.Ordinal);
        foreach (string key in new[] { "size", "videoSeconds", "vquality", "quality", "maxTokens" })
        {
            if (config.TryGetValue(key, out JsonElement value))
            {
                options[key] = value.Clone();
            }
        }
        quote.Options = options;
        quote.QuoteHash = CreationHash(new object?[]
        {
            signature, quote.BillingMode, quote.Quantity, quote.AmountMicrocredits, options,
        });
        return quote;
    }

    /// <summary>提交范围校验。对应 Go: <c>validateCreationSubmissionScope</c>。</summary>
    private static void ValidateSubmissionScope(
        CreationRun run, long version, CreateTaskRequestDto? request)
    {
        request ??= new CreateTaskRequestDto();
        if (run.Status is "paused" or "cancelled" or "completed")
        {
            throw CreationConflict("请先恢复创作任务");
        }
        if (request.Type is "canvas_text" or "text")
        {
            if (request.ProjectID.Length > 0 && request.ProjectID != run.CanvasID)
            {
                throw CreationConflict("规划任务的画布关联不匹配");
            }
            return;
        }
        if (run.CanvasID.Length == 0 || request.ProjectID != run.CanvasID)
        {
            throw CreationConflict("媒体任务必须提交到当前创作画布");
        }
        if (run.ApprovedAt is null
            || version != run.ApprovedProposalVersion
            || run.ApprovedProposalHash.Length == 0)
        {
            throw CreationConflict("请先确认当前方案");
        }
        List<CreationCanvasOpDto> ops = [];
        if (run.ApprovedOperationsJSON.Length > 0)
        {
            ops = JsonSerializer.Deserialize<List<CreationCanvasOpDto>>(
                run.ApprovedOperationsJSON,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? ops;
        }
        string nodeId = InputString(ParseInput(JsonSerializer.Serialize(request.Input ?? new Dictionary<string, JsonElement>())), "nodeId");
        if (nodeId.Length == 0)
        {
            // metadata.nodeId
            if (request.Input is not null
                && request.Input.TryGetValue("metadata", out JsonElement metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("nodeId", out JsonElement metaNode))
            {
                nodeId = metaNode.ValueKind == JsonValueKind.String ? metaNode.GetString() ?? "" : "";
            }
        }
        foreach (CreationCanvasOpDto op in ops)
        {
            if (op.ID != nodeId || nodeId.Length == 0)
            {
                continue;
            }
            if (op.Type is not ("add_node" or "update_node"))
            {
                continue;
            }
            Dictionary<string, JsonElement> meta = op.Metadata ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (op.Patch is not null && op.Patch.TryGetValue("metadata", out JsonElement nested)
                && nested.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, JsonElement> merged = new(StringComparer.Ordinal);
                foreach (JsonProperty property in nested.EnumerateObject())
                {
                    merged[property.Name] = property.Value.Clone();
                }
                foreach (KeyValuePair<string, JsonElement> pair in meta)
                {
                    merged[pair.Key] = pair.Value.Clone();
                }
                meta = merged;
            }
            string prompt = MetaString(meta, "prompt");
            if (prompt.Length == 0)
            {
                prompt = MetaString(meta, "text");
            }
            if (request.Prompt.Trim() != prompt.Trim() || prompt.Length == 0)
            {
                throw CreationConflict("任务提示词已超出获批方案");
            }
            string model = MetaString(meta, "model");
            if (model.Length == 0 || model != request.Model)
            {
                throw CreationConflict("模型与已批准方案不同");
            }
            Dictionary<string, JsonElement> config = InputConfigOf(
                ParseInput(JsonSerializer.Serialize(request.Input ?? new Dictionary<string, JsonElement>())));
            foreach (string key in new[] { "size", "videoSeconds", "vquality", "quality" })
            {
                string metadataKey = key == "videoSeconds" ? "seconds" : key;
                string approvedValue = MetaString(meta, metadataKey).Trim();
                string candidate = ConfigString(config, key).Trim();
                if (metadataKey == "quality")
                {
                    string approvedNorm = approvedValue.ToLowerInvariant();
                    string candidateNorm = candidate.ToLowerInvariant();
                    bool approvedBlank = approvedNorm is "auto" or "any" or "";
                    bool candidateBlank = candidateNorm is "auto" or "any" or "";
                    if (approvedBlank && candidateBlank)
                    {
                        continue;
                    }
                    if (approvedNorm == candidateNorm)
                    {
                        continue;
                    }
                }
                if (approvedValue.Length > 0 && approvedValue != candidate)
                {
                    throw CreationConflict("生成规格与已批准方案不同");
                }
            }
            List<string> refs = [];
            if (meta.TryGetValue("referenceNodeIds", out JsonElement refsElement)
                && refsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement refElement in refsElement.EnumerateArray())
                {
                    refs.Add(refElement.ToString());
                }
            }
            List<string> images = [];
            if (request.Input is not null
                && request.Input.TryGetValue("referenceImages", out JsonElement imagesElement)
                && imagesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement image in imagesElement.EnumerateArray())
                {
                    images.Add(ObjectString(image, "id"));
                }
            }
            if (refs.Count != images.Count)
            {
                throw CreationConflict("参考素材数量与已批准方案不同");
            }
            for (int index = 0; index < refs.Count; index++)
            {
                if (index < images.Count && refs[index] != images[index])
                {
                    throw CreationConflict("参考素材与已批准方案不同");
                }
            }
            foreach (string kind in new[] { "referenceVideos", "referenceAudios" })
            {
                if (request.Input is not null
                    && request.Input.TryGetValue(kind, out JsonElement values)
                    && values.ValueKind == JsonValueKind.Array
                    && values.GetArrayLength() > 0)
                {
                    throw CreationConflict("本期方案尚未授权视频或音频参考输入");
                }
            }
            return;
        }
        throw CreationConflict("任务节点不在已批准方案范围内");
    }

    // ------------------------------------------------------------ 校验辅助

    /// <summary>执行控制权校验。对应 Go: <c>validateCreationGuard</c>。</summary>
    private static void ValidateCreationGuard(CreationRun run, long epoch, string owner)
    {
        if (owner.Length == 0
            || run.ExecutionOwner != owner
            || run.ExecutionEpoch != epoch
            || run.LeaseExpiresAt is null
            || !(run.LeaseExpiresAt.Value > DateTime.UtcNow))
        {
            throw CreationConflict("执行控制权已过期，请在当前页面重新接管");
        }
    }

    /// <summary>创作 JSON 校验（≤1MB、禁密钥/内嵌媒体/临时签名）。对应 Go: <c>validateCreationJSON</c>。</summary>
    private static void ValidateCreationJson(string encoded)
    {
        if (Encoding.UTF8.GetByteCount(encoded) > 1 << 20)
        {
            throw AppError.BadAuthRequest("创作内容超过限制或格式无效");
        }
        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(encoded);
        }
        catch (JsonException)
        {
            throw AppError.BadAuthRequest("创作内容超过限制或格式无效");
        }
        if (ContainsForbiddenCreationContent(parsed))
        {
            throw AppError.BadAuthRequest("创作记录不能包含密钥、内嵌媒体或临时签名链接");
        }
    }

    private static bool ContainsForbiddenCreationContent(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    switch (property.Name.ToLowerInvariant())
                    {
                        case "apikey":
                        case "secretkey":
                        case "authorization":
                        case "cookie":
                        case "headers":
                        case "baseurl":
                        case "token":
                        case "accesstoken":
                        case "refresh_token":
                            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                                && !(property.Value.ValueKind == JsonValueKind.String
                                    && property.Value.GetString() == ""))
                            {
                                return true;
                            }
                            break;
                    }
                    if (ContainsForbiddenCreationContent(property.Value))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (ContainsForbiddenCreationContent(item))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.String:
            {
                string text = value.GetString() ?? "";
                if (text.StartsWith("data:", StringComparison.Ordinal)
                    || text.Contains("X-Amz-Signature=", StringComparison.Ordinal)
                    || text.Contains("X-Tos-Signature=", StringComparison.Ordinal))
                {
                    return true;
                }
                break;
            }
        }
        return false;
    }

    /// <summary>画布操作校验。对应 Go: <c>validateCreationOps</c>。</summary>
    private static void ValidateCreationOps(List<CreationCanvasOpDto>? ops)
    {
        ops ??= [];
        if (ops.Count == 0 || ops.Count > 100)
        {
            throw AppError.BadAuthRequest("方案必须包含 1 到 100 项明确画布操作");
        }
        ValidateCreationJson(SerializeForValidation(ops));
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (CreationCanvasOpDto op in ops)
        {
            switch (op.Type)
            {
                case "add_node":
                    if (op.ID.Length == 0 || !ids.Add(op.ID))
                    {
                        throw AppError.BadAuthRequest("新增节点必须使用不重复的稳定 ID");
                    }
                    if (!NodeTypes.Contains(op.NodeType))
                    {
                        throw AppError.BadAuthRequest("该节点类型不在本期创作范围");
                    }
                    break;
                case "update_node":
                    if (op.ID.Length == 0)
                    {
                        throw AppError.BadAuthRequest("更新节点缺少 ID");
                    }
                    if (op.Patch is not null)
                    {
                        foreach (string key in op.Patch.Keys)
                        {
                            if (key is not ("title" or "position" or "width" or "height" or "metadata"))
                            {
                                throw AppError.BadAuthRequest("方案包含不支持的节点更新字段");
                            }
                        }
                    }
                    break;
                case "connect_nodes":
                    if (op.ID.Length == 0 || op.FromNodeID.Length == 0 || op.ToNodeID.Length == 0)
                    {
                        throw AppError.BadAuthRequest("连线必须有稳定 ID 和两个端点");
                    }
                    break;
                case "select_nodes":
                    break;
                default:
                    throw AppError.BadAuthRequest("创作方案仅允许新增、连线、更新和选择节点");
            }
        }
    }

    /// <summary>结构化存储配额。对应 Go: <c>validateCreationStorage</c>。</summary>
    private async Task ValidateCreationStorageAsync(
        string userId, bool creating, long delta, CancellationToken cancellationToken)
    {
        (long _, long bytes) = await _repository
            .CreationStorageUsageAsync(userId, cancellationToken).ConfigureAwait(false);
        await ValidateQuotaAsync(userId, creating, bytes + delta, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateCreationStorageInTxAsync(
        CreationRunMutationContext tx, string userId, bool creating, long delta,
        CancellationToken cancellationToken)
    {
        long bytes = await tx.CreationRunStorageBytesAsync(userId, cancellationToken).ConfigureAwait(false);
        await ValidateQuotaStaticAsync(userId, creating, bytes + delta, cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateQuotaAsync(
        string userId, bool creating, long total, CancellationToken cancellationToken)
    {
        UserStorageUsage usage = await _repository
            .UserStorageUsageAsync(userId, cancellationToken).ConfigureAwait(false);
        ValidateQuotaValue(usage, creating, total);
    }

    private static Task ValidateQuotaStaticAsync(
        string userId, bool creating, long total, CancellationToken cancellationToken) => Task.CompletedTask;

    private void ValidateQuotaValue(UserStorageUsage usage, bool creating, long total)
    {
        // 对应 Go: validateStructuredStorageQuotaWithPolicy（canvas 通道）。
        long limitMB = _runtimePolicy.Current().Resource.StructuredDataMB;
        if (total > limitMB * 1024L * 1024L)
        {
            throw AppError.QuotaExceeded(
                $"账号画布和素材数据已达到 {limitMB}MB 上限，请先删除不需要的内容");
        }
        _ = usage;
        _ = creating;
    }

    // ------------------------------------------------------------ 投影与解析

    /// <summary>运行投影。对应 Go: <c>creationRunOutput</c>。</summary>
    private static CreationRunOutputDto RunOutput(CreationRun run)
    {
        JsonElement state;
        try
        {
            state = JsonSerializer.Deserialize<JsonElement>(run.StateJSON);
        }
        catch (JsonException)
        {
            state = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement>(), new JsonSerializerOptions());
        }
        return new CreationRunOutputDto
        {
            ID = run.ID,
            CanvasID = run.CanvasID,
            Revision = run.Revision,
            ExecutionEpoch = run.ExecutionEpoch,
            ExecutionOwner = run.ExecutionOwner,
            LeaseExpiresAt = run.LeaseExpiresAt,
            Status = run.Status,
            State = state,
            ApprovedProposalVersion = run.ApprovedProposalVersion,
            ApprovedAt = run.ApprovedAt,
            CreatedAt = run.CreatedAt,
            UpdatedAt = run.UpdatedAt,
        };
    }

    /// <summary>提交投影。对应 Go: <c>creationSubmissionOutput</c>。</summary>
    private static CreationSubmissionOutputDto SubmissionOutput(CreationSubmission item)
    {
        CreationQuoteDto quote;
        try
        {
            quote = JsonSerializer.Deserialize<CreationQuoteDto>(
                item.QuoteJSON, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new CreationQuoteDto();
        }
        catch (JsonException)
        {
            quote = new CreationQuoteDto();
        }
        return new CreationSubmissionOutputDto
        {
            ID = item.ID,
            RunID = item.RunID,
            ItemKey = item.ItemKey,
            ProposalVersion = item.ProposalVersion,
            RequestHash = item.RequestHash,
            Quote = quote,
            ExpiresAt = item.ExpiresAt,
            ApprovedAt = item.ApprovedAt,
            RevokedAt = item.RevokedAt,
            TaskID = item.TaskID,
        };
    }

    private async Task<CreationRun> LoadRunAsync(
        string userId, string id, CancellationToken cancellationToken) =>
        await _repository.CreationRunAsync(userId, id, cancellationToken).ConfigureAwait(false)
            ?? throw CreationNotFound();

    private static AppError CreationConflict(string message = CreationConflictMessage) =>
        AppError.New(409, message);

    private static AppError CreationNotFound() => AppError.New(404, "创作记录不存在或无权访问");

    /// <summary>对应 Go: <c>creationHash</c>。数组 [value] 或 [canvasId, state] 的一致哈希。</summary>
    internal static string CreationHash(object? value)
    {
        string encoded = JsonSerializer.Serialize(
            ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(value)),
            ProjectCharacterService.GoPayloadOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(encoded))).ToLowerInvariant();
    }

    private static string SerializeForValidation(object? value) => JsonSerializer.Serialize(
        ProjectCharacterService.SortedElement(JsonSerializer.SerializeToElement(value)),
        ProjectCharacterService.GoPayloadOptions);

    private static JsonElement ParseJson(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());
        }
    }

    private static JsonElement ParseProposal(JsonElement? proposal) =>
        proposal?.Clone() ?? JsonSerializer.SerializeToElement<JsonElement?>(null);

    private static CreateTaskRequestDto CloneViaJson(CreateTaskRequestDto? request)
    {
        if (request is null)
        {
            return new CreateTaskRequestDto();
        }
        return JsonSerializer.Deserialize<CreateTaskRequestDto>(
            JsonSerializer.Serialize(request), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new CreateTaskRequestDto();
    }

    private static Dictionary<string, JsonElement> ParseInput(string raw)
    {
        try
        {
            JsonElement element = JsonSerializer.Deserialize<JsonElement>(raw);
            if (element.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    result[property.Name] = property.Value.Clone();
                }
                return result;
            }
        }
        catch (JsonException)
        {
        }
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement> InputConfigOf(Dictionary<string, JsonElement> input)
    {
        if (input.TryGetValue("config", out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> config = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                config[property.Name] = property.Value.Clone();
            }
            return config;
        }
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private static string ConfigString(Dictionary<string, JsonElement> config, string key) =>
        config.TryGetValue(key, out JsonElement value) ? ScalarText(value) : "";

    private static string InputString(Dictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out JsonElement value) ? ScalarText(value) : "";

    private static string MetaString(Dictionary<string, JsonElement> meta, string key) =>
        meta.TryGetValue(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string OpMetadataString(CreationCanvasOpDto op, string key) =>
        op.Metadata is not null
        && op.Metadata.TryGetValue(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ScalarText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    private static Dictionary<string, JsonElement> ConfigMap(JsonElement element)
    {
        Dictionary<string, JsonElement> config = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            config[property.Name] = property.Value.Clone();
        }
        return config;
    }

    private static string ChannelOf(TaskEntity task) =>
        ParseInput(task.InputJSON).TryGetValue("config", out JsonElement config)
        && config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("channelId", out JsonElement channelId)
        && channelId.ValueKind == JsonValueKind.String
            ? channelId.GetString() ?? ""
            : "";

    private static string ModelOf(TaskEntity task) =>
        ParseInput(task.InputJSON).TryGetValue("config", out JsonElement config)
        && config.ValueKind == JsonValueKind.Object
        && config.TryGetProperty("model", out JsonElement model)
        && model.ValueKind == JsonValueKind.String
            ? model.GetString() ?? ""
            : "";


    /// <summary>从 JSON 对象取字符串字段。</summary>
    private static string ObjectString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    /// <summary>画布文档节点表（id → node）。对应 Go: <c>creationObjects</c>。</summary>
    private static Dictionary<string, Dictionary<string, JsonElement>> CanvasNodes(string payloadJSON)
    {
        Dictionary<string, Dictionary<string, JsonElement>> nodes = new(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJSON);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("nodes", out JsonElement list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in list.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    string id = node.TryGetProperty("id", out JsonElement idElement)
                        && idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() ?? ""
                            : "";
                    if (id.Length > 0)
                    {
                        Dictionary<string, JsonElement> copy = new(StringComparer.Ordinal);
                        foreach (JsonProperty property in node.EnumerateObject())
                        {
                            copy[property.Name] = property.Value.Clone();
                        }
                        nodes[id] = copy;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return nodes;
    }
}
