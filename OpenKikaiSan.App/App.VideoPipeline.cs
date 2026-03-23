using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Services;
using OpenKikaiSan.App.Utils;

namespace OpenKikaiSan.App;

public partial class App
{
    private async Task VideoDecodeLoopAsync(CancellationToken token, AppLogger logger)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                lock (_runtimeStateLock)
                {
                    _latestVideoStatus =
                        _windowCaptureService?.GetStatusText() ?? WaitingVideoStatus;
                }

                MaybeLogCaptureRenderStats(logger);
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Video capture loop failed.", ex);
                try
                {
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void MaybeLogCaptureRenderStats(AppLogger logger)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_runtimeStateLock)
        {
            if (
                _lastVideoPipelineLogAt != DateTimeOffset.MinValue
                && (now - _lastVideoPipelineLogAt).TotalSeconds < 2
            )
            {
                return;
            }

            _lastVideoPipelineLogAt = now;
        }

        var renderStats = _openXrControllerInputService?.GetVideoRenderStatsSnapshot();
        var captureStatus = _windowCaptureService?.GetStatusText() ?? "Capture: unavailable";
        logger.Debug(
            "Capture pipeline stats: "
                + $"status={captureStatus} "
                + $"renSeq={(renderStats?.LastRenderedSequence ?? 0)} "
                + $"renAgeRxMs={(renderStats?.LastRenderedAgeFromReceiveMs ?? 0)} "
                + $"renAgeDecMs={(renderStats?.LastRenderedAgeFromDecodeMs ?? 0)} "
                + $"renFail={(renderStats?.LastUploadFailureCode ?? 0)}"
        );
    }

    private void MaybeLogVideoLoopStall(AppLogger logger)
    {
        var renderStats = _openXrControllerInputService?.GetVideoRenderStatsSnapshot();
        if (renderStats is null)
        {
            return;
        }

        if (renderStats.Value.LastUploadFailureCode == 0)
        {
            return;
        }

        logger.Debug(
            "Capture render issue: "
                + $"renSeq={renderStats.Value.LastRenderedSequence} "
                + $"renFail={renderStats.Value.LastUploadFailureCode} "
                + $"status={_windowCaptureService?.GetStatusText() ?? "Capture: unavailable"}"
        );
    }

    private void ResetVideoPipelineMetrics()
    {
        lock (_runtimeStateLock)
        {
            _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
            _latestVideoStatus = WaitingVideoStatus;
        }
    }
}
