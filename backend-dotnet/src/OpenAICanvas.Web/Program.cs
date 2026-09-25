using System.Net;
using System.Reflection;
using OpenAICanvas.Persistence;
using OpenAICanvas.Persistence.Schema;
using OpenAICanvas.Web.Diagnostics;
using OpenAICanvas.Platform;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Auth;
using OpenAICanvas.Web.Endpoints;
using OpenAICanvas.Web.Security;
using OpenAICanvas.Web.Http;
using OpenAICanvas.Web.Middleware;
using OpenAICanvas.Web.Startup;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 启动参数不得绕过数据目录约束；这里与 Go 的 env() 完全同名同默认值。
CanvasEnvironment env = CanvasEnvironment.FromConfiguration(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:sszzz ";
});

// 监听地址来自 CANVAS_BACKEND_ADDR（Go 默认 ":8080"，即所有网卡）。
builder.WebHost.UseUrls(env.ListenUrl);

// Kestrel：对应 Go 的 ReadHeaderTimeout: 10s。
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.AddServerHeader = false;
});

// CORS 策略非法时直接让进程启动失败，与 Go 的 cors() 返回 error 一致。
CorsPolicy corsPolicy = CorsPolicy.Parse(env.CorsOrigins);

// 数据库：Dapper + 静态建表脚本。迁移与版本校验的语义与 Go 完全一致。
CanvasDatabase database = new(new CanvasDatabaseOptions
{
    Driver = env.DatabaseDriver,
    Dsn = env.DatabaseUrl,
    DataDir = env.DataDir,
});
SchemaMigrator migrator = new(database);

if (env.AutoMigrate)
{
    await migrator.MigrateAsync();
}
else
{
    // 受管生产部署由独立 migrate 服务完成迁移，这里只校验兼容性。
    await migrator.RequireSchemaVersionAsync();
}

SystemStatus status = new(new SystemStatusProbes
{
    DatabaseCheck = async cancellationToken =>
    {
        await using System.Data.Common.DbConnection connection =
            await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return true;
    },
    ReadSchema = async cancellationToken =>
    {
        SchemaStatus schema = await migrator.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        return new SchemaStatusDto
        {
            Current = schema.Current,
            Expected = schema.Expected,
            Ready = schema.Ready,
        };
    },
    RuntimeCheck = static () => true,
});

builder.Services.AddSingleton(corsPolicy);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(status);

// 多实例协调器（Redis 可选；PostgreSQL 强制要求 REDIS_URL）。对应 Go 的 coordinator 装配。
(OpenAICanvas.Platform.Coordinator platformCoordinator, string? coordinatorError) =
    OpenAICanvas.Platform.Coordinator.Create(env.DatabaseDriver);
if (coordinatorError is not null)
{
    Console.Error.WriteLine($"协调器初始化警告：{coordinatorError}");
}
builder.Services.AddSingleton(platformCoordinator);

// 业务组合根与平台能力。后续模块继续往 CanvasService 上挂。
builder.Services.AddSingleton(new OpenAICanvas.Persistence.Repositories.Repository(database));
// 认证宿主桥接：注册奖励、活跃记录、品牌名都经它回到业务层（对应 Go 的 authHost）。
builder.Services.AddSingleton<OpenAICanvas.Application.CanvasAuthHost>();
builder.Services.AddSingleton<OpenAICanvas.Application.CanvasService>(serviceProvider =>
    new OpenAICanvas.Application.CanvasService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        runtimePolicy: serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
        authHost: serviceProvider.GetRequiredService<OpenAICanvas.Application.CanvasAuthHost>(),
        mailSender: new SmtpMailSender(),
        dataDir: env.DataDir));
builder.Services.AddSingleton<OpenAICanvas.Web.Security.IRateLimiter, OpenAICanvas.Web.Security.InMemoryRateLimiter>();
builder.Services.AddSingleton<OpenAICanvas.Platform.IRuntimePolicyProvider,
    OpenAICanvas.Platform.DefaultRuntimePolicyProvider>();
