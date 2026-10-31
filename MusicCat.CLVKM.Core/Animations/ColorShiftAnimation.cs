using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Travelling colour wave: each zone shows its own hue and the whole hue
/// gradient scrolls across the keyboard instead of swapping fixed R/G/B.
/// Falls back to one continuously shifting colour on single-zone keyboards.
/// </summary>
public class ColorShiftAnimation : IAnimation, IColorAwareAnimation
{
    public string Name => "Wave";

    public ColorRGB Primary { get; set; } = new(0, 212, 255);

    public ColorRGB Secondary { get; set; } = new(255, 60, 200);

    /// <summary>Set by the view model when the keyboard has only one lighting zone.</summary>
    public bool SingleZone { get; set; }

    private const int FrameMs = 50;
    private const double DegreesPerSecond = 90.0;
    private const double ZoneHueStep = 120.0;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var (hue, saturation, value) = Vivid(Primary);
        var clock = Stopwatch.StartNew();

        while (true)
        {
            double phase = clock.Elapsed.TotalSeconds * DegreesPerSecond;

            if (SingleZone)
            {
                yield return new AnimationFrame(ColorRGB.FromHsv(hue + phase, saturation, value), FrameMs);
                continue;
            }

            yield return new AnimationFrame(ColorRGB.FromHsv(hue + phase, saturation, value), 0, Zone.Left);
            yield return new AnimationFrame(ColorRGB.FromHsv(hue + phase + ZoneHueStep, saturation, value), 0, Zone.Center);
            yield return new AnimationFrame(ColorRGB.FromHsv(hue + phase + ZoneHueStep * 2, saturation, value), FrameMs, Zone.Right);
        }
    }

    private static (double Hue, double Saturation, double Value) Vivid(ColorRGB color)
    {
        var (hue, saturation, value) = color.ToHsv();

        if (saturation < 0.2 || value < 0.15)
        {
            hue = 195;
            saturation = 1.0;
            value = 1.0;
        }

        return (hue, Math.Max(saturation, 0.85), Math.Max(value, 0.6));
    }
}
