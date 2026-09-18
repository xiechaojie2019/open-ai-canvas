#nullable enable
using System.Globalization;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;

namespace OpenAICanvas.Application;

/// <summary>
/// 资源上传额度管理。上传额度在写文件或 OSS 前原子预留，避免并发请求同时通过日限额检查。
/// 对应 Go: <c>app/upload_quota.go</c>。
/// </summary>
public sealed class UploadQuota
{
    private const long Megabyte = 1 << 20;
    private const long Gigabyte = 1 << 30;

    private readonly Repository _repository;
    private readonly IRuntimePolicyProvider _policyProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>已预留但尚未落盘的字节数（进程内）。对应 Go 的 <c>Service.pendingStorage</c>。</summary>
    private readonly Dictionary<string, long> _pendingStorage = new(StringComparer.Ordinal);

    public UploadQuota(Repository repository, IRuntimePolicyProvider policyProvider)
    {
        _repository = repository;
        _policyProvider = policyProvider;
    }

    /// <summary>
    /// 预留普通上传额度：同时受单文件上限、当日上传上限、账号存储总量约束。
    /// 对应 Go: <c>reserveUserUploadQuota</c>。
    /// </summary>
    public Task<string> ReserveUserUploadQuotaAsync(string userId, long size, CancellationToken cancellationToken = default)
    {
        RuntimeResourcePolicy resource = _policyProvider.Current().Resource;
        return ReserveStoredFileQuotaAsync(
            userId,
            size,
            Megabyte * resource.ResourceUploadMB,
            Megabyte * resource.DailyUploadMB,
            Gigabyte * resource.StoredFileGB,
            $"单个上传文件必须小于 {resource.ResourceUploadMB}MB",
            cancellationToken);
    }

    /// <summary>
    /// 预留分片上传额度：单文件上限由分片会话逐片校验，此处仅受当日上传与账号存储总量约束。
    /// singleFileLimit 传 size+1 使单文件上限永不命中。
    /// 对应 Go: <c>reserveChunkedUploadQuota</c>。
    /// </summary>
    public Task<string> ReserveChunkedUploadQuotaAsync(string userId, long size, CancellationToken cancellationToken = default)
    {
        RuntimeResourcePolicy resource = _policyProvider.Current().Resource;
        return ReserveStoredFileQuotaAsync(
            userId,
            size,
            size + 1,
            Megabyte * resource.DailyUploadMB,
            Gigabyte * resource.StoredFileGB,
            string.Empty,
            cancellationToken);
    }

    /// <summary>
    /// 预留重试额度：失败资源记录已计入账号存储用量，重试只重新预留当日上传额度。
    /// 对应 Go: <c>reserveRetryUploadQuota</c>。
    /// </summary>
    public async Task<string> ReserveRetryUploadQuotaAsync(string userId, long size, CancellationToken cancellationToken = default)
    {
        RuntimeResourcePolicy resource = _policyProvider.Current().Resource;
        if (size <= 0)
        {
            throw AppError.BadAuthRequest("上传文件不能为空");
        }
        if (size >= Megabyte * resource.ResourceUploadMB)
        {
            throw AppError.BadAuthRequest($"单个上传文件必须小于 {resource.ResourceUploadMB}MB");
        }
        string day = CurrentDay();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReserveDailyAsync(userId, day, size, Megabyte * resource.DailyUploadMB, cancellationToken)
                .ConfigureAwait(false);
            return day;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>回退上传额度（含预留计数）。对应 Go: <c>releaseUserUploadQuota</c>。</summary>
    public async Task ReleaseUserUploadQuotaAsync(string userId, string day, long size, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(day) || size <= 0)
        {
            return;
        }
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DecreasePendingStorage(userId, size);
            try
            {
                await _repository.ReleaseDailyUploadAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Go 此处仅记录日志，不向上抛。
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>回退重试额度（不含预留计数）。对应 Go: <c>releaseRetryUploadQuota</c>。</summary>
    public async Task ReleaseRetryUploadQuotaAsync(string userId, string day, long size, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(day) || size <= 0)
        {
            return;
        }
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _repository.ReleaseDailyUploadAsync(userId, day, size, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Go 此处仅记录日志，不向上抛。
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>落盘成功后结清预留计数（当日额度保留）。对应 Go: <c>commitUserUploadQuota</c>。</summary>
    public async Task CommitUserUploadQuotaAsync(string userId, long size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            return;
        }
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DecreasePendingStorage(userId, size);
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>账号存储总量对外文案。对应 Go: <c>formatStorageLimit</c>。</summary>
    public static string FormatStorageLimit(long value) =>
        value % Gigabyte == 0
            ? $"{value / Gigabyte}GB"
            : $"{value / Megabyte}MB";

    private async Task<string> ReserveStoredFileQuotaAsync(
        string userId,
        long size,
        long exclusiveSingleFileLimit,
        long dailyLimit,
        long storedLimit,
        string singleFileMessage,
        CancellationToken cancellationToken)
    {
        if (size <= 0)
        {
            throw AppError.BadAuthRequest("上传文件不能为空");
        }
        if (size >= exclusiveSingleFileLimit)
        {
            throw AppError.BadAuthRequest(singleFileMessage);
        }
        string day = CurrentDay();
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long storedBytes = await _repository.UserStoredFileBytesAsync(userId, cancellationToken).ConfigureAwait(false);
            long pending = _pendingStorage.TryGetValue(userId, out long value) ? value : 0;
            if (storedBytes + pending + size >= storedLimit)
            {
                throw AppError.QuotaExceeded(
                    $"账号资源和会话附件已达到 {FormatStorageLimit(storedLimit)} 上限，请联系管理员清理历史文件");
            }
            _pendingStorage[userId] = pending + size;
            try
            {
                await ReserveDailyAsync(userId, day, size, dailyLimit, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                DecreasePendingStorage(userId, size);
                throw;
            }
            return day;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task ReserveDailyAsync(
        string userId, string day, long size, long dailyLimit, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.ReserveDailyUploadAsync(userId, day, size, dailyLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
            when (error.Message == Repository.DailyUploadLimitExceeded)
        {
            throw AppError.QuotaExceeded(
                $"每个账号 UTC 自然日上传总量必须小于 {FormatStorageLimit(dailyLimit)}");
        }
    }

    private void DecreasePendingStorage(string userId, long size)
    {
        long remaining = (_pendingStorage.TryGetValue(userId, out long value) ? value : 0) - size;
        if (remaining > 0)
        {
            _pendingStorage[userId] = remaining;
            return;
        }
        _pendingStorage.Remove(userId);
    }

    /// <summary>对应 Go 的 <c>time.Now().UTC().Format("2006-01-02")</c>。</summary>
    private static string CurrentDay() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
