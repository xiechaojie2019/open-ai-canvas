using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Application.Capabilities;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>会话恢复响应 data。对应 Go: <c>handler.auth.go:/auth/session</c> 的 <c>gin.H</c>。</summary>
/// <remarks>
/// Go 的 gin.H 是 map，encoding/json 按字段名字典序输出；
/// 因此属性声明顺序必须保持 drawingEngine → features → logicalModels → runtimeLimits → user。
/// </remarks>
public sealed class SessionDto
{
    [JsonPropertyName("drawingEngine")]
    public required PublicDrawingEngineSetting DrawingEngine { get; init; }

    [JsonPropertyName("features")]
    public required PublicFeatureAvailability Features { get; init; }

    [JsonPropertyName("logicalModels")]
    public required IReadOnlyList<PublicLogicalModelDto> LogicalModels { get; init; }

    [JsonPropertyName("runtimeLimits")]
    public required PublicRuntimeLimits RuntimeLimits { get; init; }

    [JsonPropertyName("user")]
    public AuthUserDto? User { get; init; }
}

/// <summary>
/// 业务组合根。对应 Go 的 <c>internal/app.Service</c>（经 <c>internal/service</c> 再导出）。
/// </summary>
/// <remarks>
/// HTTP 层只依赖本类，不直接依赖各域包。已接入：认证、功能开放配置、绘图工具设置。
/// </remarks>
public sealed class CanvasService
{
    public CanvasService(
        Repository repository,
        IRuntimePolicyProvider? runtimePolicy = null,
        Auth.IAuthHost? authHost = null,
        Auth.IMailSender? mailSender = null,
        string? dataDir = null,
        Payment.PaymentRegistry? paymentRegistry = null,
        IPluginAvailability? pluginAvailability = null)
    {
        Repository = repository;
        RuntimePolicy = runtimePolicy ?? new DefaultRuntimePolicyProvider();
        Auth = new Auth.AuthService(repository, authHost, mailSender);
        // 打破 AuthService ←→ CanvasService 的构造循环：构造完成后补上引用。
        (authHost as CanvasAuthHost)?.Attach(this);
        Features = new FeatureAvailabilityService(repository);
        DrawingEngine = new DrawingEngineService(repository);
        CreditPolicy = new CreditPolicyService(repository);
        Channels = new ChannelService(repository);
        LogicalModels = new LogicalModelService(repository);
        ChannelAdmin = new ChannelAdminService(repository, LogicalModels);
        ChannelModels = new ChannelModelAdminService(repository, LogicalModels);
        UserData = new UserDataService(repository, RuntimePolicy);
        ResourceDelete = new ResourceDeleteService(repository, dataDir);
        CanvasShares = new CanvasShareService(repository);
        ProjectUnits = new ProjectUnitService(repository);
        ProjectAssetFolders = new ProjectAssetFolderService(repository);
        ProjectAssets = new ProjectAssetService(repository);
        // 角色域同时充当素材摘要的角色卡提供者（对应 Go 的 app.Service 内聚角色卡）。
        ProjectCharacters = new ProjectCharacterService(repository, ProjectAssets);
        ProjectAssets.AttachCharacterCardProvider(ProjectCharacters);
        ProjectShots = new ProjectShotService(repository, ProjectCharacters, ProjectAssets);
        ProjectWorkbench = new ProjectWorkbenchService(repository);
        StyleProfiles = new StyleProfileService(repository);
        Announcements = new AnnouncementService(repository);
        AdminAnalytics = new AdminAnalyticsService(repository);
        AdminUsers = new AdminUserService(repository, Auth, CreditPolicy, RuntimePolicy);
        Tasks = new TaskService(repository);
        ProjectWorkflows = new ProjectWorkflowService(repository, ProjectAssets, ProjectAssetFolders, Tasks, dataDir);
        Projects = new ProjectService(repository, ProjectWorkflows);
        Finance = new FinanceService(repository, CreditPolicy, Features);
        Skills = new SkillsService(repository, dataDir);
        PromptTemplates = new Prompts.PromptTemplateService(repository);
        ModelCatalog = new ModelCatalogService(repository, Features, LogicalModels);
        TaskCreations = new TaskCreationService(repository, RuntimePolicy, dataDir, Features);
        WorkflowPlugins = new WorkflowPluginGate(repository);
        // 插件运行时（10.1/10.2）：注册表 + 管理服务。Bootstrap 在启动处异步执行。
        Plugins = new PluginRuntime(repository, dataDir);
        PluginManagement = new PluginManagementService(repository, Plugins, Features);
        RunningHub = new RunningHubManagementService();
        TaskLifecycle = new TaskLifecycleService(repository, TaskCreations, RuntimePolicy).WithFeatures(Features);
        AdminLogMedia = new AdminLogMediaService(repository,
            new ResourceDomainService(repository, RuntimePolicy, dataDir));
        CreationRuns = new CreationRunService(repository, TaskCreations, RuntimePolicy);
        CreationRuns.UserData = UserData;
        Diagnostics = new DiagnosticsService(repository, dataDir);
        SystemPerformance = new SystemPerformanceService(repository, dataDir);
        // 支付适配器注册表默认是空的（内置适配器尚未移植）；测试可注入替身。
        Payments = new PaymentService(
            repository,
            paymentRegistry ?? new Payment.PaymentRegistry(),
            pluginAvailability ?? new ManifestPluginAvailability(),
            Features);
    }

