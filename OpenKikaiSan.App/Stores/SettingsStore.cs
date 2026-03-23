using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenKikaiSan.App.Models;
using OpenKikaiSan.App.Utils;

namespace OpenKikaiSan.App.Stores;

public sealed class SettingsStore
{
    private readonly AppLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(AppLogger logger)
    {
        _logger = logger;
    }

    public AppSettings Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.SettingsPath))
        {
            var settings = new AppSettings();
            Save(settings);
            return settings;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load settings, using defaults.", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(AppPaths.SettingsPath, json);
    }
}
