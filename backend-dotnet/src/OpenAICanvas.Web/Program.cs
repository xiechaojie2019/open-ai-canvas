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

// 业务组合根与平台能力。后续模块继续往 CanvasService 上挂。
builder.Services.AddSingleton(new OpenAICanvas.Persistence.Repositories.Repository(database));
// 认证宿主桥接：注册奖励、活跃记录、品牌名都经它回到业务层（对应 Go 的 authHost）。
builder.Services.AddSingleton<OpenAICanvas.Application.CanvasAuthHost>();
builder.Services.AddSingleton<OpenAICanvas.Application.CanvasService>(serviceProvider =>
    new OpenAICanvas.Application.CanvasService(
        serviceProvider.GetRequiredService<OpenAICanvas.Persistence.Repositories.Repository>(),
        runtimePolicy: serviceProvider.GetRequiredService<OpenAICanvas.Platform.IRuntimePolicyProvider>(),
        authHost: serviceProvider.GetRequiredService<OpenAICanvas.Application.CanvasAuthHost>(),
        dataDir: env.DataDir));
builder.Services.AddSingleton<OpenAICanvas.Web.Security.IRateLimiter, OpenAICanvas.Web.Security.InMemoryRateLimiter>();
builder.Services.AddSingleton<OpenAICanvas.Platform.IRuntimePolicyProvider,
    OpenAICanvas.Platform.DefaultRuntimePolicyProvider>();

WebApplication app = builder.Build();

// 启动种子：系统渠道缺渠道模型占位时按 ModelsJSON 补齐（Go main.go 的 EnsureSystemChannelModels）。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .EnsureSystemChannelModelsAsync().ConfigureAwait(false);

// 启动种子：为缺少模板的操作种入默认版本（Go main.go 的 EnsureDefaultPromptTemplates）。
// 幂等；失败即中断启动——否则后续生成会因为找不到模板而报错。
await app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>()
    .PromptTemplates.EnsureDefaultPromptTemplatesAsync().ConfigureAwait(false);

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

// 提示词模板管理路由。对应 Go 的 handler.RegisterAuthRoutes 的模板部分。
api.MapPromptTemplateRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

// 技能库读取与状态路由。对应 Go 的 handler.RegisterSkillRoutes。
api.MapSkillsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

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
    app.Services.GetRequiredService<OpenAICanvas.Web.Security.IRateLimiter>());

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

// 管理后台日志与存储路由。对应 Go 的 handler 日志/存储部分。
api.MapAdminAnalyticsRoutes(app.Services.GetRequiredService<OpenAICanvas.Application.CanvasService>());

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
