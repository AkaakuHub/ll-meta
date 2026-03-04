using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LLMeta.App.Models;
using LLMeta.App.Stores;
using LLMeta.App.Utils;
using LLMeta.App.ViewModels;

namespace LLMeta.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly AppLogger _logger;

    private string _statusMessage = "Ready";
    private string _sampleText = string.Empty;
    private string _openXrInputStatus = "OpenXR input: not initialized";
    private string _bridgeStatus = "Bridge: not started";
    private string _videoStatus = "Video: not started";
    private bool _isKeyboardDebugMode;
    private string _activeInputSource = "Input source: not selected";
    private string _hmdPoseState = "HMD: -";
    private string _leftControllerState = "Left: -";
    private string _rightControllerState = "Right: -";
    private List<string> _swapchainFormatOptions = ["Auto", "RGBA8", "BGRA8"];
    private string _selectedSwapchainFormatOption = "Auto";
    private List<string> _graphicsAdapterOptions = ["Auto"];
    private string _selectedGraphicsAdapterOption = "Auto";
    private List<string> _graphicsBackendOptions = ["D3D11"];
    private string _selectedGraphicsBackendOption = "D3D11";
    private string _videoRenderConfigStatus = "Video render config: not initialized";
    private string _videoRenderErrorStatus = "Video render error: none";

    public MainViewModel(AppSettings settings, SettingsStore settingsStore, AppLogger logger)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;
        _sampleText = settings.SampleText;

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        ReinitializeOpenXrCommand = new RelayCommand(_ => RequestReinitializeOpenXr());
        ApplyVideoRenderSettingsCommand = new RelayCommand(_ => RequestApplyVideoRenderSettings());
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SampleText
    {
        get => _sampleText;
        set => SetProperty(ref _sampleText, value);
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

    public ICommand SaveSettingsCommand { get; }
    public ICommand ReinitializeOpenXrCommand { get; }
    public ICommand ApplyVideoRenderSettingsCommand { get; }

    public event Action? OpenXrReinitializeRequested;
    public event Action<string, string, string>? VideoRenderSettingsApplyRequested;

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

    public List<string> SwapchainFormatOptions
    {
        get => _swapchainFormatOptions;
        set => SetProperty(ref _swapchainFormatOptions, value);
    }

    public string SelectedSwapchainFormatOption
    {
        get => _selectedSwapchainFormatOption;
        set =>
            SetProperty(ref _selectedSwapchainFormatOption, NormalizeSwapchainFormatOption(value));
    }

    public List<string> GraphicsAdapterOptions
    {
        get => _graphicsAdapterOptions;
        set => SetProperty(ref _graphicsAdapterOptions, value);
    }

    public string SelectedGraphicsAdapterOption
    {
        get => _selectedGraphicsAdapterOption;
        set =>
            SetProperty(ref _selectedGraphicsAdapterOption, NormalizeGraphicsAdapterOption(value));
    }

    public List<string> GraphicsBackendOptions
    {
        get => _graphicsBackendOptions;
        set => SetProperty(ref _graphicsBackendOptions, value);
    }

    public string SelectedGraphicsBackendOption
    {
        get => _selectedGraphicsBackendOption;
        set =>
            SetProperty(ref _selectedGraphicsBackendOption, NormalizeGraphicsBackendOption(value));
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

    public void UpdateVideoRenderConfig(OpenXrVideoRenderConfigState config, int lastFailureCode)
    {
        SetOptionsIfChanged(
            ref _swapchainFormatOptions,
            nameof(SwapchainFormatOptions),
            config.AvailableSwapchainFormats.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        );
        EnsureSelectionInOptions(
            ref _selectedSwapchainFormatOption,
            nameof(SelectedSwapchainFormatOption),
            NormalizeSwapchainFormatOption(config.RequestedSwapchainFormat),
            _swapchainFormatOptions
        );
        SetOptionsIfChanged(
            ref _graphicsAdapterOptions,
            nameof(GraphicsAdapterOptions),
            config.AvailableGraphicsAdapters.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        );
        EnsureSelectionInOptions(
            ref _selectedGraphicsAdapterOption,
            nameof(SelectedGraphicsAdapterOption),
            NormalizeGraphicsAdapterOption(config.RequestedGraphicsAdapter),
            _graphicsAdapterOptions
        );
        SetOptionsIfChanged(
            ref _graphicsBackendOptions,
            nameof(GraphicsBackendOptions),
            config.AvailableGraphicsBackends.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        );
        EnsureSelectionInOptions(
            ref _selectedGraphicsBackendOption,
            nameof(SelectedGraphicsBackendOption),
            NormalizeGraphicsBackendOption(config.RequestedGraphicsBackend),
            _graphicsBackendOptions
        );
        VideoRenderConfigStatus =
            $"Video render config: requested={config.RequestedSwapchainFormat} "
            + $"selected={config.SelectedSwapchainFormat} "
            + $"adapter={config.SelectedGraphicsAdapter} "
            + $"backend={config.SelectedGraphicsBackend} "
            + $"probe={config.ProbeSummary}";
        VideoRenderErrorStatus = BuildVideoRenderErrorStatus(lastFailureCode, VideoStatus);
    }

    public void UpdateOpenXrControllerState(OpenXrControllerState state)
    {
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

    private void SaveSettings()
    {
        _settings.SampleText = SampleText;
        _settingsStore.Save(_settings);
        StatusMessage = "Settings saved!";
        _logger.Info("Settings saved.");
    }

    private static string ToOnOff(bool value)
    {
        return value ? "ON" : "OFF";
    }

    private void RequestReinitializeOpenXr()
    {
        OpenXrReinitializeRequested?.Invoke();
    }

    private void RequestApplyVideoRenderSettings()
    {
        VideoRenderSettingsApplyRequested?.Invoke(
            SelectedSwapchainFormatOption,
            SelectedGraphicsAdapterOption,
            SelectedGraphicsBackendOption
        );
    }

    private static string NormalizeSwapchainFormatOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Auto";
        }

        var normalized = value.Trim();
        if (
            normalized.Equals("R8G8B8A8_UNORM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("RGBA8", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "RGBA8";
        }

        if (
            normalized.Equals("B8G8R8A8_UNORM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("BGRA8", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "BGRA8";
        }

        return "Auto";
    }

    private static string NormalizeGraphicsAdapterOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Auto";
        }

        return value.Trim();
    }

    private static string NormalizeGraphicsBackendOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "D3D11";
        }

        var normalized = value.Trim();
        if (normalized.Equals("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            return "D3D11";
        }

        return "D3D11";
    }

    private static string BuildVideoRenderErrorStatus(int code, string videoStatus)
    {
        if (
            code == 2
            && (
                videoStatus.Contains("waiting WebRTC frame", StringComparison.OrdinalIgnoreCase)
                || videoStatus.Contains("sync=waiting-keyframe", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return "Video render status: waiting first decoded frame";
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
            32 => "source is not NV12",
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

    private void SetOptionsIfChanged(
        ref List<string> target,
        string propertyName,
        List<string> next
    )
    {
        if (target.SequenceEqual(next, StringComparer.Ordinal))
        {
            return;
        }

        target = next;
        RaisePropertyChanged(propertyName);
    }

    private void EnsureSelectionInOptions(
        ref string selectedValue,
        string propertyName,
        string preferredValue,
        List<string> options
    )
    {
        if (options.Count == 0)
        {
            return;
        }

        if (options.Contains(selectedValue, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var nextValue = options.Contains(preferredValue, StringComparer.OrdinalIgnoreCase)
            ? preferredValue
            : options[0];
        if (string.Equals(selectedValue, nextValue, StringComparison.Ordinal))
        {
            return;
        }

        selectedValue = nextValue;
        RaisePropertyChanged(propertyName);
    }
}
