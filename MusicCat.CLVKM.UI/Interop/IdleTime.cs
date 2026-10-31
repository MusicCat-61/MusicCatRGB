using System;
using System.Runtime.InteropServices;

namespace MusicCat.CLVKM.UI.Interop;

/// <summary>System-wide idle time (mouse and keyboard), used by the sleep timer.</summary>
internal static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        uint elapsed = unchecked((uint)Environment.TickCount) - info.Time;
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
