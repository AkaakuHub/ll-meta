namespace OpenKikaiSan.App.Models;

public class AppSettings
{
    public int WindowsInputTcpPort { get; set; } = 39200;
    public SavedCaptureTarget? SavedCaptureTarget { get; set; }
}
