using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Ipc;
using MusicCat.CLVKM.UI.Logging;

namespace MusicCat.CLVKM.UI.Watcher;

/// <summary>
/// Headless mode of the executable (<c>MCRGB.exe --watcher</c>).
///
/// It exists so the keyboard hook and the notification listener never run inside the window
/// process: a hook that stops answering makes Windows drop input on the whole desktop, so it
/// belongs in a process that can be killed and restarted without taking the lighting down.
/// The two halves talk over <see cref="WatcherPipe"/>, which only the signed-in user can open.
/// </summary>
internal static class WatcherHost
{
    /// <summary>Command-line switch that starts this mode.</summary>
    public const string Switch = "--watcher";

    /// <summary>
    /// Environment variable carrying the app's process id. The watcher ends when that process
    /// does, so a crash of the window process cannot leave a keyboard hook behind for ever.
    /// </summary>
    public const string ParentProcessVariable = "MCRGB_PARENT_PID";

    private const string MutexName = @"Local\MusicCatRGB.Watcher.v1";
    private const uint WM_QUIT = 0x0012;

    /// <summary>How long the watcher waits for the app before it gives up and exits.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private static uint _hookThreadId;

    /// <summary>Runs the watcher until the app goes away; returns the process exit code.</summary>
    public static int Run()
    {
        AppLog.Initialize();

        using var single = new Mutex(true, MutexName, out var createdNew);

        if (!createdNew)
        {
            AppLog.Info("Watcher: another instance is already running, exiting");
            return 0;
        }

        AppLog.Info("Watcher: starting (keyboard hook, notification listener, pipe {Pipe})", WatcherPipe.Name);

        var server = new WatcherEventServer();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(() => server.RunAsync(stop.Token));

        var hookThread = new Thread(() => RunHooks(server))
        {
            IsBackground = true,
            Name = "watcher-hooks"
        };

        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();

        var startedUtc = DateTime.UtcNow;
        var lastClientUtc = startedUtc;
        var everConnected = false;
        var parentId = ReadParentId();

        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));

            if (parentId is { } id && !IsAlive(id))
            {
                AppLog.Event("watcher", "Watcher: the app is gone, exiting");
                break;
            }

            if (server.HasClient)
            {
                everConnected = true;
                lastClientUtc = DateTime.UtcNow;
                continue;
            }

            var silent = DateTime.UtcNow - lastClientUtc;

            if (everConnected && silent > IdleTimeout)
            {
                AppLog.Event("watcher", $"Watcher: the app stayed away for {silent.TotalSeconds:0}s, exiting");
                break;
            }

            if (!everConnected && DateTime.UtcNow - startedUtc > IdleTimeout + IdleTimeout)
            {
                AppLog.EventWarning("watcher", "Watcher: nothing ever connected, exiting");
                break;
            }
        }

        stop.Cancel();

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        hookThread.Join(TimeSpan.FromSeconds(3));

        try
        {
            serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The pipe loop already stopped; nothing to report.
        }

        AppLog.Info("Watcher: stopped");
        return 0;
    }

    /// <summary>Process id passed by the app, when it started us.</summary>
    private static int? ReadParentId()
    {
        var raw = Environment.GetEnvironmentVariable(ParentProcessVariable);
        return int.TryParse(raw, out var id) && id > 0 ? id : null;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: the app is gone.
            return false;
        }
        catch
        {
            // A process we may not query is treated as alive; the idle timeout still applies.
            return true;
        }
    }

    /// <summary>
    /// Owns both hooks on a thread with a message loop: a low-level keyboard hook and an
    /// out-of-context WinEvent hook are delivered as messages to the thread that installed them.
    /// </summary>
    private static void RunHooks(WatcherEventServer server)
    {
        _hookThreadId = GetCurrentThreadId();

        KeyboardHook? keyboard = null;
        NotificationWatcher? notifications = null;

        try
        {
            keyboard = new KeyboardHook();
            keyboard.KeyDown += OnKeyDown;
            keyboard.Install();

            notifications = new NotificationWatcher();
            notifications.NotificationShown += source =>
            {
                AppLog.Event("watcher", $"Notification detected ({source ?? "unknown source"})");
                server.Publish(new WatcherEvent(WatcherEventKind.Notification, 1, source));
            };
            notifications.Install();

            AppLog.Info("Watcher: hooks installed");

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Watcher: could not install the hooks", ex, ex.Message);
        }
        finally
        {
            notifications?.Dispose();
            keyboard?.Dispose();
        }

        void OnKeyDown() => server.Publish(new WatcherEvent(WatcherEventKind.Keypress));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Hwnd;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Position;
    }

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