// 资源上传：额度管理 + 写路径 + 分片会话（对应 Go 的 upload_quota.go / resource.go / resource_upload_session.go）。
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.UploadQuota(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>()));
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.ResourceUploadService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.UploadQuota>(),
        env.DataDir, playback: serviceProvider.GetRequiredService<OpenAICanvas.Application.VideoPlaybackService>()));
builder.Services.AddSingleton(serviceProvider => new OpenAICanvas.Application.VideoPlaybackService(
    serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(), env.DataDir,
    serviceProvider.GetService<ILogger<OpenAICanvas.Application.VideoPlaybackService>>()));
builder.Services.AddSingleton(new OpenAICanvas.Application.ChunkedUploadSessions());
// 孤儿资源清理后台作业。对应 Go 的 app/resource_deletion_worker.go。
// 可用 CANVAS_DISABLE_BACKGROUND_WORKERS=true 关闭（测试环境避免与手动触发竞争）。
if (!string.Equals(Environment.GetEnvironmentVariable("CANVAS_DISABLE_BACKGROUND_WORKERS"), "true",
        StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHostedService<OpenAICanvas.Web.Workers.ResourceCleanupWorker>();
    builder.Services.AddHostedService<OpenAICanvas.Web.Workers.VideoPlaybackWorker>();
    builder.Services.AddHostedService<OpenAICanvas.Web.Workers.TaskDispatchWorker>();
}
// 账单巡检（只读）。对应 Go 的 app/billing_review.go startBillingReviewAudit。
builder.Services.AddSingleton<OpenAICanvas.Application.TaskBillingReviewService>();
builder.Services.AddHostedService<OpenAICanvas.Web.Workers.BillingReviewWorker>();
// 任务 Worker：领取、租约维护与终态协调（对应 Go task_worker.go）。
builder.Services.AddSingleton(serviceProvider => new OpenAICanvas.Application.TaskWorkerService(
    serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
    serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
    platformCoordinator));
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.ResourceDomainService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
        env.DataDir, playback: serviceProvider.GetRequiredService<OpenAICanvas.Application.VideoPlaybackService>()));
// 云 Agent 偏好档案（阶段 11.9 首批）。对应 Go 的 app/cloud_agent_profile.go。
builder.Services.AddSingleton<OpenAICanvas.Application.AgentProfileService>();
builder.Services.AddSingleton(serviceProvider => new OpenAICanvas.Application.PlatformSettingsService(
    serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
    env.DataDir));
// 云 Agent 会话 / 媒体 / 运行时。对应 Go 的 app/cloud_agent*.go 组合根。
builder.Services.AddSingleton(sp =>
{
    OpenAICanvas.Application.CanvasService canvas = sp.GetRequiredService<OpenAICanvas.Application.CanvasService>();
    return new OpenAICanvas.Application.CloudAgent.CloudAgentSessionService(
        canvas.Repository,
        canvas.TaskCreations,
        canvas.Skills,
        sp.GetRequiredService<OpenAICanvas.Application.AgentProfileService>(),
        sp.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());
});
builder.Services.AddSingleton(sp =>
{
    OpenAICanvas.Application.CanvasService canvas = sp.GetRequiredService<OpenAICanvas.Application.CanvasService>();
    return new OpenAICanvas.Application.CloudAgent.CloudAgentMediaService(
        canvas.Repository, canvas.ModelCatalog);
});
builder.Services.AddSingleton(sp =>
{
    OpenAICanvas.Application.CanvasService canvas = sp.GetRequiredService<OpenAICanvas.Application.CanvasService>();
    return new OpenAICanvas.Application.CloudAgent.CloudAgentRuntimeService(
        canvas.Repository,
        canvas.TaskCreations,
        sp.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
        sp.GetRequiredService<OpenAICanvas.Application.CloudAgent.CloudAgentMediaService>(),
        sp.GetRequiredService<OpenAICanvas.Application.CloudAgent.CloudAgentSessionService>(),
        canvas.Skills,
        canvas.TaskLifecycle);
});
// 云 Agent 调度器。对应 Go 的 worker 循环内 advanceCloudAgents。
builder.Services.AddHostedService<OpenAICanvas.Web.Workers.CloudAgentSchedulerWorker>();
// 外观配置（品牌标识 / 皮肤主题 / 登录页素材）。对应 Go 的 app/appearance*.go。
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.Appearance.AppearanceService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.ResourceUploadService>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.ResourceDomainService>(),
        env.DataDir));
