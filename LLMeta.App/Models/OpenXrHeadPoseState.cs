namespace LLMeta.App.Models;

public readonly record struct OpenXrHeadPoseState(
    bool IsPositionValid,
    bool IsPositionTracked,
    bool IsOrientationValid,
    bool IsOrientationTracked,
    float PositionX,
    float PositionY,
    float PositionZ,
    float YawDegrees,
    float PitchDegrees,
    float RollDegrees,
    bool IsLinearVelocityValid,
    bool IsAngularVelocityValid,
    float LinearVelocityX,
    float LinearVelocityY,
    float LinearVelocityZ,
    float AngularVelocityX,
    float AngularVelocityY,
    float AngularVelocityZ
);
