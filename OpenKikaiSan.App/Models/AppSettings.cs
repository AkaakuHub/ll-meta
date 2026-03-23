namespace OpenKikaiSan.App.Models;

public class AppSettings
{
    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Error;
    public int WindowsInputTcpPort { get; set; } = 39200;
    public SavedCaptureTarget? SavedCaptureTarget { get; set; }
}
