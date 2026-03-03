using System.Windows;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;
using Velopack;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace LLMeta.App;

public partial class App : System.Windows.Application
{
    private const int AndroidBridgePort = 39090;
    private const int WebRtcSignalingPort = 39200;
    private const string EmulatorRouteHint = " (A-1: Android -> 10.0.2.2)";
    private const string OpenXrUnavailableReason =
        "OpenXR is not initialized. Click Reinitialize OpenXR or enable keyboard debug input.";
    private const string WaitingVideoStatus = "Video: waiting WebRTC frame" + EmulatorRouteHint;
    private const string ConnectedVideoStatusPrefix = "Video: WebRTC connected | decode: ";

    private OpenXrControllerInputService? _openXrControllerInputService;
    private AndroidInputBridgeTcpServerService? _androidInputBridgeTcpServerService;
    private VideoH264DecodeService? _videoH264DecodeService;
    private WebRtcSignalingTcpServerService? _webRtcSignalingTcpServerService;
    private WebRtcPeerConnectionService? _webRtcPeerConnectionService;
    private readonly KeyboardInputEmulatorService _keyboardInputEmulatorService = new();
    private readonly object _runtimeStateLock = new();
    private DispatcherTimer? _uiTimer;
    private CancellationTokenSource? _realtimeLoopCts;
    private Task? _openXrLoopTask;
    private Task? _videoDecodeLoopTask;
    private OpenXrControllerState _latestOpenXrState;
    private string _latestInputSource = "Input source: unavailable";
    private string _latestVideoStatus = WaitingVideoStatus;
    private string? _lastOpenXrStatus;
    private volatile bool _isKeyboardDebugMode;
    private uint _videoConnectionId;
    private DateTimeOffset _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
    private uint _videoFramesObserved;
    private uint _videoDecodeCalls;
    private uint _videoDecodedFrames;
    private string _lastVideoDecodeStatus = "none";
    private long _lastDecodeElapsedMs;
    private uint _videoConsecutiveNoFrameDecodes;
    private DateTimeOffset _lastVideoDecodedAt = DateTimeOffset.MinValue;
    private bool _isWaitingForVideoKeyFrame = true;

    public App()
    {
        _latestOpenXrState = _keyboardInputEmulatorService.BuildUnavailableState(
            OpenXrUnavailableReason
        );
    }

    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirectories();

        var logger = new AppLogger();
        logger.Info("Startup begin.");

