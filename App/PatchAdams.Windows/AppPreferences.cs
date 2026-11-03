using System.IO;
using System.Text.Json;

namespace PatchAdamsSharp;

internal sealed record AppPreferences(string FontFamily = "Consolas", double FontSize = 13,
    bool AskBlindModeOnNewSession = true, bool FilePanelCollapsed = false,
    bool ReviewPanelCollapsed = false);

internal static class AppPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchAdams#", "settings.json");

    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(SettingsPath)) ?? new AppPreferences()
                : new AppPreferences();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppPreferences();
        }
    }

    public static void Save(AppPreferences preferences)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Options));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
