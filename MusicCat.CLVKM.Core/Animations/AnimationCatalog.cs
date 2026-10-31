using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Single source of truth for the available effects. The UI builds its effect list from
/// <see cref="CreateAll"/> and the Windows service resolves the saved animation name with
/// <see cref="CreateByName"/>, so a new effect only has to be added here.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AnimationCatalog
{
    private static readonly (string Name, Func<IAnimation> Create)[] Entries =
    {
        ("Breathing", () => new BreatheAnimation()),
        ("Neon Pulse", () => new FreshBreatheAnimation()),
        ("Two-Color Fade", () => new TwoColorFadeAnimation()),
        ("Spectrum Cycle", () => new ColorTransformAnimation()),
        ("Rainbow", () => new RainbowAnimation()),
        ("Strobe Blink", () => new PulsatingBlinkAnimation()),
        ("Wave", () => new ColorShiftAnimation()),
        ("Zone Gradient", () => new ZoneGradientAnimation()),
        ("Random Neon", () => new RandomNeonAnimation()),
        ("Day / Night", () => new DayNightAnimation()),
        ("Soviet Garland", () => new GarlandAnimation()),
        ("Audio Sync (Spectrum)", () => new AudioSyncAnimation()),
        ("System Monitor (Heatmap)", () => new SystemMonitorAnimation()),
        ("Ambient (Screen Sync)", () => new AmbientLightingAnimation()),
        ("Weather Sync", () => new WeatherSyncAnimation()),
        ("Dual Breathe", () => new DualBreatheAnimation())
    };

    /// <summary>Effects that need an interactive desktop (audio loopback, screen capture).</summary>
    public static IReadOnlySet<string> SessionBoundNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Audio Sync (Spectrum)",
        "Ambient (Screen Sync)"
    };

    /// <summary>One fresh instance of every effect, in the order shown in the UI.</summary>
    public static IReadOnlyList<IAnimation> CreateAll() =>
        Entries.Select(entry => entry.Create()).ToArray();

    /// <summary>New instance of the effect saved under <paramref name="name"/>, or null when unknown.</summary>
    public static IAnimation? CreateByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Create();
            }
        }

        return null;
    }

    public static bool RequiresInteractiveSession(string? name) =>
        name is not null && SessionBoundNames.Contains(name);
}
