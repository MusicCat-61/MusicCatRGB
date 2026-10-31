using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Interfaces;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Drivers.Insyde;

public class InsydeDriver : IKeyboardController
{
    private const string DllName = "InsydeDCHU.dll";

    /// <summary>The vendor DLL ships next to the executable, not in the working directory.</summary>
    private static readonly string DllPath = Path.Combine(AppContext.BaseDirectory, DllName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SetDCHU_Data")]
    private static extern int SetDCHU_Data(int command, byte[] buffer, int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "WriteAppSettings")]
    private static extern int WriteAppSettings(int page, int command, int length, byte[] buffer);

    private bool _isInitialized;

    public Task InitializeAsync()
    {
        return Task.Run(() =>
        {
            // Do not rely on Directory.GetCurrentDirectory(): a Windows service starts in
            // C:\Windows\System32, where the DLL does not exist.
            if (!File.Exists(DllPath))
            {
                throw new FileNotFoundException(
                    "InsydeDCHU.dll was not found next to the application.",
                    DllPath);
            }

            // Report an unloadable DLL (missing VC runtime, wrong bitness) at startup
            // instead of failing silently on the first colour write.
            if (!NativeLibrary.TryLoad(DllPath, out var handle))
            {
                var error = Marshal.GetLastWin32Error();
                throw new DllNotFoundException($"InsydeDCHU.dll could not be loaded (Win32 error {error}).");
            }

            NativeLibrary.Free(handle);
            _isInitialized = true;
        });
    }

    public Task SetColorAsync(Zone zone, ColorRGB color)
    {
        if (!_isInitialized)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                // The Insyde path is single-zone, so every zone maps to "all zones" (0xF0).
                //
                // The DCHU entry points answer with a status echo rather than 0 on this
                // hardware (103 for SetDCHU_Data, 1 for WriteAppSettings) while the write is
                // applied anyway. Treating that as a failure (as we did between 1.0.3.4 and
                // 1.0.5.4) aborted the sequence before the colour was pushed and spammed the
                // log once per frame. All three calls now always run; their raw answers are
                // recorded once on the first frame so a genuinely stuck driver is still
                // diagnosable.
                var dchuData = new byte[] { color.G, color.R, color.B, 0xF0 };
                var dchuStatus = SetDCHU_Data(103, dchuData, 4);

                var colorData = new byte[] { color.G, color.R, color.B };
                var colorStatus = WriteAppSettings(2, 81, 3, colorData);

                var modeData = new byte[] { 0x08 };
                var modeStatus = WriteAppSettings(2, 32, 1, modeData);

                DriverDiagnostics.Note(
                    "Insyde",
                    $"first frame statuses: SetDCHU_Data(103)={dchuStatus}, " +
                    $"WriteAppSettings(2, 81)={colorStatus}, WriteAppSettings(2, 32)={modeStatus} " +
                    "(status echoes, not failures - the vendor DLL answers non-zero even when the write applies)");
            }
            catch (Exception ex)
            {
                // One failed frame must not stop the animation, but keep the reason visible.
                DriverDiagnostics.Report("Insyde", ex);
            }
        });
    }

    public Task ShutdownAsync()
    {
        _isInitialized = false;
        return Task.CompletedTask;
    }
}
