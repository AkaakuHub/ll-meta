namespace LLMeta.App.Models;

public readonly record struct VideoFramePacket(
    uint ConnectionId,
    uint Sequence,
    ulong TimestampUnixMs,
    byte Flags,
    bool IsKeyFrame,
    bool HasCodecConfig,
    string CodecName,
    byte[] Payload
);
