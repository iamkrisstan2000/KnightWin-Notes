using System.Text.Json;
using KnightWinNotes.Models;

namespace KnightWinNotes.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static string SettingsDirectory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("KNIGHTWIN_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            return Path.Combine(appData, "KnightWinNotes");
        }
    }

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Fall through to clean defaults
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string tmpPath = SettingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            // Ignore transient disk lock errors
        }
    }

    public static void AddRecentFile(AppSettings settings, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        string full = Path.GetFullPath(filePath);
        settings.RecentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, full);
        if (settings.RecentFiles.Count > 10)
            settings.RecentFiles.RemoveRange(10, settings.RecentFiles.Count - 10);
        Save(settings);
    }
}
