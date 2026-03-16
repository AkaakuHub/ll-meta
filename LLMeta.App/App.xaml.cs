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
    private const int DefaultWindowsInputTcpPort = 39200;
    private const string EmulatorRouteHint = " (A-1: Android -> 10.0.2.2)";
    private const string OpenXrUnavailableReason =
        "OpenXR is not initialized. Click Reinitialize OpenXR or enable keyboard debug input.";
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
    private string? _lastOpenXrStatus;
    private volatile bool _isKeyboardDebugMode;
    private DateTimeOffset _lastVideoPipelineLogAt = DateTimeOffset.MinValue;

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
            mainViewModel.SelectedSwapchainFormatOption = settings.PreferredSwapchainFormat;
            mainViewModel.SelectedGraphicsAdapterOption = settings.PreferredGraphicsAdapter;
            mainViewModel.SelectedGraphicsBackendOption = settings.PreferredGraphicsBackend;
            var inputTcpPort = ResolveValidWindowsInputTcpPort(settings.WindowsInputTcpPort);
            if (settings.WindowsInputTcpPort != inputTcpPort)
            {
                settings.WindowsInputTcpPort = inputTcpPort;
                settingsStore.Save(settings);
            }

            mainViewModel.SetInputTcpPortForDisplay(inputTcpPort);
            mainViewModel.OpenXrReinitializeRequested += () =>
            {
                StopRealtimeLoops();
                var reinitializeState = ReinitializeOpenXr(
                    logger,
                    settings.PreferredSwapchainFormat,
                    settings.PreferredGraphicsAdapter,
                    settings.PreferredGraphicsBackend
                );
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
                settings.WindowsInputTcpPort = port;
                settingsStore.Save(settings);
                mainViewModel.SetInputTcpPortForDisplay(port);
                InitializeWindowsInputTcpServer(logger, port, mainViewModel);
                mainViewModel.StatusMessage = $"Windows input TCP port applied: {port}";
            };
            mainViewModel.VideoRenderSettingsApplyRequested += (
                preferredSwapchainFormat,
                preferredGraphicsAdapter,
                preferredGraphicsBackend
            ) =>
            {
                settings.PreferredSwapchainFormat = preferredSwapchainFormat;
                settings.PreferredGraphicsAdapter = preferredGraphicsAdapter;
                settings.PreferredGraphicsBackend = preferredGraphicsBackend;
                settingsStore.Save(settings);
                StopRealtimeLoops();
                var reinitializeState = ReinitializeOpenXr(
                    logger,
                    settings.PreferredSwapchainFormat,
                    settings.PreferredGraphicsAdapter,
                    settings.PreferredGraphicsBackend
                );
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
                mainViewModel.StatusMessage = reinitializeState.IsInitialized
                    ? $"Video render settings applied: {settings.PreferredSwapchainFormat}, {settings.PreferredGraphicsBackend}"
                    : "Video render settings apply failed.";
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
                    var selected = await _windowCaptureService.PickAndStartCaptureAsync(MainWindow);
                    mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
                    mainViewModel.StatusMessage = selected
                        ? "Capture target selected."
                        : "Capture target selection canceled.";
                }
                catch (Exception ex)
                {
                    logger.Error("Capture target selection failed.", ex);
                    mainViewModel.CaptureStatus = _windowCaptureService.GetStatusText();
                    mainViewModel.StatusMessage = "Capture target selection failed.";
                }
            };

            InitializeWindowsInputTcpServer(logger, inputTcpPort, mainViewModel);
            _windowCaptureService = new WindowCaptureService(logger);
            _windowCaptureService.FrameCaptured += frame =>
            {
                _openXrControllerInputService?.SetLatestDecodedSbsFrame(frame);
            };
            mainViewModel.BridgeStatus =
                (_windowsInputTcpServerService?.StatusText ?? "Input TCP: not started")
                + EmulatorRouteHint;
            ResetVideoPipelineMetrics();
            mainViewModel.VideoStatus = WaitingVideoStatus;

            var initializeState = ReinitializeOpenXr(
                logger,
                settings.PreferredSwapchainFormat,
                settings.PreferredGraphicsAdapter,
                settings.PreferredGraphicsBackend
            );
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

                    if (_lastOpenXrStatus != stateSnapshot.Status)
                    {
                        _lastOpenXrStatus = stateSnapshot.Status;
                        logger.Info($"OpenXR input state: {stateSnapshot.Status}");
                    }

                    MaybeLogVideoLoopStall(logger);
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

    private static int ResolveValidWindowsInputTcpPort(int value)
    {
        if (value is >= 1 and <= 65535)
        {
            return value;
        }

        return DefaultWindowsInputTcpPort;
    }

    private void InitializeWindowsInputTcpServer(
        AppLogger logger,
        int inputTcpPort,
        MainViewModel mainViewModel
    )
    {
        var sanitizedPort = ResolveValidWindowsInputTcpPort(inputTcpPort);
        if (sanitizedPort != inputTcpPort)
        {
            mainViewModel.SetInputTcpPortForDisplay(sanitizedPort);
        }

        _windowsInputTcpServerService?.Dispose();
        _windowsInputTcpServerService = new WindowsInputTcpServerService(logger, sanitizedPort);
        _windowsInputTcpServerService.Start();
        logger.Info(_windowsInputTcpServerService.StatusText);
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
