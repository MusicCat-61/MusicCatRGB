using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Serilog;

using Timer = System.Threading.Timer;

namespace MusicCat.CLVKM.UI.Logging;

/// <summary>
/// Plain-text file logging. Every run appends to a daily file in
/// <c>%LOCALAPPDATA%\MusicCatRGB\logs</c>; the last two weeks are kept.
///
/// Two kinds of entries are written:
/// <list type="bullet">
///   <item>one line per meaningful event (effect switch, hotkey, animation start/stop,
///   colour or mode change), tagged with a category such as <c>[effect]</c>;</item>
///   <item>a single line for a burst of changes — dragging a colour slider changes the
///   value dozens of times a second, so those are coalesced into the final value.</item>
/// </list>
/// </summary>
internal static class AppLog
{
    public static string LogDirectory { get; } = Core.AppPaths.LogDirectory;

    /// <summary>How long a burst of changes is collected before one line is written.</summary>
    private const int CoalesceWindowMs = 700;

    /// <summary>A burst is flushed even if it never settles, so a long drag still shows up.</summary>
    private const int CoalesceMaxDelayMs = 2500;

    private static readonly Dictionary<string, PendingEntry> Pending = new();
    private static readonly object PendingSync = new();
    private static Timer? _flushTimer;
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
                    Path.Combine(LogDirectory, "musiccatrgb-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information(
                "MusicCatRGB {Version} started on {Os} (logs: {Folder})",
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

    /// <summary>
    /// Writes one event line, for example <c>[hotkey] Ctrl+Alt+PageUp → next effect: Wave</c>.
    /// </summary>
    public static void Event(string category, string message)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            Log.Information("[{Category}] {Message}", category, message);
        }
        catch
        {
            // A logging failure is never worth an app crash.
        }
    }

    /// <summary>
    /// Records a value that changes in bursts (colour sliders, brightness, speed…).
    /// <paramref name="describe"/> is evaluated when the burst settles, so only the final
    /// value reaches the file.
    /// </summary>
    public static void Coalesce(string category, string key, Func<string> describe)
    {
        if (!_initialized)
        {
            return;
        }

        bool flushImmediately = false;

        lock (PendingSync)
        {
            var now = DateTime.UtcNow;

            if (!Pending.TryGetValue(key, out var entry))
            {
                entry = new PendingEntry(category, describe, now);
                Pending[key] = entry;
            }
            else
            {
                entry.Describe = describe;
            }

            if (now - entry.QueuedUtc >= TimeSpan.FromMilliseconds(CoalesceMaxDelayMs))
            {
                entry.QueuedUtc = now;
                flushImmediately = true;
            }
            else
            {
                _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
                _flushTimer.Change(CoalesceWindowMs, Timeout.Infinite);
            }
        }

        if (flushImmediately)
        {
            FlushPending();
        }
    }

    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            FlushPending();
            Log.Information("MusicCatRGB stopped");
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

    private static void FlushPending()
    {
        List<PendingEntry>? ready = null;

        lock (PendingSync)
        {
            if (Pending.Count == 0)
            {
                return;
            }

            ready = new List<PendingEntry>(Pending.Values);
            Pending.Clear();
        }

        foreach (var entry in ready)
        {
            try
            {
                Log.Information("[{Category}] {Message}", entry.Category, entry.Describe());
            }
            catch
            {
                // Ignore a single broken entry.
            }
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

    private sealed class PendingEntry
    {
        public PendingEntry(string category, Func<string> describe, DateTime queuedUtc)
        {
            Category = category;
            Describe = describe;
            QueuedUtc = queuedUtc;
        }

        public string Category { get; }

        public Func<string> Describe { get; set; }

        public DateTime QueuedUtc { get; set; }
    }
}
