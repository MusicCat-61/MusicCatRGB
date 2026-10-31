using System;
using System.Diagnostics;

namespace MusicCat.CLVKM.Drivers;

/// <summary>
/// Last-resort sink for driver errors. The drivers must never break the animation loop,
/// but a failed call should still be observable: the service writes it to its log and the
/// UI shows the last error in a tooltip.
/// </summary>
public static class DriverDiagnostics
{
    private static readonly object Sync = new();

    /// <summary>Raised from whatever thread the driver runs on.</summary>
    public static event Action<string>? ErrorReported;

    public static string? LastError { get; private set; }

    public static DateTime LastErrorUtc { get; private set; }

    public static void Report(string driver, Exception exception)
    {
        var message = $"{driver}: {exception.GetType().Name}: {exception.Message}";

        lock (Sync)
        {
            LastError = message;
            LastErrorUtc = DateTime.UtcNow;
        }

        Trace.TraceError(message);

        try
        {
            ErrorReported?.Invoke(message);
        }
        catch
        {
            // A faulty subscriber must not break the driver.
        }
    }
}
