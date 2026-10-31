using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Left, centre and right zones each get one of the three picked colours and the
/// whole palette scrolls across the keyboard. Only the chosen colours are used -
/// no hue shifts or "vivid" corrections.
/// </summary>
public class ZoneGradientAnimation : IAnimation, IColorAwareAnimation
{
    public string Name => "Zone Gradient";

    public ColorRGB Primary { get; set; } = new(0, 212, 255);

    public ColorRGB Secondary { get; set; } = new(255, 60, 200);

    public ColorRGB Tertiary { get; set; } = new(255, 140, 0);

    /// <summary>Set by the view model when the keyboard has only one lighting zone.</summary>
    public bool SingleZone { get; set; }

    private const int FrameMs = 60;
    private const double CycleSeconds = 8.0;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            // Read the colours every frame: edits in the colour editor apply immediately.
            var palette = new[] { Primary, Secondary, Tertiary };

            double phase = clock.Elapsed.TotalSeconds % CycleSeconds / CycleSeconds * palette.Length;

            if (SingleZone)
            {
                yield return new AnimationFrame(Sample(palette, phase), FrameMs);
                continue;
            }

            yield return new AnimationFrame(Sample(palette, phase), 0, Zone.Left);
            yield return new AnimationFrame(Sample(palette, phase + 1), 0, Zone.Center);
            yield return new AnimationFrame(Sample(palette, phase + 2), FrameMs, Zone.Right);
        }
    }

    /// <summary>Smoothly samples the palette at a fractional position (wraps around).</summary>
    private static ColorRGB Sample(ColorRGB[] palette, double position)
    {
        var length = palette.Length;
        position = ((position % length) + length) % length;

        var index = (int)Math.Floor(position);
        var fraction = position - index;

        return ColorRGB.Lerp(palette[index], palette[(index + 1) % length], fraction);
    }
}
