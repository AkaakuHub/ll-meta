using LLMeta.App.Models;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService : IDisposable
{
    private XR? _xr;
    private Instance _instance;
    private Session _session;
    private ActionSet _actionSet;
    private XrAction _leftStickAction;
    private XrAction _rightStickAction;
    private XrAction _leftXAction;
    private XrAction _leftYAction;
    private XrAction _rightAAction;
    private XrAction _rightBAction;
    private XrAction _leftTriggerAction;
    private XrAction _leftGripAction;
    private XrAction _rightTriggerAction;
    private XrAction _rightGripAction;
    private XrAction _leftStickClickAction;
    private XrAction _rightStickClickAction;
    private Space _localSpace;
    private Space _viewSpace;
    private long _predictedDisplayTime;
    private ID3D11Device* _d3d11Device;
    private ID3D11DeviceContext* _d3d11DeviceContext;
    private SessionState _sessionState = SessionState.Unknown;
    private bool _isSessionRunning;
    private bool _isInitialized;
    private string _bindingSupportSummary = string.Empty;

    public OpenXrControllerState Initialize()
    {
        if (_isInitialized)
        {
            return CreateState("Initialized");
        }

        _xr = XR.GetApi();
        var extensionSupport = ProbeInstanceExtensionSupport(_xr);
        if (extensionSupport.EnumerateResult != Result.Success)
        {
            return CreateState($"Enumerate extensions failed: {extensionSupport.EnumerateResult}");
        }

        if (!extensionSupport.SupportsKhrD3D11Enable)
        {
            return CreateState("XR_KHR_D3D11_enable is not supported.");
        }

        var applicationInfo = CreateApplicationInfo();
        var enabledExtensions = new[] { "XR_KHR_D3D11_enable" };
        var enabledExtensionsPointer = (byte**)
            SilkMarshal.StringArrayToPtr(enabledExtensions, NativeStringEncoding.UTF8);

        try
        {
            var instanceCreateInfo = new InstanceCreateInfo
            {
                Type = StructureType.InstanceCreateInfo,
                ApplicationInfo = applicationInfo,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledExtensionNames = enabledExtensionsPointer,
            };
            var createInstanceResult = _xr.CreateInstance(ref instanceCreateInfo, ref _instance);
            if (createInstanceResult != Result.Success)
            {
                return CreateState($"CreateInstance failed: {createInstanceResult}");
            }

            var systemGetInfo = new SystemGetInfo
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay,
            };
            ulong systemId = XR.NullSystemID;
            var getSystemResult = _xr.GetSystem(_instance, ref systemGetInfo, ref systemId);
            if (getSystemResult != Result.Success)
            {
                return CreateState($"GetSystem failed: {getSystemResult}");
            }

            var getRequirementsResult = GetD3D11GraphicsRequirements(_instance, systemId);
            if (getRequirementsResult != Result.Success)
            {
                return CreateState($"GetD3D11GraphicsRequirements failed: {getRequirementsResult}");
            }

            var d3d11CreateResult = CreateD3D11Device();
            if (d3d11CreateResult != 0)
            {
                return CreateState($"D3D11 create failed: 0x{d3d11CreateResult:X8}");
            }

            var graphicsBinding = new GraphicsBindingD3D11KHR
            {
                Type = StructureType.GraphicsBindingD3D11Khr,
                Device = _d3d11Device,
            };
            var sessionCreateInfo = new SessionCreateInfo
            {
                Type = StructureType.SessionCreateInfo,
                Next = &graphicsBinding,
                SystemId = systemId,
            };
            var createSessionResult = _xr.CreateSession(
                _instance,
                ref sessionCreateInfo,
                ref _session
            );
            if (createSessionResult != Result.Success)
            {
                return CreateState($"CreateSession failed: {createSessionResult}");
            }

            var initializeActionsResult = InitializeActions();
            if (initializeActionsResult != Result.Success)
            {
                return CreateState($"InitializeActions failed: {initializeActionsResult}");
            }

            var initializeHeadTrackingResult = InitializeHeadTrackingSpaces();
            if (initializeHeadTrackingResult != Result.Success)
            {
                return CreateState(
                    $"InitializeHeadTrackingSpaces failed: {initializeHeadTrackingResult}"
                );
            }

            _isInitialized = true;
            if (_bindingSupportSummary.Length > 0)
            {
                return CreateState($"Initialized | {_bindingSupportSummary}");
            }

            return CreateState("Initialized");
        }
        catch (Exception ex)
        {
            return CreateState($"Initialize exception: {ex.Message}");
        }
        finally
        {
            SilkMarshal.Free((nint)enabledExtensionsPointer);
        }
    }

    public OpenXrControllerState Poll()
    {
        if (!_isInitialized || _xr is null)
        {
            return CreateState("Not initialized");
        }

        if (_session.Handle == 0)
        {
            return CreateState("Session is not created");
        }

        var pollEventsResult = PollEvents();
        if (pollEventsResult != Result.Success && pollEventsResult != Result.EventUnavailable)
        {
            return CreateState($"PollEvent failed: {pollEventsResult}");
        }

        if (!_isSessionRunning)
        {
            return CreateState($"Session state: {_sessionState}");
        }

        var frameResult = PumpFrame();
        if (frameResult != Result.Success)
        {
            return CreateState($"Frame loop failed: {frameResult}");
        }

        if (!CanSyncActionsInCurrentState(_sessionState))
        {
            return CreateState($"Session state: {_sessionState} (waiting focus)");
        }

        var syncResult = SyncActions();
        if (syncResult == Result.SessionNotFocused)
        {
            return CreateState($"Session state: {_sessionState} (waiting focus)");
        }

        if (syncResult != Result.Success)
        {
            return CreateState($"SyncAction failed: {syncResult}");
        }

        var leftStick = GetVector2ActionState(_leftStickAction);
        var rightStick = GetVector2ActionState(_rightStickAction);
        var leftX = GetBooleanActionState(_leftXAction);
        var leftY = GetBooleanActionState(_leftYAction);
        var rightA = GetBooleanActionState(_rightAAction);
        var rightB = GetBooleanActionState(_rightBAction);
        var leftTrigger = GetFloatActionState(_leftTriggerAction);
        var leftGrip = GetFloatActionState(_leftGripAction);
        var rightTrigger = GetFloatActionState(_rightTriggerAction);
        var rightGrip = GetFloatActionState(_rightGripAction);
        var leftStickClick = GetBooleanActionState(_leftStickClickAction);
        var rightStickClick = GetBooleanActionState(_rightStickClickAction);
        var headPose = LocateHeadPose();

        return new OpenXrControllerState(
            true,
            $"Session state: {_sessionState}",
            headPose,
            leftStick.X,
            leftStick.Y,
            rightStick.X,
            rightStick.Y,
            leftTrigger,
            leftGrip,
            rightTrigger,
            rightGrip,
            leftStickClick,
            rightStickClick,
            leftX,
            leftY,
            rightA,
            rightB
        );
    }

    public void Dispose()
    {
        if (_xr is null)
        {
            return;
        }

        if (_leftStickAction.Handle != 0)
        {
            _xr.DestroyAction(_leftStickAction);
            _leftStickAction = default;
        }

        if (_rightStickAction.Handle != 0)
        {
            _xr.DestroyAction(_rightStickAction);
            _rightStickAction = default;
        }

        if (_leftXAction.Handle != 0)
        {
            _xr.DestroyAction(_leftXAction);
            _leftXAction = default;
        }

        if (_leftYAction.Handle != 0)
        {
            _xr.DestroyAction(_leftYAction);
            _leftYAction = default;
        }

        if (_rightAAction.Handle != 0)
        {
            _xr.DestroyAction(_rightAAction);
            _rightAAction = default;
        }

        if (_rightBAction.Handle != 0)
        {
            _xr.DestroyAction(_rightBAction);
            _rightBAction = default;
        }

        if (_leftTriggerAction.Handle != 0)
        {
            _xr.DestroyAction(_leftTriggerAction);
            _leftTriggerAction = default;
        }

        if (_leftGripAction.Handle != 0)
        {
            _xr.DestroyAction(_leftGripAction);
            _leftGripAction = default;
        }

        if (_rightTriggerAction.Handle != 0)
        {
            _xr.DestroyAction(_rightTriggerAction);
            _rightTriggerAction = default;
        }

        if (_rightGripAction.Handle != 0)
        {
            _xr.DestroyAction(_rightGripAction);
            _rightGripAction = default;
        }

        if (_leftStickClickAction.Handle != 0)
        {
            _xr.DestroyAction(_leftStickClickAction);
            _leftStickClickAction = default;
        }

        if (_rightStickClickAction.Handle != 0)
        {
            _xr.DestroyAction(_rightStickClickAction);
            _rightStickClickAction = default;
        }

        if (_actionSet.Handle != 0)
        {
            _xr.DestroyActionSet(_actionSet);
            _actionSet = default;
        }

        if (_viewSpace.Handle != 0)
        {
            _xr.DestroySpace(_viewSpace);
            _viewSpace = default;
        }

        if (_localSpace.Handle != 0)
        {
            _xr.DestroySpace(_localSpace);
            _localSpace = default;
        }

        if (_session.Handle != 0)
        {
            if (_isSessionRunning)
            {
                _xr.EndSession(_session);
                _isSessionRunning = false;
            }

            _xr.DestroySession(_session);
            _session = default;
        }

        if (_instance.Handle != 0)
        {
            _xr.DestroyInstance(_instance);
            _instance = default;
        }

        if (_d3d11DeviceContext is not null)
        {
            _ = _d3d11DeviceContext->Release();
            _d3d11DeviceContext = null;
        }

        if (_d3d11Device is not null)
        {
            _ = _d3d11Device->Release();
            _d3d11Device = null;
        }

        _isInitialized = false;
        _sessionState = SessionState.Unknown;
    }

    private OpenXrControllerState CreateState(string status)
    {
        return new OpenXrControllerState(
            _isInitialized,
            status,
            new OpenXrHeadPoseState(
                false,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0
            ),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            false,
            false,
            false,
            false,
            false
        );
    }
}
