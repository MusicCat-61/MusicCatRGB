using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Smooth walk around the whole hue circle (a real spectrum, not just R→G→B).
/// </summary>
public class ColorTransformAnimation : IAnimation
{
    public string Name => "Spectrum Cycle";

    private const int FrameMs = 40;
    private const double DegreesPerSecond = 40.0; // full circle in ~9 s

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            double hue = clock.Elapsed.TotalSeconds * DegreesPerSecond % 360.0;
            yield return new AnimationFrame(ColorRGB.FromHsv(hue, 1.0, 1.0), FrameMs);
        }
    }
}
