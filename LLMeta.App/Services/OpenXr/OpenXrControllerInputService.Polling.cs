using LLMeta.App.Models;
using Silk.NET.OpenXR;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
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
        var ipdMeters = GetCurrentIpdMeters();
        var hmdVerticalFovDegrees = GetCurrentVerticalFovDegrees();
        var leftEyeFov = GetCurrentEyeFovRadians(0, hmdVerticalFovDegrees);
        var rightEyeFov = GetCurrentEyeFovRadians(1, hmdVerticalFovDegrees);
        LogInputTelemetry(ipdMeters, hmdVerticalFovDegrees, leftEyeFov, rightEyeFov);

        return new OpenXrControllerState(
            true,
            $"Session state: {_sessionState}",
            headPose,
            ipdMeters,
            hmdVerticalFovDegrees,
            leftEyeFov.AngleLeft,
            leftEyeFov.AngleRight,
            leftEyeFov.AngleUp,
            leftEyeFov.AngleDown,
            rightEyeFov.AngleLeft,
            rightEyeFov.AngleRight,
            rightEyeFov.AngleUp,
            rightEyeFov.AngleDown,
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

    private float GetCurrentIpdMeters()
    {
        const float fallbackIpdMeters = 0.064f;
        var left = _views[0].Pose.Position;
        var right = _views[1].Pose.Position;
        var dx = right.X - left.X;
        var dy = right.Y - left.Y;
        var dz = right.Z - left.Z;
        var ipdMeters = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (float.IsNaN(ipdMeters) || float.IsInfinity(ipdMeters))
        {
            return fallbackIpdMeters;
        }

        if (ipdMeters < 0.01f || ipdMeters > 0.12f)
        {
            return fallbackIpdMeters;
        }

        return ipdMeters;
    }

    private float GetCurrentVerticalFovDegrees()
    {
        const float fallbackVerticalFovDegrees = 90.0f;
        var leftFov = _views[0].Fov;
        var verticalFovRad = leftFov.AngleUp - leftFov.AngleDown;
        if (float.IsNaN(verticalFovRad) || float.IsInfinity(verticalFovRad))
        {
            return fallbackVerticalFovDegrees;
        }

        const float radToDeg = 57.2957795f;
        var verticalFovDegrees = verticalFovRad * radToDeg;
        if (verticalFovDegrees < 20.0f || verticalFovDegrees > 170.0f)
        {
            return fallbackVerticalFovDegrees;
        }

        return verticalFovDegrees;
    }

    private Fovf GetCurrentEyeFovRadians(int eyeIndex, float fallbackVerticalFovDegrees)
    {
        var fallbackHalfVertical = (fallbackVerticalFovDegrees * 0.0174532925f) * 0.5f;
        var fallback = new Fovf
        {
            AngleLeft = -fallbackHalfVertical,
            AngleRight = fallbackHalfVertical,
            AngleUp = fallbackHalfVertical,
            AngleDown = -fallbackHalfVertical,
        };

        if (eyeIndex < 0 || eyeIndex >= _views.Length)
        {
            return fallback;
        }

        var fov = _views[eyeIndex].Fov;
        if (
            float.IsNaN(fov.AngleLeft)
            || float.IsNaN(fov.AngleRight)
            || float.IsNaN(fov.AngleUp)
            || float.IsNaN(fov.AngleDown)
            || float.IsInfinity(fov.AngleLeft)
            || float.IsInfinity(fov.AngleRight)
            || float.IsInfinity(fov.AngleUp)
            || float.IsInfinity(fov.AngleDown)
        )
        {
            return fallback;
        }

        if (fov.AngleLeft >= fov.AngleRight || fov.AngleDown >= fov.AngleUp)
        {
            return fallback;
        }

        return fov;
    }

    private void LogInputTelemetry(
        float ipdMeters,
        float hmdVerticalFovDegrees,
        Fovf leftFov,
        Fovf rightFov
    )
    {
        if (_logger is null)
        {
            return;
        }

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowUnixMs - _lastInputTelemetryLogUnixMs < 1000)
        {
            return;
        }

        _lastInputTelemetryLogUnixMs = nowUnixMs;
        _logger.Info(
            "OpenXR telemetry: "
                + $"ipdMeters={ipdMeters:F4} "
                + $"vFovDeg={hmdVerticalFovDegrees:F2} "
                + $"leftFov=(L:{leftFov.AngleLeft:F4},R:{leftFov.AngleRight:F4},U:{leftFov.AngleUp:F4},D:{leftFov.AngleDown:F4}) "
                + $"rightFov=(L:{rightFov.AngleLeft:F4},R:{rightFov.AngleRight:F4},U:{rightFov.AngleUp:F4},D:{rightFov.AngleDown:F4})"
        );
    }
}
