using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MusicCat.CLVKM.UI.Interop;

internal enum GlobalHotKeyAction
{
    NextEffect,
    PreviousEffect,
    ToggleAnimation
}

/// <summary>Registers app-wide shortcuts and dispatches WM_HOTKEY on the owning WPF window.</summary>
internal sealed class GlobalHotKeyManager : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const int IdNextEffect = 0x4B01;
    private const int IdPreviousEffect = 0x4B02;
    private const int IdToggleAnimation = 0x4B03;

    private const uint VkPageUp = 0x21;
    private const uint VkPageDown = 0x22;
    private const uint VkReturn = 0x0D;

    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private readonly Action<GlobalHotKeyAction> _onHotKey;
    private readonly HashSet<int> _registeredIds = new();
    private bool _disposed;

    public GlobalHotKeyManager(IntPtr windowHandle, Action<GlobalHotKeyAction> onHotKey)
    {
        _windowHandle = windowHandle;
        _onHotKey = onHotKey;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("Could not obtain the WPF window message source.");
        _source.AddHook(WndProc);

        uint modifiers = ModControl | ModAlt | ModNoRepeat;
        TryRegister(IdNextEffect, modifiers, VkPageUp, "Ctrl+Alt+PageUp");
        TryRegister(IdPreviousEffect, modifiers, VkPageDown, "Ctrl+Alt+PageDown");
        TryRegister(IdToggleAnimation, modifiers, VkReturn, "Ctrl+Alt+Enter");
    }

    private void TryRegister(int id, uint modifiers, uint virtualKey, string shortcut)
    {
        if (RegisterHotKey(_windowHandle, id, modifiers, virtualKey))
        {
            _registeredIds.Add(id);
        }
        else
        {
            Debug.WriteLine($"Could not register global hotkey {shortcut}. Win32 error: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotKey)
        {
            return IntPtr.Zero;
        }

        GlobalHotKeyAction? action = wParam.ToInt32() switch
        {
            IdNextEffect => GlobalHotKeyAction.NextEffect,
            IdPreviousEffect => GlobalHotKeyAction.PreviousEffect,
            IdToggleAnimation => GlobalHotKeyAction.ToggleAnimation,
            _ => null
        };

        if (action.HasValue)
        {
            handled = true;
            _onHotKey(action.Value);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WndProc);

        foreach (int id in _registeredIds)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _registeredIds.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
