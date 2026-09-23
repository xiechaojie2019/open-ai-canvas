using OpenAICanvas.Application;

namespace OpenAICanvas.Web.Workers;

public sealed class VideoPlaybackWorker(
    VideoPlaybackService playback,
    ILogger<VideoPlaybackWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await playback.BackfillAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "video playback backfill worker stopped unexpectedly"); }
    }
}
