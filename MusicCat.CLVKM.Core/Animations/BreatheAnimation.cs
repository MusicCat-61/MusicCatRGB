using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Single-colour breathing: the picked primary colour fades in and out smoothly.
/// </summary>
public class BreatheAnimation : IAnimation, IColorAwareAnimation
{
    public string Name => "Breathing";

    public ColorRGB Primary { get; set; } = new(0, 212, 255);

    public ColorRGB Secondary { get; set; } = new(255, 60, 200);

    private const int FrameMs = 25;
    private const double PeriodMs = 3000;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            double phase = clock.Elapsed.TotalMilliseconds % PeriodMs / PeriodMs;
            double intensity = 0.5 - 0.5 * Math.Cos(2 * Math.PI * phase); // 0 -> 1 -> 0
            float brightness = (float)(0.05 + 0.95 * intensity);

            yield return new AnimationFrame(Primary.ApplyBrightness(brightness), FrameMs);
        }
    }
}
