using System;
using System.Collections.Generic;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Follows the system clock: warm orange in the morning, white during the day,
/// blue/violet in the evening and deep blue at night, smoothly interpolated.
/// </summary>
public class DayNightAnimation : IAnimation
{
    public string Name => "Day / Night";

    private const int FrameMs = 250;

    private static readonly (double Hour, ColorRGB Color)[] Anchors =
    {
        (0.0, new ColorRGB(38, 48, 150)),   // night - deep blue
        (5.0, new ColorRGB(96, 72, 200)),   // before dawn - violet
        (7.0, new ColorRGB(255, 128, 42)),  // morning - warm orange
        (10.0, new ColorRGB(255, 238, 214)),// late morning - soft white
        (14.0, new ColorRGB(255, 255, 255)),// day - white
        (17.0, new ColorRGB(255, 176, 84)), // sunset - golden
        (19.5, new ColorRGB(172, 92, 255)), // evening - violet
        (21.5, new ColorRGB(72, 92, 226)),  // late evening - blue
        (24.0, new ColorRGB(38, 48, 150))   // wraps to night
    };

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        while (true)
        {
            var now = DateTime.Now;
            double hour = now.Hour + now.Minute / 60.0 + now.Second / 3600.0;
            yield return new AnimationFrame(ColorAt(hour), FrameMs);
        }
    }

    /// <summary>Colour for a given hour of the day (0..24), with smooth interpolation.</summary>
    public static ColorRGB ColorAt(double hour)
    {
        hour = ((hour % 24) + 24) % 24;

        for (int i = 0; i < Anchors.Length - 1; i++)
        {
            var (fromHour, fromColor) = Anchors[i];
            var (toHour, toColor) = Anchors[i + 1];

            if (hour >= fromHour && hour <= toHour)
            {
                double span = toHour - fromHour;
                double t = span <= 0 ? 0 : (hour - fromHour) / span;
                return ColorRGB.Lerp(fromColor, toColor, t);
            }
        }

        return Anchors[^1].Color;
    }
}
