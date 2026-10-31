using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MusicCat.CLVKM.Drivers;

/// <summary>
/// Last-resort sink for driver errors. The drivers must never break the animation loop,
/// but a failed call should still be observable: the service writes it to its log and the
/// UI shows the last error in a tooltip.
///
/// Animation loops call the driver many times per second, so identical faults are
/// suppressed: the first one is reported straight away, repeats inside
/// <see cref="RepeatWindow"/> are counted and summarised on the next report.
/// </summary>
public static class DriverDiagnostics
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);

    private static readonly object Sync = new();

    /// <summary>Last time each distinct fault was reported, with the repeats we swallowed.</summary>
    private static readonly Dictionary<string, RepeatState> Repeats = new(StringComparer.Ordinal);

    /// <summary>Messages already written once and not worth repeating at all.</summary>
    private static readonly HashSet<string> Noted = new(StringComparer.Ordinal);

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

        var now = DateTime.UtcNow;
        var reported = message;

        lock (Sync)
        {
            if (Repeats.TryGetValue(message, out var state))
            {
                state.Suppressed++;

                if (now - state.ReportedUtc < RepeatWindow)
                {
                    return;
                }

                reported = $"{message} (repeats in the last {RepeatWindow.TotalSeconds:0} s: {state.Suppressed})";
                state.Suppressed = 0;
                state.ReportedUtc = now;
            }
            else
            {
                Repeats[message] = new RepeatState { ReportedUtc = now };
            }
        }

        Trace.TraceError(reported);

        try
        {
            ErrorReported?.Invoke(reported);
        }
        catch
        {
            // A faulty subscriber must not break the driver.
        }
    }

    /// <summary>
    /// Records a benign, non-fatal driver answer (a status code that is expected on this
    /// hardware). Unlike <see cref="Report"/> it is not surfaced as an error, and each
    /// distinct message is written once per run so the log stays readable.
    /// </summary>
    public static void Note(string driver, string message)
    {
        var text = $"{driver}: {message}";

        lock (Sync)
        {
            if (!Noted.Add(text))
            {
                return;
            }
        }

        Trace.TraceInformation(text);

        try
        {
            NoteReported?.Invoke(text);
        }
        catch
        {
            // A faulty subscriber must not break the driver.
        }
    }

    /// <summary>Raised once per distinct <see cref="Note"/>.</summary>
    public static event Action<string>? NoteReported;

    private sealed class RepeatState
    {
        public DateTime ReportedUtc { get; set; }

        public int Suppressed { get; set; }
    }
}
