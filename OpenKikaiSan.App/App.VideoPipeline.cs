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

    private void ResetVideoPipelineMetrics()
    {
        lock (_runtimeStateLock)
        {
            _latestVideoStatus = WaitingVideoStatus;
        }
    }
}
