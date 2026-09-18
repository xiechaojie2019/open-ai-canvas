#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Payment;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using OpenAICanvas.Protocol;
// 协议通知与实体同名：协议侧用于解析渠道回调载荷，实体侧用于收件箱落库。
using PaymentNotificationPayload = OpenAICanvas.Payment.PaymentNotification;
using PaymentNotificationEntity = OpenAICanvas.Domain.Entities.PaymentNotification;

namespace OpenAICanvas.Application;

/// <summary>
/// 充值商品、支付渠道配置与支付订单。
/// 对应 Go: <c>internal/app/payment.go</c>（不含对账部分）。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：宿主掌握订单、凭证存储、网络策略与入账事务；
/// 适配器（<see cref="IPaymentProvider"/>）只翻译某一家渠道的协议。
/// </para>
/// <para>
/// <b>适配器注册表当前为空</b>——内置的微信/支付宝适配器尚未移植（按用户指示暂缓）。
/// 因此 <c>GET /payments/providers</c> 返回空数组、下单报「未知支付渠道」，
/// 这与 Go 在「插件未启用」时的行为一致。测试通过注入测试适配器打通全流程。
/// </para>
/// </remarks>
public sealed partial class PaymentService
{
    /// <summary>单用户未支付订单上限。对应 Go: <c>maxActivePaymentOrdersPerUser</c>。</summary>
    private const int MaxActiveOrdersPerUser = 5;

    /// <summary>单笔充值积分上限（10 亿积分）。对应 Go: <c>maxTopupCreditsMicrocredits</c>。</summary>
    private static readonly long MaxTopupCreditsMicrocredits = 1_000_000_000L * CreditPolicyService.CreditScale;

    /// <summary>单笔充值金额上限（100 万元，单位分）。对应 Go 的 <c>100_000_000</c>。</summary>
    private const long MaxTopupAmountFen = 100_000_000;

    /// <summary>查单节流窗口。对应 Go 的 <c>2*time.Second</c>。</summary>
    private static readonly TimeSpan QueryThrottle = TimeSpan.FromSeconds(2);

    private readonly Repository _repository;
    private readonly PaymentRegistry _registry;
    private readonly IPluginAvailability _plugins;
    private readonly FeatureAvailabilityService _features;

    public PaymentService(
        Repository repository,
        PaymentRegistry registry,
        IPluginAvailability plugins,
        FeatureAvailabilityService features)
    {
        _repository = repository;
        _registry = registry;
        _plugins = plugins;
        _features = features;
    }

    // ------------------------------------------------------------ 充值商品

