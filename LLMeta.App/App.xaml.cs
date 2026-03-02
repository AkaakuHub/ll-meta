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

    private OpenXrControllerInputService? _openXrControllerInputService;
    private AndroidInputBridgeTcpServerService? _androidInputBridgeTcpServerService;
    private VideoH264DecodeService? _videoH264DecodeService;
    private WebRtcSignalingTcpServerService? _webRtcSignalingTcpServerService;
    private WebRtcPeerConnectionService? _webRtcPeerConnectionService;
    private readonly KeyboardInputEmulatorService _keyboardInputEmulatorService = new();
    private DispatcherTimer? _openXrPollTimer;
    private string? _lastOpenXrStatus;
    private uint _videoConnectionId;
    private DateTimeOffset _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
    private uint _videoFramesObserved;
    private uint _videoDecodeCalls;
    private uint _videoDecodedFrames;
    private string _lastVideoDecodeStatus = "none";
    private uint _videoConsecutiveNoFrameDecodes;
    private DateTimeOffset _lastVideoDecodedAt = DateTimeOffset.MinValue;

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
                var reinitializeState = ReinitializeOpenXr(logger);
                mainViewModel.UpdateOpenXrControllerState(reinitializeState);
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
                _androidInputBridgeTcpServerService.StatusText + " (A-1: Android -> 10.0.2.2)";

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
            _videoConnectionId = 0;
            _lastVideoPipelineLogAt = DateTimeOffset.MinValue;
            _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
            _videoFramesObserved = 0;
            _videoDecodeCalls = 0;
            _videoDecodedFrames = 0;
            _lastVideoDecodeStatus = "none";
            _videoConsecutiveNoFrameDecodes = 0;
            _lastVideoDecodedAt = DateTimeOffset.MinValue;
            mainViewModel.VideoStatus = "Video: waiting WebRTC frame (A-1: Android -> 10.0.2.2)";
            logger.Info(_webRtcSignalingTcpServerService.StatusText);

            var initializeState = ReinitializeOpenXr(logger);
            mainViewModel.UpdateOpenXrControllerState(initializeState);

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

            if (_openXrPollTimer is null)
            {
                _openXrPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _openXrPollTimer.Tick += (_, _) =>
                {
                    OpenXrControllerState state;
                    if (mainViewModel.IsKeyboardDebugMode)
                    {
                        state = _keyboardInputEmulatorService.BuildState();
                        mainViewModel.ActiveInputSource = "Input source: Keyboard debug";
                    }
                    else if (_openXrControllerInputService is not null)
                    {
                        state = _openXrControllerInputService.Poll();
                        mainViewModel.ActiveInputSource = "Input source: OpenXR";
                    }
                    else
                    {
                        state = _keyboardInputEmulatorService.BuildUnavailableState(
                            "OpenXR is not initialized. Click Reinitialize OpenXR or enable keyboard debug input."
                        );
                        mainViewModel.ActiveInputSource = "Input source: unavailable";
                    }

                    mainViewModel.UpdateOpenXrControllerState(state);
                    if (_androidInputBridgeTcpServerService is not null)
                    {
                        _androidInputBridgeTcpServerService.UpdateLatestState(
                            state,
                            mainViewModel.IsKeyboardDebugMode
                        );
                        mainViewModel.BridgeStatus =
                            _androidInputBridgeTcpServerService.StatusText
                            + " (A-1: Android -> 10.0.2.2)";
                    }

                    var handledAnyEncodedPacket = false;
                    var activeWebRtcPeerConnectionService = _webRtcPeerConnectionService;
                    var activeVideoDecodeService = _videoH264DecodeService;
                    if (
                        activeWebRtcPeerConnectionService is not null
                        && activeVideoDecodeService is not null
                    )
                    {
                        var decodePerTick = 0;
                        while (
                            decodePerTick < 32
                            && activeWebRtcPeerConnectionService.TryDequeueVideoFrame(
                                out var encodedPacket
                            )
                        )
                        {
                            handledAnyEncodedPacket = true;
                            decodePerTick++;
                            _videoFramesObserved++;

                            if (encodedPacket.ConnectionId != _videoConnectionId)
                            {
                                _videoH264DecodeService.Dispose();
                                _videoH264DecodeService = new VideoH264DecodeService(logger);
                                activeVideoDecodeService = _videoH264DecodeService;
                                _videoConnectionId = encodedPacket.ConnectionId;
                                _lastVideoKeyFrameRequestAt = DateTimeOffset.MinValue;
                                _videoFramesObserved = 1;
                                _videoDecodeCalls = 0;
                                _videoDecodedFrames = 0;
                                _lastVideoDecodeStatus = "none";
                                _videoConsecutiveNoFrameDecodes = 0;
                                _lastVideoDecodedAt = DateTimeOffset.MinValue;
                                activeWebRtcPeerConnectionService.RequestVideoKeyFrame();
                                logger.Info(
                                    "Video pipeline new connection: "
                                        + $"conn={_videoConnectionId} seq={encodedPacket.Sequence} payload={encodedPacket.Payload.Length}"
                                );
                            }

                            _videoDecodeCalls++;
                            var decodeStatus = activeVideoDecodeService.Decode(encodedPacket);
                            _lastVideoDecodeStatus = decodeStatus;

                            if (decodeStatus == "decoded frame")
                            {
                                _videoConsecutiveNoFrameDecodes = 0;
                                _lastVideoDecodedAt = DateTimeOffset.UtcNow;
                            }
                            else
                            {
                                _videoConsecutiveNoFrameDecodes++;
                            }

                            if (
                                _videoH264DecodeService.TryGetLatestFrame(out var decodedFrame)
                                && _openXrControllerInputService is not null
                            )
                            {
                                _videoDecodedFrames++;
                                _lastVideoDecodedAt = DateTimeOffset.UtcNow;
                                _openXrControllerInputService.SetLatestDecodedSbsFrame(
                                    decodedFrame
                                );
                            }
                        }

                        if (activeWebRtcPeerConnectionService is not null)
                        {
                            var now = DateTimeOffset.UtcNow;
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
                                }
                            }
                        }

                        if (handledAnyEncodedPacket)
                        {
                            mainViewModel.VideoStatus =
                                "Video: WebRTC connected"
                                + " | decode: "
                                + _lastVideoDecodeStatus
                                + " (A-1: Android -> 10.0.2.2)";
                        }
                    }

                    if (!handledAnyEncodedPacket)
                    {
                        if (!mainViewModel.VideoStatus.Contains("decode:"))
                        {
                            mainViewModel.VideoStatus =
                                "Video: waiting WebRTC frame (A-1: Android -> 10.0.2.2)";
                        }
                    }

                    if (_webRtcPeerConnectionService is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (
                            _lastVideoPipelineLogAt == DateTimeOffset.MinValue
                            || (now - _lastVideoPipelineLogAt).TotalSeconds >= 2
                        )
                        {
                            _lastVideoPipelineLogAt = now;
                            var stats = _webRtcPeerConnectionService.GetVideoStatsSnapshot();
                            logger.Info(
                                "Video pipeline stats: "
                                    + $"conn={_videoConnectionId} connected={stats.IsConnected} "
                                    + $"rxFrames={_videoFramesObserved} "
                                    + $"decodeCalls={_videoDecodeCalls} decodedFrames={_videoDecodedFrames} "
                                    + $"lastSeq={stats.LastSequence} lastPayload={stats.LastPayloadSize} latencyMs={stats.LastLatencyMs} "
                                    + $"lastDecodeStatus={_lastVideoDecodeStatus}"
                            );
                        }
                    }

                    if (_lastOpenXrStatus != state.Status)
                    {
                        _lastOpenXrStatus = state.Status;
                        logger.Info($"OpenXR input state: {state.Status}");
                    }
                };
                _openXrPollTimer.Start();
            }

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
        _openXrPollTimer?.Stop();
        _openXrPollTimer = null;
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
