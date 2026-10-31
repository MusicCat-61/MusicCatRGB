using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Two sine waves with different periods layered on top of each other: the
/// brightness never repeats in a simple rhythm and drifts between the two
/// picked colours, which feels much more alive than a plain breathing effect.
/// </summary>
public class DualBreatheAnimation : IAnimation, IColorAwareAnimation
{
    public string Name => "Dual Breathe";

    public ColorRGB Primary { get; set; } = new(0, 212, 255);

    public ColorRGB Secondary { get; set; } = new(255, 60, 200);

    private const int FrameMs = 25;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            double t = clock.Elapsed.TotalSeconds;

            double slowWave = Math.Sin(2 * Math.PI * t / 3.2);
            double fastWave = Math.Sin(2 * Math.PI * t / 1.9 + 1.1);

            double combined = Math.Clamp(0.6 * slowWave + 0.4 * fastWave, -1.0, 1.0);
            double intensity = 0.5 + 0.5 * combined;           // 0..1
            intensity = Math.Clamp(0.12 + 0.88 * intensity, 0.08, 1.0);

            double colourMix = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 7.3); // slow tint drift
            var colour = ColorRGB.Lerp(Primary, Secondary, 0.35 * colourMix);

            yield return new AnimationFrame(colour.ApplyBrightness((float)intensity), FrameMs);
        }
    }
}
