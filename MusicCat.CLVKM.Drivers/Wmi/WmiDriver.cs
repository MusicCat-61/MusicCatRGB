using System.Management;
using System.Runtime.Versioning;
using MusicCat.CLVKM.Core.Interfaces;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Drivers.Wmi;

[SupportedOSPlatform("windows")]
public class WmiDriver : IKeyboardController
{
    private ManagementObject? _wmiInstance;
    private const string WmiNamespace = @"root\WMI";
    private const string InstanceQuery = @"SELECT * FROM CLEVO_GET WHERE InstanceName = 'ACPI\PNP0C14\0_0'";
    private const string MethodName = "SetKBLED";

    public Task InitializeAsync()
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                using var searcher = new ManagementObjectSearcher(WmiNamespace, InstanceQuery);
                using var collection = searcher.Get();

                foreach (ManagementObject obj in collection)
                {
                    _wmiInstance = obj;
                    break;
                }

                if (_wmiInstance == null)
                {
                    // Throwing allows DeviceManager to fall back to Insyde or other methods.
                    throw new PlatformNotSupportedException("Clevo WMI device not found.");
                }
            }
            catch (Exception)
            {
                throw; // Re-throw for manager to handle
            }
        });
    }

    public Task SetColorAsync(Zone zone, ColorRGB color)
    {
        if (_wmiInstance == null) return Task.CompletedTask;

        return Task.Run(() =>
        {
            if (zone == Zone.All)
            {
                SendColor(Zone.Left, color);
                SendColor(Zone.Center, color);
                SendColor(Zone.Right, color);
            }
            else
            {
                SendColor(zone, color);
            }
        });
    }

    private void SendColor(Zone zone, ColorRGB color)
    {
        try
        {
            // Protocol: [Green, Red, Blue, Command]
            // Command = 0xF0 + ZoneIndex (0, 1, 2)
            byte zoneCode = (byte)(0xF0 + (int)zone);

            // Layout in memory (Little Endian):
            // Byte 0: Green
            // Byte 1: Red
            // Byte 2: Blue
            // Byte 3: ZoneCode
            uint packet = (uint)((zoneCode << 24) | (color.B << 16) | (color.R << 8) | color.G);

            using var inParams = _wmiInstance!.GetMethodParameters(MethodName);
            inParams["Data"] = packet;

            using var outParams = _wmiInstance.InvokeMethod(MethodName, inParams, null);

            var returnValue = outParams?["ReturnValue"];
            if (returnValue is not null && Convert.ToUInt32(returnValue) != 0)
            {
                throw new InvalidOperationException(
                    $"SetKBLED returned {returnValue} for zone {zone}.");
            }
        }
        catch (Exception ex)
        {
            // WMI can be flaky under load; one missed frame is acceptable, but keep it visible.
            DriverDiagnostics.Report("WMI", ex);
        }
    }

    public Task ShutdownAsync()
    {
        _wmiInstance?.Dispose();
        _wmiInstance = null;
        return Task.CompletedTask;
    }
}
