using System;
using System.Runtime.InteropServices;

namespace MusicCat.CLVKM.UI.Watcher;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL).
///
/// Only the fact that a key went down leaves this class: no virtual-key code, no text and
/// no window title is read or logged, so the app can react to typing without knowing what
/// was typed. Bare modifier presses are ignored, because a shortcut or a lone Shift should
/// not flash the keyboard by itself.
///
/// The hook is installed on the thread that runs the message loop in <see cref="WatcherHost"/>;
/// Windows delivers the callbacks as messages to that thread.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public KeyboardHook() => _callback = HandleKey;

    /// <summary>Raised for every key that is not a bare modifier.</summary>
    public event Action? KeyDown;

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    private IntPtr HandleKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var virtualKey = Marshal.ReadInt32(lParam);

            if (!IsBareModifier(virtualKey))
            {
                KeyDown?.Invoke();
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsBareModifier(int virtualKey) => virtualKey switch
    {
        VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or VK_CAPITAL => true,
        _ => false
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
