using System.Text.Json;

namespace MusicCat.CLVKM.Core.Settings;

public class AppSettings
{
    public byte Red { get; set; } = 0;
    public byte Green { get; set; } = 212;
    public byte Blue { get; set; } = 255;
    public int DriverType { get; set; } = 0;
    // Store animation name to restore it
    public string? LastAnimation { get; set; }
    public double Brightness { get; set; } = 100;
    public bool IsStartupEnabled { get; set; } = false;
    public bool IsAnimationActive { get; set; } = false;
    public double AnimationSpeed { get; set; } = 1.0;

    // Secondary colour used by two-colour effects
    public byte SecondaryRed { get; set; } = 255;
    public byte SecondaryGreen { get; set; } = 60;
    public byte SecondaryBlue { get; set; } = 200;

    // Applied "static" colour (what the keyboard shows when no animation runs)
    public bool HasStaticColor { get; set; }
    public byte StaticRed { get; set; } = 0;
    public byte StaticGreen { get; set; } = 212;
    public byte StaticBlue { get; set; } = 255;

    // Third colour used by the zone-gradient effect
    public byte TertiaryRed { get; set; } = 255;
    public byte TertiaryGreen { get; set; } = 140;
    public byte TertiaryBlue { get; set; } = 0;
    // UI language: "en" or "ru" (null = follow the OS language)
    public string? Language { get; set; }
    // UI theme: "dark" or "light" (null = dark)
    public string? Theme { get; set; }

    // Turn the backlight off after 5 minutes of idle time
    public bool SleepTimerEnabled { get; set; }
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
        "MusicCat.CLVKM", 
        "settings.json"
    );

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* Best effort save */ }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }
}
