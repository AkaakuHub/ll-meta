namespace LLMeta.App.Models;

public readonly record struct DecodedVideoFrame(
    uint Sequence,
    ulong TimestampUnixMs,
    ulong DecodedUnixMs,
    int Width,
    int Height,
    nint SourceTexturePointer,
    uint SourceSubresourceIndex
);
