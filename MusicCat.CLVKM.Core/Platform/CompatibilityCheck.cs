using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MusicCat.CLVKM.Core.Platform;

/// <summary>
/// What the machine looks like before the app promises to drive anything.
///
/// The lighting only works on a Clevo-class barebone: the keyboard hangs off the Insyde DCHU
/// driver, and the vendor Control Center is what initialises it. On any other laptop the app
/// would start, show an empty panel and leave the user wondering — so the first run checks
/// these three things and says so before offering the logon task.
/// </summary>
public sealed record CompatibilityReport(
    bool BoardLooksClevo,
    string BoardManufacturer,
    string BoardProduct,
    bool DriverLoadable,
    bool ControlCenterRunning,
    bool ControlCenterInstalled)
{
    /// <summary>
    /// A recognised board, or the driver together with the Control Center: the second half
    /// exists for rebrands whose manufacturer string nobody has heard of.
    /// </summary>
    public bool IsCompatible =>
        BoardLooksClevo || (DriverLoadable && (ControlCenterRunning || ControlCenterInstalled));

    /// <summary>Nothing at all matched — the app almost certainly cannot drive this keyboard.</summary>
    public bool IsClearlyIncompatible =>
        !BoardLooksClevo && !DriverLoadable && !ControlCenterRunning && !ControlCenterInstalled;

    /// <summary>Board name for the log and the dialog, e.g. "MACHENIKE V175RNXW-HM".</summary>
    public string BoardDisplay =>
        string.Join(' ', new[] { BoardManufacturer, BoardProduct }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static CompatibilityReport Unknown { get; } = new(false, string.Empty, string.Empty, false, false, false);
}

[SupportedOSPlatform("windows")]
public static class CompatibilityCheck
{
    /// <summary>
    /// Brands that sell Clevo or Tongfang barebones. The list does not have to be complete:
    /// the driver and the Control Center rescue a machine whose name is missing here.
    /// </summary>
    private static readonly string[] KnownBareboneBrands =
    {
        "clevo", "tongfang", "machenike", "machhenike", "monster", "hasee", "sager", "eurocom",
        "xmg", "schenker", "dream machines", "aftershock", "illegear", "metabox", "eluktronics",
        "rimo", "ruky", "thunderobot", "hyperbook", "pc specialist", "chillblast", "overpowered",
        "eurocom", "nexoc", "captiva", "one", "prime", "be quiet"
    };

    private const string DriverDll = "InsydeDCHU.dll";

    /// <summary>Runs the whole check; never throws, an unreadable machine yields "unknown".</summary>
    public static CompatibilityReport Run(TimeSpan? searchBudget = null)
    {
        var (manufacturer, product) = ReadBoard();

        return new CompatibilityReport(
            BoardLooksClevo: LooksLikeClevo(manufacturer, product),
            BoardManufacturer: manufacturer,
            BoardProduct: product,
            DriverLoadable: IsDriverLoadable(),
            ControlCenterRunning: IsControlCenterRunning(),
            ControlCenterInstalled: FindControlCenter(searchBudget ?? TimeSpan.FromSeconds(3)) is not null);
    }

    /// <summary>True when a manufacturer or model name belongs to that family.</summary>
    public static bool LooksLikeClevo(string? manufacturer, string? product)
    {
        var text = $"{manufacturer} {product}".ToLowerInvariant();

        return text.Length > 1
            && KnownBareboneBrands.Any(brand => text.Contains(brand, StringComparison.Ordinal));
    }

    private static (string Manufacturer, string Product) ReadBoard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");

            foreach (var board in searcher.Get().OfType<ManagementObject>())
            {
                var manufacturer = board["Manufacturer"]?.ToString() ?? string.Empty;
                var product = board["Product"]?.ToString() ?? string.Empty;

                if (manufacturer.Length > 0 || product.Length > 0)
                {
                    return (manufacturer.Trim(), product.Trim());
                }
            }
        }
        catch
        {
            // WMI may be unavailable; the driver and Control Center checks carry the verdict.
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Loads the vendor driver the way the real driver class will. In the single-file build the
    /// DLL is extracted next to the runtime rather than lying beside the executable, so a plain
    /// File.Exists would lie about it — asking the loader is the honest check.
    /// </summary>
    private static bool IsDriverLoadable()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, DriverDll)))
        {
            return true;
        }

        try
        {
            if (NativeLibrary.TryLoad(DriverDll, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsControlCenterRunning()
    {
        try
        {
            return Process.GetProcessesByName("FnKey").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Looks for FnKey.exe in the two Program Files trees, three levels deep and within a time
    /// budget: the usual location is Program Files (x86)\ControlCenter\FnKey.exe, and a full
    /// recursive scan of both trees can take minutes on a machine with many toolchains.
    /// </summary>
    private static string? FindControlCenter(TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(root => !string.IsNullOrEmpty(root) && Directory.Exists(root)))
        {
            var found = FindFile(root, "FnKey.exe", depth: 3, clock, budget);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindFile(string directory, string fileName, int depth, Stopwatch clock, TimeSpan budget)
    {
        if (depth < 0 || clock.Elapsed > budget)
        {
            return null;
        }

        try
        {
            var direct = Path.Combine(directory, fileName);

            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (clock.Elapsed > budget)
                {
                    return null;
                }

                var found = FindFile(child, fileName, depth - 1, clock, budget);

                if (found is not null)
                {
                    return found;
                }
            }
        }
        catch
        {
            // Access denied on a system folder: skip it and keep looking.
        }

        return null;
    }
}