    public Repository Repository { get; }

    public IRuntimePolicyProvider RuntimePolicy { get; }

    public Auth.AuthService Auth { get; }

    public FeatureAvailabilityService Features { get; }

    public DrawingEngineService DrawingEngine { get; }

    public CreditPolicyService CreditPolicy { get; }

    public ChannelService Channels { get; }

    public LogicalModelService LogicalModels { get; }

    public ChannelAdminService ChannelAdmin { get; }

    public ChannelModelAdminService ChannelModels { get; }

    public UserDataService UserData { get; }

    public ResourceDeleteService ResourceDelete { get; }

    public CanvasShareService CanvasShares { get; }

    public ProjectService Projects { get; }

    public ProjectUnitService ProjectUnits { get; }

    public ProjectAssetFolderService ProjectAssetFolders { get; }

    public ProjectAssetService ProjectAssets { get; }

    /// <summary>项目角色与配音。对应 Go: <c>app/project_character.go</c> 的角色域。</summary>
    public ProjectCharacterService ProjectCharacters { get; }

    /// <summary>分镜与资产候选。对应 Go: <c>app/project_shot.go</c> 与候选确认。</summary>
    public ProjectShotService ProjectShots { get; }

    /// <summary>工作台读视图。对应 Go: <c>app/project_workbench_read.go</c>。</summary>
    public ProjectWorkbenchService ProjectWorkbench { get; }

    public StyleProfileService StyleProfiles { get; }

    public AnnouncementService Announcements { get; }

    public AdminAnalyticsService AdminAnalytics { get; }

