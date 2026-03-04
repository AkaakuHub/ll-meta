using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
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

        ReleaseLatestVideoTexture();
        DestroyStereoRendering();

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
        _systemId = XR.NullSystemID;
    }
}
