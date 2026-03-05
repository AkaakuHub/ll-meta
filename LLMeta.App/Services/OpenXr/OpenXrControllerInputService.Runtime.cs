using LLMeta.App.Models;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private Result PollEvents()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        while (true)
        {
            var eventBuffer = new EventDataBuffer { Type = StructureType.EventDataBuffer };
            var result = _xr.PollEvent(_instance, ref eventBuffer);
            if (result == Result.EventUnavailable)
            {
                return result;
            }

            if (result != Result.Success)
            {
                return result;
            }

            if (eventBuffer.Type != StructureType.EventDataSessionStateChanged)
            {
                continue;
            }

            var stateChanged = *(EventDataSessionStateChanged*)&eventBuffer;
            if (stateChanged.Session.Handle != _session.Handle)
            {
                continue;
            }

            var previousState = _sessionState;
            _sessionState = stateChanged.State;
            _logger?.Info(
                $"OpenXR session state changed: {previousState} -> {_sessionState} running={_isSessionRunning}"
            );
            if (_sessionState == SessionState.Ready)
            {
                var beginInfo = new SessionBeginInfo
                {
                    Type = StructureType.SessionBeginInfo,
                    PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                };
                var beginResult = _xr.BeginSession(_session, ref beginInfo);
                _isSessionRunning = beginResult == Result.Success;
                _logger?.Info(
                    $"OpenXR begin session result: {beginResult} running={_isSessionRunning}"
                );
            }
            else if (_sessionState == SessionState.Stopping)
            {
                _ = _xr.EndSession(_session);
                _isSessionRunning = false;
                _logger?.Info("OpenXR end session requested due to Stopping state.");
            }
            else if (
                _sessionState == SessionState.Exiting
                || _sessionState == SessionState.LossPending
            )
            {
                _isSessionRunning = false;
            }
        }
    }

    private Result PumpFrame()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var waitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var frameState = new FrameState { Type = StructureType.FrameState };
        var waitResult = _xr.WaitFrame(_session, ref waitInfo, ref frameState);
        if (waitResult != Result.Success)
        {
            return waitResult;
        }
        _predictedDisplayTime = frameState.PredictedDisplayTime;

        var beginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        var beginResult = _xr.BeginFrame(_session, ref beginInfo);
        if (beginResult != Result.Success)
        {
            return beginResult;
        }

        if (frameState.ShouldRender == 0)
        {
            var endNoRenderInfo = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = (CompositionLayerBaseHeader**)0,
            };
            return _xr.EndFrame(_session, ref endNoRenderInfo);
        }

        var renderResult = RenderStereoProjectionLayer(frameState.PredictedDisplayTime, out _);
        if (renderResult != Result.Success)
        {
            return renderResult;
        }

        return Result.Success;
    }

    private Result InitializeHeadTrackingSpaces()
    {
        if (_xr is null)
        {
            return Result.ErrorHandleInvalid;
        }

        var identityPose = new Posef
        {
            Orientation = new Quaternionf
            {
                X = 0,
                Y = 0,
                Z = 0,
                W = 1,
            },
            Position = new Vector3f
            {
                X = 0,
                Y = 0,
                Z = 0,
            },
        };

        var localCreateInfo = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Local,
            PoseInReferenceSpace = identityPose,
        };
        var localCreateResult = _xr.CreateReferenceSpace(
            _session,
            ref localCreateInfo,
            ref _localSpace
        );
        if (localCreateResult != Result.Success)
        {
            return localCreateResult;
        }

        var viewCreateInfo = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.View,
            PoseInReferenceSpace = identityPose,
        };
        return _xr.CreateReferenceSpace(_session, ref viewCreateInfo, ref _viewSpace);
    }
}
