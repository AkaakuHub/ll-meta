using System;
using System.Globalization;
using System.Windows.Input;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Utils;

namespace OpenKikaiSan.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppLogger _logger;
    private readonly RelayCommand _applyInputTcpPortCommand;

    private string _statusMessage = "Ready";
    private string _openXrInputStatus = "OpenXR input: not initialized";
    private string _bridgeStatus = "Input TCP: not started";
    private string _videoStatus = "Video: not started";
    private bool _isKeyboardDebugMode;
    private string _activeInputSource = "Input source: not selected";
    private string _hmdPoseState = "HMD: -";
    private string _leftControllerState = "Left: -";
    private string _rightControllerState = "Right: -";
    private string _videoRenderConfigStatus = "Video render config: not initialized";
    private string _videoRenderErrorStatus = "Video render error: none";
    private string _windowsInputTcpPort = string.Empty;
    private string _activeWindowsInputTcpPort = string.Empty;
    private int _appliedWindowsInputTcpPort;
    private string _captureStatus = "Capture: not selected";
    private OpenXrControllerState _currentOpenXrState;

    public MainViewModel(AppSettings settings, AppLogger logger)
    {
        _logger = logger;
        _appliedWindowsInputTcpPort = settings.WindowsInputTcpPort;
        _windowsInputTcpPort = settings.WindowsInputTcpPort.ToString(CultureInfo.InvariantCulture);
        _activeWindowsInputTcpPort = settings.WindowsInputTcpPort.ToString(
            CultureInfo.InvariantCulture
        );

        _applyInputTcpPortCommand = new RelayCommand(
            _ => ApplyInputTcpPort(),
            _ => CanApplyInputTcpPort()
        );
        ApplyInputTcpPortCommand = _applyInputTcpPortCommand;
        ReinitializeOpenXrCommand = new RelayCommand(_ => RequestReinitializeOpenXr());
        SelectCaptureTargetCommand = new RelayCommand(_ => RequestSelectCaptureTarget());
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string OpenXrInputStatus
    {
        get => _openXrInputStatus;
        set => SetProperty(ref _openXrInputStatus, value);
    }

    public bool IsKeyboardDebugMode
    {
        get => _isKeyboardDebugMode;
        set => SetProperty(ref _isKeyboardDebugMode, value);
    }

    public string LeftControllerState
    {
        get => _leftControllerState;
        set => SetProperty(ref _leftControllerState, value);
    }

    public string HmdPoseState
    {
        get => _hmdPoseState;
        set => SetProperty(ref _hmdPoseState, value);
    }

    public string RightControllerState
    {
        get => _rightControllerState;
        set => SetProperty(ref _rightControllerState, value);
    }

    public ICommand ApplyInputTcpPortCommand { get; }
    public ICommand ReinitializeOpenXrCommand { get; }
    public ICommand SelectCaptureTargetCommand { get; }

    public event Action? OpenXrReinitializeRequested;
    public event Action<int>? InputTcpPortApplyRequested;
    public event Action? CaptureTargetSelectionRequested;

    public string BridgeStatus
    {
        get => _bridgeStatus;
        set => SetProperty(ref _bridgeStatus, value);
    }

    public string VideoStatus
    {
        get => _videoStatus;
        set => SetProperty(ref _videoStatus, value);
    }

    public string ActiveInputSource
    {
        get => _activeInputSource;
        set => SetProperty(ref _activeInputSource, value);
    }

    public string VideoRenderConfigStatus
    {
        get => _videoRenderConfigStatus;
        set => SetProperty(ref _videoRenderConfigStatus, value);
    }

    public string VideoRenderErrorStatus
    {
        get => _videoRenderErrorStatus;
        set => SetProperty(ref _videoRenderErrorStatus, value);
    }

    public string WindowsInputTcpPort
    {
        get => _windowsInputTcpPort;
        set
        {
            SetProperty(ref _windowsInputTcpPort, value);
            _applyInputTcpPortCommand.RaiseCanExecuteChanged();
        }
    }

    public string ActiveWindowsInputTcpPort
    {
        get => _activeWindowsInputTcpPort;
        private set => SetProperty(ref _activeWindowsInputTcpPort, value);
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        set => SetProperty(ref _captureStatus, value);
    }

    public OpenXrControllerState CurrentOpenXrState
    {
        get => _currentOpenXrState;
        private set => SetProperty(ref _currentOpenXrState, value);
    }

    public void UpdateVideoRenderConfig(OpenXrVideoRenderConfigState config, int lastFailureCode)
    {
        VideoRenderConfigStatus =
            $"Video render config: swapchain={config.SelectedSwapchainFormat} "
            + $"runtimeAdapter={config.RuntimeGraphicsAdapter} "
            + $"backend={config.GraphicsBackend} "
            + $"probe={config.ProbeSummary}";
        VideoRenderErrorStatus = BuildVideoRenderErrorStatus(lastFailureCode, VideoStatus);
    }

    public void UpdateOpenXrControllerState(OpenXrControllerState state)
    {
        CurrentOpenXrState = state;
        OpenXrInputStatus = $"OpenXR: {state.Status}";
        HmdPoseState =
            $"HMD Pos({state.HeadPose.PositionX:0.000}, {state.HeadPose.PositionY:0.000}, {state.HeadPose.PositionZ:0.000}) "
            + $"YPR({state.HeadPose.YawDegrees:0.0}, {state.HeadPose.PitchDegrees:0.0}, {state.HeadPose.RollDegrees:0.0}) "
            + $"| PosValid:{ToOnOff(state.HeadPose.IsPositionValid)} PosTracked:{ToOnOff(state.HeadPose.IsPositionTracked)} "
            + $"OriValid:{ToOnOff(state.HeadPose.IsOrientationValid)} OriTracked:{ToOnOff(state.HeadPose.IsOrientationTracked)}";
        LeftControllerState =
            $"Left Stick ({state.LeftStickX:0.00}, {state.LeftStickY:0.00}) Click:{ToOnOff(state.LeftStickClickPressed)} | X:{ToOnOff(state.LeftXPressed)} Y:{ToOnOff(state.LeftYPressed)} | Trigger:{state.LeftTriggerValue:0.00} | Grip:{state.LeftGripValue:0.00}";
        RightControllerState =
            $"Right Stick ({state.RightStickX:0.00}, {state.RightStickY:0.00}) Click:{ToOnOff(state.RightStickClickPressed)} | A:{ToOnOff(state.RightAPressed)} B:{ToOnOff(state.RightBPressed)} | Trigger:{state.RightTriggerValue:0.00} | Grip:{state.RightGripValue:0.00}";
    }

    public void SetInputTcpPortForDisplay(int port)
    {
        _appliedWindowsInputTcpPort = port;
        ActiveWindowsInputTcpPort = port.ToString(CultureInfo.InvariantCulture);
        WindowsInputTcpPort = port.ToString(CultureInfo.InvariantCulture);
        _applyInputTcpPortCommand.RaiseCanExecuteChanged();
    }

    private void ApplyInputTcpPort()
    {
        if (
            !int.TryParse(
                WindowsInputTcpPort.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var port
            )
        )
        {
            StatusMessage = "Invalid port format.";
            return;
        }

        if (port < 1 || port > 65535)
        {
            StatusMessage = "Port must be 1 to 65535.";
            return;
        }

        WindowsInputTcpPort = port.ToString(CultureInfo.InvariantCulture);
        _logger.Info($"Input TCP port apply requested: {port}");
        InputTcpPortApplyRequested?.Invoke(port);
    }

    private bool CanApplyInputTcpPort()
    {
        if (
            !int.TryParse(
                WindowsInputTcpPort.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var port
            )
        )
        {
            return false;
        }

        if (port < 1 || port > 65535)
        {
            return false;
        }

        return port != _appliedWindowsInputTcpPort;
    }

    private static string ToOnOff(bool value)
    {
        return value ? "ON" : "OFF";
    }

    private void RequestReinitializeOpenXr()
    {
        OpenXrReinitializeRequested?.Invoke();
    }

    private void RequestSelectCaptureTarget()
    {
        CaptureTargetSelectionRequested?.Invoke();
    }

    private static string BuildVideoRenderErrorStatus(int code, string videoStatus)
    {
        if (
            code == 2
            && (
                videoStatus.Contains("waiting capture frame", StringComparison.OrdinalIgnoreCase)
                || videoStatus.Contains("Capture: not selected", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "Video render status: waiting first captured frame";
        }

        return $"Video render error: code={code} ({DescribeRenderFailureCode(code)})";
    }

    private static string DescribeRenderFailureCode(int code)
    {
        return code switch
        {
            0 => "none",
            1 => "D3D11 device context unavailable",
            2 => "latest decoded texture unavailable",
            4 => "invalid stereo source width",
            31 => "format conversion entry failed",
            32 => "source format conversion rejected",
            34 => "VideoProcessor resources missing",
            35 => "D3D11 device/context unavailable",
            36 => "ID3D11VideoDevice unavailable",
            37 => "ID3D11VideoContext unavailable",
            38 => "CreateVideoProcessorEnumerator failed",
            39 => "NV12 input format check failed",
            40 => "NV12 input unsupported",
            41 => "target format check failed",
            42 => "VideoProcessorBlt failed",
            43 => "target output format unsupported",
            44 => "CreateVideoProcessor failed",
            45 => "VideoProcessor output texture create failed",
            46 => "VideoProcessor output view create failed",
            47 => "VideoProcessor input view create failed",
            _ => "unknown",
        };
    }
}
