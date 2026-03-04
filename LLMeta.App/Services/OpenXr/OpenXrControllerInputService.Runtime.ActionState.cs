using LLMeta.App.Models;
using Silk.NET.OpenXR;
using XrAction = Silk.NET.OpenXR.Action;

namespace LLMeta.App.Services;

public sealed unsafe partial class OpenXrControllerInputService
{
    private OpenXrHeadPoseState LocateHeadPose()
    {
        if (
            _xr is null
            || _viewSpace.Handle == 0
            || _localSpace.Handle == 0
            || _predictedDisplayTime == 0
        )
        {
            return CreateEmptyHeadPose();
        }

        var velocity = new SpaceVelocity { Type = StructureType.SpaceVelocity };
        var location = new SpaceLocation { Type = StructureType.SpaceLocation, Next = &velocity };
        var locateResult = _xr.LocateSpace(
            _viewSpace,
            _localSpace,
            _predictedDisplayTime,
            ref location
        );
        if (locateResult != Result.Success)
        {
            return CreateEmptyHeadPose();
        }

        var locationFlags = location.LocationFlags;
        var velocityFlags = velocity.VelocityFlags;
        var orientation = location.Pose.Orientation;
        var euler = ToEulerDegrees(orientation);

        return new OpenXrHeadPoseState(
            (locationFlags & SpaceLocationFlags.PositionValidBit) != 0,
            (locationFlags & SpaceLocationFlags.PositionTrackedBit) != 0,
            (locationFlags & SpaceLocationFlags.OrientationValidBit) != 0,
            (locationFlags & SpaceLocationFlags.OrientationTrackedBit) != 0,
            location.Pose.Position.X,
            location.Pose.Position.Y,
            location.Pose.Position.Z,
            euler.Yaw,
            euler.Pitch,
            euler.Roll,
            (velocityFlags & SpaceVelocityFlags.LinearValidBit) != 0,
            (velocityFlags & SpaceVelocityFlags.AngularValidBit) != 0,
            velocity.LinearVelocity.X,
            velocity.LinearVelocity.Y,
            velocity.LinearVelocity.Z,
            velocity.AngularVelocity.X,
            velocity.AngularVelocity.Y,
            velocity.AngularVelocity.Z
        );
    }

    private static bool CanSyncActionsInCurrentState(SessionState state)
    {
        return state == SessionState.Focused;
    }

    private bool GetBooleanActionState(XrAction action)
    {
        if (_xr is null)
        {
            return false;
        }

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = action,
            SubactionPath = XR.NullPath,
        };
        var state = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
        var result = _xr.GetActionStateBoolean(_session, ref getInfo, ref state);
        if (result != Result.Success || state.IsActive == 0)
        {
            return false;
        }

        return state.CurrentState != 0;
    }

    private Vector2f GetVector2ActionState(XrAction action)
    {
        if (_xr is null)
        {
            return new Vector2f();
        }

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = action,
            SubactionPath = XR.NullPath,
        };
        var state = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
        var result = _xr.GetActionStateVector2(_session, ref getInfo, ref state);
        if (result != Result.Success || state.IsActive == 0)
        {
            return new Vector2f();
        }

        return state.CurrentState;
    }

    private float GetFloatActionState(XrAction action)
    {
        if (_xr is null)
        {
            return 0;
        }

        var getInfo = new ActionStateGetInfo
        {
            Type = StructureType.ActionStateGetInfo,
            Action = action,
            SubactionPath = XR.NullPath,
        };
        var state = new ActionStateFloat { Type = StructureType.ActionStateFloat };
        var result = _xr.GetActionStateFloat(_session, ref getInfo, ref state);
        if (result != Result.Success || state.IsActive == 0)
        {
            return 0;
        }

        return state.CurrentState;
    }

    private static OpenXrHeadPoseState CreateEmptyHeadPose()
    {
        return new OpenXrHeadPoseState(
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
        );
    }

    private static (float Yaw, float Pitch, float Roll) ToEulerDegrees(Quaternionf q)
    {
        var sinrCosp = 2.0f * (q.W * q.X + q.Y * q.Z);
        var cosrCosp = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
        var roll = MathF.Atan2(sinrCosp, cosrCosp);

        var sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
        var pitch =
            MathF.Abs(sinp) >= 1.0f ? MathF.CopySign(MathF.PI / 2.0f, sinp) : MathF.Asin(sinp);

        var sinyCosp = 2.0f * (q.W * q.Z + q.X * q.Y);
        var cosyCosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
        var yaw = MathF.Atan2(sinyCosp, cosyCosp);

        const float rad2Deg = 57.2957795f;
        return (yaw * rad2Deg, pitch * rad2Deg, roll * rad2Deg);
    }
}
