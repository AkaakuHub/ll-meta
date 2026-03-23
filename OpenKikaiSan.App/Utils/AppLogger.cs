using System.IO;
using System.Text;
using OpenKikaiSan.App.Models;

namespace OpenKikaiSan.App.Utils;

public sealed class AppLogger
{
    private const long MaxLogFileBytes = 1024 * 1024;
    private const int MaxArchiveFiles = 3;
    private readonly object _lock = new();
    private AppLogLevel _minimumLevel = AppLogLevel.Error;

    public AppLogLevel MinimumLevel
    {
        get
        {
            lock (_lock)
            {
                return _minimumLevel;
            }
        }
    }

    public void SetMinimumLevel(AppLogLevel minimumLevel)
    {
        lock (_lock)
        {
            _minimumLevel = minimumLevel;
        }
    }

    public void Debug(string message) => Write(AppLogLevel.Debug, "DEBUG", message);

    public void Info(string message) => Write(AppLogLevel.Info, "INFO", message);

    public void Warn(string message, Exception? exception = null)
    {
        var extra = exception is null ? string.Empty : $" | {exception}";
        Write(AppLogLevel.Warn, "WARN", message + extra);
    }

    public void Error(string message, Exception? exception = null)
    {
        var extra = exception is null ? string.Empty : $" | {exception}";
        Write(AppLogLevel.Error, "ERROR", message + extra);
    }

    public void Fatal(string message, Exception? exception = null)
    {
        var extra = exception is null ? string.Empty : $" | {exception}";
        Write(AppLogLevel.Fatal, "FATAL", message + extra);
    }

    private void Write(AppLogLevel level, string label, string message)
    {
        lock (_lock)
        {
            if (level > _minimumLevel)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogPath)!);
            var line = $"[{DateTimeOffset.Now:O}] {label} {message}{Environment.NewLine}";
            RotateIfNeeded(line);
            File.AppendAllText(AppPaths.LogPath, line, Encoding.UTF8);
        }
    }

    private static void RotateIfNeeded(string nextLine)
    {
        var logPath = AppPaths.LogPath;
        if (!File.Exists(logPath))
        {
            return;
        }

        var nextBytes = Encoding.UTF8.GetByteCount(nextLine);
        var currentLength = new FileInfo(logPath).Length;
        if (currentLength + nextBytes <= MaxLogFileBytes)
        {
            return;
        }

        var logDirectory = Path.GetDirectoryName(logPath)!;
        var archivePath = Path.Combine(
            logDirectory,
            $"app.{DateTimeOffset.Now:yyyyMMdd_HHmmss_ffff}.log"
        );
        File.Move(logPath, archivePath, overwrite: true);

        var archives = new DirectoryInfo(logDirectory)
            .GetFiles("app.*.log")
            .OrderByDescending(static file => file.CreationTimeUtc)
            .ToArray();
        foreach (var archive in archives.Skip(MaxArchiveFiles))
        {
            archive.Delete();
        }
    }
}
