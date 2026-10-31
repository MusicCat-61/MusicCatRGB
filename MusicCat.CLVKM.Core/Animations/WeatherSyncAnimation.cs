using System;
using System.Collections.Generic;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.Core.Services;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Follows the current weather (Open-Meteo): blue for rain, yellow for sun,
/// grey for clouds, white for snow. The colour fades smoothly between updates.
/// </summary>
public class WeatherSyncAnimation : IAnimation
{
    public string Name => "Weather Sync";

    private const int FrameMs = 500;
    private const double PulsePeriodSeconds = 8.0;

    private ColorRGB _shown = new(150, 175, 210);

    public IEnumerable<AnimationFrame> GenerateFrames()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            // Fire-and-forget: the service itself throttles to one request per 15 minutes.
            _ = WeatherService.Instance.RefreshAsync();

            var target = WeatherService.Instance.Color;
            _shown = ColorRGB.Lerp(_shown, target, 0.08);

            double pulse = 0.85 + 0.15 * Math.Sin(clock.Elapsed.TotalSeconds * 2 * Math.PI / PulsePeriodSeconds);

            yield return new AnimationFrame(_shown.ApplyBrightness((float)pulse), FrameMs);
        }
    }
}
