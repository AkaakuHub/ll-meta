namespace OpenKikaiSan.App.Models;

public readonly record struct OpenXrVideoRenderConfigState(
    string SelectedSwapchainFormat,
    string RuntimeGraphicsAdapter,
    string GraphicsBackend,
    string ProbeSummary
);
