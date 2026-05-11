using System.IO;
using System.Text.Json;

namespace ScreenMemory.AI.App.Services;

public class AppSettingsData
{
    public List<string> WatchedFolders { get; set; } = new();
}

public class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "ScreenMemory AI");
        Directory.CreateDirectory(folder);

        _settingsPath = Path.Combine(folder, "settings.json");
    }

    public AppSettingsData Load()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettingsData();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();
        }
        catch
        {
            return new AppSettingsData();
        }
    }

    public void Save(AppSettingsData settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_settingsPath, json);
    }
}