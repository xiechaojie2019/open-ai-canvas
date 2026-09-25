#nullable enable
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAICanvas.Domain.Entities;
using OpenAICanvas.Domain.Kernel;
using OpenAICanvas.Outbound;
using OpenAICanvas.Persistence.Repositories;
using OpenAICanvas.Platform;
using TaskEntity = OpenAICanvas.Domain.Entities.Task;

namespace OpenAICanvas.Application;

/// <summary>时间线本地执行失败，消息已经过用户可见文案收敛。</summary>
public sealed class TimelineTaskException : AppError
{
    public TimelineTaskException(string message, Exception? cause = null)
        : base(500, message, cause: cause)
    {
    }
}

/// <summary>
/// 时间线转写/渲染 Worker 执行器。只使用本地资源、ffmpeg 和 loopback whisper.cpp；
/// 不进入模型渠道或云存储 provider。
/// </summary>
public sealed class TimelineTaskExecutor
{
    private const string WhisperBaseUrlEnvironment = "CANVAS_WHISPER_BASE_URL";
    private const string FfmpegPathEnvironment = "CANVAS_FFMPEG_PATH";
    private const int MaxWhisperResponseBytes = 8 << 20;
    private const int RenderWidth = 1920;
    private const int RenderHeight = 1080;
    private const int RenderFPS = 30;
    private const int RenderSampleRate = 44100;

    private readonly Repository _repository;
    private readonly ResourceDomainService _resources;
    private readonly ResourceUploadService _uploads;
    private readonly FeatureAvailabilityService? _features;

    public TimelineTaskExecutor(
        Repository repository,
        ResourceDomainService resources,
        ResourceUploadService uploads,
        FeatureAvailabilityService? features = null)
    {
        _repository = repository;
        _resources = resources;
        _uploads = uploads;
        _features = features;
    }

