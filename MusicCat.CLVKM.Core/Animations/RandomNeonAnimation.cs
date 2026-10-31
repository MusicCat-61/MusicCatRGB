using System;
using System.Collections.Generic;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Smoothly fades to a random neon colour every 2-5 seconds.
/// </summary>
public class RandomNeonAnimation : IAnimation
{
    public string Name => "Random Neon";

    private const int FadeStepMs = 40;
    private const int FadeSteps = 20;

    private static readonly ColorRGB[] Palette =
    {
        new(0, 229, 255),   // cyan
        new(255, 0, 190),   // magenta
        new(140, 255, 0),   // lime
        new(170, 60, 255),  // violet
        new(255, 120, 0),   // orange
        new(255, 40, 120),  // pink
        new(0, 140, 255),   // azure
        new(0, 255, 170)    // aqua
    };

    private readonly Random _random = new();

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var current = Palette[_random.Next(Palette.Length)];

        while (true)
        {
            ColorRGB next;
            do
            {
                next = Palette[_random.Next(Palette.Length)];
            }
            while (next == current);

            for (int step = 1; step <= FadeSteps; step++)
            {
                yield return new AnimationFrame(
                    ColorRGB.Lerp(current, next, step / (double)FadeSteps),
                    FadeStepMs);
            }

            current = next;

            int holdMs = _random.Next(2000, 5001) - FadeSteps * FadeStepMs;
            if (holdMs > 0)
            {
                yield return new AnimationFrame(current, holdMs);
            }
        }
    }
}
