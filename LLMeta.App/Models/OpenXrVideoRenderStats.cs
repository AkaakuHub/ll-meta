namespace LLMeta.App.Models;

public readonly record struct OpenXrVideoRenderStats(
    uint LastRenderedSequence,
    long LastRenderedAtUnixMs,
    long LastRenderedAgeFromReceiveMs,
    long LastRenderedAgeFromDecodeMs,
    int LastUploadFailureCode,
    long LastUploadFailureAtUnixMs
);
