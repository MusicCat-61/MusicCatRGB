using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Classic seven-colour rainbow — red, orange, yellow, green, cyan, blue, violet —
/// stepped strictly in order, 600 ms per colour with a short 120 ms cross-fade
/// (full circle 4.2 s). On a three-zone keyboard the zones lag by a third of a step.
/// </summary>
public class RainbowAnimation : IAnimation
{
    public string Name => "Rainbow";

    /// <summary>Set by the view model when the keyboard has only one lighting zone.</summary>
    public bool SingleZone { get; set; }

    private const int FrameMs = 40;
    private const int StepMs = 600;
    private const int FadeMs = 120;

    private static readonly ColorRGB[] Palette =
    {
        new(255, 0, 0),     // red
        new(255, 96, 0),    // orange
        new(255, 220, 0),   // yellow
        new(0, 200, 60),    // green
        new(0, 200, 255),   // cyan
        new(30, 60, 255),   // blue
        new(150, 0, 255)    // violet
    };

    private const double CycleMs = StepMs * 7.0;

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var elapsed = clock.Elapsed.TotalMilliseconds;

            if (SingleZone)
            {
                yield return new AnimationFrame(Sample(elapsed), FrameMs);
                continue;
            }

            // Same rainbow on every zone, but each next zone lags a third of a step.
            yield return new AnimationFrame(Sample(elapsed), 0, Zone.Left);
            yield return new AnimationFrame(Sample(elapsed - StepMs / 3.0), 0, Zone.Center);
            yield return new AnimationFrame(Sample(elapsed - 2.0 * StepMs / 3.0), FrameMs, Zone.Right);
        }
    }

    /// <summary>Palette colour at the given moment: hold, then a short cross-fade into the next one.</summary>
    private static ColorRGB Sample(double milliseconds)
    {
        var time = ((milliseconds % CycleMs) + CycleMs) % CycleMs;
        var index = (int)(time / StepMs) % Palette.Length;
        var local = time % StepMs;

        if (local <= StepMs - FadeMs)
        {
            return Palette[index];
        }

        var blend = (local - (StepMs - FadeMs)) / FadeMs;
        return ColorRGB.Lerp(Palette[index], Palette[(index + 1) % Palette.Length], blend);
    }
}
