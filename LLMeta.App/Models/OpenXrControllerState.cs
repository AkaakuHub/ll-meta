namespace LLMeta.App.Models;

public readonly record struct OpenXrControllerState(
    bool IsInitialized,
    string Status,
    OpenXrHeadPoseState HeadPose,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY,
    float LeftTriggerValue,
    float LeftGripValue,
    float RightTriggerValue,
    float RightGripValue,
    bool LeftStickClickPressed,
    bool RightStickClickPressed,
    bool LeftXPressed,
    bool LeftYPressed,
    bool RightAPressed,
    bool RightBPressed
);