    /// <summary>对应 Go: <c>Service.CanvasShareStatus</c>。</summary>
    public Task<CanvasShareStatusDto> CanvasShareStatusAsync(
        string userId, string projectId, CancellationToken cancellationToken = default) =>
        CanvasShares.StatusAsync(userId, projectId, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateCanvasShare</c>。</summary>
    public Task<CanvasShareStatusDto> CreateCanvasShareAsync(
        string userId, string projectId, int expiresDays, bool rotate,
        CancellationToken cancellationToken = default) =>
        CanvasShares.CreateAsync(userId, projectId, expiresDays, rotate, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteCanvasShare</c>。</summary>
    public Task DeleteCanvasShareAsync(
        string userId, string projectId, CancellationToken cancellationToken = default) =>
        CanvasShares.DeleteAsync(userId, projectId, cancellationToken);

    /// <summary>对应 Go: <c>Service.PublicCanvasShare</c>。</summary>
    public Task<PublicCanvasShareDto> PublicCanvasShareAsync(
        string token, CancellationToken cancellationToken = default) =>
        CanvasShares.PublicAsync(token, cancellationToken);

    /// <summary>对应 Go: <c>Service.PrepareSharedCanvasResourceDelivery</c>。</summary>
    public Task<(Domain.Entities.Resource Resource, string LocalPath)> PrepareSharedCanvasResourceDeliveryAsync(
        string token, string resourceId, string dataDir, CancellationToken cancellationToken = default) =>
        CanvasShares.PrepareSharedResourceDeliveryAsync(token, resourceId, dataDir, cancellationToken);

    /// <summary>对应 Go: <c>Service.ListStyleProfiles</c>。</summary>
    public Task<IReadOnlyList<Domain.Entities.StyleProfile>> ListStyleProfilesAsync(
        string userId, CancellationToken cancellationToken = default) =>
        StyleProfiles.ListStyleProfilesAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateStyleProfile</c>。</summary>
    public Task<Domain.Entities.StyleProfile> CreateStyleProfileAsync(
        string userId, StyleProfileRequest request, CancellationToken cancellationToken = default) =>
        StyleProfiles.CreateStyleProfileAsync(userId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateStyleProfile</c>。</summary>
    public Task<Domain.Entities.StyleProfile> UpdateStyleProfileAsync(
        string userId, string id, StyleProfileRequest request, CancellationToken cancellationToken = default) =>
        StyleProfiles.UpdateStyleProfileAsync(userId, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.SetStyleProfileFavorite</c>。</summary>
    public Task SetStyleProfileFavoriteAsync(
        string userId, string id, bool favorite, CancellationToken cancellationToken = default) =>
        StyleProfiles.SetStyleProfileFavoriteAsync(userId, id, favorite, cancellationToken);

    /// <summary>对应 Go: <c>Service.TouchStyleProfile</c>。</summary>
    public Task TouchStyleProfileAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        StyleProfiles.TouchStyleProfileAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteStyleProfile</c>。</summary>
    public Task DeleteStyleProfileAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        StyleProfiles.DeleteStyleProfileAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.VoiceProfiles</c>。</summary>
    public Task<IReadOnlyList<Domain.Entities.VoiceProfile>> VoiceProfilesAsync(
        string userId, CancellationToken cancellationToken = default) =>
        Repository.VoiceProfilesAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.ProjectAssetFolders</c>。</summary>
    public Task<IReadOnlyList<Domain.Entities.ProjectAssetFolder>> ProjectAssetFoldersAsync(
        string userId, string projectId, CancellationToken cancellationToken = default) =>
        ProjectAssetFolders.ProjectAssetFoldersAsync(userId, projectId, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateProjectAssetFolder</c>。</summary>
    public Task<Domain.Entities.ProjectAssetFolder> CreateProjectAssetFolderAsync(
        string userId, string projectId, CreateProjectAssetFolderRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectAssetFolders.CreateProjectAssetFolderAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateProjectAssetFolder</c>。</summary>
    public Task<Domain.Entities.ProjectAssetFolder> UpdateProjectAssetFolderAsync(
        string userId, string projectId, string folderId, UpdateProjectAssetFolderRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectAssetFolders.UpdateProjectAssetFolderAsync(userId, projectId, folderId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteProjectAssetFolder</c>。</summary>
    public Task DeleteProjectAssetFolderAsync(
        string userId, string projectId, string folderId, CancellationToken cancellationToken = default) =>
        ProjectAssetFolders.DeleteProjectAssetFolderAsync(userId, projectId, folderId, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateProjectUnit</c>。</summary>
    public Task<Domain.Entities.ProjectUnit> CreateProjectUnitAsync(
        string userId, string projectId, CreateProjectUnitRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.CreateProjectUnitAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.ProjectUnitSummaries</c>。</summary>
    public Task<ProjectUnitSummariesDto> ProjectUnitSummariesAsync(
        string userId, string projectId, CancellationToken cancellationToken = default) =>
        ProjectUnits.ProjectUnitSummariesAsync(userId, projectId, cancellationToken);

    /// <summary>对应 Go: <c>Service.GetProjectUnit</c>。</summary>
    public Task<Domain.Entities.ProjectUnit> GetProjectUnitAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default) =>
        ProjectUnits.GetProjectUnitAsync(userId, projectId, unitId, cancellationToken);

    /// <summary>对应 Go: <c>Service.ImportProjectUnits</c>。</summary>
    public Task<IReadOnlyList<Domain.Entities.ProjectUnit>> ImportProjectUnitsAsync(
        string userId, string projectId, ImportProjectUnitsRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.ImportProjectUnitsAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.ReorderProjectUnits</c>。</summary>
    public Task ReorderProjectUnitsAsync(
        string userId, string projectId, ReorderProjectUnitsRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.ReorderProjectUnitsAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateProjectUnit</c>。</summary>
    public Task<Domain.Entities.ProjectUnit> UpdateProjectUnitAsync(
        string userId, string projectId, string unitId, UpdateProjectUnitRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.UpdateProjectUnitAsync(userId, projectId, unitId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteProjectUnit</c>。</summary>
    public Task DeleteProjectUnitAsync(
        string userId, string projectId, string unitId, CancellationToken cancellationToken = default) =>
        ProjectUnits.DeleteProjectUnitAsync(userId, projectId, unitId, cancellationToken);

    /// <summary>对应 Go: <c>Service.LinkCanvasUnit</c>。</summary>
    public Task<Domain.Entities.CanvasUnitLink> LinkCanvasUnitAsync(
        string userId, string projectId, LinkCanvasUnitRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.LinkCanvasUnitAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UnlinkCanvasUnit</c>。</summary>
    public Task UnlinkCanvasUnitAsync(
        string userId, string projectId, string canvasId, string unitId,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.UnlinkCanvasUnitAsync(userId, projectId, canvasId, unitId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UnlinkCanvasProject</c>。</summary>
    public Task UnlinkCanvasProjectAsync(
        string userId, string projectId, string canvasId,
        CancellationToken cancellationToken = default) =>
        ProjectUnits.UnlinkCanvasProjectAsync(userId, projectId, canvasId, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminAPICallLogs</c>。</summary>
    public Task<ApiCallLogPageDto> AdminApiCallLogsAsync(
        User actor, OpenAICanvas.Persistence.Repositories.ApiCallLogFilter filter,
        CancellationToken cancellationToken = default) =>
        AdminAnalytics.ApiCallLogsAsync(actor, filter, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminAPICallLog</c>。</summary>
    public Task<Domain.Entities.ApiCallLog> AdminApiCallLogAsync(
        User actor, string id, CancellationToken cancellationToken = default) =>
        AdminAnalytics.ApiCallLogAsync(actor, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminAPICallLogsExportCSV</c>。</summary>
    public Task<string> AdminApiCallLogsExportCsvAsync(
        User actor, OpenAICanvas.Persistence.Repositories.ApiCallLogFilter filter,
        CancellationToken cancellationToken = default) =>
        AdminAnalytics.ApiCallLogsExportCsvAsync(actor, filter, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminStorageStats</c>。</summary>
    public Task<AdminStorageStatsDto> AdminStorageStatsAsync(
        User actor, CancellationToken cancellationToken = default) =>
        AdminAnalytics.StorageStatsAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.ProjectAssets</c>。</summary>
    public Task<List<ProjectAssetSummaryDto>> ProjectAssetsAsync(
        string userId, string projectId, ProjectAssetFilter? filter = null,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.ProjectAssetsAsync(userId, projectId, filter, cancellationToken);

    /// <summary>对应 Go: <c>Service.ProjectAssetsPage</c>。</summary>
    public Task<object> ProjectAssetsPageAsync(
        string userId, string projectId, int page, int pageSize,
        string category, string mediaType, string status, string? folderId, string query,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.ProjectAssetsPageAsync(
            userId, projectId, page, pageSize,
            new ProjectAssetFilter
            {
                Category = category,
                MediaType = mediaType,
                Status = status,
                FolderId = folderId ?? "",
                Query = query,
            },
            cancellationToken);

    /// <summary>对应 Go: <c>Service.LinkProjectAsset</c>。</summary>
    public Task<ProjectAssetSummaryDto> LinkProjectAssetAsync(
        string userId, string projectId, LinkProjectAssetRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.LinkProjectAssetAsync(userId, projectId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UnlinkProjectAsset</c>。</summary>
    public Task UnlinkProjectAssetAsync(
        string userId, string projectId, string assetId, CancellationToken cancellationToken = default) =>
        ProjectAssets.UnlinkProjectAssetAsync(userId, projectId, assetId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateProjectAsset</c>。</summary>
    public Task<ProjectAssetSummaryDto> UpdateProjectAssetAsync(
        string userId, string projectId, string assetId, UpdateProjectAssetRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.UpdateProjectAssetAsync(userId, projectId, assetId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateProjectAssetVersion</c>。</summary>
    public Task<AssetVersion> CreateProjectAssetVersionAsync(
        string userId, string projectId, string assetId, CreateAssetVersionRequest request,
        CancellationToken cancellationToken = default) =>
        ProjectAssets.CreateProjectAssetVersionAsync(userId, projectId, assetId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserAnnouncements</c>。</summary>
    public Task<UserAnnouncementFeedDto> UserAnnouncementsAsync(
        User user, CancellationToken cancellationToken = default) =>
        Announcements.UserAnnouncementsAsync(user, cancellationToken);

    /// <summary>对应 Go: <c>Service.MarkAnnouncementsRead</c>。</summary>
    public Task<long> MarkAnnouncementsReadAsync(
        User user, IReadOnlyList<string>? announcementIds, CancellationToken cancellationToken = default) =>
        Announcements.MarkAnnouncementsReadAsync(user, announcementIds, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminAnnouncementPage</c>。</summary>
    public Task<AnnouncementPageDto> AdminAnnouncementPageAsync(
        User actor, string keyword, string status, long page, long limit,
        CancellationToken cancellationToken = default) =>
        Announcements.AdminAnnouncementPageAsync(actor, keyword, status, page, limit, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateAnnouncement</c>。</summary>
    public Task<Domain.Entities.Announcement> CreateAnnouncementAsync(
        User actor, AnnouncementRequest request, CancellationToken cancellationToken = default) =>
        Announcements.CreateAnnouncementAsync(actor, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateAnnouncement</c>。</summary>
    public Task<Domain.Entities.Announcement> UpdateAnnouncementAsync(
        User actor, string id, AnnouncementRequest request, CancellationToken cancellationToken = default) =>
        Announcements.UpdateAnnouncementAsync(actor, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.CloseAnnouncement</c>。</summary>
    public Task<Domain.Entities.Announcement> CloseAnnouncementAsync(
        User actor, string id, CancellationToken cancellationToken = default) =>
        Announcements.CloseAnnouncementAsync(actor, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.DiscardAnnouncementImage</c>。</summary>
    public Task DiscardAnnouncementImageAsync(
        User actor, string resourceId, CancellationToken cancellationToken = default) =>
        Announcements.DiscardAnnouncementImageAsync(actor, resourceId, cancellationToken);

    /// <summary>对应 Go: <c>Service.OpenAnnouncementImage</c>（校验部分）。</summary>
    public Task<Domain.Entities.Resource> OpenAnnouncementImageAsync(
        User actor, string announcementId, CancellationToken cancellationToken = default) =>
        Announcements.OpenAnnouncementImageAsync(actor, announcementId, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminRegistrationSetting</c>。</summary>
    public Task<Auth.PublicRegistrationSetting> AdminRegistrationSettingAsync(
        User actor, CancellationToken cancellationToken = default) =>
        Auth.AdminRegistrationSettingAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateRegistrationSetting</c>。</summary>
    public Task<Auth.PublicRegistrationSetting> UpdateRegistrationSettingAsync(
        User actor, Auth.RegistrationSettingRequest request, CancellationToken cancellationToken = default) =>
        Auth.UpdateRegistrationSettingAsync(actor, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminEmailSetting</c>。</summary>
    public Task<Auth.PublicEmailSetting> AdminEmailSettingAsync(
        User actor, CancellationToken cancellationToken = default) =>
        Auth.AdminEmailSettingAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateEmailSetting</c>。</summary>
    public Task<Auth.PublicEmailSetting> UpdateEmailSettingAsync(
        User actor, Auth.EmailSettingRequest request, CancellationToken cancellationToken = default) =>
        Auth.UpdateEmailSettingAsync(actor, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminLinuxDOSetting</c>。</summary>
    public Task<Auth.PublicLinuxDOSetting> AdminLinuxDOSettingAsync(
        User actor, CancellationToken cancellationToken = default) =>
        Auth.AdminLinuxDOSettingAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateLinuxDOSetting</c>。</summary>
    public Task<Auth.PublicLinuxDOSetting> UpdateLinuxDOSettingAsync(
        User actor, Auth.LinuxDOSettingRequest request, CancellationToken cancellationToken = default) =>
        Auth.UpdateLinuxDOSettingAsync(actor, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminCreditPolicy</c>。</summary>
    public Task<CreditPolicy> AdminCreditPolicyAsync(
        User actor, CancellationToken cancellationToken = default) =>
        CreditPolicy.AdminAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateCreditPolicy</c>。</summary>
    public Task<CreditPolicy> UpdateCreditPolicyAsync(
        User actor, CreditPolicy policy, CancellationToken cancellationToken = default) =>
        CreditPolicy.UpdateAsync(actor, policy, cancellationToken);

    /// <summary>对应 Go: <c>Service.ListProjects</c>。</summary>
    public Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(
        string userId, CancellationToken cancellationToken = default) =>
        Projects.ListProjectsAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.ListProjectsPage</c>。</summary>
    public Task<ProjectListPageDto> ListProjectsPageAsync(
        string userId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Projects.ListProjectsPageAsync(userId, page, pageSize, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateProject</c>。</summary>
    public Task<Project> CreateProjectAsync(
        string userId, CreateProjectRequest request, CancellationToken cancellationToken = default) =>
        Projects.CreateProjectAsync(userId, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateProject</c>。</summary>
    public Task<Project> UpdateProjectAsync(
        string userId, string id, UpdateProjectRequest request, CancellationToken cancellationToken = default) =>
        Projects.UpdateProjectAsync(userId, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteProject</c>。</summary>
    public Task DeleteProjectAsync(
        string userId, string id, CancellationToken cancellationToken = default) =>
        Projects.DeleteProjectAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteUserAsset</c>（宿主 DeleteUserAssetWithResources）。</summary>
    public Task DeleteUserAssetAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default) =>
        ResourceDelete.DeleteUserAssetWithResourcesAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserAssetsByIDs</c>。</summary>
    public Task<List<JsonElement>> UserAssetsByIDsAsync(
        string userId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        UserData.UserAssetsByIDsAsync(userId, ids, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserAssetSummaries</c>（素材）。</summary>
    public Task<List<UserDataSummaryDto>> UserAssetSummariesAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        UserData.UserAssetSummariesAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserAssetsPage</c>。</summary>
    public Task<UserAssetPageDto> UserAssetsPageAsync(
        string userId,
        int page,
        int pageSize,
        string kind,
        string category,
        string? folderId,
        bool uncategorized,
        string status,
        string query,
        CancellationToken cancellationToken = default) =>
        UserData.UserAssetsPageAsync(
            userId, page, pageSize, kind, category, folderId, uncategorized, status, query, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserAsset</c>。</summary>
    public Task<JsonElement> UserAssetAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default) =>
        UserData.UserAssetAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpsertUserAsset</c>。</summary>
    public Task<UserDataSummaryDto> UpsertUserAssetAsync(
        string userId,
        JsonElement asset,
        CancellationToken cancellationToken = default) =>
        UserData.UpsertUserAssetAsync(userId, asset, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserDataSnapshot</c>。</summary>
    public Task<(List<JsonElement> Assets, List<JsonElement> Projects)> UserDataSnapshotAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        UserData.UserDataSnapshotAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.AssetFolders</c>。</summary>
    public Task<IReadOnlyList<AssetFolder>> AssetFoldersAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        UserData.AssetFoldersAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateAssetFolder</c>。</summary>
    public Task<AssetFolder> CreateAssetFolderAsync(
        string userId,
        string name,
        CancellationToken cancellationToken = default) =>
        UserData.CreateAssetFolderAsync(userId, name, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateAssetFolder</c>。</summary>
    public Task<AssetFolder> UpdateAssetFolderAsync(
        string userId,
        string folderId,
        string name,
        CancellationToken cancellationToken = default) =>
        UserData.UpdateAssetFolderAsync(userId, folderId, name, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteAssetFolder</c>。</summary>
    public Task DeleteAssetFolderAsync(
        string userId,
        string folderId,
        CancellationToken cancellationToken = default) =>
        UserData.DeleteAssetFolderAsync(userId, folderId, cancellationToken);

    /// <summary>对应 Go: <c>Service.MoveUserAssetsToFolder</c>。</summary>
    public Task<(IReadOnlyList<string> AssetIDs, string FolderID)> MoveUserAssetsToFolderAsync(
        string userId,
        IReadOnlyList<string>? assetIds,
        string folderId,
        CancellationToken cancellationToken = default) =>
        UserData.MoveUserAssetsToFolderAsync(userId, assetIds, folderId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserCanvasProjectsPage</c>。</summary>
    public Task<CanvasLibraryPageDto> UserCanvasProjectsPageAsync(
        string userId,
        int page,
        int pageSize,
        string projectId,
        string search,
        string sort,
        CancellationToken cancellationToken = default) =>
        UserData.UserCanvasProjectsPageAsync(userId, page, pageSize, projectId, search, sort, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserCanvasProjectSummaries</c>。</summary>
    public Task<List<UserDataSummaryDto>> UserCanvasProjectSummariesAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        UserData.UserCanvasProjectSummariesAsync(userId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UserCanvasProject</c>。</summary>
    public Task<JsonElement> UserCanvasProjectAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default) =>
        UserData.UserCanvasProjectAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpsertUserCanvasProject</c>。</summary>
    public Task<UserDataSummaryDto> UpsertUserCanvasProjectAsync(
        string userId,
        JsonElement project,
        CancellationToken cancellationToken = default) =>
        UserData.UpsertUserCanvasProjectAsync(userId, project, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteUserCanvasProject</c>。</summary>
    public Task DeleteUserCanvasProjectAsync(
        string userId,
        string id,
        CancellationToken cancellationToken = default) =>
        UserData.DeleteUserCanvasProjectAsync(userId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.PreviewAdminChannelModels</c>。</summary>
    public Task<IReadOnlyList<string>> PreviewAdminChannelModelsAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default) =>
        ChannelModels.PreviewAdminChannelModelsAsync(actor, channelId, cancellationToken);

    /// <summary>对应 Go: <c>Service.ImportAdminChannelModels</c>。</summary>
    public Task<ChannelModelAdminService.AdminChannelModelFetchResultDto> ImportAdminChannelModelsAsync(
        User actor,
        string channelId,
        IReadOnlyList<string>? selected,
        CancellationToken cancellationToken = default) =>
        ChannelModels.ImportAdminChannelModelsAsync(actor, channelId, selected, cancellationToken);

    /// <summary>对应 Go: <c>Service.EnsureSystemChannelModels</c>。</summary>
    public Task EnsureSystemChannelModelsAsync(CancellationToken cancellationToken = default) =>
        ChannelAdmin.EnsureSystemChannelModelsAsync(cancellationToken);

    public AdminUserService AdminUsers { get; }

    public TaskService Tasks { get; }

    /// <summary>项目工作流 v2 与工作台聚合。对应 Go: <c>project_workflow.go</c>。</summary>
    public ProjectWorkflowService ProjectWorkflows { get; }

    public FinanceService Finance { get; }

    public SkillsService Skills { get; }

    public Prompts.PromptTemplateService PromptTemplates { get; }

    /// <summary>创作端模型目录。对应 Go: <c>app/model_catalog.go</c>。</summary>
    public ModelCatalogService ModelCatalog { get; }

    /// <summary>任务创建准入。对应 Go: <c>app/task_creation.go</c>。</summary>
    public TaskCreationService TaskCreations { get; }

    /// <summary>工作流插件门控（4.12）。对应 Go 的 workflow_plugins + plugin_states 组合。</summary>
    public WorkflowPluginGate WorkflowPlugins { get; }

    /// <summary>插件运行时（10.1/10.2）。对应 Go: <c>app.PluginRuntime</c>。</summary>
    public PluginRuntime Plugins { get; }

    /// <summary>插件管理服务（10.1）。对应 Go: <c>app.PluginManagement</c>。</summary>
    public PluginManagementService PluginManagement { get; }

    /// <summary>RunningHub 管理代理（4.12）。对应 Go: <c>runninghub_management.go</c>。</summary>
    public RunningHubManagementService RunningHub { get; }

    /// <summary>任务重试与取消。对应 Go: <c>app/task_lifecycle.go</c>。</summary>
    public TaskLifecycleService TaskLifecycle { get; }

    /// <summary>智能创作运行。对应 Go: <c>app/creation.go</c>。</summary>
    public CreationRunService CreationRuns { get; }

    /// <summary>管理端日志媒体。对应 Go: <c>PrepareAdminAPICallLogMediaDelivery</c>。</summary>
    public AdminLogMediaService AdminLogMedia { get; }

    /// <summary>用户诊断包。对应 Go: <c>app/diagnostics.go</c>。</summary>
    public DiagnosticsService Diagnostics { get; }

    /// <summary>系统性能与运行时缓存。对应 Go: <c>app/admin_system_performance.go</c>。</summary>
    public SystemPerformanceService SystemPerformance { get; }

    /// <summary>失效逻辑模型路由目录缓存。对应 Go: <c>invalidateRouteCatalog</c>。</summary>
    public bool TryInvalidateRouteCatalog()
    {
        LogicalModels.InvalidateRouteCatalog();
        return true;
    }



    public PaymentService Payments { get; }

    // ------------------------------------------------------------ 权限

    /// <summary>
    /// 管理员校验。对应 Go: <c>Service.RequireAdmin</c>。
    /// </summary>
    /// <remarks>管理员权限在服务层校验，不依赖前端隐藏按钮。</remarks>
    public static void RequireAdmin(User? user)
    {
        if (user is null)
        {
            throw AppError.Unauthorized("请先登录");
        }

        if (user.Role != UserRole.UserRoleAdmin)
        {
            throw AppError.Forbidden("需要管理员权限");
        }
    }

    // ------------------------------------------------------------ 认证转发

    public Task<Auth.PublicAuthSettingsDto> PublicAuthSettingsAsync(CancellationToken cancellationToken = default) =>
        Auth.PublicAuthSettingsAsync(cancellationToken);

    public Task<Auth.AuthSessionResultDto> RegisterAsync(
        Auth.RegisterRequest request,
        CancellationToken cancellationToken = default) =>
        Auth.RegisterAsync(request, cancellationToken);

    public Task<Auth.AuthSessionResultDto> LoginAsync(
        Auth.LoginRequest request,
        CancellationToken cancellationToken = default) =>
        Auth.LoginAsync(request, cancellationToken);

    public Task LogoutAsync(string? cookieValue, CancellationToken cancellationToken = default) =>
        Auth.LogoutAsync(cookieValue, cancellationToken);

    public Task<User> CurrentUserAsync(string? cookieValue, CancellationToken cancellationToken = default) =>
        Auth.CurrentUserAsync(cookieValue, cancellationToken);

    public Task<Auth.AuthUserDto> PublicAuthUserAsync(
        User user,
        CancellationToken cancellationToken = default) =>
        Auth.PublicAuthUserAsync(user, cancellationToken);

    public Task<string> SendRegistrationEmailCodeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        Auth.IssueRegistrationEmailCodeAsync(email, cancellationToken);

    /// <summary>对应 Go: <c>Service.SendPasswordResetEmailCode</c>。</summary>
    public Task SendPasswordResetEmailCodeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        Auth.SendPasswordResetEmailCodeAsync(email, cancellationToken);

    /// <summary>对应 Go: <c>Service.ResetPassword</c>。</summary>
    public Task ResetPasswordAsync(
        Auth.PasswordResetRequest request,
        CancellationToken cancellationToken = default) =>
        Auth.ResetPasswordAsync(request, cancellationToken);

    // ------------------------------------------------------------ 平台设置转发

    /// <summary>对应 Go: <c>Service.FeatureAvailability</c>。</summary>
    public Task<PublicFeatureAvailability> FeatureAvailabilityAsync(CancellationToken cancellationToken = default) =>
        Features.GetAsync(cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateFeatureAvailability</c>。</summary>
    public Task<PublicFeatureAvailability> UpdateFeatureAvailabilityAsync(
        FeatureAvailability value,
        User actor,
        CancellationToken cancellationToken = default) =>
        Features.UpdateAsync(value, actor.ID, cancellationToken);

    /// <summary>对应 Go: <c>Service.DrawingEngineSetting</c>。</summary>
    public Task<PublicDrawingEngineSetting> DrawingEngineSettingAsync(
        CancellationToken cancellationToken = default) =>
        DrawingEngine.GetAsync(cancellationToken);

    /// <summary>对应 Go: <c>Service.PublicRuntimeLimits</c>。</summary>
    public PublicRuntimeLimits PublicRuntimeLimits() => RuntimePolicy.PublicLimits();

    /// <summary>
    /// 品牌名（同步属性，取已缓存值）。对应 Go: <c>appearanceBrandName</c>。
    /// 外观配置可从 <c>AppearanceService.BrandNameAsync</c> 获取最新值。
    /// </summary>
    public string BrandName => OpenAICanvas.Auth.NullAuthHost.DefaultBrandName;

    /// <summary>
    /// 确保注册奖励已发放（幂等）。对应 Go: <c>ensureSignupBonus</c>。
    /// </summary>
    public async Task EnsureSignupBonusAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!await Features.FeatureEnabledAsync(OpenAICanvas.Platform.FeatureNames.Credits, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        CreditPolicy policy = await CreditPolicy.GetAsync(cancellationToken).ConfigureAwait(false);
        if (policy.SignupBonusMicrocredits == 0)
        {
            return;
        }

        await Repository.GrantCreditsOnceAsync(
            userId,
            CreditLedgerType.CreditLedgerSignupBonus,
            policy.SignupBonusMicrocredits,
            "signup:" + userId,
            "新用户默认积分",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 记录用户活跃事件。对应 Go: <c>recordActivity</c>。
    /// </summary>
    /// <remarks>
    /// 接口是 <c>void</c>，而 Go 侧也是同步写库（失败只记日志），所以这里同步等待，
    /// 保证「登录后立刻查活跃」可见。
    /// </remarks>
    public void RecordActivity(string userId, string @event, int count)
    {
        try
        {
            Repository.RecordUserActivityAsync(userId, @event, count, DateTime.UtcNow)
                .GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 与 Go 一致：活跃记录失败不影响主流程。
        }
    }

    /// <summary>会话恢复。对应 Go: <c>handler.auth.go:/auth/session</c>。</summary>
    public async Task<SessionDto> SessionAsync(
        string? cookieValue,
        CancellationToken cancellationToken = default)
    {
        User? user;
        try
        {
            user = await CurrentUserAsync(cookieValue, cancellationToken).ConfigureAwait(false);
        }
        catch (AppError)
        {
            // Go 的 /auth/session 在 currentUser 失败时返回 {"user": null}，不是 401。
            user = null;
        }

        if (user is null)
        {
            return new SessionDto
            {
                User = null,
                LogicalModels = [],
                RuntimeLimits = PublicRuntimeLimits(),
                DrawingEngine = await DrawingEngineSettingAsync(cancellationToken).ConfigureAwait(false),
                Features = await FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false),
            };
        }

        AuthUserDto publicUser = await PublicAuthUserAsync(user, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PublicLogicalModelDto> logicalModels = await LogicalModels
            .PublicLogicalModelsAsync(null, cancellationToken).ConfigureAwait(false);
        return new SessionDto
        {
            User = publicUser,
            LogicalModels = logicalModels,
            RuntimeLimits = PublicRuntimeLimits(),
            DrawingEngine = await DrawingEngineSettingAsync(cancellationToken).ConfigureAwait(false),
            Features = await FeatureAvailabilityAsync(cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>对应 Go: <c>Service.PublicLogicalModels</c>。</summary>
    public Task<IReadOnlyList<PublicLogicalModelDto>> PublicLogicalModelsAsync(
        ModelRequestIntent? intent,
        CancellationToken cancellationToken = default) =>
        LogicalModels.PublicLogicalModelsAsync(intent, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminLogicalModels</c>。</summary>
    public Task<IReadOnlyList<AdminLogicalModelDto>> AdminLogicalModelsAsync(
        User actor,
        CancellationToken cancellationToken = default) =>
        LogicalModels.AdminLogicalModelsAsync(actor, cancellationToken);

    /// <summary>对应 Go: <c>Service.SaveAdminLogicalModel</c>。</summary>
    public Task<AdminLogicalModelDto> SaveAdminLogicalModelAsync(
        User actor,
        string id,
        LogicalModelRequest request,
        CancellationToken cancellationToken = default) =>
        LogicalModels.SaveAdminLogicalModelAsync(actor, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteAdminLogicalModel</c>。</summary>
    public Task DeleteAdminLogicalModelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default) =>
        LogicalModels.DeleteAdminLogicalModelAsync(actor, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.SimulateLogicalModelRoute</c>。</summary>
    public Task<RouteSimulationResultDto> SimulateLogicalModelRouteAsync(
        string logicalModelId,
        ModelRequestIntent intent,
        CancellationToken cancellationToken = default) =>
        LogicalModels.SimulateLogicalModelRouteAsync(logicalModelId, intent, cancellationToken);

    /// <summary>对应 Go: <c>Service.QuoteLogicalModel</c>。</summary>
    public Task<LogicalModelQuoteDto> QuoteLogicalModelAsync(
        string logicalModelId,
        ModelRequestIntent intent,
        CancellationToken cancellationToken = default) =>
        LogicalModels.QuoteLogicalModelAsync(logicalModelId, intent, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminSystemChannelPage</c>。</summary>
    public Task<AdminChannelPageDto> AdminChannelPageAsync(
        User actor,
        string keyword,
        string status,
        long page,
        long limit,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.AdminChannelPageAsync(actor, keyword, status, page, limit, cancellationToken);

    /// <summary>对应 Go: <c>Service.CreateSystemChannel</c>。</summary>
    public Task<PublicModelChannelDto> CreateSystemChannelAsync(
        User actor,
        ChannelRequest request,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.CreateSystemChannelAsync(actor, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DuplicateSystemChannel</c>。</summary>
    public Task<PublicModelChannelDto> DuplicateSystemChannelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.DuplicateSystemChannelAsync(actor, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateSystemChannel</c>。</summary>
    public Task<PublicModelChannelDto> UpdateSystemChannelAsync(
        User actor,
        string id,
        ChannelRequest request,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.UpdateSystemChannelAsync(actor, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteSystemChannel</c>。</summary>
    public Task DeleteSystemChannelAsync(
        User actor,
        string id,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.DeleteSystemChannelAsync(actor, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminChannelModels</c>。</summary>
    public Task<IReadOnlyList<Domain.Entities.ChannelModel>> AdminChannelModelsAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.AdminChannelModelsAsync(actor, channelId, cancellationToken);

    /// <summary>对应 Go: <c>Service.UpdateAdminChannelModelSort</c>。</summary>
    public Task UpdateAdminChannelModelSortAsync(
        User actor,
        string channelId,
        string modelId,
        long? sortOrder,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.UpdateAdminChannelModelSortAsync(actor, channelId, modelId, sortOrder, cancellationToken);

    /// <summary>对应 Go: <c>Service.TestAdminChannelModel</c>。</summary>
    public Task<long> TestAdminChannelModelAsync(
        User actor,
        string channelId,
        ChannelModelRequest request,
        TaskWorkerService worker,
        CancellationToken cancellationToken = default) =>
        ChannelModels.TestAdminChannelModelAsync(actor, channelId, request, worker, cancellationToken);

    /// <summary>对应 Go: <c>Service.SaveAdminChannelModel</c>。</summary>
    public Task<Domain.Entities.ChannelModel> SaveAdminChannelModelAsync(
        User actor,
        string channelId,
        string id,
        ChannelModelRequest request,
        CancellationToken cancellationToken = default) =>
        ChannelModels.SaveAdminChannelModelAsync(actor, channelId, id, request, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteAdminChannelModel</c>。</summary>
    public Task<long> DeleteAdminChannelModelAsync(
        User actor,
        string channelId,
        string id,
        CancellationToken cancellationToken = default) =>
        ChannelModels.DeleteAdminChannelModelAsync(actor, channelId, id, cancellationToken);

    /// <summary>对应 Go: <c>Service.AdminChannelOrder</c>。</summary>
    public Task<IReadOnlyList<ChannelAdminService.ChannelOrderItemDto>> AdminChannelOrderAsync(
        User actor,
        string channelId,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.AdminChannelOrderAsync(actor, channelId, cancellationToken);

    /// <summary>对应 Go: <c>Service.SaveAdminChannelOrder</c>。</summary>
    public Task SaveAdminChannelOrderAsync(
        User actor,
        string channelId,
        IReadOnlyList<string>? ids,
        IReadOnlyList<string>? expectedIds,
        CancellationToken cancellationToken = default) =>
        ChannelAdmin.SaveAdminChannelOrderAsync(actor, channelId, ids, expectedIds, cancellationToken);

    /// <summary>对应 Go: <c>Service.DeleteAdminChannelModels</c>。</summary>
    public Task<long> DeleteAdminChannelModelsAsync(
        User actor,
        string channelId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default) =>
        ChannelModels.DeleteAdminChannelModelsAsync(actor, channelId, ids, cancellationToken);
}
