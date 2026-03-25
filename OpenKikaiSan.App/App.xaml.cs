using System.Net.Sockets;
using System.Windows;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Services;
using OpenKikaiSan.App.Services.WindowCapture;
using OpenKikaiSan.App.Stores;
using OpenKikaiSan.App.Utils;
using OpenKikaiSan.App.ViewModels;
using Velopack;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace OpenKikaiSan.App;

public partial class App : System.Windows.Application
{
    private const int DefaultWindowsInputTcpPort = 39200;
    private const string EmulatorRouteHint = " (A-1: Android -> 10.0.2.2)";
    private const string OpenXrUnavailableReason =
        "Not active. Refresh OpenXR or enable keyboard debug input.";
    private const string WaitingVideoStatus = "Video: waiting capture frame";

    private OpenXrControllerInputService? _openXrControllerInputService;
    private WindowsInputTcpServerService? _windowsInputTcpServerService;
    private WindowCaptureService? _windowCaptureService;
    private readonly KeyboardInputEmulatorService _keyboardInputEmulatorService = new();
    private readonly object _runtimeStateLock = new();
    private DispatcherTimer? _uiTimer;
    private CancellationTokenSource? _realtimeLoopCts;
    private Task? _openXrLoopTask;
    private Task? _videoDecodeLoopTask;
    private OpenXrControllerState _latestOpenXrState;
    private string _latestInputSource = "Input source: unavailable";
    private string _latestVideoStatus = WaitingVideoStatus;
    private volatile bool _isKeyboardDebugMode;

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
                logger.Fatal("DispatcherUnhandledException", args.Exception);
                System.Windows.MessageBox.Show(args.Exception.Message, "OpenKikaiSan Error");
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    logger.Fatal("UnhandledException", ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                logger.Fatal("UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            var settingsStore = new SettingsStore(logger);
            var settings = settingsStore.Load();
            logger.SetMinimumLevel(settings.LogLevel);
            logger.Info($"Logger minimum level: {settings.LogLevel}");
            var captureTargetRestoreService = new CaptureTargetRestoreService(logger);
            var mainViewModel = new MainViewModel(settings, logger);
            var inputTcpPort = ResolveValidWindowsInputTcpPort(settings.WindowsInputTcpPort);
            if (settings.WindowsInputTcpPort != inputTcpPort)
            {
                settings.WindowsInputTcpPort = inputTcpPort;
                settingsStore.Save(settings);
            }

            mainViewModel.SetInputTcpPortForDisplay(inputTcpPort);
            mainViewModel.SetLogLevelForDisplay(settings.LogLevel);
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
                _windowCaptureService?.SetD3D11DevicePointer(
                    _openXrControllerInputService?.GetD3D11DevicePointer() ?? IntPtr.Zero
                );
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
            mainViewModel.InputTcpPortApplyRequested += port =>
            {
                TryApplyWindowsInputTcpPort(
                    logger,
                    settingsStore,
                    settings,
                    port,
                    mainViewModel,
                    allowAutomaticFallback: false,
                    promptForAutomaticFallback: true
                );
            };
            mainViewModel.LogLevelApplyRequested += logLevel =>
            {
                settings.LogLevel = logLevel;
                settingsStore.Save(settings);
                logger.SetMinimumLevel(logLevel);
                mainViewModel.SetLogLevelForDisplay(logLevel);
                mainViewModel.StatusMessage = $"Log level applied: {logLevel}";
            };
            mainViewModel.CaptureTargetSelectionRequested += async () =>
            {
                if (MainWindow is null || _windowCaptureService is null)
                {
                    mainViewModel.StatusMessage = "Capture target selection is unavailable.";
                    return;
                }

                try
                {
                    var item = await _windowCaptureService.PickCaptureItemAsync(MainWindow);
                    if (item is null)
                    {
                        mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
                        mainViewModel.StatusMessage = "Capture target selection canceled.";
                        return;
                    }

                    var started = _windowCaptureService.StartCapture(item);
                    mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
                    if (!started)
                    {
                        mainViewModel.StatusMessage = "Capture target selection failed.";
                        return;
                    }

                    var savedCaptureTarget = captureTargetRestoreService.TryCreateSavedTarget(item);
                    SaveCaptureTargetSelection(settings, settingsStore, savedCaptureTarget);
                    mainViewModel.StatusMessage = "Capture target selected.";
                }
                catch (Exception ex)
                {
                    logger.Error("Capture target selection failed.", ex);
                    mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
                    mainViewModel.StatusMessage = "Capture target selection failed.";
                }
            };

            TryApplyWindowsInputTcpPort(
                logger,
                settingsStore,
                settings,
                inputTcpPort,
                mainViewModel,
                allowAutomaticFallback: true,
                promptForAutomaticFallback: false
            );
            _windowCaptureService = new WindowCaptureService(logger);
            _windowCaptureService.FrameCaptured += frame =>
            {
                _openXrControllerInputService?.SetLatestDecodedSbsFrame(frame);
            };
            _windowCaptureService.CaptureStopped += () =>
            {
                _openXrControllerInputService?.ClearLatestDecodedSbsFrame();
                ResetVideoPipelineMetrics();
            };
            mainViewModel.BridgeStatus =
                (_windowsInputTcpServerService?.StatusText ?? "Input TCP: not started")
                + EmulatorRouteHint;
            ResetVideoPipelineMetrics();
            mainViewModel.VideoStatus = WaitingVideoStatus;

            var initializeState = ReinitializeOpenXr(logger);
            mainViewModel.UpdateOpenXrControllerState(initializeState);
            lock (_runtimeStateLock)
            {
                _latestOpenXrState = initializeState;
                _latestInputSource = initializeState.IsInitialized
                    ? "Input source: OpenXR"
                    : "Input source: unavailable";
            }
            _windowCaptureService.SetD3D11DevicePointer(
                _openXrControllerInputService?.GetD3D11DevicePointer() ?? IntPtr.Zero
            );

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
            RestoreCaptureTargetIfAvailable(settings, captureTargetRestoreService, mainViewModel);

            if (_uiTimer is null)
            {
                _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
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
                    if (_openXrControllerInputService is not null)
                    {
                        var renderConfig =
                            _openXrControllerInputService.GetVideoRenderConfigStateSnapshot();
                        var renderStats =
                            _openXrControllerInputService.GetVideoRenderStatsSnapshot();
                        mainViewModel.UpdateVideoRenderConfig(
                            renderConfig,
                            renderStats.LastUploadFailureCode
                        );
                    }
                    if (_windowsInputTcpServerService is not null)
                    {
                        mainViewModel.BridgeStatus =
                            _windowsInputTcpServerService.StatusText + EmulatorRouteHint;
                    }
                    if (_windowCaptureService is not null)
                    {
                        mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
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
            logger.Fatal("Startup failed.", ex);
            System.Windows.MessageBox.Show(ex.Message, "OpenKikaiSan Error");
            Shutdown();
        }
    }

    private static int ResolveValidWindowsInputTcpPort(int value)
    {
        if (value is >= 1 and <= 65535)
        {
            return value;
        }

        return DefaultWindowsInputTcpPort;
    }

    private void TryApplyWindowsInputTcpPort(
        AppLogger logger,
        SettingsStore settingsStore,
        AppSettings settings,
        int inputTcpPort,
        MainViewModel mainViewModel,
        bool allowAutomaticFallback,
        bool promptForAutomaticFallback
    )
    {
        var sanitizedPort = ResolveValidWindowsInputTcpPort(inputTcpPort);
        if (sanitizedPort != inputTcpPort)
        {
            mainViewModel.SetInputTcpPortForDisplay(sanitizedPort);
        }

        if (
            TryStartWindowsInputTcpServer(
                logger,
                sanitizedPort,
                mainViewModel,
                out var activePort,
                out var socketErrorCode,
                out _
            )
        )
        {
            settings.WindowsInputTcpPort = activePort;
            settingsStore.Save(settings);
            mainViewModel.SetInputTcpPortForDisplay(activePort);
            mainViewModel.StatusMessage = $"Windows input TCP port applied: {activePort}";
            return;
        }

        if (
            allowAutomaticFallback
            && socketErrorCode == SocketError.AddressAlreadyInUse
            && TryStartWindowsInputTcpServer(
                logger,
                0,
                mainViewModel,
                out var resolvedFallbackPort,
                out _,
                out _
            )
        )
        {
            settings.WindowsInputTcpPort = resolvedFallbackPort;
            settingsStore.Save(settings);
            mainViewModel.SetInputTcpPortForDisplay(resolvedFallbackPort);
            mainViewModel.StatusMessage =
                $"Input TCP port {sanitizedPort} was already in use. Switched to {resolvedFallbackPort}.";
            return;
        }

        if (socketErrorCode == SocketError.AddressAlreadyInUse)
        {
            if (
                promptForAutomaticFallback
                && TryPromptAndSwitchToAutomaticPort(
                    logger,
                    settingsStore,
                    settings,
                    sanitizedPort,
                    mainViewModel
                )
            )
            {
                return;
            }

            mainViewModel.StatusMessage =
                $"Input TCP port {sanitizedPort} is already in use by another process.";
            return;
        }

        mainViewModel.StatusMessage = $"Failed to start Input TCP on port {sanitizedPort}.";
    }

    private bool TryPromptAndSwitchToAutomaticPort(
        AppLogger logger,
        SettingsStore settingsStore,
        AppSettings settings,
        int requestedPort,
        MainViewModel mainViewModel
    )
    {
        var result = System.Windows.MessageBox.Show(
            $"Input TCP port {requestedPort} is already in use.{Environment.NewLine}{Environment.NewLine}Switch to an available port automatically?",
            "Input TCP Port Conflict",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );
        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        if (
            !TryStartWindowsInputTcpServer(
                logger,
                0,
                mainViewModel,
                out var activePort,
                out _,
                out _
            )
        )
        {
            mainViewModel.StatusMessage = "Failed to allocate an available Input TCP port.";
            return false;
        }

        settings.WindowsInputTcpPort = activePort;
        settingsStore.Save(settings);
        mainViewModel.SetInputTcpPortForDisplay(activePort);
        mainViewModel.StatusMessage =
            $"Input TCP port {requestedPort} was unavailable. Switched to {activePort}.";
        return true;
    }

    private bool TryStartWindowsInputTcpServer(
        AppLogger logger,
        int inputTcpPort,
        MainViewModel mainViewModel,
        out int activePort,
        out SocketError socketErrorCode,
        out string bridgeStatusOnFailure
    )
    {
        activePort = inputTcpPort;
        socketErrorCode = SocketError.Success;
        bridgeStatusOnFailure = string.Empty;

        var candidateServerService = new WindowsInputTcpServerService(logger, inputTcpPort);

        try
        {
            candidateServerService.Start();
            activePort = candidateServerService.BoundPort;
            var previousServerService = _windowsInputTcpServerService;
            _windowsInputTcpServerService = candidateServerService;
            previousServerService?.Dispose();
            logger.Info(_windowsInputTcpServerService.StatusText);
            return true;
        }
        catch (SocketException ex)
        {
            socketErrorCode = ex.SocketErrorCode;
            bridgeStatusOnFailure =
                BuildTcpPortErrorMessage(inputTcpPort, ex.SocketErrorCode) + EmulatorRouteHint;
            logger.Error($"Input TCP startup failed on port {inputTcpPort}.", ex);
            candidateServerService.Dispose();
            if (_windowsInputTcpServerService is null)
            {
                mainViewModel.BridgeStatus = bridgeStatusOnFailure;
            }

            return false;
        }
        catch (Exception ex)
        {
            bridgeStatusOnFailure =
                $"Input TCP: startup failed ({inputTcpPort})" + EmulatorRouteHint;
            logger.Error($"Input TCP startup failed on port {inputTcpPort}.", ex);
            candidateServerService.Dispose();
            if (_windowsInputTcpServerService is null)
            {
                mainViewModel.BridgeStatus = bridgeStatusOnFailure;
            }

            return false;
        }
    }

    private static string BuildTcpPortErrorMessage(int port, SocketError socketErrorCode)
    {
        return socketErrorCode switch
        {
            SocketError.AddressAlreadyInUse when port == 0 =>
                "Input TCP: failed to allocate an available port",
            SocketError.AddressAlreadyInUse =>
                $"Input TCP: port {port} is already used by another process",
            SocketError.AccessDenied => $"Input TCP: access denied for port {port}",
            _ => $"Input TCP: failed to bind port {port} ({socketErrorCode})",
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopRealtimeLoops();
        _uiTimer?.Stop();
        _uiTimer = null;
        _openXrControllerInputService?.Dispose();
        _openXrControllerInputService = null;
        _windowsInputTcpServerService?.Dispose();
        _windowsInputTcpServerService = null;
        _windowCaptureService?.Dispose();
        _windowCaptureService = null;
        base.OnExit(e);
    }
}
