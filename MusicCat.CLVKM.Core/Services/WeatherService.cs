using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Services;

/// <summary>
/// Free weather lookup without an API key: coordinates come from an IP geolocation
/// service, the current condition from Open-Meteo (with api.met.no as a fallback).
/// The result is cached and refreshed at most every 15 minutes, so the animation
/// loop can poll it cheaply.
/// </summary>
public sealed class WeatherService
{
    private static readonly Lazy<WeatherService> LazyInstance = new(() => new WeatherService());

    public static WeatherService Instance => LazyInstance.Value;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>Deep midnight blue for a clear night sky.</summary>
    public static readonly ColorRGB ClearNight = new(24, 30, 104);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly object _sync = new();

    private ColorRGB _color = new(150, 175, 210);
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private string? _status;
    private bool _refreshing;

    private WeatherService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "KeyGlowMaster/1.0 (https://github.com/gutsv-project/Clevo-Monster-RGB-Controller)");
    }

    /// <summary>Current weather colour (a neutral blue-grey until the first refresh).</summary>
    public ColorRGB Color
    {
        get
        {
            lock (_sync)
            {
                return _color;
            }
        }
    }

    public string? Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>Refreshes the weather when the cached value is older than 15 minutes.</summary>
    public async Task RefreshAsync()
    {
        lock (_sync)
        {
            if (_refreshing || DateTime.UtcNow - _lastAttemptUtc < RefreshInterval)
            {
                return;
            }

            _refreshing = true;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        var success = false;

        try
        {
            var (latitude, longitude) = await ResolveCoordinatesAsync().ConfigureAwait(false);
            var (color, status) = await FetchWeatherAsync(latitude, longitude).ConfigureAwait(false);

            lock (_sync)
            {
                _color = color;
                _status = status;
            }

            success = true;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _status = ex.Message;
            }
        }
        finally
        {
            lock (_sync)
            {
                _refreshing = false;

                if (!success)
                {
                    // Try again sooner than a normal refresh, but do not hammer the APIs.
                    _lastAttemptUtc = DateTime.UtcNow - RefreshInterval + RetryInterval;
                }
            }
        }
    }

    private async Task<(ColorRGB Color, string Status)> FetchWeatherAsync(double latitude, double longitude)
    {
        try
        {
            return await FetchFromOpenMeteoAsync(latitude, longitude).ConfigureAwait(false);
        }
        catch
        {
            // Open-Meteo can be unreachable on some networks - api.met.no is a drop-in fallback.
            return await FetchFromMetNoAsync(latitude, longitude).ConfigureAwait(false);
        }
    }

    private async Task<(ColorRGB, string)> FetchFromOpenMeteoAsync(double latitude, double longitude)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:F4}&longitude={longitude:F4}&current=weather_code,is_day");

        var json = await _http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var current = document.RootElement.GetProperty("current");
        int code = current.GetProperty("weather_code").GetInt32();
        bool isDay = current.TryGetProperty("is_day", out var day) && day.GetInt32() == 1;

        return (MapWeatherCode(code, isDay), $"open-meteo: {code}");
    }

    private async Task<(ColorRGB, string)> FetchFromMetNoAsync(double latitude, double longitude)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={latitude:F4}&lon={longitude:F4}");

        var json = await _http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("properties").GetProperty("timeseries")[0];
        var data = entry.GetProperty("data");

        var details = data.TryGetProperty("next_1_hours", out var next1)
            ? next1
            : data.GetProperty("next_6_hours");

        string symbol = details.GetProperty("summary").GetProperty("symbol_code").GetString() ?? string.Empty;

        return (MapSymbolCode(symbol), $"met.no: {symbol}");
    }

    private async Task<(double Latitude, double Longitude)> ResolveCoordinatesAsync()
    {
        try
        {
            var json = await _http.GetStringAsync("https://ipwho.is/").ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            double latitude = document.RootElement.GetProperty("latitude").GetDouble();
            double longitude = document.RootElement.GetProperty("longitude").GetDouble();

            if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
            {
                return (latitude, longitude);
            }
        }
        catch
        {
            // Fall through to the default location.
        }

        return (55.7558, 37.6173); // Moscow
    }

    /// <summary>WMO weather code → keyboard colour.</summary>
    public static ColorRGB MapWeatherCode(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? new ColorRGB(255, 214, 90) : ClearNight,                    // clear / mainly clear
        2 => new ColorRGB(200, 205, 215),                                            // partly cloudy
        3 => new ColorRGB(165, 172, 185),                                            // overcast
        45 or 48 => new ColorRGB(150, 155, 165),                                     // fog
        >= 51 and <= 67 => new ColorRGB(60, 140, 255),                               // drizzle / rain
        >= 71 and <= 77 => new ColorRGB(235, 240, 255),                              // snow
        >= 80 and <= 82 => new ColorRGB(60, 140, 255),                               // rain showers
        85 or 86 => new ColorRGB(235, 240, 255),                                     // snow showers
        >= 95 => new ColorRGB(170, 110, 255),                                        // thunderstorm
        _ => new ColorRGB(180, 185, 200)
    };

    /// <summary>met.no symbol code ("clearsky_day", "lightrain", "snow", …) → keyboard colour.</summary>
    public static ColorRGB MapSymbolCode(string symbol)
    {
        var code = (symbol ?? string.Empty).ToLowerInvariant();
        bool night = code.EndsWith("_night", StringComparison.Ordinal);

        if (code.Contains("thunder")) return new ColorRGB(170, 110, 255);
        if (code.Contains("snow") || code.Contains("sleet")) return new ColorRGB(235, 240, 255);
        if (code.Contains("rain") || code.Contains("drizzle") || code.Contains("shower")) return new ColorRGB(60, 140, 255);
        if (code.Contains("fog")) return new ColorRGB(150, 155, 165);
        if (code.Contains("cloudy")) return code.Contains("partly") ? new ColorRGB(200, 205, 215) : new ColorRGB(165, 172, 185);
        if (code.Contains("clear") || code.Contains("fair")) return night ? ClearNight : new ColorRGB(255, 214, 90);

        return new ColorRGB(180, 185, 200);
    }
}
