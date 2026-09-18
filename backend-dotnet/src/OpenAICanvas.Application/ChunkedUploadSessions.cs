#nullable enable
using System.Security.Cryptography;
using OpenAICanvas.Domain.Kernel;

namespace OpenAICanvas.Application;

/// <summary>
/// 分片上传会话：把"导入本地媒体"拆成 开始 → 逐片 → 合并 三段，单片上限 8MB，
/// 文件整体不再受 multipart 单请求大小限制。
/// 对应 Go: <c>handler/resource_upload_session.go</c> 的会话状态部分。
/// </summary>
/// <remarks>
/// 会话状态只存在内存（重启即失效 → 前端整传重试），磁盘暂存在系统临时目录，随会话清理。
/// 当前为单实例内存实现，多实例共享见 PENDING-CONFIRMATIONS.md。
/// </remarks>
public sealed class ChunkedUploadSessions
{
    /// <summary>单片大小。对应 Go: <c>chunkUploadChunkSize</c>。</summary>
    public const long ChunkSize = 8L << 20;

    /// <summary>MaxBytesReader 允许的超片余量。对应 Go: <c>chunkUploadSlackBytes</c>。</summary>
    public const long ChunkSlackBytes = 64L << 10;

    /// <summary>会话存活时长。对应 Go: <c>chunkUploadTTL</c>。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(90);

    /// <summary>单用户并发会话上限。对应 Go: <c>chunkUploadMaxPerUser</c>。</summary>
    public const int MaxPerUser = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, ChunkedUploadSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>创建会话并返回它。调用方负责检查并发上限与文件大小上限。</summary>
    public ChunkedUploadSession Create(
        string userId,
        string fileName,
        string kind,
        long size,
        int width,
        int height,
        long durationMs,
        string? idempotencyKey)
    {
        RemoveExpired();
        string dir = Path.Combine(Path.GetTempPath(), "canvas-chunk-upload-" + IdGenerator.NewId());
        Directory.CreateDirectory(dir);
        ChunkedUploadSession session = new()
        {
            ID = NewSessionID(),
            UserID = userId,
            FileName = fileName,
            Kind = kind,
            Size = size,
            Width = width,
            Height = height,
            DurationMs = durationMs,
            IdempotencyKey = idempotencyKey,
            ChunkCount = (int)((size + ChunkSize - 1) / ChunkSize),
            Dir = dir,
            CreatedAt = DateTime.UtcNow,
        };
        lock (_gate)
        {
            _sessions[session.ID] = session;
        }
        return session;
    }

    /// <summary>统计该用户当前进行中的会话数（用于并发上限兜底）。</summary>
    public int CountForUser(string userId)
    {
        lock (_gate)
        {
            int active = 0;
            foreach (ChunkedUploadSession session in _sessions.Values)
            {
                if (session.UserID == userId)
                {
                    active++;
                }
            }
            return active;
        }
    }

    /// <summary>按 ID 取会话（顺带清理过期会话）。未命中返回 null。对应 Go: <c>takeChunkSession</c>。</summary>
    public ChunkedUploadSession? Take(string id)
    {
        RemoveExpired();
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out ChunkedUploadSession? session) ? session : null;
        }
    }

    /// <summary>丢弃会话并清理临时目录。对应 Go: <c>dropChunkSession</c>。</summary>
    public void Drop(string id)
    {
        ChunkedUploadSession? session = null;
        lock (_gate)
        {
            if (_sessions.Remove(id, out ChunkedUploadSession? found))
            {
                session = found;
            }
        }
        if (session is not null)
        {
            TryRemoveDirectory(session.Dir);
        }
    }

    private void RemoveExpired()
    {
        DateTime now = DateTime.UtcNow;
        List<ChunkedUploadSession> expired = new();
        lock (_gate)
        {
            foreach ((string id, ChunkedUploadSession session) in _sessions)
            {
                if (now - session.CreatedAt > Ttl)
                {
                    expired.Add(session);
                    _sessions.Remove(id);
                }
            }
        }
        foreach (ChunkedUploadSession session in expired)
        {
            TryRemoveDirectory(session.Dir);
        }
    }

    private static void TryRemoveDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // Go 此处忽略清理失败。
        }
    }

    /// <summary>对应 Go: <c>newUploadSessionID</c>（12 字节随机数的十六进制）。</summary>
    private static string NewSessionID()
    {
        Span<byte> raw = stackalloc byte[12];
        try
        {
            RandomNumberGenerator.Fill(raw);
        }
        catch (CryptographicException)
        {
            return DateTime.UtcNow.Ticks.ToString();
        }
        return Convert.ToHexString(raw).ToLowerInvariant();
    }
}

/// <summary>单个分片上传会话。对应 Go: <c>chunkedUploadSession</c>。</summary>
public sealed class ChunkedUploadSession
{
    public required string ID { get; init; }
    public required string UserID { get; init; }
    public required string FileName { get; init; }
    public required string Kind { get; init; }
    public required long Size { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long DurationMs { get; init; }
    public required string? IdempotencyKey { get; init; }
    public required int ChunkCount { get; init; }
    public required string Dir { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>分片落盘路径。对应 Go: <c>chunkPath</c>。</summary>
    public string ChunkPath(int index) => Path.Combine(Dir, $"chunk-{index}");

    /// <summary>合并结果文件路径。对应 Go 的 <c>filepath.Join(session.Dir, "merged")</c>。</summary>
    public string MergedPath() => Path.Combine(Dir, "merged");

    /// <summary>
    /// 第 index 片的期望字节数（末片按文件余量，其余固定 chunkSize）。
    /// 对应 Go: <c>chunkSizeAt</c>。
    /// </summary>
    public long ChunkSizeAt(int index)
    {
        if (index == ChunkCount - 1)
        {
            long rest = Size - (index * ChunkedUploadSessions.ChunkSize);
            return rest < 0 ? 0 : rest;
        }
        return ChunkedUploadSessions.ChunkSize;
    }

    /// <summary>是否所有分片都已按期望长度落盘。对应 Go: <c>hasAllChunks</c>。</summary>
    public bool HasAllChunks()
    {
        for (int i = 0; i < ChunkCount; i++)
        {
            FileInfo info = new(ChunkPath(i));
            if (!info.Exists || info.Length != ChunkSizeAt(i))
            {
                return false;
            }
        }
        return true;
    }
}
