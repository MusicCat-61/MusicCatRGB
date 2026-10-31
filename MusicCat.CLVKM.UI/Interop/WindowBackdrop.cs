using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Brushes = System.Windows.Media.Brushes;

namespace MusicCat.CLVKM.UI.Interop;

/// <summary>
/// Applies the Windows 11 Mica backdrop to a WPF window and removes the native
/// window frame (accent-coloured border and drop shadow) so the window is fully
/// borderless - the same silhouette the original app had.
/// Requires Windows 11 build 22621+, DWM composition and a non-layered window
/// (AllowsTransparency must be false). Returns false when the native material
/// is unavailable so the caller can fall back to a solid brush.
/// </summary>
internal static class WindowBackdrop
{
    private const int DWMWA_NCRENDERING_POLICY = 2;          // DWMNCRP_*
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;   // DWMWCP_*
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMSBT_MAINWINDOW = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool TryApplyMica(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        // No native frame, border or shadow - always.
        RemoveWindowFrame(hwnd);

        if (Environment.OSVersion.Version.Build < 22621 || SystemParameters.HighContrast)
        {
            return false;
        }

        // Dark or light title bar / caption buttons.
        int darkFlag = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

        // The WPF composition surface must be transparent for the DWM material to show through.
        var previousColor = source.CompositionTarget.BackgroundColor;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        int backdrop = DWMSBT_MAINWINDOW;
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
        {
            source.CompositionTarget.BackgroundColor = previousColor;
            return false;
        }

        window.Background = Brushes.Transparent;
        return true;
    }

    /// <summary>Switches the window (and its Mica backdrop) between dark and light.</summary>
    public static void SetDarkMode(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private static void RemoveWindowFrame(IntPtr hwnd)
    {
        // Disables the DWM-drawn frame, which on Windows 11 shows up as an
        // accent-coloured strip above the content (plus the drop shadow).
        int policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }
}
