#nullable enable
using OpenAICanvas.Auth;
using OpenAICanvas.Domain.Entities;

namespace OpenAICanvas.Application;

/// <summary>
/// 认证模块的宿主桥接。对应 Go: <c>internal/app/auth_bridge.go: authHost</c>。
/// </summary>
/// <remarks>
/// 用延迟绑定打破 <c>AuthService ←→ CanvasService</c> 的构造循环：
/// 组合根先构造本对象并交给 <see cref="AuthService"/>，再由 <see cref="CanvasService"/>
/// 构造完成后回调 <see cref="Attach"/> 补上引用（与 Go 的 <c>authHost{svc: service}</c> 等价）。
/// </remarks>
public sealed class CanvasAuthHost : IAuthHost
{
    private CanvasService? _service;

    /// <summary>由 <see cref="CanvasService"/> 在构造末尾回调。</summary>
    public void Attach(CanvasService service) => _service = service;

    /// <summary>管理员校验。直接复用服务上的静态校验。</summary>
    public void RequireAdmin(User actor) => CanvasService.RequireAdmin(actor);

    /// <summary>
    /// 设置加密密钥，用于验证码 HMAC。
    /// 当前加密为占位实现（见 PENDING-CONFIRMATIONS 第 1 条），返回 null 与 Go 的 nopHost 一致。
    /// </summary>
    public byte[]? SettingsEncryptionKey() => null;

    /// <summary>品牌名，用于邮件正文。外观设置未移植前返回默认品牌名。</summary>
    public string BrandName() => _service?.BrandName ?? NullAuthHost.DefaultBrandName;

    /// <summary>确保注册奖励已发放。对应 Go: <c>authHost.EnsureSignupBonus</c>。</summary>
    public Task EnsureSignupBonusAsync(string userId, CancellationToken cancellationToken = default) =>
        _service?.EnsureSignupBonusAsync(userId, cancellationToken) ?? Task.CompletedTask;

    /// <summary>记录用户活跃事件。对应 Go: <c>authHost.RecordActivity</c>。</summary>
    public void RecordActivity(string userId, string @event, int count) =>
        _service?.RecordActivity(userId, @event, count);
}
