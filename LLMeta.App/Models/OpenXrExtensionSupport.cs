using Silk.NET.OpenXR;

namespace LLMeta.App.Models;

public readonly record struct OpenXrExtensionSupport(
    Result EnumerateResult,
    bool SupportsKhrD3D11Enable
);