        try
        {
            DispatcherUnhandledException += (_, args) =>
            {
                logger.Error("DispatcherUnhandledException", args.Exception);
                System.Windows.MessageBox.Show(args.Exception.Message, "LLMeta Error");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    logger.Error("UnhandledException", ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                logger.Error("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            var settingsStore = new SettingsStore(logger);
            var settings = settingsStore.Load();
            var mainViewModel = new MainViewModel(settings, settingsStore, logger);
            mainViewModel.OpenXrReinitializeRequested += () =>
            {
                StopRealtimeLoops();
                var reinitializeState = ReinitializeOpenXr(logger);
                lock (_runtimeStateLock)
                {
                    _latestOpenXrState = reinitializeState;
                    _latestInputSource = reinitializeState.IsInitialized
                        ? "Input source: OpenXR"
                        : "Input source: unavailable";
                }
                StartRealtimeLoops(logger);
                if (reinitializeState.IsInitialized)
                {
                    mainViewModel.StatusMessage =
                        "OpenXR reinitialized. Disable keyboard debug input to use real device.";
                }
                else
                {
                    mainViewModel.StatusMessage = "OpenXR reinitialize failed.";
                }
            };

            _androidInputBridgeTcpServerService = new AndroidInputBridgeTcpServerService(
                logger,
                AndroidBridgePort
            );
            _androidInputBridgeTcpServerService.Start();
            mainViewModel.BridgeStatus =
                _androidInputBridgeTcpServerService.StatusText + EmulatorRouteHint;

            _webRtcSignalingTcpServerService = new WebRtcSignalingTcpServerService(
                logger,
                WebRtcSignalingPort
            );
            _webRtcPeerConnectionService = new WebRtcPeerConnectionService(logger);
            _webRtcPeerConnectionService.OutboundSignalingMessage += outboundMessage =>
            {
                if (_webRtcSignalingTcpServerService is null)
                {
                    return;
                }

                var sent = _webRtcSignalingTcpServerService.TrySend(outboundMessage);
                if (!sent)
                {
                    logger.Info($"WebRTC signaling tx dropped: type={outboundMessage.Type}");
                }
            };
            _webRtcSignalingTcpServerService.MessageReceived += message =>
            {
                logger.Info($"WebRTC signaling rx: type={message.Type}");
                if (_webRtcPeerConnectionService is null)
                {
                    return;
                }

                _ = _webRtcPeerConnectionService.HandleSignalingMessageAsync(message);
            };
            _webRtcSignalingTcpServerService.Start();
            _videoH264DecodeService = new VideoH264DecodeService(logger);
            ResetVideoPipelineMetrics();
            mainViewModel.VideoStatus = WaitingVideoStatus;
            logger.Info(_webRtcSignalingTcpServerService.StatusText);

            var initializeState = ReinitializeOpenXr(logger);
            mainViewModel.UpdateOpenXrControllerState(initializeState);
            lock (_runtimeStateLock)
            {
                _latestOpenXrState = initializeState;
                _latestInputSource = initializeState.IsInitialized
                    ? "Input source: OpenXR"
                    : "Input source: unavailable";
            }

            MainWindow = new MainWindow { DataContext = mainViewModel };
            MainWindow.PreviewKeyDown += (_, args) =>
            {
                if (mainViewModel.IsKeyboardDebugMode)
                {
                    _keyboardInputEmulatorService.OnKeyDown(args.Key);
                }
            };
            MainWindow.PreviewKeyUp += (_, args) =>
            {
                if (mainViewModel.IsKeyboardDebugMode)
                {
                    _keyboardInputEmulatorService.OnKeyUp(args.Key);
                }
            };
            MainWindow.Show();

            if (_uiTimer is null)
            {
                _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _uiTimer.Tick += (_, _) =>
                {
                    _isKeyboardDebugMode = mainViewModel.IsKeyboardDebugMode;

                    OpenXrControllerState stateSnapshot;
                    string inputSourceSnapshot;
                    string videoStatusSnapshot;
                    lock (_runtimeStateLock)
                    {
                        stateSnapshot = _latestOpenXrState;
                        inputSourceSnapshot = _latestInputSource;
                        videoStatusSnapshot = _latestVideoStatus;
                    }

                    mainViewModel.ActiveInputSource = inputSourceSnapshot;
                    mainViewModel.UpdateOpenXrControllerState(stateSnapshot);
                    mainViewModel.VideoStatus = videoStatusSnapshot;
                    if (_androidInputBridgeTcpServerService is not null)
                    {
                        mainViewModel.BridgeStatus =
                            _androidInputBridgeTcpServerService.StatusText + EmulatorRouteHint;
                    }

                    if (_lastOpenXrStatus != stateSnapshot.Status)
                    {
                        _lastOpenXrStatus = stateSnapshot.Status;
                        logger.Info($"OpenXR input state: {stateSnapshot.Status}");
                    }
                };
                _uiTimer.Start();
            }

            _isKeyboardDebugMode = mainViewModel.IsKeyboardDebugMode;
            StartRealtimeLoops(logger);

            logger.Info("Startup completed.");
        }
        catch (Exception ex)
        {
            logger.Error("Startup failed.", ex);
            System.Windows.MessageBox.Show(ex.Message, "LLMeta Error");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopRealtimeLoops();
        _uiTimer?.Stop();
        _uiTimer = null;
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;
        _androidInputBridgeTcpServerService?.Dispose();
        _androidInputBridgeTcpServerService = null;
        _webRtcSignalingTcpServerService?.Dispose();
        _webRtcSignalingTcpServerService = null;
        _webRtcPeerConnectionService?.Dispose();
        _webRtcPeerConnectionService = null;
        _videoH264DecodeService?.Dispose();
        _videoH264DecodeService = null;
        _videoConnectionId = 0;
        base.OnExit(e);
    }

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

                _androidInputBridgeTcpServerService?.UpdateLatestState(state, keyboardDebugMode);
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

    private async Task VideoDecodeLoopAsync(CancellationToken token, AppLogger logger)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var activeWebRtcPeerConnectionService = _webRtcPeerConnectionService;
                var activeVideoDecodeService = _videoH264DecodeService;
                if (
                    activeWebRtcPeerConnectionService is null
                    || activeVideoDecodeService is null
                    || !activeWebRtcPeerConnectionService.TryDequeueVideoFrame(
                        out var encodedPacket
                    )
                )
                {
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Delay(1, token);
                    continue;
                }

                lock (_runtimeStateLock)
                {
                    _videoFramesObserved += 1;
                }

                if (encodedPacket.ConnectionId != _videoConnectionId)
                {
                    _videoH264DecodeService?.Dispose();
                    _videoH264DecodeService = new VideoH264DecodeService(logger);
                    activeVideoDecodeService = _videoH264DecodeService;
                    lock (_runtimeStateLock)
                    {
                        _videoConnectionId = encodedPacket.ConnectionId;
                        _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
                        _videoFramesObserved = 1;
                        _videoDecodeCalls = 0;
                        _videoDecodedFrames = 0;
                        _lastVideoDecodeStatus = "none";
                        _videoConsecutiveNoFrameDecodes = 0;
                        _lastVideoDecodedAt = DateTimeOffset.MinValue;
                        _isWaitingForVideoKeyFrame = true;
                    }
                    activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                    logger.Info(
                        "Video pipeline new connection: "
                            + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} payload={encodedPacket.Payload.Length}"
                    );
                }

                var now = DateTimeOffset.UtcNow;
                bool shouldDecodeCurrentPacket;
                lock (_runtimeStateLock)
                {
                    shouldDecodeCurrentPacket =
                        !_isWaitingForVideoKeyFrame || encodedPacket.IsKeyFrame;
                    if (_isWaitingForVideoKeyFrame && encodedPacket.IsKeyFrame)
                    {
                        _isWaitingForVideoKeyFrame = false;
                        logger.Info("Video pipeline sync: keyframe received, decode resumed.");
                    }
                }

                if (!shouldDecodeCurrentPacket)
                {
                    lock (_runtimeStateLock)
                    {
                        if (
                            _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds >= 1000
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                        }
                        _lastVideoDecodeStatus = "waiting keyframe";
                    }
                    MaybeLogVideoPipelineStats(logger);
                    await Task.Delay(1, token);
                    continue;
                }

                lock (_runtimeStateLock)
                {
                    _videoDecodeCalls += 1;
                }
                var decodeStopwatch = Stopwatch.StartNew();
                var decodeStatus = activeVideoDecodeService.Decode(encodedPacket);
                decodeStopwatch.Stop();
                lock (_runtimeStateLock)
                {
                    _lastVideoDecodeStatus = decodeStatus;
                    _lastDecodeElapsedMs = decodeStopwatch.ElapsedMilliseconds;
                    if (decodeStatus == "decoded frame")
                    {
                        _videoConsecutiveNoFrameDecodes = 0;
                        _lastVideoDecodedAt = now;
                    }
                    else
                    {
                        _videoConsecutiveNoFrameDecodes += 1;
                    }
                }

                if (
                    _videoH264DecodeService is not null
                    && _videoH264DecodeService.TryGetLatestFrame(out var decodedFrame)
                    && _openXrControllerInputService is not null
                )
                {
                    lock (_runtimeStateLock)
                    {
                        _videoDecodedFrames += 1;
                        _lastVideoDecodedAt = now;
                    }
                    _openXrControllerInputService.SetLatestDecodedSbsFrame(decodedFrame);
                }

                lock (_runtimeStateLock)
                {
                    var stalledForMs =
                        _lastVideoDecodedAt == DateTimeOffset.MinValue
                            ? double.MaxValue
                            : (now - _lastVideoDecodedAt).TotalMilliseconds;
                    var shouldRequestKeyFrame =
                        _videoConsecutiveNoFrameDecodes >= 45 && stalledForMs >= 1200;
                    if (shouldRequestKeyFrame)
                    {
                        if (
                            _lastVideoKeyFrameRequestAt == DateTimeOffset.MinValue
                            || (now - _lastVideoKeyFrameRequestAt).TotalMilliseconds >= 1200
                        )
                        {
                            _lastVideoKeyFrameRequestAt = now;
                            activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                            _videoConsecutiveNoFrameDecodes = 0;
                            _isWaitingForVideoKeyFrame = true;
                        }
                    }
                    var statsSnapshot = activeWebRtcPeerConnectionService.GetVideoStatsSnapshot();
                    var syncStatus = _isWaitingForVideoKeyFrame
                        ? "sync=waiting-keyframe"
                        : "sync=ok";
                    _latestVideoStatus =
                        ConnectedVideoStatusPrefix
                        + _lastVideoDecodeStatus
                        + $" | {syncStatus}"
                        + $" | rxFps={statsSnapshot.ReceivedFps:F1}"
                        + $" | rxKbps={statsSnapshot.ReceivedBitrateKbps:F0}"
                        + $" | q={statsSnapshot.QueueDepth}"
                        + $" | qDelayMs={statsSnapshot.LastLatencyMs}"
                        + $" | decMs={_lastDecodeElapsedMs}"
                        + EmulatorRouteHint;
                }

                MaybeLogVideoPipelineStats(logger);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.Error("Video decode loop failed.", ex);
                try
                {
                    await Task.Delay(20, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void MaybeLogVideoPipelineStats(AppLogger logger)
    {
        if (_webRtcPeerConnectionService is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        uint connectionId;
        uint framesObserved;
        uint decodeCalls;
        uint decodedFrames;
        string decodeStatus;
        long decodeElapsedMs;
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
            connectionId = _videoConnectionId;
            framesObserved = _videoFramesObserved;
            decodeCalls = _videoDecodeCalls;
            decodedFrames = _videoDecodedFrames;
            decodeStatus = _lastVideoDecodeStatus;
            decodeElapsedMs = _lastDecodeElapsedMs;
        }

        var stats = _webRtcPeerConnectionService.GetVideoStatsSnapshot();
        logger.Info(
            "Video pipeline stats: "
                + $"conn={connectionId} connected={stats.IsConnected} "
                + $"rxFrames={framesObserved} "
                + $"decodeCalls={decodeCalls} decodedFrames={decodedFrames} "
                + $"lastSeq={stats.LastSequence} lastPayload={stats.LastPayloadSize} "
                + $"queue={stats.QueueDepth} queueDelayMs={stats.LastLatencyMs} "
                + $"rxFps={stats.ReceivedFps:F1} rxKbps={stats.ReceivedBitrateKbps:F0} "
                + $"rawRtpPkts={stats.RawRtpPackets} pliReq={stats.PliRequests} "
                + $"decodeMs={decodeElapsedMs} "
                + $"lastDecodeStatus={decodeStatus}"
        );
    }

    private void ResetVideoPipelineMetrics()
    {
        lock (_runtimeStateLock)
        {
            _videoConnectionId = 0;
            _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
            _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
            _videoFramesObserved = 0;
            _videoDecodeCalls = 0;
            _videoDecodedFrames = 0;
            _lastVideoDecodeStatus = "none";
            _lastDecodeElapsedMs = 0;
            _videoConsecutiveNoFrameDecodes = 0;
            _lastVideoDecodedAt = DateTimeOffset.MinValue;
            _isWaitingForVideoKeyFrame = true;
            _latestVideoStatus = WaitingVideoStatus;
        }
    }

    private OpenXrControllerState ReinitializeOpenXr(AppLogger logger)
    {
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;

        var openXrControllerInputService = new OpenXrControllerInputService();
        var initializeState = openXrControllerInputService.Initialize();
        logger.Info($"OpenXR input initialize: {initializeState.Status}");

        if (initializeState.IsInitialized)
        {
            _openXrControllerInputService = openXrControllerInputService;
        }
        else
        {
            openXrControllerInputService.Dispose();
        }

        return initializeState;
    }
}