    /// <summary>执行一个已领取的时间线任务，返回写入 tasks.result_json 的载荷。</summary>
    public async Task<Dictionary<string, object?>> ExecuteAsync(
        TaskEntity task,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return task.Type switch
            {
                "timeline_transcription" => await ExecuteTranscriptionAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                "timeline_render" => await ExecuteRenderAsync(task, cancellationToken).ConfigureAwait(false),
                _ => throw new TimelineTaskException("任务类型没有可用的时间线执行分支"),
            };
        }
        catch (TimelineTaskException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimelineTaskException("时间线任务执行超时");
        }
    }

    private async Task<Dictionary<string, object?>> ExecuteTranscriptionAsync(
        TaskEntity task,
        CancellationToken cancellationToken)
    {
        string baseUrl = (Environment.GetEnvironmentVariable(WhisperBaseUrlEnvironment) ?? "").Trim();
        if (baseUrl.Length == 0)
        {
            throw new TimelineTaskException(
                "未配置本地转写服务：请设置 CANVAS_WHISPER_BASE_URL 指向 whisper.cpp 服务");
        }

        TimelineTranscriptionInput input = DeserializeInput<TimelineTranscriptionInput>(
            task.InputJSON, "任务缺少有效的转写输入");
        string resourceId = input.ResourceID.Trim();
        if (resourceId.Length == 0)
        {
            throw new TimelineTaskException("任务缺少有效的资源引用");
        }
        if (_features is not null)
        {
            await _features.RequireFeatureAsync(
                FeatureNames.TimelineTranscription, cancellationToken).ConfigureAwait(false);
        }

        Resource resource = await RequireLocalResourceAsync(
            task.UserID,
            resourceId,
            requireMedia: true,
            cancellationToken).ConfigureAwait(false);

        await UpdateProgressAsync(task, "等待转写服务", 15, cancellationToken).ConfigureAwait(false);
        using TemporaryDirectory temp = new("open-ai-canvas-whisper-");
        string inputPath = Path.Combine(temp.Path, "input" + ExtensionForMime(resource.MimeType));
        await CopyResourceToFileAsync(task.UserID, resource, inputPath, cancellationToken)
            .ConfigureAwait(false);
        string wavPath = Path.Combine(temp.Path, "audio16k.wav");
        await RunFfmpegAsync(
            [
                "-nostdin", "-y", "-i", inputPath, "-vn", "-ac", "1", "-ar", "16000",
                "-c:a", "pcm_s16le", wavPath,
            ],
            temp.Path,
            "音频预处理失败（ffmpeg）",
            cancellationToken).ConfigureAwait(false);

        await UpdateProgressAsync(task, "正在转写…", 40, cancellationToken).ConfigureAwait(false);
        (List<TimelineTranscriptionSegment> segments, string language) =
            await TranscribeAsync(baseUrl, wavPath, input.Language, cancellationToken).ConfigureAwait(false);
        await UpdateProgressAsync(task, "整理字幕…", 80, cancellationToken).ConfigureAwait(false);

        TimelineTranscriptionResult result = new()
        {
            Segments = segments,
            SRT = BuildTimelineSrt(segments),
            Language = language,
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["segments"] = result.Segments,
            ["srt"] = result.SRT,
            ["language"] = result.Language,
        };
    }

    private async Task<Dictionary<string, object?>> ExecuteRenderAsync(
        TaskEntity task,
        CancellationToken cancellationToken)
    {
        TimelineRenderInput input = DeserializeInput<TimelineRenderInput>(
            task.InputJSON, "任务缺少有效的时间线快照");
        RenderPlan plan = BuildRenderPlan(input.Timeline);
        if (!plan.HasMedia)
        {
            throw new TimelineTaskException("时间线没有可渲染的媒体片段");
        }

        await UpdateProgressAsync(task, "准备媒体…", 10, cancellationToken).ConfigureAwait(false);
        using TemporaryDirectory temp = new("open-ai-canvas-render-");
        await MaterializeRenderSourcesAsync(task.UserID, plan, temp.Path, cancellationToken)
            .ConfigureAwait(false);

        string ffprobe = ResolveSiblingTool("ffprobe");
        foreach (RenderSegment segment in plan.Segments)
        {
            if (segment.Source is null || segment.Kind == "image")
            {
                continue;
            }
            segment.Source.HasAudio = await ProbeHasAudioAsync(
                ffprobe, segment.Source.Path, temp.Path, cancellationToken).ConfigureAwait(false);
        }

        string outputPath = Path.Combine(temp.Path, "render-output.mp4");
        List<string> arguments = BuildRenderFfmpegArguments(plan, outputPath);
        if (arguments.Count == 0)
        {
            throw new TimelineTaskException("无法生成渲染命令");
        }
        await UpdateProgressAsync(task, "正在渲染…", 30, cancellationToken).ConfigureAwait(false);
        await RunFfmpegAsync(arguments, temp.Path, "ffmpeg 渲染失败", cancellationToken)
            .ConfigureAwait(false);

        await UpdateProgressAsync(task, "写入资源…", 85, cancellationToken).ConfigureAwait(false);
        FileInfo output = new(outputPath);
        if (!output.Exists || output.Length <= 0)
        {
            throw new TimelineTaskException("渲染产物为空");
        }

        long durationMs = PlanDurationMs(plan);
        string fileName = "timeline-render-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".mp4";
        Resource rendered;
        await using (FileStream outputStream = File.OpenRead(outputPath))
        {
            try
            {
                rendered = await _uploads.UploadGeneratedResourceFromStreamAsync(
                    task.UserID,
                    fileName,
                    output.Length,
                    "video",
                    RenderWidth,
                    RenderHeight,
                    durationMs,
                    outputStream,
                    "video/mp4",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AppError)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new TimelineTaskException("保存渲染产物失败", error);
            }
        }

        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["resourceId"] = rendered.ID,
            ["fileName"] = fileName,
            ["size"] = output.Length,
            ["durationMs"] = durationMs,
        };
        if (plan.SubtitleSRT.Length > 0)
        {
            result["subtitleSrt"] = plan.SubtitleSRT;
        }
        return result;
    }

    private async Task<Resource> RequireLocalResourceAsync(
        string userId,
        string resourceId,
        bool requireMedia,
        CancellationToken cancellationToken)
    {
        Resource? resource = await _resources.GetResourceAsync(userId, resourceId, cancellationToken)
            .ConfigureAwait(false);
        if (resource is null)
        {
            throw new TimelineTaskException("无法读取时间线引用的媒体，可能已被删除");
        }
        if (resource.Status != ResourceStatus.ResourceStatusReady)
        {
            throw new TimelineTaskException("时间线引用的资源尚未上传完成");
        }
        if (!string.IsNullOrWhiteSpace(resource.Provider)
            && !string.Equals(resource.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new TimelineTaskException("时间线本地执行暂不支持云存储资源");
        }
        if (requireMedia && !IsMediaMime(resource.MimeType))
        {
            throw new TimelineTaskException("仅支持音视频文件转写");
        }
        return resource;
    }

    private async Task CopyResourceToFileAsync(
        string userId,
        Resource resource,
        string destination,
        CancellationToken cancellationToken)
    {
        ResourceStream stream;
        try
        {
            stream = await _resources.OpenResourceRangeAsync(
                userId, resource.ID, null, cancellationToken).ConfigureAwait(false);
        }
        catch (TimelineTaskException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new TimelineTaskException("无法读取时间线引用的媒体，可能已被删除", error);
        }

        await using Stream source = stream.Body;
        await using FileStream output = new(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaterializeRenderSourcesAsync(
        string userId,
        RenderPlan plan,
        string directory,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> cache = new(StringComparer.Ordinal);
        foreach (RenderSegment segment in plan.Segments)
        {
            if (segment.ResourceID.Length == 0)
            {
                continue;
            }
            if (!cache.TryGetValue(segment.ResourceID, out string? path))
            {
                Resource resource = await RequireLocalResourceAsync(
                    userId, segment.ResourceID, requireMedia: false, cancellationToken).ConfigureAwait(false);
                if (!IsMediaMime(resource.MimeType) && !resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TimelineTaskException("时间线引用的资源不是可渲染媒体");
                }
                path = Path.Combine(directory, "source-" + cache.Count.ToString(CultureInfo.InvariantCulture)
                    + ExtensionForMime(resource.MimeType));
                await CopyResourceToFileAsync(userId, resource, path, cancellationToken).ConfigureAwait(false);
                cache[segment.ResourceID] = path;
            }
            segment.Source = new RenderSource
            {
                ResourceID = segment.ResourceID,
                Path = path,
                Clip = segment.Clip,
            };
        }
    }

    private async Task<(List<TimelineTranscriptionSegment> Segments, string Language)> TranscribeAsync(
        string rawBaseUrl,
        string wavPath,
        string language,
        CancellationToken cancellationToken)
    {
        Uri baseUri = await OutboundGuard.ValidateLoopbackUrlAsync(rawBaseUrl).ConfigureAwait(false);
        Uri endpoint = new(baseUri.AbsoluteUri.TrimEnd('/') + "/inference");
        using HttpClient client = OutboundHttpClient.Create(
            TimeSpan.FromMinutes(20), allowAutoRedirect: false, useProxy: false, allowLoopbackOnly: true);
        using MultipartFormDataContent form = new();
        await using FileStream audio = File.OpenRead(wavPath);
        StreamContent audioContent = new(audio);
        form.Add(audioContent, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent("verbose_json", Encoding.UTF8), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language.Trim(), Encoding.UTF8), "language");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(endpoint, form, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new TimelineTaskException("无法连接本地转写服务(whisper.cpp)", error);
        }
        using (response)
        {
            string payload = await ReadResponseTextAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TimelineTaskException(
                    $"本地转写服务返回 {(int)response.StatusCode}: {Clip(payload.Trim(), 2048)}");
            }
            WhisperVerboseResponse parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<WhisperVerboseResponse>(
                    payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new JsonException("empty response");
            }
            catch (JsonException error)
            {
                throw new TimelineTaskException("转写结果解析失败", error);
            }

            List<TimelineTranscriptionSegment> segments = [];
            foreach (WhisperSegment segment in parsed.Segments ?? [])
            {
                string text = segment.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                segments.Add(new TimelineTranscriptionSegment
                {
                    StartMs = (long)Math.Round(segment.Start * 1000, MidpointRounding.AwayFromZero),
                    EndMs = (long)Math.Round(segment.End * 1000, MidpointRounding.AwayFromZero),
                    Text = text,
                });
            }
            if (segments.Count == 0)
            {
                throw new TimelineTaskException("本地转写服务未识别出语音内容");
            }
            return (segments, parsed.Language ?? "");
        }
    }

    private static async Task<string> ReadResponseTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaxWhisperResponseBytes)
            {
                throw new TimelineTaskException("本地转写服务返回结果过大");
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        string ffmpeg = ResolveFfmpegBinary();
        ProcessResult result = await RunProcessAsync(
            ffmpeg, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string detail = Clip(result.StandardError.Trim(), 400);
            throw new TimelineTaskException(
                detail.Length == 0 ? failurePrefix : failurePrefix + "：" + detail);
        }
    }

    private static async Task<bool> ProbeHasAudioAsync(
        string ffprobe,
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            ffprobe,
            [
                "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index",
                "-of", "csv=p=0", path,
            ],
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动本地媒体工具");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            throw new TimelineTaskException("本地媒体工具未安装或路径无效", error);
        }

        using (process)
        using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程可能已经退出。
            }
        }))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
        }
    }

    private async Task UpdateProgressAsync(
        TaskEntity task,
        string stage,
        long progress,
        CancellationToken cancellationToken) =>
        await _repository.UpdateTaskProgressForLeaseAsync(
            task.ID, task.LeaseOwner, stage, progress, cancellationToken).ConfigureAwait(false);

    private static T DeserializeInput<T>(string raw, string message)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new JsonException("empty input");
        }
        catch (JsonException error)
        {
            throw new TimelineTaskException(message, error);
        }
    }

    private static string ResolveFfmpegBinary()
    {
        string configured = (Environment.GetEnvironmentVariable(FfmpegPathEnvironment) ?? "").Trim();
        return configured.Length == 0 ? "ffmpeg" : configured;
    }

    private static string ResolveSiblingTool(string tool)
    {
        string configured = (Environment.GetEnvironmentVariable(FfmpegPathEnvironment) ?? "").Trim();
        if (configured.Length == 0)
        {
            return tool;
        }
        string? directory = Path.GetDirectoryName(configured);
        return string.IsNullOrWhiteSpace(directory)
            ? tool
            : Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
    }

    private static bool IsMediaMime(string mime) =>
        mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static string ExtensionForMime(string mime) => mime.Trim().ToLowerInvariant() switch
    {
        "video/mp4" or "audio/mp4" => ".mp4",
        "video/webm" or "audio/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/flac" => ".flac",
        "audio/aac" => ".aac",
        "audio/ogg" or "video/ogg" => ".ogg",
        _ => ".bin",
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[^max..];

    private static string FormatSrtTimestamp(long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
        long hours = milliseconds / 3_600_000;
        long minutes = milliseconds % 3_600_000 / 60_000;
        long seconds = milliseconds % 60_000 / 1_000;
        long millis = milliseconds % 1_000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00},{millis:000}");
    }

    private static string BuildTimelineSrt(IReadOnlyList<TimelineTranscriptionSegment> segments)
    {
        StringBuilder output = new();
        for (int index = 0; index < segments.Count; index++)
        {
            TimelineTranscriptionSegment segment = segments[index];
            output.Append(index + 1).Append('\n')
                .Append(FormatSrtTimestamp(segment.StartMs)).Append(" --> ")
                .Append(FormatSrtTimestamp(segment.EndMs)).Append('\n')
                .Append(segment.Text).Append("\n\n");
        }
        return output.ToString();
    }

    private static RenderPlan BuildRenderPlan(RenderProject project)
    {
        Dictionary<string, bool> visible = new(StringComparer.Ordinal);
        foreach (RenderTrack track in project.Tracks ?? [])
        {
            visible[track.ID] = track.Visible is not false;
        }

        List<RenderClip> clips = (project.Clips ?? [])
            .Where(clip => visible.TryGetValue(clip.TrackID, out bool isVisible) && isVisible)
            .Where(clip => clip.Kind is "video" or "image")
            .Where(clip => clip.DurationMs > 0)
            .OrderBy(clip => clip.StartMs)
            .ThenBy(clip => clip.ID, StringComparer.Ordinal)
            .ToList();

        RenderPlan plan = new()
        {
            SubtitleSRT = BuildRenderSubtitleSrt(project),
        };
        long cursor = 0;
        foreach (RenderClip clip in clips)
        {
            long gap = clip.StartMs - cursor;
            if (gap > 0)
            {
                plan.Segments.Add(new RenderSegment { Kind = "gap", DurationMs = gap, GapMs = gap });
            }
            string resourceId = ResourceIDFromClip(clip);
            plan.Segments.Add(new RenderSegment
            {
                Kind = clip.Kind,
                DurationMs = clip.DurationMs,
                Clip = clip,
                ResourceID = resourceId,
            });
            if (resourceId.Length > 0)
            {
                plan.HasMedia = true;
            }
            cursor = clip.StartMs + clip.DurationMs;
        }
        return plan;
    }

    private static string BuildRenderSubtitleSrt(RenderProject project)
    {
        List<RenderClip> subtitles = (project.Clips ?? [])
            .Where(clip => clip.Kind == "subtitle" && clip.DurationMs > 0 && clip.Text.Trim().Length > 0)
            .OrderBy(clip => clip.StartMs)
            .ThenBy(clip => clip.ID, StringComparer.Ordinal)
            .ToList();
        StringBuilder output = new();
        for (int index = 0; index < subtitles.Count; index++)
        {
            RenderClip clip = subtitles[index];
            output.Append(index + 1).Append('\n')
                .Append(FormatSrtTimestamp(clip.StartMs)).Append(" --> ")
                .Append(FormatSrtTimestamp(clip.StartMs + clip.DurationMs)).Append('\n')
                .Append(clip.Text.Trim()).Append("\n\n");
        }
        return output.ToString();
    }

    private static string ResourceIDFromClip(RenderClip clip)
    {
        string key = (clip.DirectMedia?.StorageKey ?? "").Trim();
        return key.StartsWith("resource:", StringComparison.Ordinal)
            ? key["resource:".Length..].Trim()
            : "";
    }

    private static List<string> BuildRenderFfmpegArguments(RenderPlan plan, string target)
    {
        if (plan.Segments.Count == 0)
        {
            return [];
        }
        string scale = string.Create(
            CultureInfo.InvariantCulture,
            $"scale={RenderWidth}:{RenderHeight}:force_original_aspect_ratio=decrease,pad={RenderWidth}:{RenderHeight}:(ow-iw)/2:(oh-ih)/2");
        List<string> args = ["-nostdin", "-y"];
        foreach (RenderSegment segment in plan.Segments)
        {
            string seconds = (segment.DurationMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture);
            switch (segment.Kind)
            {
                case "gap":
                    args.AddRange([
                        "-f", "lavfi", "-t", seconds,
                        "-i", $"color=c=black:s={RenderWidth}x{RenderHeight}:r={RenderFPS}",
                        "-f", "lavfi", "-t", seconds,
                        "-i", $"anullsrc=r={RenderSampleRate}:cl=stereo",
                    ]);
                    break;
                case "image":
                    if (segment.Source is null)
                    {
                        AddBlackSegment(args, seconds);
                    }
                    else
                    {
                        args.AddRange([
                            "-loop", "1", "-t", seconds, "-i", segment.Source.Path,
                            "-f", "lavfi", "-t", seconds,
                            "-i", $"anullsrc=r={RenderSampleRate}:cl=stereo",
                        ]);
                    }
                    break;
                default:
                    if (segment.Source is null)
                    {
                        AddBlackSegment(args, seconds);
                        break;
                    }
                    if (segment.Clip.SourceStartMs > 0)
                    {
                        args.AddRange(["-ss", (segment.Clip.SourceStartMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture)]);
                    }
                    args.AddRange(["-t", seconds, "-i", segment.Source.Path]);
                    if (segment.Clip.SourceStartMs > 0)
                    {
                        args.AddRange(["-ss", (segment.Clip.SourceStartMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture)]);
                    }
                    args.AddRange(["-t", seconds, "-i", segment.Source.Path]);
                    break;
            }
        }

        List<string> filters = [];
        List<string> labels = [];
        for (int index = 0; index < plan.Segments.Count; index++)
        {
            RenderSegment segment = plan.Segments[index];
            int videoInput = 2 * index;
            int audioInput = 2 * index + 1;
            string videoLabel = "v" + index.ToString(CultureInfo.InvariantCulture);
            string audioLabel = "a" + index.ToString(CultureInfo.InvariantCulture);
            string duration = (segment.DurationMs / 1000d).ToString("0.000", CultureInfo.InvariantCulture);
            if (segment.Source is not null && segment.Kind != "image" && !segment.Source.HasAudio)
            {
                filters.Add($"[{videoInput}:v]fps={RenderFPS},{scale},setsar=1[{videoLabel}]");
                string silentLabel = "silent" + index.ToString(CultureInfo.InvariantCulture);
                filters.Add($"anullsrc=r={RenderSampleRate}:cl=stereo[{silentLabel}]");
                filters.Add($"[{silentLabel}]atrim=0:{duration},asetpts=N/SR/TB[{audioLabel}]");
            }
            else
            {
                double volume = segment.Clip.Volume > 0 ? segment.Clip.Volume : 1.0;
                filters.Add($"[{videoInput}:v]fps={RenderFPS},{scale},setsar=1[{videoLabel}]");
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{audioInput}:a]aformat=sample_fmts=fltp:sample_rates={RenderSampleRate}:channel_layouts=stereo,volume={volume:0.000},apad[{audioLabel}]"));
            }
            labels.Add($"[{videoLabel}]");
            labels.Add($"[{audioLabel}]");
        }
        filters.Add(string.Join(string.Empty, labels) + $"concat=n={plan.Segments.Count}:v=1:a=1[vout][aout]");
        args.AddRange([
            "-filter_complex", string.Join(";", filters),
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart",
            "-t", (PlanDurationMs(plan) / 1000d).ToString("0.000", CultureInfo.InvariantCulture), target,
        ]);
        return args;
    }

    private static void AddBlackSegment(List<string> args, string seconds)
    {
        args.AddRange([
            "-f", "lavfi", "-t", seconds,
            "-i", $"color=c=black:s={RenderWidth}x{RenderHeight}:r={RenderFPS}",
            "-f", "lavfi", "-t", seconds,
            "-i", $"anullsrc=r={RenderSampleRate}:cl=stereo",
        ]);
    }

    private static long PlanDurationMs(RenderPlan plan) => plan.Segments.Sum(segment => segment.DurationMs);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不覆盖任务终态。
            }
        }
    }

    private sealed class TimelineTranscriptionInput
    {
        [JsonPropertyName("resourceId")]
        public string ResourceID { get; set; } = "";

        [JsonPropertyName("language")]
        public string Language { get; set; } = "";
    }

    private sealed class TimelineTranscriptionSegment
    {
        [JsonPropertyName("startMs")]
        public long StartMs { get; set; }

        [JsonPropertyName("endMs")]
        public long EndMs { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    private sealed class TimelineTranscriptionResult
    {
        [JsonPropertyName("segments")]
        public List<TimelineTranscriptionSegment> Segments { get; set; } = [];

        [JsonPropertyName("srt")]
        public string SRT { get; set; } = "";

        [JsonPropertyName("language")]
        public string Language { get; set; } = "";
    }

    private sealed class WhisperVerboseResponse
    {
        [JsonPropertyName("segments")]
        public List<WhisperSegment>? Segments { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }

    private sealed class WhisperSegment
    {
        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    private sealed class TimelineRenderInput
    {
        [JsonPropertyName("projectId")]
        public string ProjectID { get; set; } = "";

        [JsonPropertyName("timeline")]
        public RenderProject Timeline { get; set; } = new();
    }

    private sealed class RenderProject
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("tracks")]
        public List<RenderTrack>? Tracks { get; set; }

        [JsonPropertyName("clips")]
        public List<RenderClip>? Clips { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }
    }

    private sealed class RenderTrack
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("visible")]
        public bool? Visible { get; set; }
    }

    private sealed class RenderClip
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("trackId")]
        public string TrackID { get; set; } = "";

        [JsonPropertyName("startMs")]
        public long StartMs { get; set; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("sourceStartMs")]
        public long SourceStartMs { get; set; }

        [JsonPropertyName("volume")]
        public double Volume { get; set; }

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("directMedia")]
        public DirectMedia? DirectMedia { get; set; }
    }

    private sealed class DirectMedia
    {
        [JsonPropertyName("storageKey")]
        public string StorageKey { get; set; } = "";
    }

    private sealed class RenderPlan
    {
        public List<RenderSegment> Segments { get; } = [];
        public string SubtitleSRT { get; set; } = "";
        public bool HasMedia { get; set; }
    }

    private sealed class RenderSegment
    {
        public string Kind { get; set; } = "";
        public long DurationMs { get; set; }
        public long GapMs { get; set; }
        public RenderClip Clip { get; set; } = new();
        public string ResourceID { get; set; } = "";
        public RenderSource? Source { get; set; }
    }

    private sealed class RenderSource
    {
        public string ResourceID { get; set; } = "";
        public string Path { get; set; } = "";
        public bool HasAudio { get; set; }
        public RenderClip Clip { get; set; } = new();
    }
}