    /// <summary>用户可见的充值商品。对应 Go: <c>TopupProducts</c>。</summary>
    public async Task<IReadOnlyList<TopupProduct>> TopupProductsAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken).ConfigureAwait(false);
        return await _repository.TopupProductsAsync(false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>全部充值商品（含停用）。对应 Go: <c>AdminTopupProducts</c>。</summary>
    public async Task<IReadOnlyList<TopupProduct>> AdminTopupProductsAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        return await _repository.TopupProductsAsync(true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建充值商品。对应 Go: <c>CreateTopupProduct</c>。</summary>
    public async Task<TopupProduct> CreateTopupProductAsync(
        User? actor, TopupProductRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        TopupProduct product = TopupProductFromRequest(IdGenerator.NewId(), actor!.ID, request);
        await _repository.CreateTopupProductAsync(product, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor, "topup_product.create", "topup_product", product.ID, "创建积分充值商品",
            new { amountFen = product.AmountFen, creditsMicrocredits = product.CreditsMicrocredits },
            cancellationToken).ConfigureAwait(false);

        return product;
    }

    /// <summary>更新充值商品。对应 Go: <c>UpdateTopupProduct</c>。</summary>
    public async Task<TopupProduct> UpdateTopupProductAsync(
        User? actor, string id, TopupProductRequest request, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (await _repository.TopupProductAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("充值商品不存在");
        }

        TopupProduct product = TopupProductFromRequest(id.Trim(), actor!.ID, request);
        await _repository.UpdateTopupProductAsync(product, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor, "topup_product.update", "topup_product", product.ID, "更新积分充值商品",
            new { enabled = product.Enabled }, cancellationToken).ConfigureAwait(false);

        return await _repository.TopupProductAsync(product.ID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("充值商品不存在");
    }

    private static TopupProduct TopupProductFromRequest(string id, string actorId, TopupProductRequest request)
    {
        string name = request.Name.Trim();
        if (name.Length == 0 || name.EnumerateRunes().Count() > 120)
        {
            throw AppError.BadAuthRequest("充值商品名称不能为空且不能超过 120 个字符");
        }
        if (request.AmountFen <= 0 || request.AmountFen > MaxTopupAmountFen)
        {
            throw AppError.BadAuthRequest("充值金额必须为 1 分至 100 万元");
        }
        if (request.CreditsMicrocredits <= 0 || request.CreditsMicrocredits > MaxTopupCreditsMicrocredits)
        {
            throw AppError.BadAuthRequest("充值积分必须为 0.000001 至 10 亿积分");
        }

        DateTime now = DateTime.UtcNow;
        return new TopupProduct
        {
            ID = id,
            Name = name,
            Description = KernelUtil.TruncateRunes(request.Description.Trim(), 500),
            AmountFen = request.AmountFen,
            CreditsMicrocredits = request.CreditsMicrocredits,
            Enabled = request.Enabled,
            SortOrder = request.SortOrder,
            CreatedBy = actorId,
            UpdatedBy = actorId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ------------------------------------------------------------ 支付渠道

    /// <summary>用户可见的已启用且已配置渠道。对应 Go: <c>PaymentProviders</c>。</summary>
    public async Task<List<PaymentProviderView>> PaymentProvidersAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken).ConfigureAwait(false);

        List<PaymentProviderView> items = [];
        foreach (PaymentProviderDescriptor descriptor in _registry.Descriptors())
        {
            (PaymentProviderView view, _) = await ProviderViewAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (view.Enabled && view.Configured)
            {
                items.Add(view);
            }
        }

        return items;
    }

    /// <summary>管理端渠道列表（含配置与字段定义）。对应 Go: <c>AdminPaymentProviders</c>。</summary>
    public async Task<List<AdminPaymentProviderView>> AdminPaymentProvidersAsync(
        User? actor, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        List<AdminPaymentProviderView> items = [];
        foreach (PaymentProviderDescriptor descriptor in _registry.Descriptors())
        {
            (PaymentProviderView baseView, PaymentProviderConfig? config) =
                await ProviderViewAsync(descriptor, cancellationToken).ConfigureAwait(false);

            PaymentPluginManifest? manifest = PaymentPluginManifests.ForProvider(descriptor.ID);
            List<ManifestField> fields = manifest?.ConfigFields ?? [];

            Dictionary<string, string> values = new(StringComparer.Ordinal);
            Dictionary<string, bool> secretConfigured = new(StringComparer.Ordinal);
            string configId = "";
            bool configEnabled = false;
            long version = 0;
            DateTime? updatedAt = null;

            if (config is not null)
            {
                Dictionary<string, string> decrypted = await DecryptConfigAsync(config, cancellationToken)
                    .ConfigureAwait(false);
                configId = config.ID;
                configEnabled = config.Enabled;
                version = config.Version;
                updatedAt = config.CreatedAt;

                foreach (ManifestField field in fields)
                {
                    if (field.Secret)
                    {
                        // 敏感字段只回「是否已配置」，绝不回明文。
                        secretConfigured[field.Name] = decrypted.TryGetValue(field.Name, out string? secret)
                            && secret.Trim().Length > 0;
                        continue;
                    }
                    values[field.Name] = decrypted.TryGetValue(field.Name, out string? value) ? value : "";
                }
            }

            items.Add(new AdminPaymentProviderView
            {
                ID = baseView.ID,
                PluginID = baseView.PluginID,
                Name = baseView.Name,
                Icon = baseView.Icon,
                CheckoutMode = baseView.CheckoutMode,
                Enabled = baseView.Enabled,
                PluginEnabled = baseView.PluginEnabled,
                Configured = baseView.Configured,
                CloseAfterMinutes = baseView.CloseAfterMinutes,
                ConfigID = configId,
                ConfigEnabled = configEnabled,
                Version = version,
                Values = values,
                SecretConfigured = secretConfigured,
                ConfigFields = fields,
                UpdatedAt = updatedAt,
            });
        }

        return items;
    }

    /// <summary>
    /// 更新渠道配置（新建一个版本）。对应 Go: <c>UpdatePaymentProviderConfig</c>。
    /// </summary>
    /// <remarks>
    /// 敏感字段留空表示「保持原值」——这是与前端约定的语义，不能覆盖成空串。
    /// </remarks>
    public async Task<AdminPaymentProviderView> UpdatePaymentProviderConfigAsync(
        User? actor, string providerId, UpdatePaymentProviderConfigRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        IPaymentProvider provider = _registry.Get(providerId)
            ?? throw AppError.BadAuthRequest("未知支付渠道");
        PaymentProviderDescriptor descriptor = provider.Descriptor;

        PaymentPluginManifest manifest = PaymentPluginManifests.ForProvider(descriptor.ID)
            ?? throw AppError.BadAuthRequest("支付插件清单不存在");

        ManifestPaymentExpiryPolicy policy = manifest.Contribution.ExpiryPolicy;
        if (request.CloseAfterMinutes < policy.MinMinutes || request.CloseAfterMinutes > policy.MaxMinutes)
        {
            throw AppError.BadAuthRequest(
                $"未支付关闭时间必须为 {policy.MinMinutes}-{policy.MaxMinutes} 分钟");
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        PaymentProviderConfig? current = await _repository
            .LatestPaymentProviderConfigAsync(descriptor.ID, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            values = await DecryptConfigAsync(current, cancellationToken).ConfigureAwait(false);
        }

        foreach (ManifestField field in manifest.ConfigFields)
        {
            bool supplied = request.Values.TryGetValue(field.Name, out string? raw);
            string value = (raw ?? "").Trim();

            // 敏感字段未提供或留空：沿用已存值。
            if (field.Secret && (!supplied || value.Length == 0))
            {
                continue;
            }

            if (!supplied && field.Default is not null
                && !(values.TryGetValue(field.Name, out string? existing) && existing.Trim().Length > 0))
            {
                value = Convert.ToString(field.Default, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }

            values[field.Name] = value;
        }

        // 公网地址用于拼接回调 URL，必须是干净的根地址。
        if (values.TryGetValue("publicBaseUrl", out string? baseUrl))
        {
            ValidatePublicBaseUrl(baseUrl);
        }

        foreach (ManifestField field in manifest.ConfigFields)
        {
            if (field.Required && (!values.TryGetValue(field.Name, out string? value) || value.Trim().Length == 0))
            {
                throw AppError.BadAuthRequest($"{field.Label} 不能为空");
            }
        }

        string encoded = JsonSerializer.Serialize(values);
        PaymentProviderConfig config = new()
        {
            ID = IdGenerator.NewId(),
            ProviderID = descriptor.ID,
            PluginID = descriptor.PluginID,
            PluginVersion = descriptor.PluginVersion,
            Enabled = request.Enabled,
            CloseAfterMinutes = request.CloseAfterMinutes,
            ConfigCipher = EncryptSecret(encoded),
            ConfigDigest = Sha256Hex(encoded),
            CreatedBy = actor!.ID,
            CreatedAt = DateTime.UtcNow,
        };

        await _repository.CreatePaymentProviderConfigAsync(config, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor, "payment_provider.update", "payment_provider", descriptor.ID,
            "更新支付渠道配置", new { enabled = request.Enabled }, cancellationToken).ConfigureAwait(false);

        (PaymentProviderView _, PaymentProviderConfig? latest) =
            await ProviderViewAsync(descriptor, cancellationToken).ConfigureAwait(false);

        // 直接复用管理端列表里对应项的构造逻辑。
        List<AdminPaymentProviderView> all = await AdminPaymentProvidersAsync(actor, cancellationToken)
            .ConfigureAwait(false);
        _ = latest;
        return all.FirstOrDefault(item => string.Equals(item.ID, descriptor.ID, StringComparison.Ordinal))
            ?? throw AppError.NotFound("支付渠道不存在");
    }

    private async Task<(PaymentProviderView View, PaymentProviderConfig? Config)> ProviderViewAsync(
        PaymentProviderDescriptor descriptor, CancellationToken cancellationToken)
    {
        PaymentProviderView view = new()
        {
            ID = descriptor.ID,
            PluginID = descriptor.PluginID,
            Name = descriptor.Name,
            Icon = descriptor.Icon,
            CheckoutMode = descriptor.CheckoutMode,
        };

        PaymentPluginManifest? manifest = PaymentPluginManifests.ForProvider(descriptor.ID);
        if (manifest is not null)
        {
            view.CloseAfterMinutes = manifest.Contribution.ExpiryPolicy.DefaultMinutes;
        }

        bool pluginAvailable = await _plugins.IsAvailableAsync(descriptor.PluginID, cancellationToken)
            .ConfigureAwait(false);
        view.PluginEnabled = pluginAvailable;
        view.Enabled = pluginAvailable;

        PaymentProviderConfig? config = await _repository
            .LatestPaymentProviderConfigAsync(descriptor.ID, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            return (view, null);
        }

        view.Configured = config.ConfigCipher.Trim().Length > 0;
        view.Enabled = view.Enabled && config.Enabled;
        view.CloseAfterMinutes = config.CloseAfterMinutes;
        return (view, config);
    }

    /// <summary>解密渠道配置。对应 Go: <c>decryptPaymentConfig</c>。</summary>
    private static Task<Dictionary<string, string>> DecryptConfigAsync(
        PaymentProviderConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (config is null)
        {
            throw new InvalidOperationException("支付渠道配置不存在");
        }

        string plain = DecryptSecret(config.ConfigCipher);
        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("支付渠道配置内容无效");
        }

        return Task.FromResult(values ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>公网地址校验。对应 Go: <c>validatePaymentPublicBaseURL</c>。</summary>
    private static void ValidatePublicBaseUrl(string value)
    {
        string trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != "https" && parsed.Scheme != "http")
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || !string.IsNullOrEmpty(parsed.Query)
            || (parsed.AbsolutePath.Length > 0 && parsed.AbsolutePath != "/"))
        {
            throw new InvalidOperationException("服务器公网地址必须是有效的 HTTP(S) 根地址");
        }
    }

    // ------------------------------------------------------------ 支付订单

    /// <summary>创建支付订单。对应 Go: <c>CreatePaymentOrder</c>。</summary>
    public async Task<PaymentOrderView> CreatePaymentOrderAsync(
        User? actor, CreatePaymentOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        await _features.RequireFeatureAsync(FeatureNames.Credits, cancellationToken).ConfigureAwait(false);

        string idempotencyKey = request.IdempotencyKey.Trim();
        if (idempotencyKey.Length == 0)
        {
            throw AppError.BadAuthRequest("支付幂等标识不能为空");
        }
        if (idempotencyKey.Length > 120)
        {
            throw AppError.BadAuthRequest("支付幂等标识过长");
        }

        string productId = request.ProductID.Trim();
        string providerId = request.ProviderID.Trim();

        // 幂等：同键重试必须命中同一订单；商品或渠道变了说明客户端用错了键。
        PaymentOrder? existing = await _repository
            .PaymentOrderByIdempotencyAsync(actor.ID, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ProductID != productId || existing.ProviderID != providerId)
            {
                throw AppError.New(409, "支付幂等标识已用于不同的商品或支付渠道");
            }
            return PaymentOrderViewOf(existing);
        }

        TopupProduct product = await _repository.TopupProductAsync(productId, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.BadAuthRequest("充值商品不存在或已停用");
        if (!product.Enabled)
        {
            throw AppError.BadAuthRequest("充值商品不存在或已停用");
        }

        IPaymentProvider provider = _registry.Get(providerId)
            ?? throw AppError.BadAuthRequest("未知支付渠道");
        (PaymentProviderView view, PaymentProviderConfig? config) =
            await ProviderViewAsync(provider.Descriptor, cancellationToken).ConfigureAwait(false);

        if (!view.Enabled || !view.Configured || config is null)
        {
            throw AppError.Forbidden("支付渠道未启用或尚未配置");
        }

        long activeCount = await _repository.ActivePaymentOrderCountAsync(actor.ID, cancellationToken)
            .ConfigureAwait(false);
        if (activeCount >= MaxActiveOrdersPerUser)
        {
            throw AppError.New(409, "未支付订单过多，请先完成或关闭已有订单");
        }

        DateTime now = DateTime.UtcNow;
        PaymentOrder order = new()
        {
            ID = IdGenerator.NewId(),
            UserID = actor.ID,
            IdempotencyKey = idempotencyKey,
            MerchantOrderNo = IdGenerator.NewId(),
            ProductID = product.ID,
            ProductName = product.Name,
            ProviderID = provider.Descriptor.ID,
            PluginID = provider.Descriptor.PluginID,
            PluginVersion = provider.Descriptor.PluginVersion,
            ProviderConfigID = config.ID,
            ProviderConfigVersion = config.Version,
            AmountFen = product.AmountFen,
            Currency = "CNY",
            CreditsMicrocredits = product.CreditsMicrocredits,
            Status = PaymentOrderStatus.PaymentOrderCreated,
            CheckoutMode = provider.Descriptor.CheckoutMode,
            ExpiresAt = now.AddMinutes(config.CloseAfterMinutes),
            CreatedAt = now,
            UpdatedAt = now,
        };

        (PaymentOrder claimed, bool created) = await _repository
            .CreatePaymentOrderAsync(order, cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            return PaymentOrderViewOf(claimed);
        }

        Dictionary<string, string> values = await DecryptConfigAsync(config, cancellationToken).ConfigureAwait(false);
        string baseUrl = (values.GetValueOrDefault("publicBaseUrl") ?? "").TrimEnd('/');

        Checkout checkout;
        try
        {
            checkout = await provider.CreateOrderAsync(values, new CreateRequest
            {
                MerchantOrderNo = order.MerchantOrderNo,
                Description = product.Name,
                AmountFen = order.AmountFen,
                Currency = order.Currency,
                ExpiresAt = order.ExpiresAt,
                NotifyURL = baseUrl + "/api/payments/notify/" + Uri.EscapeDataString(order.ProviderID)
                    + "/" + Uri.EscapeDataString(config.ID),
                ReturnURL = baseUrl + "/api/payments/return/" + Uri.EscapeDataString(order.ProviderID)
                    + "?orderId=" + Uri.EscapeDataString(order.ID),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 下单失败要留痕，方便管理员排查渠道侧问题。
            await _repository.SetPaymentOrderCreateFailureAsync(order.ID, SafePaymentError(error), cancellationToken)
                .ConfigureAwait(false);
            throw AppError.Wrap(502, "支付渠道下单失败，请稍后重试", error);
        }

        try
        {
            await _repository.SetPaymentOrderCheckoutAsync(
                order.ID, checkout.Mode, checkout.Value, checkout.ExpiresAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _repository.SetPaymentOrderCreateFailureAsync(order.ID, SafePaymentError(error), cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(order.ID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    /// <summary>用户查询自己的订单。对应 Go: <c>PaymentOrder</c>。</summary>
    public async Task<PaymentOrderView> PaymentOrderAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        PaymentOrder order = await _repository.PaymentOrderForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(order);
    }

    /// <summary>收银台跳转地址。对应 Go: <c>PaymentCheckout</c>。</summary>
    public async Task<string> PaymentCheckoutAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        PaymentOrder order = await _repository.PaymentOrderForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        if (order.Status != PaymentOrderStatus.PaymentOrderPending
            || order.CheckoutMode != "redirect"
            || string.IsNullOrWhiteSpace(order.CheckoutValue)
            || order.ExpiresAt <= DateTime.UtcNow)
        {
            throw AppError.BadAuthRequest("支付订单当前不能跳转收银台");
        }

        return order.CheckoutValue;
    }

    /// <summary>
    /// 刷新收银台。对应 Go: <c>RefreshPaymentCheckout</c>。
    /// </summary>
    /// <remarks>
    /// 始终复用原商户单号，且刷新前必须先查单——否则「下单响应不确定」会变成重复支付。
    /// </remarks>
    public async Task<PaymentOrderView> RefreshPaymentCheckoutAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        PaymentOrder order = await _repository.PaymentOrderForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        bool refreshableMode = order.CheckoutMode is "qr_code" or "redirect";
        bool refreshableStatus = order.Status is PaymentOrderStatus.PaymentOrderPending
            or PaymentOrderStatus.PaymentOrderCreateFailed;
        if (!refreshableMode || !refreshableStatus || order.ExpiresAt <= DateTime.UtcNow)
        {
            throw AppError.BadAuthRequest("支付订单当前不能刷新收银台");
        }

        (IPaymentProvider provider, PaymentProviderConfig config, Dictionary<string, string> values) =
            await RuntimeForOrderAsync(order, cancellationToken).ConfigureAwait(false);

        try
        {
            PaymentResult result = await provider.QueryOrderAsync(
                values, new QueryRequest { MerchantOrderNo = order.MerchantOrderNo }, cancellationToken)
                .ConfigureAwait(false);

            if (result.MerchantOrderNo.Trim() != order.MerchantOrderNo)
            {
                throw new PaymentEvidenceMismatchException();
            }

            await _repository.RecordPaymentQueryAsync(order.ID, result.ProviderStatus, cancellationToken)
                .ConfigureAwait(false);
            (PaymentOrder updated, _) = await ApplyPaymentResultAsync(
                order.ProviderID, result, cancellationToken).ConfigureAwait(false);

            if (result.Paid || result.Closed)
            {
                return PaymentOrderViewOf(updated);
            }
        }
        catch (PaymentOrderNotFoundException)
        {
            // 上游查不到单：继续走重建收银台。
        }
        catch (PaymentProviderException error)
        {
            throw AppError.Wrap(502, "刷新支付收银台前查单失败，请稍后重试", error);
        }

        string baseUrl = (values.GetValueOrDefault("publicBaseUrl") ?? "").TrimEnd('/');
        Checkout checkout;
        try
        {
            checkout = await provider.CreateOrderAsync(values, new CreateRequest
            {
                MerchantOrderNo = order.MerchantOrderNo,
                Description = order.ProductName,
                AmountFen = order.AmountFen,
                Currency = order.Currency,
                ExpiresAt = order.ExpiresAt,
                NotifyURL = baseUrl + "/api/payments/notify/" + Uri.EscapeDataString(order.ProviderID)
                    + "/" + Uri.EscapeDataString(config.ID),
                ReturnURL = baseUrl + "/api/payments/return/" + Uri.EscapeDataString(order.ProviderID)
                    + "?orderId=" + Uri.EscapeDataString(order.ID),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw AppError.Wrap(502, "刷新支付收银台失败，请稍后重试", error);
        }

        await _repository.SetPaymentOrderCheckoutAsync(
            order.ID, checkout.Mode, checkout.Value, checkout.ExpiresAt, cancellationToken).ConfigureAwait(false);

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(order.ID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    /// <summary>主动查单。对应 Go: <c>QueryPaymentOrder</c>（含 2 秒节流）。</summary>
    public async Task<PaymentOrderView> QueryPaymentOrderAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        PaymentOrder order = await _repository.PaymentOrderForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        if (order.Status is PaymentOrderStatus.PaymentOrderCredited or PaymentOrderStatus.PaymentOrderClosed)
        {
            return PaymentOrderViewOf(order);
        }

        // 前端会轮询，2 秒内直接返回缓存结果，避免打爆渠道接口。
        if (order.LastQueriedAt is not null && DateTime.UtcNow - order.LastQueriedAt.Value < QueryThrottle)
        {
            return PaymentOrderViewOf(order);
        }

        await QueryPaymentOrderInternalAsync(order, cancellationToken).ConfigureAwait(false);

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(order.ID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    /// <summary>用户关单。对应 Go: <c>ClosePaymentOrder</c>。</summary>
    public async Task<PaymentOrderView> ClosePaymentOrderAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        if (actor is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        PaymentOrder order = await _repository.PaymentOrderForUserAsync(actor.ID, id, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        if (order.Status is PaymentOrderStatus.PaymentOrderCredited or PaymentOrderStatus.PaymentOrderClosed)
        {
            return PaymentOrderViewOf(order);
        }

        await ClosePaymentOrderInternalAsync(order, cancellationToken).ConfigureAwait(false);

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(order.ID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    // ------------------------------------------------------------ 支付通知

    /// <summary>
    /// 接收并处理支付回调。对应 Go: <c>AcceptPaymentNotification</c>。
    /// </summary>
    /// <remarks>
    /// 回调路径要快，但会立刻尝试入账一次；失败则落一条持久化收件箱记录交给 worker 重试。
    /// </remarks>
    public async Task AcceptPaymentNotificationAsync(
        string providerId, string configId, IReadOnlyDictionary<string, string[]> headers, byte[] rawBody,
        CancellationToken cancellationToken = default)
    {
        IPaymentProvider provider = _registry.Get(providerId)
            ?? throw AppError.BadAuthRequest("未知支付通知渠道");

        PaymentProviderConfig config = await _repository
            .PaymentProviderConfigAsync(configId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.BadAuthRequest("支付通知配置版本不存在");
        if (config.ProviderID != providerId)
        {
            throw AppError.BadAuthRequest("支付通知配置版本不存在");
        }

        Dictionary<string, string> values = await DecryptConfigAsync(config, cancellationToken).ConfigureAwait(false);

        PaymentNotificationPayload notification;
        try
        {
            notification = await provider.VerifyNotificationAsync(values, headers, rawBody, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw AppError.BadAuthRequest("支付通知验签失败");
        }

        PaymentOrder order = await _repository
            .PaymentOrderByMerchantAsync(providerId, notification.MerchantOrderNo, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.BadAuthRequest("支付通知订单不存在或配置版本不匹配");
        if (order.ProviderConfigID != config.ID)
        {
            throw AppError.BadAuthRequest("支付通知订单不存在或配置版本不匹配");
        }

        string normalized = JsonSerializer.Serialize(notification, PaymentJson.Options);
        string payloadCipher = EncryptSecret(Encoding.UTF8.GetString(rawBody));
        byte[] digest = SHA256.HashData(rawBody);

        PaymentNotificationEntity inbox = new()
        {
            ID = IdGenerator.NewId(),
            ProviderID = providerId,
            ProviderEventID = KernelUtil.TruncateRunes(notification.EventID, 160),
            ProviderConfigID = config.ID,
            MerchantOrderNo = order.MerchantOrderNo,
            PaymentOrderID = order.ID,
            PayloadDigest = Convert.ToHexString(digest).ToLowerInvariant(),
            PayloadCipher = payloadCipher,
            NormalizedJSON = normalized,
            Status = PaymentNotificationStatus.PaymentNotificationPending,
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        bool created = await _repository.SaveVerifiedPaymentNotificationAsync(inbox, cancellationToken)
            .ConfigureAwait(false);
        if (!created)
        {
            // 重复回调：已处理过，直接成功返回。
            return;
        }

        try
        {
            await ProcessPaymentNotificationAsync(inbox, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await _repository.RetryPaymentNotificationAsync(
                inbox.ID, SafePaymentError(error), DateTime.UtcNow.AddSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessPaymentNotificationAsync(
        PaymentNotificationEntity notification, CancellationToken cancellationToken)
    {
        PaymentNotificationPayload? parsed = JsonSerializer.Deserialize<PaymentNotificationPayload>(
            notification.NormalizedJSON, PaymentJson.Options);

        if (parsed is null || !parsed.Paid)
        {
            throw new InvalidOperationException("支付通知不是成功状态");
        }

        await ApplyPaymentResultAsync(notification.ProviderID, new PaymentResult
        {
            MerchantOrderNo = parsed.MerchantOrderNo,
            ProviderTradeNo = parsed.ProviderTradeNo,
            ProviderStatus = parsed.ProviderStatus,
            AmountFen = parsed.AmountFen,
            Currency = parsed.Currency,
            Paid = parsed.Paid,
            Closed = parsed.Closed,
            PaidAt = parsed.PaidAt,
        }, cancellationToken).ConfigureAwait(false);

        await _repository.CompletePaymentNotificationAsync(notification.ID, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 管理端订单

    /// <summary>管理端订单分页（含用户信息）。对应 Go: <c>AdminPaymentOrderPage</c>。</summary>
    public async Task<AdminPaymentOrderPage> AdminPaymentOrderPageAsync(
        User? actor, string status, string keyword, int page, int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = AdminUserService.NormalizePage(page, limit);

        (IReadOnlyList<PaymentOrder> orders, long total) = await _repository.AdminPaymentOrdersAsync(
            status, keyword, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        // 批量取用户，避免 N+1。
        List<string> userIds = orders.Select(order => order.UserID).Distinct(StringComparer.Ordinal).ToList();
        Dictionary<string, User> users = await _repository.UsersByIDsAsync(userIds, cancellationToken)
            .ConfigureAwait(false);

        List<AdminPaymentOrderView> views = new(orders.Count);
        foreach (PaymentOrder order in orders)
        {
            PaymentOrderView baseView = PaymentOrderViewOf(order);
            users.TryGetValue(order.UserID, out User? user);

            views.Add(new AdminPaymentOrderView
            {
                ID = baseView.ID,
                UserID = baseView.UserID,
                MerchantOrderNo = baseView.MerchantOrderNo,
                ProductID = baseView.ProductID,
                ProductName = baseView.ProductName,
                ProviderID = baseView.ProviderID,
                AmountFen = baseView.AmountFen,
                Currency = baseView.Currency,
                CreditsMicrocredits = baseView.CreditsMicrocredits,
                Status = baseView.Status,
                ProviderStatus = baseView.ProviderStatus,
                ProviderTradeNo = baseView.ProviderTradeNo,
                Checkout = baseView.Checkout,
                ExpiresAt = baseView.ExpiresAt,
                ProviderPaidAt = baseView.ProviderPaidAt,
                CreditedAt = baseView.CreditedAt,
                ClosedAt = baseView.ClosedAt,
                CreatedAt = baseView.CreatedAt,
                UpdatedAt = baseView.UpdatedAt,
                User = user is null
                    ? null
                    : new AdminPaymentOrderUser
                    {
                        ID = user.ID,
                        Username = user.Username,
                        DisplayName = user.DisplayName,
                        Email = user.Email,
                    },
            });
        }

        return new AdminPaymentOrderPage
        {
            Orders = views,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>管理端主动查单。对应 Go: <c>AdminQueryPaymentOrder</c>。</summary>
    public async Task<PaymentOrderView> AdminQueryPaymentOrderAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        PaymentOrder order = await _repository.PaymentOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");

        if (order.Status is PaymentOrderStatus.PaymentOrderCredited or PaymentOrderStatus.PaymentOrderClosed)
        {
            return PaymentOrderViewOf(order);
        }

        await QueryPaymentOrderInternalAsync(order, cancellationToken).ConfigureAwait(false);

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    /// <summary>管理端关单。对应 Go: <c>AdminClosePaymentOrder</c>。</summary>
    public async Task<PaymentOrderView> AdminClosePaymentOrderAsync(
        User? actor, string id, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        PaymentOrder order = await _repository.PaymentOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");

        if (order.Status is PaymentOrderStatus.PaymentOrderCredited or PaymentOrderStatus.PaymentOrderClosed)
        {
            return PaymentOrderViewOf(order);
        }

        await ClosePaymentOrderInternalAsync(order, cancellationToken).ConfigureAwait(false);

        PaymentOrder reloaded = await _repository.PaymentOrderAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付订单不存在");
        return PaymentOrderViewOf(reloaded);
    }

    // ------------------------------------------------------------ 回调应答

    /// <summary>
    /// 渠道回调的成功应答。对应 Go: <c>PaymentNotificationResponse</c>。
    /// </summary>
    public (int Status, string ContentType, string Body) NotificationResponse(string providerId, bool success) =>
        NotificationResponseFor(providerId, success, failureStatus: 500);

    /// <summary>渠道回调的失败应答。对应 Go: <c>PaymentNotificationFailureResponse</c>。</summary>
    public (int Status, string ContentType, string Body) NotificationFailureResponse(string providerId, int status) =>
        NotificationResponseFor(providerId, success: false, failureStatus: status);

    /// <summary>
    /// 按渠道描述符决定应答内容。对应 Go: <c>PaymentNotificationResponseForWithRegistry</c>。
    /// </summary>
    /// <remarks>
    /// 渠道可以自定义成功/失败应答（支付宝要求回 <c>success</c> 文本，微信要求 204），
    /// 未注册的渠道回落到通用应答。
    /// </remarks>
    private (int Status, string ContentType, string Body) NotificationResponseFor(
        string providerId, bool success, int failureStatus)
    {
        IPaymentProvider? provider = _registry.Get(providerId);
        if (provider is not null)
        {
            PaymentProviderDescriptor descriptor = provider.Descriptor;
            ManifestPaymentResponse response = success
                ? new ManifestPaymentResponse
                {
                    Status = descriptor.NotificationSuccess.Status,
                    ContentType = descriptor.NotificationSuccess.ContentType,
                    Body = descriptor.NotificationSuccess.Body,
                }
                : new ManifestPaymentResponse
                {
                    Status = descriptor.NotificationFailure.Status,
                    ContentType = descriptor.NotificationFailure.ContentType,
                    Body = descriptor.NotificationFailure.Body,
                };

            int status = response.Status == 0 ? failureStatus : response.Status;
            string contentType = response.ContentType.Length == 0
                ? "text/plain; charset=utf-8"
                : response.ContentType;
            return (status, contentType, response.Body);
        }

        return success ? (204, "", "") : (failureStatus, "", "");
    }

    // ------------------------------------------------------------ 内部

    private async Task QueryPaymentOrderInternalAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        (IPaymentProvider provider, _, Dictionary<string, string> values) =
            await RuntimeForOrderAsync(order, cancellationToken).ConfigureAwait(false);

        PaymentResult result;
        try
        {
            result = await provider.QueryOrderAsync(
                values, new QueryRequest { MerchantOrderNo = order.MerchantOrderNo }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PaymentOrderNotFoundException)
        {
            await _repository.RecordPaymentQueryAsync(order.ID, "NOT_FOUND", cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception error)
        {
            throw AppError.Wrap(502, "支付渠道查单失败，请稍后重试", error);
        }

        if (result.MerchantOrderNo.Trim() != order.MerchantOrderNo)
        {
            throw new PaymentEvidenceMismatchException();
        }

        await _repository.RecordPaymentQueryAsync(order.ID, result.ProviderStatus, cancellationToken)
            .ConfigureAwait(false);
        await ApplyPaymentResultAsync(order.ProviderID, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClosePaymentOrderInternalAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        (IPaymentProvider provider, _, Dictionary<string, string> values) =
            await RuntimeForOrderAsync(order, cancellationToken).ConfigureAwait(false);

        bool queryNotFound = false;
        try
        {
            PaymentResult result = await provider.QueryOrderAsync(
                values, new QueryRequest { MerchantOrderNo = order.MerchantOrderNo }, cancellationToken)
                .ConfigureAwait(false);

            if (result.MerchantOrderNo.Trim() != order.MerchantOrderNo)
            {
                throw new PaymentEvidenceMismatchException();
            }

            await _repository.RecordPaymentQueryAsync(order.ID, result.ProviderStatus, cancellationToken)
                .ConfigureAwait(false);
            await ApplyPaymentResultAsync(order.ProviderID, result, cancellationToken).ConfigureAwait(false);

            if (result.Paid || result.Closed)
            {
                return;
            }
        }
        catch (PaymentOrderNotFoundException)
        {
            queryNotFound = true;
        }
        catch (PaymentProviderException error)
        {
            throw AppError.Wrap(502, "关单前查单失败，请稍后重试", error);
        }

        try
        {
            PaymentResult closed = await provider.CloseOrderAsync(
                values, new CloseRequest { MerchantOrderNo = order.MerchantOrderNo }, cancellationToken)
                .ConfigureAwait(false);

            string closedNo = string.IsNullOrEmpty(closed.MerchantOrderNo)
                ? order.MerchantOrderNo
                : closed.MerchantOrderNo;
            if (closedNo.Trim() != order.MerchantOrderNo)
            {
                throw new PaymentEvidenceMismatchException();
            }

            if (closed.Paid || closed.Closed)
            {
                await ApplyPaymentResultAsync(order.ProviderID, closed, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _repository.MarkPaymentOrderClosedAsync(order.ID, closed.ProviderStatus, cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (PaymentProviderException closeError)
        {
            // 支付可能在「首次查单」与「关单请求」之间完成；关单失败后必须再查一次，
            // 否则已成功的支付会卡在 closing 重试循环里。
            PaymentResult? recheck = null;
            try
            {
                recheck = await provider.QueryOrderAsync(
                    values, new QueryRequest { MerchantOrderNo = order.MerchantOrderNo }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PaymentProviderException)
            {
                // 二次查单失败：落到下面的统一错误。
            }

            if (recheck is not null)
            {
                if (recheck.MerchantOrderNo.Trim() != order.MerchantOrderNo)
                {
                    throw new PaymentEvidenceMismatchException();
                }

                await _repository.RecordPaymentQueryAsync(order.ID, recheck.ProviderStatus, cancellationToken)
                    .ConfigureAwait(false);
                await ApplyPaymentResultAsync(order.ProviderID, recheck, cancellationToken).ConfigureAwait(false);

                if (recheck.Paid || recheck.Closed)
                {
                    return;
                }
            }

            if (queryNotFound && closeError is PaymentOrderNotFoundException)
            {
                await _repository.MarkPaymentOrderClosedAsync(order.ID, "NOT_FOUND", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            throw AppError.Wrap(502, "支付渠道关单失败，请稍后重试", closeError);
        }
    }

    /// <summary>把渠道结果落到订单上（成功入账 / 关闭 / 仅记录）。对应 Go: <c>applyPaymentResult</c>。</summary>
    private async Task<(PaymentOrder Order, bool Granted)> ApplyPaymentResultAsync(
        string providerId, PaymentResult result, CancellationToken cancellationToken)
    {
        PaymentOrder order = await _repository
            .PaymentOrderByMerchantAsync(providerId, result.MerchantOrderNo, cancellationToken)
            .ConfigureAwait(false) ?? throw AppError.NotFound("支付订单不存在");

        if (result.Paid)
        {
            if (result.AmountFen != order.AmountFen || result.Currency != order.Currency)
            {
                throw new PaymentEvidenceMismatchException();
            }

            return await _repository.CompletePaymentOrderAsync(providerId, result.MerchantOrderNo, new PaymentEvidence
            {
                ProviderTradeNo = result.ProviderTradeNo,
                ProviderStatus = result.ProviderStatus,
                AmountFen = result.AmountFen,
                Currency = result.Currency,
                PaidAt = result.PaidAt,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (result.Closed)
        {
            await _repository.MarkPaymentOrderClosedAsync(order.ID, result.ProviderStatus, cancellationToken)
                .ConfigureAwait(false);
            PaymentOrder reloaded = await _repository.PaymentOrderAsync(order.ID, cancellationToken)
                .ConfigureAwait(false) ?? order;
            return (reloaded, false);
        }

        return (order, false);
    }

    /// <summary>按订单解析适配器与配置。对应 Go: <c>paymentRuntimeForOrder</c>。</summary>
    private async Task<(IPaymentProvider Provider, PaymentProviderConfig Config, Dictionary<string, string> Values)>
        RuntimeForOrderAsync(PaymentOrder order, CancellationToken cancellationToken)
    {
        IPaymentProvider provider = _registry.Get(order.ProviderID)
            ?? throw AppError.NotFound("支付宿主适配器不存在");

        PaymentProviderConfig config = await _repository
            .PaymentProviderConfigAsync(order.ProviderConfigID, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("支付渠道配置不存在");

        // 配置版本必须与下单时快照一致，否则订单会用错密钥。
        if (config.ProviderID != order.ProviderID
            || config.PluginID != order.PluginID
            || (config.PluginVersion.Length > 0 && order.PluginVersion.Length > 0
                && config.PluginVersion != order.PluginVersion)
            || config.Version != order.ProviderConfigVersion)
        {
            throw new PaymentStateConflictException();
        }

        Dictionary<string, string> values = await DecryptConfigAsync(config, cancellationToken).ConfigureAwait(false);
        return (provider, config, values);
    }

    /// <summary>订单视图投影。对应 Go: <c>paymentOrderView</c>。</summary>
    private static PaymentOrderView PaymentOrderViewOf(PaymentOrder order)
    {
        PaymentCheckoutView checkout = new()
        {
            Mode = order.CheckoutMode,
            ExpiresAt = order.CheckoutExpiresAt,
        };

        // 只有「未支付且未过期」才回传可跳转的收银台信息。
        if (order.Status == PaymentOrderStatus.PaymentOrderPending && order.ExpiresAt > DateTime.UtcNow)
        {
            if (order.CheckoutMode == "qr_code")
            {
                checkout = new PaymentCheckoutView
                {
                    Mode = order.CheckoutMode,
                    Value = order.CheckoutValue,
                    ExpiresAt = order.CheckoutExpiresAt,
                };
            }
            else if (order.CheckoutMode == "redirect")
            {
                checkout = new PaymentCheckoutView
                {
                    Mode = order.CheckoutMode,
                    URL = "/api/payments/orders/" + Uri.EscapeDataString(order.ID) + "/checkout",
                    ExpiresAt = order.CheckoutExpiresAt,
                };
            }
        }

        return new PaymentOrderView
        {
            ID = order.ID,
            UserID = order.UserID,
            MerchantOrderNo = order.MerchantOrderNo,
            ProductID = order.ProductID,
            ProductName = order.ProductName,
            ProviderID = order.ProviderID,
            AmountFen = order.AmountFen,
            Currency = order.Currency,
            CreditsMicrocredits = order.CreditsMicrocredits,
            Status = order.Status,
            ProviderStatus = order.ProviderStatus,
            ProviderTradeNo = order.ProviderTradeNo ?? "",
            Checkout = checkout,
            ExpiresAt = order.ExpiresAt,
            ProviderPaidAt = order.ProviderPaidAt,
            CreditedAt = order.CreditedAt,
            ClosedAt = order.ClosedAt,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
        };
    }

    /// <summary>
    /// 对外暴露的错误文案。对应 Go: <c>safePaymentError</c>。
    /// 渠道异常可能带密钥等敏感信息，只保留稳定前缀。
    /// </summary>
    internal static string SafePaymentError(Exception error) => error switch
    {
        PaymentProviderException provider => KernelUtil.TruncateRunes(provider.Message, 1000),
        AppError app => KernelUtil.TruncateRunes(app.Message, 1000),
        _ => KernelUtil.TruncateRunes(error.Message, 1000),
    };

    /// <summary>
    /// 渠道配置加密。对应 Go: <c>encryptSettingSecret</c>。
    /// 当前为占位实现（见 PENDING-CONFIRMATIONS 第 1 条）。
    /// </summary>
    private static string EncryptSecret(string value) => value;

    /// <summary>渠道配置解密。占位实现，与 <see cref="EncryptSecret"/> 对应。</summary>
    private static string DecryptSecret(string value) => value;

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task AppendAuditAsync(
        User actor, string action, string targetType, string targetId, string summary,
        object metadata, CancellationToken cancellationToken)
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
