using System.Buffers.Binary;
using LLMeta.App.Models;

namespace LLMeta.App.Services;

public sealed partial class AndroidInputBridgeTcpServerService
{
    private void BuildPacket(byte[] destination, BridgeFrame frame, uint sequence)
    {
        var span = destination.AsSpan();
        span.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        span[4] = Version;
        span[5] = frame.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), BodySize);

        var body = span.Slice(HeaderSize, BodySize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(0, 4), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(4, 8), frame.TimestampUnixMs);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(12, 4), frame.LeftStickX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(16, 4), frame.LeftStickY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(20, 4), frame.RightStickX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(24, 4), frame.RightStickY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(28, 4), frame.LeftTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(32, 4), frame.LeftGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(36, 4), frame.RightTriggerValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(40, 4), frame.RightGripValue);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(44, 4), frame.YawRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(48, 4), frame.PitchRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(52, 4), frame.RollRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(56, 4), frame.HmdPositionX);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(60, 4), frame.HmdPositionY);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(64, 4), frame.HmdPositionZ);
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(68, 4), frame.ButtonsBitMask);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(72, 4), frame.IpdMeters);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(76, 4), frame.HmdVerticalFovDegrees);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(80, 4), frame.LeftEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(84, 4), frame.LeftEyeAngleRightRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(88, 4), frame.LeftEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(92, 4), frame.LeftEyeAngleDownRadians);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(96, 4), frame.RightEyeAngleLeftRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            body.Slice(100, 4),
            frame.RightEyeAngleRightRadians
        );
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(104, 4), frame.RightEyeAngleUpRadians);
        BinaryPrimitives.WriteSingleLittleEndian(
            body.Slice(108, 4),
            frame.RightEyeAngleDownRadians
        );
    }

    private readonly record struct BridgeFrame(
        ulong TimestampUnixMs,
        float LeftStickX,
        float LeftStickY,
        float RightStickX,
        float RightStickY,
        float LeftTriggerValue,
        float LeftGripValue,
        float RightTriggerValue,
        float RightGripValue,
        float YawRadians,
        float PitchRadians,
        float RollRadians,
        float HmdPositionX,
        float HmdPositionY,
        float HmdPositionZ,
        uint ButtonsBitMask,
        float IpdMeters,
        float HmdVerticalFovDegrees,
        float LeftEyeAngleLeftRadians,
        float LeftEyeAngleRightRadians,
        float LeftEyeAngleUpRadians,
        float LeftEyeAngleDownRadians,
        float RightEyeAngleLeftRadians,
        float RightEyeAngleRightRadians,
        float RightEyeAngleUpRadians,
        float RightEyeAngleDownRadians,
        byte Flags
    )
    {
        private const float DegToRad = 0.0174532925f;
        private const uint ButtonA = 1 << 0;
        private const uint ButtonB = 1 << 1;
        private const uint ButtonX = 1 << 2;
        private const uint ButtonY = 1 << 3;
        private const uint ButtonLeftStickClick = 1 << 4;
        private const uint ButtonRightStickClick = 1 << 5;

        public static BridgeFrame Empty =>
            new(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0.064f,
                90.0f,
                -0.7853982f,
                0.7853982f,
                0.7853982f,
                -0.7853982f,
                -0.7853982f,
                0.7853982f,
                0.7853982f,
                -0.7853982f,
                0
            );

        public static BridgeFrame FromState(OpenXrControllerState state, bool isKeyboardDebugMode)
        {
            var buttons = 0u;
            if (state.RightAPressed)
            {
                buttons |= ButtonA;
            }

            if (state.RightBPressed)
            {
                buttons |= ButtonB;
            }

            if (state.LeftXPressed)
            {
                buttons |= ButtonX;
            }

            if (state.LeftYPressed)
            {
                buttons |= ButtonY;
            }

            if (state.LeftStickClickPressed)
            {
                buttons |= ButtonLeftStickClick;
            }

            if (state.RightStickClickPressed)
            {
                buttons |= ButtonRightStickClick;
            }

            var flags = (byte)0;
            if (isKeyboardDebugMode)
            {
                flags |= 1;
            }

            return new BridgeFrame(
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.LeftTriggerValue,
                state.LeftGripValue,
                state.RightTriggerValue,
                state.RightGripValue,
                state.HeadPose.YawDegrees * DegToRad,
                state.HeadPose.PitchDegrees * DegToRad,
                state.HeadPose.RollDegrees * DegToRad,
                state.HeadPose.PositionX,
                state.HeadPose.PositionY,
                state.HeadPose.PositionZ,
                buttons,
                state.IpdMeters,
                state.HmdVerticalFovDegrees,
                state.LeftEyeAngleLeftRadians,
                state.LeftEyeAngleRightRadians,
                state.LeftEyeAngleUpRadians,
                state.LeftEyeAngleDownRadians,
                state.RightEyeAngleLeftRadians,
                state.RightEyeAngleRightRadians,
                state.RightEyeAngleUpRadians,
                state.RightEyeAngleDownRadians,
                flags
            );
        }
    }
}
