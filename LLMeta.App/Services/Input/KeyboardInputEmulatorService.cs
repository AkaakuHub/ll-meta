using System.Collections.Generic;
using System.Windows.Input;
using LLMeta.App.Models;

namespace LLMeta.App.Services;

public sealed class KeyboardInputEmulatorService
{
    private readonly HashSet<Key> _pressedKeys = new();
    private float _emulatedYawDegrees;
    private float _emulatedPitchDegrees;
    private float _emulatedRollDegrees;
    private float _emulatedPositionX;
    private float _emulatedPositionY = 1.6f;
    private float _emulatedPositionZ;

    public void OnKeyDown(Key key)
    {
        _pressedKeys.Add(key);
    }

    public void OnKeyUp(Key key)
    {
        _ = _pressedKeys.Remove(key);
    }

    public OpenXrControllerState BuildState()
    {
        ApplyHeadPoseStep();

        return new OpenXrControllerState(
            true,
            "Debug keyboard mode",
            new OpenXrHeadPoseState(
                true,
                true,
                true,
                true,
                _emulatedPositionX,
                _emulatedPositionY,
                _emulatedPositionZ,
                _emulatedYawDegrees,
                _emulatedPitchDegrees,
                _emulatedRollDegrees,
                false,
                false,
                0,
                0,
                0,
                0,
                0,
                0
            ),
            BuildAxis(Key.A, Key.D),
            BuildAxis(Key.S, Key.W),
            BuildAxis(Key.J, Key.L),
            BuildAxis(Key.K, Key.I),
            BuildTriggerValue(Key.Q),
            BuildTriggerValue(Key.E),
            BuildTriggerValue(Key.U),
            BuildTriggerValue(Key.O),
            IsPressed(Key.LeftShift),
            IsPressed(Key.RightShift),
            IsPressed(Key.D1),
            IsPressed(Key.D2),
            IsPressed(Key.D3),
            IsPressed(Key.D4)
        );
    }

    public OpenXrControllerState BuildUnavailableState(string status)
    {
        return new OpenXrControllerState(
            false,
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

    private float BuildAxis(Key negative, Key positive)
    {
        var value = 0.0f;
        if (IsPressed(negative))
        {
            value -= 1.0f;
        }

        if (IsPressed(positive))
        {
            value += 1.0f;
        }

        return value;
    }

    private float BuildTriggerValue(Key key)
    {
        return IsPressed(key) ? 1.0f : 0.0f;
    }

    private bool IsPressed(Key key)
    {
        return _pressedKeys.Contains(key);
    }

    private void ApplyHeadPoseStep()
    {
        const float angleStep = 2.0f;
        const float positionStep = 0.02f;

        if (IsPressed(Key.Left))
        {
            _emulatedYawDegrees -= angleStep;
        }

        if (IsPressed(Key.Right))
        {
            _emulatedYawDegrees += angleStep;
        }

        if (IsPressed(Key.Up))
        {
            _emulatedPitchDegrees += angleStep;
        }

        if (IsPressed(Key.Down))
        {
            _emulatedPitchDegrees -= angleStep;
        }

        if (IsPressed(Key.PageUp))
        {
            _emulatedRollDegrees += angleStep;
        }

        if (IsPressed(Key.PageDown))
        {
            _emulatedRollDegrees -= angleStep;
        }

        if (IsPressed(Key.F))
        {
            _emulatedPositionX -= positionStep;
        }

        if (IsPressed(Key.H))
        {
            _emulatedPositionX += positionStep;
        }

        if (IsPressed(Key.T))
        {
            _emulatedPositionY += positionStep;
        }

        if (IsPressed(Key.G))
        {
            _emulatedPositionY -= positionStep;
        }

        if (IsPressed(Key.R))
        {
            _emulatedPositionZ += positionStep;
        }

        if (IsPressed(Key.V))
        {
            _emulatedPositionZ -= positionStep;
        }
    }
}
