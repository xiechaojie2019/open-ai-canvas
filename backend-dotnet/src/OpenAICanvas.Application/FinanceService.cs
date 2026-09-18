#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>兑换码批次分页。对应 Go: <c>app.RedeemBatchPage</c>。</summary>
public sealed class RedeemBatchPageDto
{
    [JsonPropertyName("batches")]
    public required IReadOnlyList<RedeemBatch> Batches { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>批次内单个兑换码详情。对应 Go: <c>app.AdminRedeemCodeDetail</c>。</summary>
public sealed class AdminRedeemCodeDetailDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("code")]
    [GoOmitEmpty]
    public string Code { get; init; } = "";

    [JsonPropertyName("codeSuffix")]
    public string CodeSuffix { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("redeemedBy")]
    [GoOmitEmpty]
    public string RedeemedBy { get; init; } = "";

    [JsonPropertyName("redeemedUsername")]
    [GoOmitEmpty]
    public string RedeemedUsername { get; init; } = "";

    [JsonPropertyName("redeemedDisplayName")]
    [GoOmitEmpty]
    public string RedeemedDisplayName { get; init; } = "";

    [JsonPropertyName("redeemedAt")]
    public DateTime? RedeemedAt { get; init; }

    [JsonPropertyName("redeemedIp")]
    [GoOmitEmpty]
    public string RedeemedIP { get; init; } = "";

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; init; }
}

/// <summary>批次兑换码分页。对应 Go: <c>app.AdminRedeemCodePage</c>。</summary>
public sealed class AdminRedeemCodePageDto
{
    [JsonPropertyName("batch")]
    public required RedeemBatch Batch { get; init; }

    [JsonPropertyName("codes")]
    public required IReadOnlyList<AdminRedeemCodeDetailDto> Codes { get; init; }

