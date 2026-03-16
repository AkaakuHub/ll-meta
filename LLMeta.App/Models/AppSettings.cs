namespace LLMeta.App.Models;

public class AppSettings
{
    public string SampleText { get; set; } = "Hello, World!";
    public string PreferredSwapchainFormat { get; set; } = "Auto";
    public string PreferredGraphicsAdapter { get; set; } = "Auto";
    public string PreferredGraphicsBackend { get; set; } = "D3D11";
    public int WindowsInputTcpPort { get; set; } = 39200;
}
