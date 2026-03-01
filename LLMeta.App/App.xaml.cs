using System.Windows;
using LLMeta.App.Models;
using LLMeta.App.Services;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;
using Velopack;
using WinForms = System.Windows.Forms;

namespace LLMeta.App;

public partial class App : System.Windows.Application
{
    private WinForms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private bool _isExitRequested;
    private AppLogger? _logger;

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
        _logger = logger;
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
            var startupRegistryService = new StartupRegistryService();
            var openXrInputService = new OpenXrInputService();
            var openXrProbeResult = openXrInputService.ProbeHeadMountedDisplaySession();
            logger.Info(
                $"OpenXR enumerate extensions result: {openXrProbeResult.EnumerateExtensionsResult}"
            );
            logger.Info(
                $"OpenXR supports XR_KHR_D3D11_enable: {openXrProbeResult.SupportsKhrD3D11Enable}"
            );
            logger.Info(
                $"OpenXR supports XR_KHR_D3D12_enable: {openXrProbeResult.SupportsKhrD3D12Enable}"
            );
            logger.Info(
                $"OpenXR supports XR_MND_headless: {openXrProbeResult.SupportsMndHeadless}"
            );
            logger.Info($"OpenXR instance create result: {openXrProbeResult.InstanceCreateResult}");
            logger.Info(
                $"OpenXR get system result: {openXrProbeResult.GetSystemResult}, systemId: {openXrProbeResult.SystemId}"
            );
            logger.Info(
                $"OpenXR get D3D11 graphics requirements result: {openXrProbeResult.GetD3D11GraphicsRequirementsResult}"
            );
            logger.Info(
                $"OpenXR D3D11 create device HRESULT: 0x{openXrProbeResult.D3D11CreateDeviceHResult:X8}"
            );
            logger.Info($"OpenXR create session result: {openXrProbeResult.CreateSessionResult}");
            if (!string.IsNullOrWhiteSpace(openXrProbeResult.Diagnostics))
            {
                logger.Info($"OpenXR diagnostics: {openXrProbeResult.Diagnostics}");
            }

            var mainViewModel = new MainViewModel(
                settings,
                settingsStore,
                startupRegistryService,
                logger
            );

            _mainViewModel = mainViewModel;

            if (settings.StartWithWindows)
            {
                var exePath = startupRegistryService.ResolveExecutablePath();
                startupRegistryService.Enable(exePath);
            }
            else
            {
                startupRegistryService.Disable();
            }

            _mainWindow = new MainWindow { DataContext = mainViewModel };
            MainWindow = _mainWindow;
            _mainWindow.Closing += OnMainWindowClosing;

            if (settings.StartMinimized)
            {
                _mainWindow.WindowState = WindowState.Minimized;
                _mainWindow.Hide();
            }
            else
            {
                _mainWindow.Show();
            }

            InitializeTrayIcon();

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
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void InitializeTrayIcon()
    {
        if (_mainViewModel is null)
        {
            return;
        }

        var menu = new WinForms.ContextMenuStrip();

        var openItem = new WinForms.ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowMainWindow(forceShow: true);

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var exePath = new StartupRegistryService().ResolveExecutablePath();
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "LLMeta",
        };
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowMainWindow(forceShow: true);
            }
        };

        _mainViewModel.ExitRequested += (_, _) => ExitApp();
    }

    private void ShowMainWindow(bool forceShow)
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (forceShow)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        _isExitRequested = true;
        _notifyIcon?.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }
}
