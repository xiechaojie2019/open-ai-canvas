#nullable enable
using System.Globalization;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>触发对账请求。对应 Go: <c>app.RunPaymentReconciliationRequest</c>。</summary>
public sealed class RunPaymentReconciliationRequest
{
    [JsonPropertyName("providerId")]
    public string ProviderID { get; set; } = "";

    [JsonPropertyName("billDate")]
    public string BillDate { get; set; } = "";
}

/// <summary>对账运行分页。对应 Go: <c>app.AdminPaymentReconciliationPage</c>。</summary>
public sealed class AdminPaymentReconciliationPage
{
    [JsonPropertyName("runs")]
    public required IReadOnlyList<PaymentReconciliationRun> Runs { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>对账明细分页。对应 Go: <c>app.AdminPaymentReconciliationItemPage</c>。</summary>
public sealed class AdminPaymentReconciliationItemPage
{
    [JsonPropertyName("run")]
    public required PaymentReconciliationRun Run { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<PaymentReconciliationItem> Items { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>
/// 支付对账。对应 Go: <c>internal/app/payment_reconciliation.go</c>。
/// </summary>
public sealed partial class PaymentService
{
    /// <summary>
    /// 账单日按渠道所在地时区计算（Go 固定 <c>Asia/Shanghai</c>）。
    /// </summary>
    /// <remarks>
    /// 用固定偏移而非 <c>TimeZoneInfo.FindSystemTimeZoneById</c>：后者的 ID
    /// 在 Windows（<c>China Standard Time</c>）与 Linux（<c>Asia/Shanghai</c>）不同，
    /// 而 Go 侧就是硬编码 +08:00。
    /// </remarks>
    private static readonly TimeSpan BillTimeZoneOffset = TimeSpan.FromHours(8);

    /// <summary>对账窗口上限：首期只支持最近三个月。对应 Go 的 <c>AddDate(0, -3, 0)</c>。</summary>
    private const int MaxBillHistoryMonths = 3;

    /// <summary>执行对账。对应 Go: <c>RunPaymentReconciliation</c>。</summary>
    public async Task<PaymentReconciliationRun> RunPaymentReconciliationAsync(
        User? actor,
        RunPaymentReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await RunReconciliationAsync(
            actor, request.ProviderID.Trim(), request.BillDate.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对账运行分页。对应 Go: <c>AdminPaymentReconciliationPage</c>。</summary>
    public async Task<AdminPaymentReconciliationPage> AdminPaymentReconciliationPageAsync(
        User? actor, string providerId, string status, int page, int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        (IReadOnlyList<PaymentReconciliationRun> runs, long total) =
            await _repository.AdminPaymentReconciliationRunsAsync(
                providerId, status, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
                .ConfigureAwait(false);

        return new AdminPaymentReconciliationPage
        {
            Runs = runs,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>对账明细分页。对应 Go: <c>AdminPaymentReconciliationItems</c>。</summary>
    public async Task<AdminPaymentReconciliationItemPage> AdminPaymentReconciliationItemsAsync(
        User? actor, string runId, string result, int page, int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        PaymentReconciliationRun run = await _repository
            .PaymentReconciliationRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("对账运行不存在");

        (IReadOnlyList<PaymentReconciliationItem> items, long total) =
            await _repository.PaymentReconciliationItemsAsync(
                run.ID, result, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
                .ConfigureAwait(false);

        return new AdminPaymentReconciliationItemPage
        {
            Run = run,
            Items = items,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    // ------------------------------------------------------------ 主流程

    private async Task<PaymentReconciliationRun> RunReconciliationAsync(
        User? actor, string providerId, string billDateValue, CancellationToken cancellationToken)
    {
        IPaymentProvider provider = _registry.Get(providerId)
            ?? throw AppError.BadAuthRequest("未知支付渠道");

        DateTimeOffset billDate = ParseBillDate(billDateValue);

        PaymentProviderConfig config = await _repository
            .LatestPaymentProviderConfigAsync(providerId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("支付渠道尚未配置");

        Dictionary<string, string> values = await DecryptConfigAsync(config, cancellationToken).ConfigureAwait(false);

        try
        {
            await provider.ValidateConfigAsync(values, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw AppError.BadAuthRequest(error.Message);
        }

        string startedBy = actor?.ID ?? "system";
        (PaymentReconciliationRun run, bool started) = await _repository.BeginPaymentReconciliationAsync(
            new PaymentReconciliationRun
            {
                ID = IdGenerator.NewId(),
                ProviderID = providerId,
                ConfigID = config.ID,
                BillDate = billDateValue,
                Status = PaymentReconciliationStatus.PaymentReconciliationRunning,
                StartedBy = startedBy,
                StartedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

        if (!started)
        {
            return run;
        }

        if (actor is not null)
        {
            try
            {
                await AppendAuditAsync(
                    actor, "payment_reconciliation.run", "payment_provider", providerId,
                    "执行支付渠道对账", new { billDate = billDateValue, runId = run.ID },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await _repository.FailPaymentReconciliationAsync(run.ID, SafePaymentError(error), cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }

        DateTimeOffset start = billDate;
        DateTimeOffset end = start.AddDays(1);

        IReadOnlyList<PaymentOrder> localCredited = await _repository.CreditedPaymentOrdersBetweenAsync(
            providerId, start.UtcDateTime, end.UtcDateTime, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BillRecord> records;
        try
        {
            records = await provider.DownloadTradeBillAsync(values, billDate.DateTime, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PaymentTradeBillNotFoundException error) when (localCredited.Count == 0)
        {
            // 账单不存在本身不是异常——前提是当天既没有已入账订单、也没有悬而未决的订单。
            long candidateCount = await _repository.UnresolvedPaymentOrderCandidateCountOverlappingAsync(
                providerId, start.UtcDateTime, end.UtcDateTime, cancellationToken).ConfigureAwait(false);

            if (candidateCount == 0)
            {
                records = [];
            }
            else
            {
                return await FailReconciliationAsync(run, error, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            return await FailReconciliationAsync(run, error, cancellationToken).ConfigureAwait(false);
        }

        List<PaymentReconciliationItem> items;
        int matched;
        int recovered;
        int failed;
        try
        {
            (items, matched, recovered, failed) = await CompareReconciliationAsync(
                provider, values, run, billDate, records, localCredited, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return await FailReconciliationAsync(run, error, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _repository.CompletePaymentReconciliationAsync(
                run.ID, items, matched, recovered, failed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return await FailReconciliationAsync(run, error, cancellationToken).ConfigureAwait(false);
        }

        return await _repository.PaymentReconciliationRunAsync(run.ID, cancellationToken).ConfigureAwait(false)
            ?? run;
    }

    /// <summary>
    /// 逐条比对渠道账单与本地订单。对应 Go: <c>comparePaymentReconciliation</c>。
    /// </summary>
    /// <remarks>
    /// <b>关键安全约束</b>：下载到的账单只是「线索」，不足以作为付款授权。
    /// 每一笔补发都必须先经<b>签名查单</b>确认，否则伪造的账单文件就能凭空充值。
    /// </remarks>
    private async Task<(List<PaymentReconciliationItem> Items, int Matched, int Recovered, int Failed)>
        CompareReconciliationAsync(
            IPaymentProvider provider,
            Dictionary<string, string> values,
            PaymentReconciliationRun run,
            DateTimeOffset billDate,
            IReadOnlyList<BillRecord> records,
            IReadOnlyList<PaymentOrder> localCredited,
            CancellationToken cancellationToken)
    {
        List<PaymentReconciliationItem> items = new(records.Count + localCredited.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int matched = 0;
        int recovered = 0;
        int failed = 0;

        foreach (BillRecord record in records)
        {
            string merchantOrderNo = (record.MerchantOrderNo ?? "").Trim();
            if (merchantOrderNo.Length == 0 || !seen.Add(merchantOrderNo))
            {
                // 空单号或重复单号：跳过，避免同一笔被处理两次。
                continue;
            }

            PaymentReconciliationItem item = NewItem(run, merchantOrderNo, (record.ProviderTradeNo ?? "").Trim(),
                record.AmountFen, record.Currency);

            PaymentOrder? order = await _repository
                .PaymentOrderByMerchantAsync(run.ProviderID, merchantOrderNo, cancellationToken).ConfigureAwait(false);

            if (order is null)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationLocalOrderNotFound;
                item.Detail = "渠道账单存在成功交易，但本地订单不存在";
                items.Add(item);
                failed++;
                continue;
            }

            item.PaymentOrderID = order.ID;

            if (record.AmountFen != order.AmountFen || record.Currency != order.Currency)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationAmountMismatch;
                item.Detail = $"渠道金额 {record.AmountFen} {record.Currency}，本地金额 {order.AmountFen} {order.Currency}";
                items.Add(item);
                failed++;
                continue;
            }

            if (!string.IsNullOrEmpty(order.ProviderTradeNo)
                && order.ProviderTradeNo != record.ProviderTradeNo)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationTradeNoMismatch;
                item.Detail = "渠道交易号与本地已记录交易号不一致";
                items.Add(item);
                failed++;
                continue;
            }

            if (order.Status == PaymentOrderStatus.PaymentOrderCredited)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationMatched;
                item.Resolved = true;
                items.Add(item);
                matched++;
                continue;
            }

            // ---- 需要补发：必须先经签名查单确认 ----
            PaymentResult confirmed;
            try
            {
                confirmed = await provider.QueryOrderAsync(
                    values, new QueryRequest { MerchantOrderNo = merchantOrderNo }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationCreditFailed;
                item.Detail = "渠道账单显示已支付，但签名查单确认失败：" + SafeReconciliationError(error);
                items.Add(item);
                failed++;
                continue;
            }

            if (confirmed.MerchantOrderNo.Trim() != merchantOrderNo || !confirmed.Paid)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationCreditFailed;
                item.Detail = "渠道账单显示已支付，但签名查单未确认该订单已支付";
                items.Add(item);
                failed++;
                continue;
            }

            if (confirmed.AmountFen != order.AmountFen || confirmed.Currency != order.Currency)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationAmountMismatch;
                item.Detail = $"签名查单金额 {confirmed.AmountFen} {confirmed.Currency}，本地金额 {order.AmountFen} {order.Currency}";
                items.Add(item);
                failed++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(confirmed.ProviderTradeNo)
                || confirmed.ProviderTradeNo.Trim() != (record.ProviderTradeNo ?? "").Trim())
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationTradeNoMismatch;
                item.Detail = "渠道账单交易号与签名查单交易号不一致";
                items.Add(item);
                failed++;
                continue;
            }

            DateTime paidAt = confirmed.PaidAt;
            if (paidAt == default)
            {
                paidAt = record.PaidAt;
            }
            if (paidAt == default)
            {
                // 两边都没给时间：用账单日正午兜底，保证 provider_paid_at 落在该账单日区间内。
                paidAt = billDate.AddHours(12).UtcDateTime;
            }

            try
            {
                await _repository.CompletePaymentOrderAsync(run.ProviderID, merchantOrderNo, new PaymentEvidence
                {
                    ProviderTradeNo = confirmed.ProviderTradeNo,
                    ProviderStatus = confirmed.ProviderStatus,
                    AmountFen = confirmed.AmountFen,
                    Currency = confirmed.Currency,
                    PaidAt = paidAt,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                item.Result = PaymentReconciliationResult.PaymentReconciliationCreditFailed;
                item.Detail = "渠道账单确认已支付，但自动补发积分失败：" + SafePaymentError(error);
                items.Add(item);
                failed++;
                continue;
            }

            item.Result = PaymentReconciliationResult.PaymentReconciliationRecovered;
            item.Resolved = true;
            item.Detail = "已根据渠道账单自动补发积分";
            items.Add(item);
            recovered++;
        }

        // 反向检查：本地已入账，但渠道账单里找不到对应成功交易。
        foreach (PaymentOrder order in localCredited)
        {
            if (seen.Contains(order.MerchantOrderNo))
            {
                continue;
            }

            items.Add(new PaymentReconciliationItem
            {
                ID = IdGenerator.NewId(),
                RunID = run.ID,
                ProviderID = run.ProviderID,
                PaymentOrderID = order.ID,
                MerchantOrderNo = order.MerchantOrderNo,
                ProviderTradeNo = order.ProviderTradeNo ?? "",
                AmountFen = order.AmountFen,
                Currency = order.Currency,
                Result = PaymentReconciliationResult.PaymentReconciliationProviderRecordMissing,
                Detail = "本地订单已入账，但渠道成功交易账单中未找到对应记录",
                CreatedAt = DateTime.UtcNow,
            });
            failed++;
        }

        return (items, matched, recovered, failed);
    }

    private static PaymentReconciliationItem NewItem(
        PaymentReconciliationRun run, string merchantOrderNo, string providerTradeNo,
        long amountFen, string currency) => new()
    {
        ID = IdGenerator.NewId(),
        RunID = run.ID,
        ProviderID = run.ProviderID,
        MerchantOrderNo = merchantOrderNo,
        ProviderTradeNo = providerTradeNo,
        AmountFen = amountFen,
        Currency = currency,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>标记失败并返回最新状态。对应 Go: <c>failPaymentReconciliation</c>。</summary>
    private async Task<PaymentReconciliationRun> FailReconciliationAsync(
        PaymentReconciliationRun run, Exception cause, CancellationToken cancellationToken)
    {
        await _repository.FailPaymentReconciliationAsync(run.ID, SafeReconciliationError(cause), cancellationToken)
            .ConfigureAwait(false);

        return await _repository.PaymentReconciliationRunAsync(run.ID, cancellationToken).ConfigureAwait(false)
            ?? run;
    }

    /// <summary>
    /// 对账错误文案。对应 Go: <c>safePaymentReconciliationError</c>。
    /// </summary>
    /// <remarks>
    /// 对账是管理员专属诊断，解析/持久化错误不含渠道密钥，直接暴露原文比只留类型更有用；
    /// 但「账单不存在」要归一成稳定码，便于前端识别。
    /// </remarks>
    private static string SafeReconciliationError(Exception error) => error switch
    {
        PaymentTradeBillNotFoundException => "TRADE_BILL_NOT_FOUND",
        PaymentProviderException provider when provider.Code.Length > 0 =>
            KernelUtil.TruncateRunes(provider.Code, 1000),
        _ => KernelUtil.TruncateRunes(error.Message.Trim(), 1000),
    };

    /// <summary>
    /// 解析账单日（渠道时区）。对应 Go: <c>parsePaymentBillDate</c>。
    /// </summary>
    private static DateTimeOffset ParseBillDate(string value)
    {
        if (!DateTime.TryParseExact(
                value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed))
        {
            throw AppError.BadAuthRequest("账单日期格式必须为 YYYY-MM-DD");
        }

        DateTimeOffset candidate = new(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), BillTimeZoneOffset);

        // 「今天」按渠道时区算，否则跨零点时区会差一天。
        DateTimeOffset nowInBillZone = DateTimeOffset.UtcNow.ToOffset(BillTimeZoneOffset);
        DateTimeOffset today = new(nowInBillZone.Date, BillTimeZoneOffset);

        if (candidate >= today)
        {
            throw AppError.BadAuthRequest("只能对账昨天及更早的账单");
        }

        if (candidate < today.AddMonths(-MaxBillHistoryMonths))
        {
            throw AppError.BadAuthRequest("首期仅支持最近三个月的账单对账");
        }

        return candidate;
    }
}