// 资源删除与物理清理（Outbox + drain）。对应 Go 的 app/resource_delete.go。
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.ResourceDeleteService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        env.DataDir));
// 孤儿资源清理。对应 Go 的 app/resource_deletion_worker.go。
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.ResourceCleanupService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.ResourceDeleteService>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.Appearance.AppearanceService>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.CanvasService>().Announcements,
        serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>()));
// 管理端存储管理（资源分页 / 批量删除 / 直连下发）。对应 Go 的 app/admin_storage*.go。
builder.Services.AddSingleton(serviceProvider =>
    new OpenAICanvas.Application.AdminStorageService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.ResourceDomainService>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.ResourceDeleteService>(),
        serviceProvider.GetRequiredService<OpenAICanvas.Application.Appearance.AppearanceService>()));
// 对象存储平台/个人设置及 S3 兼容连接测试。对应 Go 的 settings/storage_s3。
builder.Services.AddSingleton(serviceProvider => new OpenAICanvas.Application.StorageSettingsService(
    serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(), env.DataDir));

WebApplication app = builder.Build();

// 启动种子：系统渠道缺渠道模型占位时按 ModelsJSON 补齐（Go main.go 的 EnsureSystemChannelModels）。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .EnsureSystemChannelModelsAsync().ConfigureAwait(false);

// 启动种子：为缺少模板的操作种入默认版本（Go main.go 的 EnsureDefaultPromptTemplates）。
// 幂等；失败即中断启动——否则后续生成会因为找不到模板而报错。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .PromptTemplates.EnsureDefaultPromptTemplatesAsync().ConfigureAwait(false);

// 插件运行时引导（10.1/10.2）：扫描官方包目录、合并 bundled 清单并加载注册表。
// 与 Go 一致在监听前完成；坏包跳过不阻断启动。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .Plugins.BootstrapAsync().ConfigureAwait(false);

CanvasLog.Configure(app.Services.GetRequiredService<ILoggerFactory>());

// 与 Go cmd/server/main.go 相同：在开始监听前，为已有系统渠道补齐 ModelsJSON 对应的
// 禁用模型占位记录。这样旧库升级后仍可由管理员继续配置价格和能力。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .EnsureSystemChannelModelsAsync();

// 中间件顺序必须与 Go 的 r.Use(...) 顺序一致。
app.UseMiddleware<RequestCorrelationMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CanvasCorsMiddleware>();
app.UseMiddleware<AccessLogMiddleware>();

RouteGroupBuilder api = app.MapGroup("/api");

api.MapSystemStatusRoutes(status);
OpenApiEndpoints.MapOpenApiRoutes(api);

// 认证路由（阶段 2 子集）。对应 Go 的 handler.RegisterAuthRoutes 中被实现的部分。
api.MapAuthRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 功能开放配置路由。对应 Go 的 handler.RegisterFeatureAvailabilityRoutes。
api.MapFeatureRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 管理后台用户管理路由。对应 Go 的 handler.RegisterAdminRoutes 的用户部分。
api.MapAdminUserRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 任务读取与文本回放路由。对应 Go 的 handler.RegisterTaskRoutes 的持久化部分。
api.MapTaskRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
// 任务创建 / SSE / 时间线转写。对应 Go 的 handler/routes.go 任务写路径。
api.MapTaskCreationRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 提示词模板管理路由。对应 Go 的 handler.RegisterAuthRoutes 的模板部分。
api.MapPromptTemplateRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
// 用户提示词偏好路由。对应 Go 的 handler/user_data.go settings/prompt-templates 部分。
api.MapUserPromptPreferenceRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
// 创作运行路由。对应 Go 的 handler/creation.go。
api.MapCreationRunRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 技能库读取与状态路由。对应 Go 的 handler.RegisterSkillRoutes。
api.MapSkillsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapSkillPackageRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapSkillWriteRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapAdminLogMediaRoute(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceDomainService>());
api.MapDiagnosticsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapSystemPerformanceRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    (OpenAICanvas.Web.Security.InMemoryRateLimiter)app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>());

