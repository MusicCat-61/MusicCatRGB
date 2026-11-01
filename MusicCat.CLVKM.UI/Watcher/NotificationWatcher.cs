using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Timer = System.Threading.Timer;

namespace MusicCat.CLVKM.UI.Watcher;

/// <summary>
/// Watches for Windows toast notifications.
///
/// Microsoft only exposes the notification list to packaged apps (<c>UserNotificationListener</c>
/// needs a package identity and a declared capability), so this works the way every other
/// unpackaged app does: it listens for the window Windows creates to put a toast on screen and
/// for the shell's notification host becoming visible. Two signals are combined so a missed
/// WinEvent still costs nothing:
///
/// <list type="bullet">
///   <item>a <c>SetWinEventHook</c> on show/create, filtered by window class;</item>
///   <item>a cheap poll every couple of seconds that notices a host window becoming visible,
///   which covers a shell that reuses one window instead of creating a new one.</item>
/// </list>
///
/// The class names live in <see cref="ToastWindowClasses"/> on purpose: Microsoft has renamed
/// them between Windows versions, and this is the one place to extend when a future build stops
/// being noticed.
/// </summary>
internal sealed class NotificationWatcher : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>Repeat toasts on the same window are one notification for our purposes.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Windows draws a toast as a small, never-focused popup: measured on Windows 11 26200 it
    /// is a few hundred pixels wide. The notification centre uses the same host class but fills
    /// the screen, so anything larger than this is not a toast.
    /// </summary>
    private const int MaxToastWidth = 800;
    private const int MaxToastHeight = 500;

    private static readonly string[] ToastWindowClasses =
    {
        // The popup ShellExperienceHost creates for a toast on Windows 10 and 11.
        "Xaml_WindowedPopupClass",
        // Windows 10 and early Windows 11 toast host, also used by the touch keyboard, so the
        // owning process is checked as well below.
        "Windows.UI.Core.CoreWindow",
        // Older shells and a few insider builds.
        "ToastChildWindow",
        "Shell_ToastHostWindow"
    };

    /// <summary>Processes that actually draw toasts; other users of the classes are ignored.</summary>
    private static readonly string[] ToastProcesses =
    {
        "ShellExperienceHost",
        "ShellHost"
    };

    private readonly WinEventDelegate _nativeCallback;
    private readonly Dictionary<IntPtr, long> _seenWindows = new();
    private readonly HashSet<IntPtr> _visibleWindows = new();
    private readonly Timer _pollTimer;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private IntPtr _hook;

    public NotificationWatcher()
    {
        _nativeCallback = OnWindowEvent;

        // A one second tick is enough: a toast that stays on screen for five seconds is
        // noticed on the first tick after it appears.
        _pollTimer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Raised once per notification; the argument is the owning process name.</summary>
    public event Action<string?>? NotificationShown;

    /// <summary>How many toasts were seen since the watcher started (for the log).</summary>
    public int Count { get; private set; }

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        // Out-of-context delivery needs the message loop that runs in WatcherHost. The range
        // must be ascending: EVENT_OBJECT_CREATE (0x8000) comes before EVENT_OBJECT_SHOW (0x8002).
        _hook = SetWinEventHook(
            EVENT_OBJECT_CREATE,
            EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _nativeCallback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWinEventHook failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only real top-level windows matter; children and the cursor are noise.
        if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */ || idChild != 0)
        {
            return;
        }

        if (eventType == EVENT_OBJECT_SHOW)
        {
            Consider(hwnd);
        }
    }

    private void Poll()
    {
        try
        {
            var visible = new List<IntPtr>(8);

            EnumWindows((hwnd, _) =>
            {
                if (IsToastCandidate(hwnd))
                {
                    visible.Add(hwnd);
                }

                return true;
            }, IntPtr.Zero);

            // A host that turned visible since the last tick means a notification appeared.
            foreach (var hwnd in visible)
            {
                if (!_visibleWindows.Contains(hwnd))
                {
                    Report(hwnd);
                }
            }

            _visibleWindows.Clear();

            foreach (var hwnd in visible)
            {
                _visibleWindows.Add(hwnd);
            }
        }
        catch
        {
            // Polling must never take the watcher down.
        }
    }

    private bool IsToastCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        var className = GetClassName(hwnd);

        if (className is null || Array.IndexOf(ToastWindowClasses, className) < 0)
        {
            return false;
        }

        if (GetWindowProcessId(hwnd) == _ownProcessId)
        {
            return false;
        }

        return IsSmallPopup(hwnd, className);
    }

    /// <summary>
    /// A toast is a small popup that never takes focus. The notification centre reuses the same
    /// host classes but covers the screen, and app windows do take focus, so both are dropped.
    /// </summary>
    private bool IsSmallPopup(IntPtr hwnd, string className)
    {
        GetWindowRect(hwnd, out var rect);

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0 || width > MaxToastWidth || height > MaxToastHeight)
        {
            return false;
        }

        var owner = DescribeOwner(hwnd);

        if (owner is null || Array.IndexOf(ToastProcesses, owner) < 0)
        {
            return false;
        }

        // The XAML popup class is used for ordinary shell flyouts too; those can take focus.
        if (className == "Xaml_WindowedPopupClass" && !IsNoActivate(hwnd))
        {
            return false;
        }

        return true;
    }

    private void Consider(IntPtr hwnd)
    {
        if (IsToastCandidate(hwnd))
        {
            Report(hwnd);
        }
    }

    /// <summary>True when the window never activates itself — the way toasts behave.</summary>
    private static bool IsNoActivate(IntPtr hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExNoActivate) != 0;

    private void Report(IntPtr hwnd)
    {
        var now = Environment.TickCount64;

        if (_seenWindows.TryGetValue(hwnd, out var last) && now - last < RepeatWindow.TotalMilliseconds)
        {
            return;
        }

        _seenWindows[hwnd] = now;
        Count++;

        NotificationShown?.Invoke(DescribeOwner(hwnd));
    }

    /// <summary>Process name of the window's owner, for the log; never the window text.</summary>
    private static string? DescribeOwner(IntPtr hwnd)
    {
        try
        {
            var processId = GetWindowProcessId(hwnd);
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);

        return GetClassNameNative(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    public void Dispose()
    {
        _pollTimer.Dispose();

        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private const int GwlExStyle = -20;

    private const long WsExNoActivate = 0x08000000L;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookAssembly,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameNative(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowProcessIdNative(IntPtr hwnd, out uint processId);

    private static uint GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowProcessIdNative(hwnd, out var processId);
        return processId;
    }
}
