namespace LLMeta.App.Models;

public readonly record struct OpenXrVideoRenderConfigState(
    string RequestedSwapchainFormat,
    string SelectedSwapchainFormat,
    string[] AvailableSwapchainFormats,
    string RequestedGraphicsAdapter,
    string SelectedGraphicsAdapter,
    string[] AvailableGraphicsAdapters,
    string RequestedGraphicsBackend,
    string SelectedGraphicsBackend,
    string[] AvailableGraphicsBackends,
    string ProbeSummary
);
