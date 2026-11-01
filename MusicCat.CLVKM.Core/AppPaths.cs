using System;
using System.IO;

namespace MusicCat.CLVKM.Core;

/// <summary>
/// Where the application keeps its data. Branding lives here so the settings file, the
/// logs and the cleanup code can never drift apart.
/// </summary>
public static class AppPaths
{
    /// <summary>Product name shown to the user.</summary>
    public const string ProductName = "MusicCatRGB";

    /// <summary>Folder under %LOCALAPPDATA%.</summary>
    public const string FolderName = "MusicCatRGB";

    /// <summary>Folder used before the 1.0.6 rebrand; migrated on first start.</summary>
    public const string LegacyFolderName = "MusicCat.CLVKM";

    /// <summary>Name of the scheduled task that starts the app at logon.</summary>
    public const string StartupTaskName = "MusicCatRGB";

    /// <summary>Same task under its previous name, removed on start.</summary>
    public const string LegacyStartupTaskName = "MusicCat.CLVKM";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDirectory => Path.Combine(LocalAppData, FolderName);

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LegacyDataDirectory => Path.Combine(LocalAppData, LegacyFolderName);

    /// <summary>
    /// Carries the previous version's settings over to the new folder once, so a rebrand
    /// does not reset the colours, effect and language the user picked.
    /// </summary>
    public static void EnsureMigrated()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return;
            }

            var legacySettings = Path.Combine(LegacyDataDirectory, "settings.json");
            if (!File.Exists(legacySettings))
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);
            File.Copy(legacySettings, SettingsPath, overwrite: false);
        }
        catch
        {
            // Migration is best effort: a fresh settings file is written on the next save.
        }
    }
}
