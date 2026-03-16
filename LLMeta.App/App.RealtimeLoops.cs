using LLMeta.App.Models;
using LLMeta.App.Utils;

namespace LLMeta.App;

public partial class App
{
    private void StartRealtimeLoops(AppLogger logger)
    {
        StopRealtimeLoops();
        _realtimeLoopCts = new CancellationTokenSource();
        var token = _realtimeLoopCts.Token;
        _openXrLoopTask = Task.Run(() => OpenXrLoopAsync(token, logger), token);
        _videoDecodeLoopTask = Task.Run(() => VideoDecodeLoopAsync(token, logger), token);
    }

    private void StopRealtimeLoops()
    {
        var activeCts = _realtimeLoopCts;
        if (activeCts is null)
        {
            return;
        }

        _realtimeLoopCts = null;
        activeCts.Cancel();
        try
        {
            var tasks = new List<Task>();
            if (_openXrLoopTask is not null)
            {
                tasks.Add(_openXrLoopTask);
            }

            if (_videoDecodeLoopTask is not null)
            {
                tasks.Add(_videoDecodeLoopTask);
            }

            if (tasks.Count > 0)
            {
                Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(2));
            }
        }
        catch { }
        finally
        {
            activeCts.Dispose();
            _openXrLoopTask = null;
            _videoDecodeLoopTask = null;
        }
    }

    private async Task OpenXrLoopAsync(CancellationToken token, AppLogger logger)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var keyboardDebugMode = _isKeyboardDebugMode;
                OpenXrControllerState state;
                string inputSource;
                if (keyboardDebugMode)
                {
                    state = _keyboardInputEmulatorService.BuildState();
                    inputSource = "Input source: Keyboard debug";
                    await Task.Delay(11, token);
                }
                else if (_openXrControllerInputService is not null)
                {
                    state = _openXrControllerInputService.Poll();
                    inputSource = "Input source: OpenXR";
                }
                else
                {
                    state = _keyboardInputEmulatorService.BuildUnavailableState(
                        OpenXrUnavailableReason
                    );
                    inputSource = "Input source: unavailable";
                    await Task.Delay(11, token);
                }

                lock (_runtimeStateLock)
                {
                    _latestOpenXrState = state;
                    _latestInputSource = inputSource;
                }

                _windowsInputTcpServerService?.UpdateLatestInputState(state, keyboardDebugMode);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("OpenXR realtime loop failed.", ex);
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
}
