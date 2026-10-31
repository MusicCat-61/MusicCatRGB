using System;
using System.Collections.Generic;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.Core.Services;

namespace MusicCat.CLVKM.Core.Animations;

/// <summary>
/// Follows the current weather: blue for rain, yellow for sun, grey for clouds, white
/// for snow. While a request is running the colour is steady amber, and when the API
/// cannot be reached it is steady red — those two states do not pulse on purpose.
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

            var snapshot = WeatherService.Instance.Snapshot;
            _shown = ColorRGB.Lerp(_shown, WeatherService.Instance.Color, 0.08);

            // Only a real weather colour gets the slow pulse; amber/red stay steady.
            var pulse = snapshot.State == WeatherState.Ready
                ? 0.85 + 0.15 * Math.Sin(clock.Elapsed.TotalSeconds * 2 * Math.PI / PulsePeriodSeconds)
                : 1.0;

            yield return new AnimationFrame(_shown.ApplyBrightness((float)pulse), FrameMs);
        }
    }
}
