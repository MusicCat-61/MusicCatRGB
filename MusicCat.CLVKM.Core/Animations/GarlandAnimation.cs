using System;
using System.Collections.Generic;
using System.Diagnostics;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// "Soviet garland": each zone behaves like its own string of old filament bulbs -
/// random warm colours, chaotic brightness, occasional burn-outs and bright flashes.
/// </summary>
public class GarlandAnimation : IAnimation
{
    public string Name => "Soviet Garland";

    /// <summary>Set by the view model when the keyboard has only one lighting zone.</summary>
    public bool SingleZone { get; set; }

    private const int TickMs = 40;

    private static readonly ColorRGB[] Lamp =
    {
        new(255, 214, 90),   // warm yellow
        new(255, 150, 40),   // orange
        new(232, 64, 40),    // red
        new(90, 200, 90),    // green
        new(255, 236, 170)   // warm white
    };

    private sealed class LampState
    {
        public ColorRGB Color;
        public double Level = 1;
        public double Target = 1;
        public int NextChangeMs;
    }

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var random = new Random();
        var states = new[] { NewState(random), NewState(random), NewState(random) };
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var now = (int)clock.ElapsedMilliseconds;

            foreach (var state in states)
            {
                Update(state, now, random);
            }

            // Slight "voltage sag" so the whole string breathes together.
            double voltage = 0.94 + 0.06 * Math.Sin(now / 900.0);

            if (SingleZone)
            {
                yield return new AnimationFrame(Scaled(states[0], voltage), TickMs);
                continue;
            }

            yield return new AnimationFrame(Scaled(states[0], voltage), 0, Zone.Left);
            yield return new AnimationFrame(Scaled(states[1], voltage), 0, Zone.Center);
            yield return new AnimationFrame(Scaled(states[2], voltage), TickMs, Zone.Right);
        }
    }

    private static LampState NewState(Random random) => new()
    {
        Color = Lamp[random.Next(Lamp.Length)],
        Level = 0.6 + random.NextDouble() * 0.4,
        Target = 0.6 + random.NextDouble() * 0.4,
        NextChangeMs = random.Next(100, 600)
    };

    private static void Update(LampState state, int now, Random random)
    {
        if (now >= state.NextChangeMs)
        {
            int roll = random.Next(100);

            if (roll < 8)
            {
                // Filament burns out for a moment.
                state.Target = 0;
                state.NextChangeMs = now + random.Next(150, 600);
            }
            else
            {
                state.Target = roll < 26
                    ? 1.0                                              // bright flash
                    : roll < 55
                        ? 0.55 + random.NextDouble() * 0.25           // dim
                        : 0.85 + random.NextDouble() * 0.15;           // normal

                state.NextChangeMs = now + random.Next(120, 500);

                if (random.Next(100) < 45)
                {
                    state.Color = Lamp[random.Next(Lamp.Length)];
                }
            }
        }

        // Fast decay when the bulb goes out, gentler rise when it comes back.
        double factor = state.Target < state.Level ? 0.55 : 0.32;
        state.Level += (state.Target - state.Level) * factor;
    }

    private static ColorRGB Scaled(LampState state, double voltage)
    {
        double level = Math.Clamp(state.Level * voltage, 0.0, 1.0);
        return state.Color.ApplyBrightness((float)level);
    }
}
