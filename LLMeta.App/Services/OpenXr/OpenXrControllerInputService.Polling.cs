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
}
