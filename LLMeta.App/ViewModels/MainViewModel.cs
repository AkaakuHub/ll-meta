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
    private bool _isKeyboardDebugMode;
    private string _hmdPoseState = "HMD: -";
    private string _leftControllerState = "Left: -";
    private string _rightControllerState = "Right: -";

    public MainViewModel(AppSettings settings, SettingsStore settingsStore, AppLogger logger)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _logger = logger;
        _sampleText = settings.SampleText;

        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
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

    public string BridgeStatus
    {
        get => _bridgeStatus;
        set => SetProperty(ref _bridgeStatus, value);
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
}
