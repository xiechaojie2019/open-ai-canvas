using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Domain.Serialization;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>创建用户请求（管理员）。对应 Go: <c>app.CreateAdminUserRequest</c>。</summary>
public sealed class CreateAdminUserRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>更新用户请求（管理员）。对应 Go: <c>app.UpdateUserRequest</c>。</summary>
public sealed class UpdateUserRequest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>批量停用请求。对应 Go: <c>app.BulkDisableUsersRequest</c>。</summary>
public sealed class BulkDisableUsersRequest
{
    [JsonPropertyName("userIds")]
    public List<string> UserIds { get; set; } = [];
}

/// <summary>批量停用结果。对应 Go: <c>app.BulkDisableUsersResult</c>。</summary>
public sealed class BulkDisableUsersResult
{
    [JsonPropertyName("users")]
    public required IReadOnlyList<User> Users { get; init; }

    [JsonPropertyName("disabledCount")]
    public int DisabledCount { get; init; }
}

/// <summary>
/// 管理后台用户视图。对应 Go: <c>app.AdminUser</c>。
/// </summary>
/// <remarks>
/// Go 用结构体嵌入 <c>model.User</c>，JSON 会把 User 字段平铺到同一层；
/// 这里按 User 的声明顺序显式展开，再接两个积分字段。
/// </remarks>
public sealed class AdminUserDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    [GoOmitEmpty]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("availableMicrocredits")]
    public long AvailableMicrocredits { get; init; }

    [JsonPropertyName("reservedMicrocredits")]
    public long ReservedMicrocredits { get; init; }

    public static AdminUserDto From(User user, CreditAccount? account) => new()
    {
        Id = user.ID,
        Username = user.Username,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role,
        Status = user.Status,
        LastLoginAt = user.LastLoginAt,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        AvailableMicrocredits = account?.AvailableMicrocredits ?? 0,
        ReservedMicrocredits = account?.ReservedMicrocredits ?? 0,
    };
}

/// <summary>用户分页。对应 Go: <c>app.AdminUserPage</c>。</summary>
public sealed class AdminUserPageDto
{
    [JsonPropertyName("users")]
    public required IReadOnlyList<AdminUserDto> Users { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    /// <summary>注意：Go 的 json tag 是 <c>pageSize</c>，不是 <c>limit</c>。</summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>用户引用项。对应 Go: <c>app.AdminUserReference</c>。</summary>
public sealed class AdminUserReferenceDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>渠道引用项。对应 Go: <c>app.AdminChannelReference</c>。</summary>
public sealed class AdminChannelReferenceDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("models")]
    public required IReadOnlyList<string> Models { get; init; }
}

/// <summary>引用数据。对应 Go: <c>app.AdminReferenceData</c>。</summary>
public sealed class AdminReferenceDataDto
{
    [JsonPropertyName("users")]
    public required IReadOnlyList<AdminUserReferenceDto> Users { get; init; }

    [JsonPropertyName("channels")]
    public required IReadOnlyList<AdminChannelReferenceDto> Channels { get; init; }
}