    [JsonPropertyName("plaintextAvailable")]
    public bool PlaintextAvailable { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>账单分页。对应 Go: <c>app.BillingOrderPage</c>。</summary>
public sealed class BillingOrderPageDto
{
    [JsonPropertyName("orders")]
    public required IReadOnlyList<BillingOrder> Orders { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>创建兑换码批次请求。对应 Go: <c>app.CreateRedeemBatchRequest</c>。</summary>
public sealed class CreateRedeemBatchRequest
{
    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>创建兑换码批次结果。对应 Go: <c>app.CreateRedeemBatchResult</c>。</summary>
public sealed class CreateRedeemBatchResultDto
{
    [JsonPropertyName("batch")]
    public required RedeemBatch Batch { get; init; }

    [JsonPropertyName("codes")]
    public required IReadOnlyList<string> Codes { get; init; }
}

/// <summary>管理员调账请求。对应 Go: <c>app.AdminCreditAdjustmentRequest</c>。</summary>
public sealed class AdminCreditAdjustmentRequest
{
    [JsonPropertyName("amountMicrocredits")]
    public long AmountMicrocredits { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}


/// <summary>人工核对单个账单请求。对应 Go: <c>app.ResolveBillingRequest</c>。</summary>
public sealed class ResolveBillingRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>批量核对请求。对应 Go: <c>app.ResolveBillingBatchRequest</c>。</summary>
public sealed class ResolveBillingBatchRequest
{
    [JsonPropertyName("ids")]
    public List<string> IDs { get; set; } = [];

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("note")]
    public string Note { get; set; } = "";
}

/// <summary>批量核对失败项。对应 Go: <c>app.ResolveBillingBatchFailure</c>。</summary>
public sealed class ResolveBillingBatchFailureDto
{
    [JsonPropertyName("id")]
    public string ID { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";
}

/// <summary>批量核对结果。对应 Go: <c>app.ResolveBillingBatchResult</c>。</summary>
public sealed class ResolveBillingBatchResultDto
{
    [JsonPropertyName("resolvedCount")]
    public int ResolvedCount { get; set; }

    [JsonPropertyName("failed")]
    public required IReadOnlyList<ResolveBillingBatchFailureDto> Failed { get; init; }
}

/// <summary>
/// 财务服务：钱包、兑换码、调账与账单列表。
/// 对应 Go 的 <c>internal/app/finance.go</c>（不含依赖 provider 的计费路径）。
/// </summary>
/// <remarks>
/// 未覆盖：<c>taskBillingOrder</c>（任务计费下单）、<c>ReserveProxyBilling</c>（代理预留）、
/// <c>SettleBillingOrder</c> / <c>RefundBillingOrder</c>（结算/退款，属资金核心，另行专项）。
/// </remarks>
public sealed class FinanceService
{
    /// <summary>兑换码明文长度 32（16 字节 hex）。对应 Go: <c>newRedeemCode</c>。</summary>
    private const int RedeemCodeLength = 32;

    private readonly Repository _repository;
    private readonly CreditPolicyService _creditPolicy;
    private readonly FeatureAvailabilityService _features;

    public FinanceService(
        Repository repository,
        CreditPolicyService creditPolicy,
        FeatureAvailabilityService features)
    {
        _repository = repository;
        _creditPolicy = creditPolicy;
        _features = features;
    }

    // ------------------------------------------------------------ 钱包

    /// <summary>钱包与账本分页。对应 Go: <c>Wallet</c>。</summary>
    public async Task<WalletSummaryDto> WalletAsync(
        User? user,
        string entryType,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (user is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken)
            .ConfigureAwait(false);

        if (page <= 0)
        {
            page = 1;
        }
        if (limit <= 0 || limit > 100)
        {
            limit = 30;
        }

        CreditAccount account = await _repository.CreditAccountAsync(user.ID, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("积分账户不存在");

        (IReadOnlyList<CreditLedgerEntry> entries, long total) = await _repository.CreditLedgerAsync(
            user.ID, entryType.Trim(), limit, (page - 1) * limit, cancellationToken).ConfigureAwait(false);

        PublicCreditPolicy policy = await _creditPolicy.PublicAsync(user.ID, cancellationToken)
            .ConfigureAwait(false);

        return new WalletSummaryDto
        {
            Account = account,
            Entries = entries,
            Total = total,
            Page = page,
            PageSize = limit,
            Policy = policy,
        };
    }

    // ------------------------------------------------------------ 兑换码核销

    /// <summary>核销兑换码。对应 Go: <c>RedeemCredits</c>。</summary>
    public async Task<CreditAccount> RedeemCreditsAsync(
        User? user,
        string code,
        string redeemedIp,
        CancellationToken cancellationToken = default)
    {
        if (user is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken)
            .ConfigureAwait(false);

        string normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length != RedeemCodeLength)
        {
            throw AppError.BadAuthRequest("兑换码无效或已使用");
        }

        try
        {
            return await _repository.RedeemCodeAsync(
                user.ID,
                HashRedeemCode(normalized),
                KernelUtil.TruncateRunes(redeemedIp.Trim(), 64),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RedeemCodeInvalidException)
        {
            throw AppError.BadAuthRequest("兑换码无效或已使用");
        }
    }

    // ------------------------------------------------------------ 签到

    /// <summary>
    /// 每日签到。对应 Go: <c>CheckinCredits</c>。
    /// 返回账户与是否本次真正发放（已签到过时 granted = false）。
    /// </summary>
    public async Task<(CreditAccount Account, bool Granted)> CheckinCreditsAsync(
        User? user, CancellationToken cancellationToken = default)
    {
        if (user is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken)
            .ConfigureAwait(false);

        CreditPolicy policy = await _creditPolicy.GetAsync(cancellationToken).ConfigureAwait(false);
        if (policy.CheckinBonusMicrocredits == 0)
        {
            throw AppError.BadAuthRequest("当前未开启签到奖励");
        }

        string day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return await _repository.GrantCreditsOnceAsync(
            user.ID,
            CreditLedgerType.CreditLedgerCheckinBonus,
            policy.CheckinBonusMicrocredits,
            $"checkin:{user.ID}:{day}",
            "每日签到奖励",
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 管理端：兑换码批次

    /// <summary>创建兑换码批次。对应 Go: <c>AdminCreateRedeemBatch</c>。</summary>
    public async Task<CreateRedeemBatchResultDto> AdminCreateRedeemBatchAsync(
        User? actor,
        CreateRedeemBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (request.AmountMicrocredits <= 0)
        {
            throw AppError.BadAuthRequest("兑换码积分必须大于 0");
        }
        if (request.Count <= 0 || request.Count > 5000)
        {
            throw AppError.BadAuthRequest("单批兑换码数量需为 1-5000");
        }
        if (request.ExpiresAt is not null && request.ExpiresAt <= DateTime.UtcNow)
        {
            throw AppError.BadAuthRequest("兑换码过期时间必须晚于当前时间");
        }

        RedeemBatch batch = new()
        {
            ID = IdGenerator.NewId(),
            AmountMicrocredits = request.AmountMicrocredits,
            Count = request.Count,
            Note = KernelUtil.TruncateRunes(request.Note.Trim(), 500),
            CreatedBy = actor!.ID,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        List<string> codes = new(request.Count);
        List<RedeemCode> items = new(request.Count);
        for (int index = 0; index < request.Count; index++)
        {
            string plain = NewRedeemCode();
            codes.Add(plain);
            items.Add(new RedeemCode
            {
                ID = IdGenerator.NewId(),
                BatchID = batch.ID,
                CodeHash = HashRedeemCode(plain),
                CodeSuffix = plain[^4..],
                AmountMicrocredits = request.AmountMicrocredits,
                Status = RedeemCodeStatus.RedeemCodeUnused,
                ExpiresAt = request.ExpiresAt,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        // 明文码加密后随批次存档，供后续导出；密钥缺失时降级为空（与 Go 的占位加密一致）。
        batch.CodesCipher = EncryptBatchCodes(codes);

        await _repository.CreateRedeemBatchAsync(batch, items, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor,
            "redeem_batch.create",
            "redeem_batch",
            batch.ID,
            "创建兑换码批次",
            new { count = batch.Count, amountMicrocredits = batch.AmountMicrocredits },
            cancellationToken).ConfigureAwait(false);

        return new CreateRedeemBatchResultDto { Batch = batch, Codes = codes };
    }

    /// <summary>批次列表。对应 Go: <c>AdminRedeemBatchPage</c>。</summary>
    public async Task<RedeemBatchPageDto> AdminRedeemBatchPageAsync(
        User? actor,
        string keyword,
        string validity,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        (IReadOnlyList<RedeemBatch> batches, long total) = await _repository.AdminRedeemBatchesAsync(
            keyword, validity, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        return new RedeemBatchPageDto
        {
            Batches = batches,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>批次内兑换码。对应 Go: <c>AdminRedeemCodePage</c>。</summary>
    public async Task<AdminRedeemCodePageDto> AdminRedeemCodePageAsync(
        User? actor,
        string batchId,
        string status,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        RedeemBatch batch = await _repository.RedeemBatchAsync(batchId.Trim(), cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("兑换码批次不存在");

        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        (IReadOnlyList<AdminRedeemCodeRow> rows, long total) = await _repository.AdminRedeemCodesAsync(
            batch.ID, status.Trim(), normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        List<string> plainCodes = DecryptBatchCodes(batch.CodesCipher);
        Dictionary<string, string> plainByHash = new(StringComparer.Ordinal);
        foreach (string code in plainCodes)
        {
            plainByHash[HashRedeemCode(code)] = code;
        }

        DateTime now = DateTime.UtcNow;
        List<AdminRedeemCodeDetailDto> details = new(rows.Count);
        foreach (AdminRedeemCodeRow row in rows)
        {
            // 未使用但已过期的码，对外呈现为 expired。
            string effectiveStatus = row.Status == RedeemCodeStatus.RedeemCodeUnused
                && row.ExpiresAt is not null && row.ExpiresAt <= now
                ? "expired"
                : row.Status;

            details.Add(new AdminRedeemCodeDetailDto
            {
                ID = row.ID,
                Code = plainByHash.TryGetValue(row.CodeHash, out string? plain) ? plain : "",
                CodeSuffix = row.CodeSuffix,
                Status = effectiveStatus,
                RedeemedBy = row.RedeemedBy,
                RedeemedUsername = row.RedeemedUsername,
                RedeemedDisplayName = row.RedeemedDisplayName,
                RedeemedAt = row.RedeemedAt,
                RedeemedIP = row.RedeemedIP,
                ExpiresAt = row.ExpiresAt,
                AmountMicrocredits = row.AmountMicrocredits,
            });
        }

        batch.CodesCipher = "";
        return new AdminRedeemCodePageDto
        {
            Batch = batch,
            Codes = details,
            PlaintextAvailable = plainCodes.Count > 0,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>禁用批次内全部可用兑换码。对应 Go: <c>AdminDisableRedeemBatch</c>。</summary>
    public async Task<long> AdminDisableRedeemBatchAsync(
        User? actor, string batchId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (await _repository.RedeemBatchAsync(batchId.Trim(), cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("兑换码批次不存在");
        }

        long count = await _repository.DisableRedeemBatchAsync(batchId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (count == 0)
        {
            throw AppError.BadAuthRequest("该批次没有可禁用的兑换码");
        }

        await AppendAuditAsync(
            actor!, "redeem_batch.disable", "redeem_batch", batchId,
            "禁用批次内全部未使用兑换码", new { disabledCount = count }, cancellationToken).ConfigureAwait(false);

        return count;
    }

    /// <summary>禁用单个兑换码。对应 Go: <c>AdminDisableRedeemCode</c>。</summary>
    public async Task AdminDisableRedeemCodeAsync(
        User? actor, string batchId, string codeId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        bool disabled = await _repository.DisableRedeemCodeAsync(
            batchId, codeId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!disabled)
        {
            throw AppError.BadAuthRequest("兑换码不存在、已使用、已禁用或已过期");
        }

        await AppendAuditAsync(
            actor!, "redeem_code.disable", "redeem_code", codeId,
            "禁用单个兑换码", new { batchId }, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 管理端：调账与账单

    /// <summary>管理员调账。对应 Go: <c>AdminAdjustCredits</c>。</summary>
    public async Task<CreditAccount> AdminAdjustCreditsAsync(
        User? actor,
        string userId,
        AdminCreditAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (request.AmountMicrocredits == 0)
        {
            throw AppError.BadAuthRequest("调账积分不能为 0");
        }

        string note = request.Note.Trim();
        if (note.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写调账原因");
        }

        if (await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("用户不存在");
        }

        CreditAccount account;
        try
        {
            account = await _repository.AdjustCreditsAsync(
                userId, actor!.ID, request.AmountMicrocredits,
                KernelUtil.TruncateRunes(note, 500), cancellationToken).ConfigureAwait(false);
        }
        catch (InsufficientCreditsException)
        {
            throw AppError.BadAuthRequest("用户可用积分不足，不能执行本次扣减");
        }

        await AppendAuditAsync(
            actor,
            "credits.adjust",
            "user",
            userId,
            "管理员调整用户积分",
            new { amountMicrocredits = request.AmountMicrocredits, note = KernelUtil.TruncateRunes(note, 500) },
            cancellationToken).ConfigureAwait(false);

        return account;
    }

    /// <summary>账单列表。对应 Go: <c>AdminBillingOrderPage</c>。</summary>
    public async Task<BillingOrderPageDto> AdminBillingOrderPageAsync(
        User? actor,
        string status,
        string keyword,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        (IReadOnlyList<BillingOrder> orders, long total) = await _repository.AdminBillingOrdersAsync(
            status, keyword, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        return new BillingOrderPageDto
        {
            Orders = orders,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }


    // ------------------------------------------------------------ 管理端：账单人工核对

    /// <summary>
    /// 人工核对单个账单：结算或退款。对应 Go: <c>ResolveBillingOrder</c>。
    /// </summary>
    public async Task<BillingOrder> ResolveBillingOrderAsync(
        User? actor,
        string id,
        ResolveBillingRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        string note = request.Note.Trim();
        if (note.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写核对依据");
        }

        string action = request.Action.Trim();
        if (action != "settle" && action != "refund")
        {
            throw AppError.BadAuthRequest("请选择结算或退款");
        }

        return await ResolveOneAsync(actor!, id.Trim(), action, note, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量人工核对。逐单提交并明确返回失败项，避免部分成功时给出整体成功的错误反馈。
    /// 对应 Go: <c>ResolveBillingOrders</c>。
    /// </summary>
    public async Task<ResolveBillingBatchResultDto> ResolveBillingOrdersAsync(
        User? actor,
        ResolveBillingBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        string note = request.Note.Trim();
        if (note.Length == 0)
        {
            throw AppError.BadAuthRequest("请填写核对依据");
        }

        string action = request.Action.Trim();
        if (action != "settle" && action != "refund")
        {
            throw AppError.BadAuthRequest("请选择结算或退款");
        }

        // 去重并保序，与 Go 的 seen 集合一致。
        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string rawId in request.IDs)
        {
            string id = rawId.Trim();
            if (id.Length == 0)
            {
                throw AppError.BadAuthRequest("计费订单 ID 无效");
            }
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            throw AppError.BadAuthRequest("请选择要处理的计费订单");
        }
        if (ids.Count > 100)
        {
            throw AppError.BadAuthRequest("单次最多处理 100 条计费订单");
        }

        List<ResolveBillingBatchFailureDto> failed = [];
        int resolved = 0;
        foreach (string id in ids)
        {
            try
            {
                await ResolveOneAsync(actor!, id, action, note, cancellationToken).ConfigureAwait(false);
                resolved++;
            }
            catch (Exception error)
            {
                failed.Add(new ResolveBillingBatchFailureDto
                {
                    ID = id,
                    Message = error is AppError appError ? appError.Message : error.Message,
                });
            }
        }

        return new ResolveBillingBatchResultDto { ResolvedCount = resolved, Failed = failed };
    }

    private async Task<BillingOrder> ResolveOneAsync(
        User actor, string id, string action, string note, CancellationToken cancellationToken)
    {
        BillingOrder order = await _repository.BillingOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("计费订单不存在");

        // 只有仍在冻结/待核对状态的订单需要人工介入。
        if (order.Status != BillingStatus.BillingStatusUncertain
            && order.Status != BillingStatus.BillingStatusRunning
            && order.Status != BillingStatus.BillingStatusReserved)
        {
            throw AppError.BadAuthRequest("当前订单不需要人工核对");
        }

        if (action == "settle")
        {
            await _repository.SettleBillingOrderAsync(id, order.ProviderRequestID, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _repository.RefundBillingOrderAsync(id, note, cancellationToken).ConfigureAwait(false);
        }

        await _repository.RecordBillingResolutionAsync(
            id, actor.ID, KernelUtil.TruncateRunes(note, 500), cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor,
            "billing.resolve",
            "user",
            order.UserID,
            "人工核对用户计费订单",
            new { billingOrderId = id, action, note = KernelUtil.TruncateRunes(note, 500) },
            cancellationToken).ConfigureAwait(false);

        return await _repository.BillingOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("计费订单不存在");
    }

    // ------------------------------------------------------------ 内部


    /// <summary>生成兑换码明文（16 字节随机，设置 UUID v4 版本位）。对应 Go: <c>newRedeemCode</c>。</summary>
    public static string NewRedeemCode()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(16);
        raw[6] = (byte)((raw[6] & 0x0f) | 0x40);
        raw[8] = (byte)((raw[8] & 0x3f) | 0x80);
        return Convert.ToHexString(raw).ToLowerInvariant();
    }

    /// <summary>兑换码哈希。对应 Go: <c>hashRedeemCode</c>（小写去空格后 SHA-256 hex）。</summary>
    public static string HashRedeemCode(string code)
    {
        byte[] sum = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant()));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>
    /// 批次明文码的存档加密。对应 Go: <c>encryptSettingSecret</c>。
    /// 当前为占位实现（明文 JSON），与 Go 的 AES-GCM 差异记入待确认清单。
    /// </summary>
    private static string EncryptBatchCodes(IReadOnlyList<string> codes) =>
        JsonSerializer.Serialize(codes);

    private static List<string> DecryptBatchCodes(string cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(cipher) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task AppendAuditAsync(
        User actor,
        string action,
        string targetType,
        string targetId,
        string summary,
        object metadata,
        CancellationToken cancellationToken)
    {
        await _repository.AppendAdminAuditAsync(new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = action,
            TargetType = targetType,
            TargetID = targetId,
            Summary = summary,
            MetadataJSON = JsonSerializer.Serialize(metadata),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }
}
