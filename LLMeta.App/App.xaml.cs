using System.Windows;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;
using Velopack;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

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
}