/// <summary>钱包摘要。对应 Go: <c>app.WalletSummary</c>。</summary>
public sealed class WalletSummaryDto
{
    [JsonPropertyName("account")]
    public required CreditAccount Account { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<CreditLedgerEntry> Entries { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    /// <summary>注意：Go 的 json tag 是 <c>pageSize</c>。</summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }

    [JsonPropertyName("policy")]
    public required PublicCreditPolicy Policy { get; init; }
}

/// <summary>用户任务分页。对应 Go: <c>app.AdminTaskPage</c>。</summary>
public sealed class AdminTaskPageDto
{
    [JsonPropertyName("tasks")]
    public required IReadOnlyList<TaskEntity> Tasks { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}

/// <summary>审计事件分页。对应 Go: <c>app.AdminAuditPage</c>。</summary>
public sealed class AdminAuditPageDto
{
    [JsonPropertyName("events")]
    public required IReadOnlyList<AdminAuditEvent> Events { get; init; }

    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}


/// <summary>用户详情聚合。对应 Go: <c>app.AdminUserDetail</c>。</summary>
public sealed class AdminUserDetailDto
{
    [JsonPropertyName("user")]
    public required User User { get; init; }

    [JsonPropertyName("account")]
    public required CreditAccount Account { get; init; }

    [JsonPropertyName("counts")]
    public required AdminUserCounts Counts { get; init; }

    [JsonPropertyName("storageUsage")]
    public required UserStorageUsage StorageUsage { get; init; }

    [JsonPropertyName("storedFileBytes")]
    public long StoredFileBytes { get; init; }

    [JsonPropertyName("dailyUploadBytes")]
    public long DailyUploadBytes { get; init; }

    [JsonPropertyName("quota")]
    public required RuntimeResourcePolicy Quota { get; init; }
}
/// <summary>
/// 管理后台用户服务。对应 Go: <c>internal/app/admin.go</c> 与 <c>admin_audit.go</c>。
/// </summary>
public sealed class AdminUserService
{
    private readonly Repository _repository;
    private readonly AuthService _auth;
    private readonly CreditPolicyService _creditPolicy;
    private readonly IRuntimePolicyProvider _runtimePolicy;

    public AdminUserService(Repository repository, AuthService auth, CreditPolicyService creditPolicy, IRuntimePolicyProvider runtimePolicy)
    {
        _repository = repository;
        _auth = auth;
        _creditPolicy = creditPolicy;
        _runtimePolicy = runtimePolicy;
    }

    /// <summary>分页列表。对应 Go: <c>AdminUsers</c>。</summary>
    public async Task<AdminUserPageDto> ListAsync(
        User actor,
        string keyword,
        string role,
        string status,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);
        (int normalizedPage, int normalizedLimit) = NormalizePage(page, limit);

        (IReadOnlyList<User> users, long total) = await _repository.AdminUsersAsync(
            keyword, role, status, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CreditAccount> accounts = await _repository
            .CreditAccountsAsync(users.Select(u => u.ID).ToList(), cancellationToken).ConfigureAwait(false);
        Dictionary<string, CreditAccount> accountByUser = accounts.ToDictionary(a => a.UserID, StringComparer.Ordinal);

        return new AdminUserPageDto
        {
            Users = users.Select(u => AdminUserDto.From(
                u, accountByUser.TryGetValue(u.ID, out CreditAccount? account) ? account : null)).ToList(),
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>创建用户。对应 Go: <c>CreateAdminUser</c>。</summary>
    public async Task<AdminUserDto> CreateAsync(
        User actor,
        CreateAdminUserRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        string username = AuthService.NormalizeUsername(request.Username);
        string email = AuthService.NormalizeEmail(request.Email);
        string displayName = AuthService.NormalizeDisplayName(request.DisplayName, username);

        AuthService.ValidateUsername(username);
        AuthService.ValidatePassword(request.Password);
        if (email.Length > 0)
        {
            AuthService.ValidateEmail(email);
        }

        if (request.Role is not (UserRole.UserRoleAdmin or UserRole.UserRoleUser))
        {
            throw AppError.BadAuthRequest("用户角色无效");
        }

        if (request.Status is not (UserStatus.UserStatusActive or UserStatus.UserStatusDisabled))
        {
            throw AppError.BadAuthRequest("用户状态无效");
        }

        if (await _repository.UserByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AppError.BadAuthRequest("用户名已存在");
        }

        if (email.Length > 0
            && await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AppError.BadAuthRequest("邮箱已被注册");
        }

        DateTime now = DateTime.UtcNow;
        User user = new()
        {
            ID = IdGenerator.NewId(),
            Username = username,
            Email = email,
            DisplayName = displayName,
            Role = request.Role,
            Status = request.Status,
            PasswordHash = AuthService.HashPassword(request.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repository.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(actor, "user.create", "user", user.ID, "创建用户账号",
            new { role = user.Role, status = user.Status }, cancellationToken).ConfigureAwait(false);

        CreditAccount? account = await _repository.CreditAccountAsync(user.ID, cancellationToken)
            .ConfigureAwait(false);

        return AdminUserDto.From(user, account);
    }

    /// <summary>更新用户。对应 Go: <c>UpdateUser</c>。</summary>
    public async Task<User> UpdateAsync(
        User actor,
        string userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        User user = await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("用户不存在");

        if (string.Equals(actor.ID, user.ID, StringComparison.Ordinal)
            && request.Status == UserStatus.UserStatusDisabled)
        {
            throw AppError.BadAuthRequest("不能禁用当前管理员账号");
        }

        string nextRole = request.Role is UserRole.UserRoleAdmin or UserRole.UserRoleUser ? request.Role : user.Role;
        string nextStatus = request.Status is UserStatus.UserStatusActive or UserStatus.UserStatusDisabled
            ? request.Status
            : user.Status;

        // 降级或停用最后一个管理员会锁死后台，必须拦住。
        if (user.Role == UserRole.UserRoleAdmin && nextRole != UserRole.UserRoleAdmin)
        {
            if (await _repository.ActiveAdminCountExcludingAsync(user.ID, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw AppError.BadAuthRequest("至少需要保留一个管理员");
            }
        }

        if (user.Role == UserRole.UserRoleAdmin && nextStatus != UserStatus.UserStatusActive)
        {
            if (await _repository.ActiveAdminCountExcludingAsync(user.ID, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw AppError.BadAuthRequest("至少需要保留一个可用管理员");
            }
        }

        if (request.DisplayName.Trim().Length > 0)
        {
            user.DisplayName = AuthService.NormalizeDisplayName(request.DisplayName, user.Username);
        }

        if (request.Email.Length > 0)
        {
            string email = AuthService.NormalizeEmail(request.Email);
            AuthService.ValidateEmail(email);

            User? existing = await _repository.UserByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !string.Equals(existing.ID, user.ID, StringComparison.Ordinal))
            {
                throw AppError.BadAuthRequest("邮箱已被注册");
            }

            user.Email = email;
        }

        if (request.Password.Length > 0)
        {
            AuthService.ValidatePassword(request.Password);
            user.PasswordHash = AuthService.HashPassword(request.Password);

            // 改口令必须先失效旧会话，否则旧 Cookie 仍能继续用。
            try
            {
                await _repository.DeleteUserAuthSessionsAsync(user.ID, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"清理旧登录会话失败，密码未更新：{error.Message}", error);
            }
        }

        user.Role = nextRole;
        user.Status = nextStatus;
        user.UpdatedAt = DateTime.UtcNow;

        await _repository.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(actor, "user.update", "user", user.ID, "更新用户账号状态或资料",
            new { role = user.Role, status = user.Status }, cancellationToken).ConfigureAwait(false);

        return user;
    }

    /// <summary>
    /// 删除用户。对应 Go: <c>DeleteUser</c>。
    /// </summary>
    /// <remarks>
    /// 有资金流水后必须保留用户主体，所以这个入口实际是"停用 + 清除全部登录态"，
    /// 而不是物理删除。
    /// </remarks>
    public async Task DeleteAsync(User actor, string userId, CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (string.Equals(actor.ID, userId, StringComparison.Ordinal))
        {
            throw AppError.BadAuthRequest("不能删除当前登录的管理员账号");
        }

        User user = await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("用户不存在");

        if (user.Role == UserRole.UserRoleAdmin)
        {
            if (await _repository.ActiveAdminCountExcludingAsync(user.ID, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw AppError.BadAuthRequest("至少需要保留一个管理员");
            }
        }

        await _repository.DeleteUserAuthSessionsAsync(user.ID, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteUserTaskTextDeltasAsync(user.ID, cancellationToken).ConfigureAwait(false);

        user.Status = UserStatus.UserStatusDisabled;
        user.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(actor, "user.disable", "user", user.ID, "停用用户并清除登录态", null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>批量停用。对应 Go: <c>BulkDisableUsers</c>。</summary>
    public async Task<BulkDisableUsersResult> BulkDisableAsync(
        User actor,
        BulkDisableUsersRequest request,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> userIds = [];
        foreach (string rawId in request.UserIds)
        {
            string id = rawId.Trim();
            if (id.Length == 0)
            {
                throw AppError.BadAuthRequest("用户 ID 无效");
            }

            if (seen.Add(id))
            {
                userIds.Add(id);
            }
        }

        if (userIds.Count == 0)
        {
            throw AppError.BadAuthRequest("请选择要停用的用户");
        }

        if (userIds.Count > 100)
        {
            throw AppError.BadAuthRequest("单次最多停用 100 个用户");
        }

        string metadata = JsonSerializer.Serialize(new { userIds, count = userIds.Count });
        DateTime now = DateTime.UtcNow;

        List<AdminAuditEvent> events = userIds.Select(userId => new AdminAuditEvent
        {
            ID = IdGenerator.NewId(),
            ActorUserID = actor.ID,
            Action = "user.bulk_disable",
            TargetType = "user",
            TargetID = userId,
            Summary = "批量停用用户并清除登录态",
            MetadataJSON = metadata,
            CreatedAt = now,
        }).ToList();

        (Repository.BulkDisableOutcome outcome, IReadOnlyList<User> users) = await _repository
            .BulkDisableUsersAsync(actor.ID, userIds, events, now, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case Repository.BulkDisableOutcome.UserNotFound:
                throw AppError.BadAuthRequest("部分用户不存在，请刷新列表后重试");
            case Repository.BulkDisableOutcome.IncludesCurrentAdmin:
                throw AppError.BadAuthRequest("不能停用当前登录的管理员账号");
            case Repository.BulkDisableOutcome.RemovesLastActiveAdmin:
                throw AppError.BadAuthRequest("批量操作后至少需要保留一个可用管理员");
        }

        return new BulkDisableUsersResult { Users = users, DisabledCount = users.Count };
    }

    /// <summary>引用数据（用户下拉 + 渠道下拉）。对应 Go: <c>AdminReferences</c>。</summary>
    public async Task<AdminReferenceDataDto> ReferencesAsync(
        User actor,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        IReadOnlyList<User> users = await _repository.AdminUserReferencesAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ModelChannel> channels = await _repository.AdminSystemChannelReferencesAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AdminChannelReferenceDto> channelRefs = [];
        foreach (ModelChannel channel in channels)
        {
            IReadOnlyList<ChannelModel> items = await _repository
                .ChannelModelsAsync(channel.ID, enabledOnly: false, cancellationToken).ConfigureAwait(false);

            channelRefs.Add(new AdminChannelReferenceDto
            {
                Id = channel.ID,
                Name = channel.Name,
                Enabled = channel.Enabled,
                Models = UniqueNonEmpty(items.Select(i => i.ModelKey)),
            });
        }

        return new AdminReferenceDataDto
        {
            Users = users.Select(u => new AdminUserReferenceDto
            {
                Id = u.ID,
                Username = u.Username,
                DisplayName = u.DisplayName,
            }).ToList(),
            Channels = channelRefs,
        };
    }

    /// <summary>用户积分账本。对应 Go: <c>AdminUserLedger</c>。</summary>
    public async Task<WalletSummaryDto> LedgerAsync(
        User actor,
        string userId,
        string entryType,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("用户不存在");
        }

        (int normalizedPage, int normalizedLimit) = NormalizePage(page, limit);

        CreditAccount account = await _repository.CreditAccountAsync(userId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AppError.NotFound("积分账户不存在");

        (IReadOnlyList<CreditLedgerEntry> entries, long total) = await _repository.CreditLedgerAsync(
            userId, entryType, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        PublicCreditPolicy policy = await _creditPolicy.PublicAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return new WalletSummaryDto
        {
            Account = account,
            Entries = entries,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
            Policy = policy,
        };
    }

    /// <summary>用户任务分页。对应 Go: <c>AdminUserTasks</c>。</summary>
    public async Task<AdminTaskPageDto> TasksAsync(
        User actor,
        string userId,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("用户不存在");
        }

        (int normalizedPage, int normalizedLimit) = NormalizePage(page, limit);

        (IReadOnlyList<TaskEntity> tasks, long total) = await _repository.AdminUserTasksAsync(
            userId, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        return new AdminTaskPageDto
        {
            Tasks = tasks,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>用户审计事件分页。对应 Go: <c>AdminUserAuditEvents</c>。</summary>
    public async Task<AdminAuditPageDto> AuditEventsAsync(
        User actor,
        string userId,
        int page,
        int limit,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        if (await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw AppError.NotFound("用户不存在");
        }

        (int normalizedPage, int normalizedLimit) = NormalizePage(page, limit);

        (IReadOnlyList<AdminAuditEvent> events, long total) = await _repository.AdminAuditEventsAsync(
            "user", userId, normalizedLimit, (normalizedPage - 1) * normalizedLimit, cancellationToken)
            .ConfigureAwait(false);

        return new AdminAuditPageDto
        {
            Events = events,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedLimit,
        };
    }

    /// <summary>用户详情聚合。对应 Go: <c>AdminUserDetail</c>。</summary>
    public async Task<AdminUserDetailDto> DetailAsync(
        User actor,
        string userId,
        CancellationToken cancellationToken = default)
    {
        CanvasService.RequireAdmin(actor);

        User? user = await _repository.UserAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("用户不存在");

        CreditAccount account = await _repository.CreditAccountAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppError.NotFound("积分账户不存在");

        AdminUserCounts counts = await _repository.AdminUserCountsAsync(userId, cancellationToken).ConfigureAwait(false);
        UserStorageUsage usage = await _repository.UserStorageUsageAsync(userId, cancellationToken).ConfigureAwait(false);
        long storedFileBytes = await _repository.UserStoredFileBytesAsync(userId, cancellationToken).ConfigureAwait(false);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        long dailyUploadBytes = await _repository.DailyUploadBytesAsync(userId, today, cancellationToken).ConfigureAwait(false);
        RuntimeResourcePolicy quota = _runtimePolicy.Current().Resource;

        return new AdminUserDetailDto
        {
            User = user,
            Account = account,
            Counts = counts,
            StorageUsage = usage,
            StoredFileBytes = storedFileBytes,
            DailyUploadBytes = dailyUploadBytes,
            Quota = quota,
        };
    }

    /// <summary>分页归一化。对应 Go: <c>normalizeAdminPage</c>。</summary>
    public static (int Page, int Limit) NormalizePage(int page, int limit)
    {
        if (page <= 0)
        {
            page = 1;
        }

        if (limit is <= 0 or > 100)
        {
            limit = 20;
        }

        return (page, limit);
    }

    /// <summary>去重并去掉空串，保持首次出现顺序。对应 Go: <c>uniqueNonEmpty</c>。</summary>
    private static List<string> UniqueNonEmpty(IEnumerable<string> values)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> result = [];
        foreach (string value in values)
        {
            if (value.Length > 0 && seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>追加审计事件。对应 Go: <c>appendAdminAudit</c>。</summary>
    private async Task AppendAuditAsync(
        User actor,
        string action,
        string targetType,
        string targetId,
        string summary,
        object? metadata,
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
            MetadataJSON = metadata is null ? string.Empty : JsonSerializer.Serialize(metadata),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
    }
}
