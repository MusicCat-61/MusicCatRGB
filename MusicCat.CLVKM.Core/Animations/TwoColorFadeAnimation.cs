using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Gentle sine fade back and forth between the two colours picked in the UI.
/// </summary>
public class TwoColorFadeAnimation : IAnimation, IColorAwareAnimation
{
    public string Name => "Two-Color Fade";

    public ColorRGB Primary { get; set; } = new(0, 212, 255);

    public ColorRGB Secondary { get; set; } = new(255, 60, 200);

    private const int FrameMs = 40;
    private const double PeriodMs = 4000;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            double phase = clock.Elapsed.TotalMilliseconds % PeriodMs / PeriodMs;
            double blend = (1 - Math.Cos(2 * Math.PI * phase)) / 2;

            yield return new AnimationFrame(ColorRGB.Lerp(Primary, Secondary, blend), FrameMs);
        }
    }
}
