#nullable enable
using System.Diagnostics;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Persistence.Repositories;

namespace OpenAICanvas.Application;

/// <summary>本地视频浏览器兼容播放副本：H.265/MPEG-4 转 H.264/AAC。</summary>
public sealed class VideoPlaybackService
{
    public const string None = "none";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Failed = "failed";

    private readonly Repository _repository;
    private readonly string _dataDir;

    public VideoPlaybackService(Repository repository, string dataDir)
    {
        _repository = repository;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? "data" : dataDir;
    }

    public async Task ScheduleAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        if (!IsLocalVideo(resource) || !File.Exists(SourcePath(resource))) return;
        string? codec = await ProbeCodecAsync(SourcePath(resource), cancellationToken).ConfigureAwait(false);
        if (codec is null || !IsTranscodeRequired(codec))
        {
            if (resource.PlaybackStatus != None) { resource.PlaybackStatus = None; resource.UpdatedAt = DateTime.UtcNow; await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false); }
            return;
        }
        if (!await HasToolAsync("ffmpeg", cancellationToken).ConfigureAwait(false))
        {
            resource.PlaybackStatus = None; resource.UpdatedAt = DateTime.UtcNow; await _repository.SaveResourceAsync(resource, cancellationToken).ConfigureAwait(false); return;
        }
        if (!await _repository.ClaimPlaybackTranscodeAsync(resource.ID, cancellationToken).ConfigureAwait(false)) return;
        _ = Task.Run(() => RunAsync(resource.UserID, resource.ID), CancellationToken.None);
    }

    public async Task BackfillAsync(CancellationToken cancellationToken = default)
    {
        await _repository.ResetStuckPlaybackTranscodesAsync(cancellationToken).ConfigureAwait(false);
        foreach (Resource resource in await _repository.PlaybackPendingVideosAsync(100, cancellationToken).ConfigureAwait(false)) await ScheduleAsync(resource, cancellationToken).ConfigureAwait(false);
        foreach (Resource resource in await _repository.PlaybackNoneVideosAsync(20, cancellationToken).ConfigureAwait(false)) await ScheduleAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    public Task<ResourceStream?> OpenPlaybackAsync(Resource resource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resource.Provider != "local" || resource.PlaybackStatus != Ready || string.IsNullOrWhiteSpace(resource.PlaybackObjectKey)) return Task.FromResult<ResourceStream?>(null);
        string path = SafePath(Path.Combine(_dataDir, "resources"), resource.PlaybackObjectKey);
        if (!File.Exists(path)) return Task.FromResult<ResourceStream?>(null);
        FileStream stream = File.OpenRead(path);
        Resource playback = new()
        {
            ID = resource.ID,
            UserID = resource.UserID,
            Kind = resource.Kind,
            Status = resource.Status,
            Provider = resource.Provider,
            ObjectKey = resource.PlaybackObjectKey,
            MimeType = "video/mp4",
            Size = stream.Length,
            ETag = resource.ETag + ":pb",
            CreatedAt = resource.CreatedAt,
            UpdatedAt = resource.UpdatedAt,
        };
        return Task.FromResult<ResourceStream?>(new ResourceStream(playback, stream, "", "bytes") { ContentLength = stream.Length });
    }

    private async Task RunAsync(string userId, string resourceId)
    {
        Resource? resource = await _repository.ResourceForUserAsync(userId, resourceId).ConfigureAwait(false);
        if (resource is null) return;
        string key = "playback/" + resource.ID + ".mp4"; string destination = SafePath(Path.Combine(_dataDir, "resources"), key); string error = string.Empty; string state = Failed;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ProcessStartInfo psi = new("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", SourcePath(resource), "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", destination }) psi.ArgumentList.Add(arg);
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg"); string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false); await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg 转码失败：" + Clip(stderr, 1000));
            state = Ready;
        }
        catch (Exception ex) { error = Clip(ex.Message, 1000); }
        resource.PlaybackStatus = state; resource.PlaybackObjectKey = state == Ready ? key : string.Empty; resource.PlaybackError = error; resource.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveResourceAsync(resource).ConfigureAwait(false);
    }

    private string SourcePath(Resource resource) => SafePath(Path.Combine(_dataDir, "resources"), resource.ObjectKey);
    private static bool IsLocalVideo(Resource r) => r.Kind == "video" && r.Status == ResourceStatus.ResourceStatusReady && r.Provider == "local";
    private static bool IsTranscodeRequired(string codec) => codec is "hevc" or "h265" or "mpeg4";
    private static string SafePath(string root, string key) { string fullRoot = Path.GetFullPath(root); string full = Path.GetFullPath(Path.Combine(fullRoot, key.Replace('/', Path.DirectorySeparatorChar))); if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("资源路径无效"); return full; }
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task<string?> ProbeCodecAsync(string path, CancellationToken cancellationToken)
    {
        if (!await HasToolAsync("ffprobe", cancellationToken).ConfigureAwait(false)) return null;
        ProcessStartInfo psi = new("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=nw=1:nk=1", path }) psi.ArgumentList.Add(arg);
        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffprobe"); string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false); await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); return process.ExitCode == 0 ? output.Trim().ToLowerInvariant() : null;
    }

    private static async Task<bool> HasToolAsync(string tool, CancellationToken cancellationToken)
    {
        try { using Process process = Process.Start(new ProcessStartInfo(tool) { ArgumentList = { "-version" }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!; await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); return process.ExitCode == 0; } catch { return false; }
    }
}