// 支付路由（渠道、商品、订单、回调）。对应 Go 的 handler.RegisterPaymentRoutes。
api.MapPaymentRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>());

// 钱包、兑换码与账单路由。对应 Go 的 handler.RegisterFinanceRoutes 的非支付部分。
api.MapFinanceRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 创作端前台模型目录路由（公开读取部分）。对应 Go 的 handler.RegisterLogicalModelRoutes。
api.MapModelRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 系统渠道管理路由。对应 Go 的 handler.RegisterAuthRoutes / RegisterFinanceRoutes 渠道部分。
api.MapChannelRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.TaskWorkerService>());

// 本地媒体分片上传路由。对应 Go 的 handler.RegisterChunkedUploadRoutes。
api.MapChunkedUploadRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceUploadService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.UploadQuota>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ChunkedUploadSessions>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 资源列表/详情/整传/导入/存储用量/OSS 直链/ARK 同步路由。对应 Go 的 handler resources 部分。
api.MapResourceCrudRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceUploadService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceDomainService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 外观配置路由。对应 Go 的 handler.RegisterAppearanceRoutes。
api.MapAppearanceRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.Appearance.AppearanceService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 管理端存储管理路由。对应 Go 的 handler.RegisterAdminStorageRoutes。
api.MapAdminStorageRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.AdminStorageService>());

// 存储服务配置（管理端及个人 S3 设置）。
api.MapStorageSettingsRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.StorageSettingsService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>());

// 资源文件下发路由（鉴权 + 匿名签名）。对应 Go 的 GET /resources/:id/file 等。
api.MapResourceDeliveryRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceDomainService>());

// 用户数据（画布/素材）路由。对应 Go 的 handler/user_data.go（当前接 canvas-projects 部分）。
api.MapUserDataRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 风格档案与声音档案路由。对应 Go 的 handler.RegisterStyleProfileRoutes 与 voice-profiles。
api.MapStyleProfileRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 项目素材文件夹路由。对应 Go 的 handler.RegisterProjectRoutes asset-folders 部分。
api.MapProjectAssetFolderRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 项目单元与画布链接路由。对应 Go 的 handler.RegisterProjectRoutes 单元/链接部分。
api.MapProjectUnitRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 云 Agent 路由。对应 Go 的 handler.RegisterAgentRoutes。
api.MapAgentRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.AgentProfileService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.CloudAgent.CloudAgentSessionService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.CloudAgent.CloudAgentRuntimeService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 用户自定义渠道中转。对应 Go 的 RegisterCustomRelayRoutes 与 /ai/models。
api.MapCustomRelayRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.PlatformSettingsService>(),
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>().Features,
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 系统更新状态（Docker 部署：固定状态，用户决策 2026-09-25）。对应 Go 的 RegisterAdminUpdateRoutes。
api.MapSystemUpdateRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 项目素材关联与角色路由。对应 Go 的 handler.RegisterProjectRoutes assets/characters 部分。
api.MapProjectAssetLinkRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapProjectCharacterRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
api.MapProjectShotRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 管理后台日志与存储路由。对应 Go 的 handler 日志/存储部分。
api.MapAdminAnalyticsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 管理端分析总览与模型价格路由。对应 Go 的 handler/admin_analytics.go。
api.MapAdminInsightRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 平台设置：运行时策略 / 绘图工具 / 响应拦截 / 方舟素材库 / 公告配图上传。
api.MapAdminPlatformSettingsRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    new OpenAICanvas.Application.PlatformSettingsService(
        app.Services.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        env.DataDir),
    app.Services.GetRequiredService<OpenAICanvas.Application.ResourceUploadService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>());

// 公告路由。对应 Go 的 handler.RegisterAnnouncementRoutes。
api.MapAnnouncementRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    env.DataDir);

