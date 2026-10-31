using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace MusicCat.CLVKM.UI.Logging;

/// <summary>
/// Plain-text file logging. Every run appends to a daily file in
/// <c>%LOCALAPPDATA%\MusicCat.CLVKM\logs</c>; the last two weeks are kept.
/// </summary>
internal static class AppLog
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicCat.CLVKM",
        "logs");

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            Directory.CreateDirectory(LogDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "kgmaster-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information(
                "KeyGlowMaster {Version} started on {Os} (logs: {Folder})",
                typeof(AppLog).Assembly.GetName().Version,
                Environment.OSVersion.VersionString,
                LogDirectory);
        }
        catch
        {
            // Logging must never block startup.
        }
    }

    public static void Info(string message, params object?[] args) => Write(level: 0, message, args, null);

    public static void Warn(string message, params object?[] args) => Write(level: 1, message, args, null);

    public static void Error(string message, Exception? exception = null, params object?[] args) =>
        Write(level: 2, message, args, exception);

    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            Log.Information("KeyGlowMaster stopped");
            Log.CloseAndFlush();
        }
        catch
        {
            // Ignore logging failures on exit.
        }
    }

    /// <summary>Opens the log folder in Explorer; creates it on the first run.</summary>
    public static bool OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Write(int level, string message, object?[] args, Exception? exception)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            switch (level)
            {
                case 0:
                    Log.Information(message, args);
                    break;
                case 1:
                    Log.Warning(message, args);
                    break;
                default:
                    Log.Error(exception, message, args);
                    break;
            }
        }
        catch
        {
            // A logging failure is never worth an app crash.
        }
    }
}