// 平台设置管理路由。对应 Go 的 handler 设置部分（registration/email/linuxdo/credits）。
api.MapAdminSettingsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 画布分享路由。对应 Go 的 handler.RegisterCanvasShareRoutes。
api.MapCanvasShareRoutes(
    app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>(),
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>(),
    env.DataDir);

// 插件协议目录路由。对应 Go 的 handler/plugin.go GET /plugins/catalog（插件中心 10.1 另行移植）。
    api.MapPluginRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());
    api.MapRunningHubRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 根级 OAuth 回调兼容路由（传统登记地址）。对应 Go: <c>RegisterOAuthCallbackRoutes</c>。
app.MapGet("/oauth/linuxdo/callback", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var service = app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>();
    var limiter = app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>();
    var policyProvider = app.Services.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>();

    RuntimeRequestPolicy policy = policyProvider.Current().Request;
    if (!await AuthEndpoints.EnforceRateLimitAsync(
            context, limiter, "linuxdo-callback:" + AuthEndpoints.ClientIp(context),
            30, TimeSpan.FromMinutes(10)).ConfigureAwait(false))
    {
        return Results.Empty;
    }

    try
    {
        AuthService.LinuxDOCallbackResult result = await service.Auth
            .CompleteLinuxDOLoginAsync(
                context.Request.Query["state"].ToString(),
                context.Request.Query["code"].ToString(),
                cancellationToken).ConfigureAwait(false);

        SessionCookie.Set(context, result.Session.Session, result.Session.MaxAgeSecs);
        return Results.Redirect(result.Next);
    }
    catch (Exception error)
    {
        string message = error is AppError ae ? ae.Message : "Linux.do 登录失败";
        return Results.Redirect("/login?oauth_error=" + Uri.EscapeDataString(message));
    }
});

// 其余业务路由在后续阶段注册：
// handler.RegisterCanvasAPI(api, svc) 的等价物 —— 阶段 2 起逐模块追加。

// 对应 Go 的 r.NoRoute(handler.SystemProxyNoRouteHandler(svc))。
app.MapFallback((HttpContext context) => ApiResults.Fail(StatusCodes.Status404NotFound, null));

status.MarkStarted();
app.Logger.LogInformation("backend listening on {Address}", env.Address);

await app.RunAsync();

/// <summary>访问日志，格式对齐 Go 的 gin.LoggerWithFormatter。</summary>
internal sealed class AccessLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AccessLogMiddleware> _logger;

    public AccessLogMiddleware(RequestDelegate next, ILogger<AccessLogMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        long startedAt = Environment.TickCount64;
        try
        {
            await _next(context);
        }
        finally
        {
            long elapsedMs = Environment.TickCount64 - startedAt;
            string path = RedactCanvasSharePath(context.Request.Path.Value ?? "/");
            _logger.LogInformation(
                "{ClientIP} - [{Timestamp}] \"{Method} {Path}\" {Status} {Elapsed}ms",
                context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                context.Request.Method,
                path,
                context.Response.StatusCode,
                elapsedMs);
        }
    }

    /// <summary>对应 Go: <c>redactCanvasSharePath</c>。分享 token 不写入日志。</summary>
    private static string RedactCanvasSharePath(string path)
    {
        const string prefix = "/api/public/canvas-shares/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return path;
        }

        string remainder = path[prefix.Length..];
        int index = remainder.IndexOf('/');
        return index >= 0 ? prefix + ":token" + remainder[index..] : prefix + ":token";
    }
}

/// <summary>OpenAPI 规范静态输出，对应 Go: <c>handler/openapi_embed.go</c>。</summary>
internal static class OpenApiEndpoints
{
    private const string ResourceName = "openapi.yaml";

    public static void MapOpenApiRoutes(IEndpointRouteBuilder api)
    {
        api.MapGet("/openapi.yaml", () =>
        {
            Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            using StreamReader reader = new(stream);
            return Results.Text(reader.ReadToEnd(), "application/yaml; charset=utf-8", System.Text.Encoding.UTF8);
        });
    }
}

/// <summary>供集成测试通过 WebApplicationFactory 启动本应用。</summary>
public partial class Program
{
}
