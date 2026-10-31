using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Models;

namespace MusicCat.CLVKM.Core.Services;

/// <summary>State of the weather lookup / API link for the current city.</summary>
public enum WeatherState
{
    /// <summary>Nothing requested yet.</summary>
    Idle,

    /// <summary>No city configured by the user.</summary>
    NoCity,

    /// <summary>A request is in flight.</summary>
    Updating,

    /// <summary>Weather data is fresh.</summary>
    Ready,

    /// <summary>The weather API could not be reached.</summary>
    Offline,

    /// <summary>Geocoding found nothing for the entered city.</summary>
    CityNotFound
}

/// <summary>Normalised weather condition, independent of the provider's code system.</summary>
public enum WeatherCondition
{
    Unknown,
    Clear,
    MainlyClear,
    PartlyCloudy,
    Overcast,
    Fog,
    Drizzle,
    Rain,
    FreezingRain,
    Snow,
    RainShowers,
    SnowShowers,
    Thunderstorm
}

/// <summary>Immutable snapshot of the last weather lookup.</summary>
public sealed record WeatherSnapshot
{
    public WeatherState State { get; init; } = WeatherState.Idle;

    /// <summary>Resolved place name ("Moscow", "Москва"), null until the city is geocoded.</summary>
    public string? City { get; init; }

    public double? TemperatureC { get; init; }

    public WeatherCondition Condition { get; init; } = WeatherCondition.Unknown;

    public bool IsDay { get; init; } = true;

    /// <summary>Wind speed in km/h.</summary>
    public double? WindSpeedKmh { get; init; }

    /// <summary>Direction the wind comes from, in degrees (0 = north).</summary>
    public double? WindDirectionDegrees { get; init; }

    /// <summary>Which provider answered ("open-meteo", "met.no").</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Technical error text of the last failed attempt.</summary>
    public string? Error { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

internal sealed class WeatherException : Exception
{
    public WeatherException(WeatherState state, string message) : base(message) => State = state;

    public WeatherState State { get; }
}

/// <summary>
/// Weather lookup without an API key: the city the user entered is geocoded with the
/// Open-Meteo geocoder, the current condition comes from Open-Meteo (with api.met.no as
/// a fallback). The result is cached and refreshed at most every 15 minutes, so the
/// animation loop can poll it cheaply.
/// </summary>
public sealed class WeatherService
{
    private static readonly Lazy<WeatherService> LazyInstance = new(() => new WeatherService());

    public static WeatherService Instance => LazyInstance.Value;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);

    /// <summary>Deep midnight blue for a clear night sky.</summary>
    public static readonly ColorRGB ClearNight = new(24, 30, 104);

    private readonly HttpClient _http;
    private readonly object _sync = new();

    private string _city = string.Empty;
    private string _language = "en";
    private WeatherSnapshot _snapshot = new();
    private ColorRGB _color = new(150, 175, 210);
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _refreshing;
    private Location? _location;

    /// <summary>Provider that answered last; a blocked primary is not retried on every refresh.</summary>
    private string _preferredProvider = "open-meteo";

    private WeatherService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "KeyGlowMaster/1.0 (https://github.com/MusicCat-61/KeyGlowMaster)");
    }

    /// <summary>Raised after every finished attempt (successful or not). May run on a worker thread.</summary>
    public event EventHandler<WeatherSnapshot>? Updated;

    /// <summary>City entered by the user. Changing it drops the cached place and retries immediately.</summary>
    public string City
    {
        get
        {
            lock (_sync)
            {
                return _city;
            }
        }
        set
        {
            var trimmed = (value ?? string.Empty).Trim();
            WeatherSnapshot snapshot;

            lock (_sync)
            {
                _city = trimmed;
                _location = null;
                _lastAttemptUtc = DateTime.MinValue;
                _snapshot = new WeatherSnapshot
                {
                    State = trimmed.Length == 0 ? WeatherState.NoCity : WeatherState.Updating
                };
                snapshot = _snapshot;
            }

            RaiseUpdated(snapshot);
        }
    }

    /// <summary>Language tag used for geocoding ("en", "ru"): the resolved place name follows it.</summary>
    public string Language
    {
        get
        {
            lock (_sync)
            {
                return _language;
            }
        }
        set
        {
            lock (_sync)
            {
                var tag = string.IsNullOrWhiteSpace(value) ? "en" : value.Trim();
                if (string.Equals(_language, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _language = tag;
                _location = null;
                _lastAttemptUtc = DateTime.MinValue; // re-geocode so the place name follows the UI language
            }
        }
    }

    /// <summary>Last known weather state, including the previous values when an update fails.</summary>
    public WeatherSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Current weather colour (a neutral blue-grey until the first successful refresh).</summary>
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

    /// <summary>
    /// Refreshes the weather when it is older than 15 minutes (2 minutes after a failure).
    /// Safe to call from the animation loop: it returns immediately while a request is in flight.
    /// </summary>
    public async Task RefreshAsync()
    {
        WeatherSnapshot? immediate = null;
        string? city = null;

        lock (_sync)
        {
            if (_refreshing)
            {
                return;
            }

            if (_city.Length == 0)
            {
                if (_snapshot.State != WeatherState.NoCity)
                {
                    _snapshot = new WeatherSnapshot { State = WeatherState.NoCity };
                    immediate = _snapshot;
                }
            }
            else if (DateTime.UtcNow - _lastAttemptUtc >= RefreshInterval)
            {
                _refreshing = true;
                _lastAttemptUtc = DateTime.UtcNow;
                _snapshot = _snapshot with { State = WeatherState.Updating };
                city = _city;
            }
        }

        if (immediate is not null)
        {
            RaiseUpdated(immediate);
            return;
        }

        if (city is null)
        {
            return;
        }

        try
        {
            var location = await ResolveLocationAsync(city).ConfigureAwait(false);
            var snapshot = await FetchWeatherAsync(location).ConfigureAwait(false);

            // The user may have typed another city while this request was running.
            if (IsCurrentCity(city))
            {
                Publish(snapshot);
            }
        }
        catch (WeatherException ex)
        {
            if (IsCurrentCity(city))
            {
                Publish(Failed(ex.State, ex.Message));
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentCity(city))
            {
                Publish(Failed(WeatherState.Offline, ex.Message));
            }
        }
        finally
        {
            lock (_sync)
            {
                _refreshing = false;
            }
        }
    }

    private bool IsCurrentCity(string city)
    {
        lock (_sync)
        {
            return string.Equals(_city, city, StringComparison.Ordinal);
        }
    }

    private void Publish(WeatherSnapshot snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot;

            if (snapshot.State == WeatherState.Ready)
            {
                _color = MapCondition(snapshot.Condition, snapshot.IsDay);
            }
            else if (snapshot.State is WeatherState.Offline or WeatherState.CityNotFound)
            {
                // Try again sooner than a normal refresh, but do not hammer the APIs.
                _lastAttemptUtc = DateTime.UtcNow - RefreshInterval + RetryInterval;
            }
        }

        RaiseUpdated(snapshot);
    }

    private WeatherSnapshot Failed(WeatherState state, string message)
    {
        lock (_sync)
        {
            // Keep the last good readings so the panel can show stale data while offline.
            return _snapshot with { State = state, Error = message };
        }
    }

    private void RaiseUpdated(WeatherSnapshot snapshot) => Updated?.Invoke(this, snapshot);

    private async Task<Location> ResolveLocationAsync(string city)
    {
        string language;

        lock (_sync)
        {
            if (_location is { } cached && string.Equals(cached.Query, city, StringComparison.OrdinalIgnoreCase))
            {
                return cached;
            }

            language = _language;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&format=json&language={Uri.EscapeDataString(language)}");

        var json = await _http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            throw new WeatherException(WeatherState.CityNotFound, $"City \"{city}\" was not found");
        }

        var first = results[0];
        double latitude = first.GetProperty("latitude").GetDouble();
        double longitude = first.GetProperty("longitude").GetDouble();

        var name = first.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? city
            : city;

        var location = new Location(city, latitude, longitude, name);

        lock (_sync)
        {
            _location = location;
        }

        return location;
    }

    private async Task<WeatherSnapshot> FetchWeatherAsync(Location location)
    {
        // Try the provider that worked last first: on networks where Open-Meteo is blocked
        // a failing primary would cost a full HTTP timeout on every refresh.
        var metNoFirst = string.Equals(_preferredProvider, "met.no", StringComparison.Ordinal);

        try
        {
            return await TryProviderAsync(location, metNoFirst).ConfigureAwait(false);
        }
        catch (Exception firstError)
        {
            try
            {
                return await TryProviderAsync(location, !metNoFirst).ConfigureAwait(false);
            }
            catch (Exception secondError)
            {
                throw new WeatherException(
                    WeatherState.Offline,
                    $"{firstError.Message} / {secondError.Message}");
            }
        }
    }

    private async Task<WeatherSnapshot> TryProviderAsync(Location location, bool metNo)
    {
        var snapshot = metNo
            ? await FetchFromMetNoAsync(location).ConfigureAwait(false)
            : await FetchFromOpenMeteoAsync(location).ConfigureAwait(false);

        _preferredProvider = snapshot.Provider;
        return snapshot;
    }

    private async Task<WeatherSnapshot> FetchFromOpenMeteoAsync(Location location)
    {
        const string currentQuery =
            "current=weather_code,is_day,temperature_2m,wind_speed_10m,wind_direction_10m&wind_speed_unit=kmh&timezone=auto";

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude:F4}&longitude={location.Longitude:F4}&{currentQuery}");

        var json = await _http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var current = document.RootElement.GetProperty("current");

        var code = current.GetProperty("weather_code").GetInt32();
        var isDay = !current.TryGetProperty("is_day", out var day) || day.GetInt32() != 0;

        return new WeatherSnapshot
        {
            State = WeatherState.Ready,
            City = location.Name,
            TemperatureC = ReadDouble(current, "temperature_2m"),
            Condition = ConditionFromWeatherCode(code),
            IsDay = isDay,
            WindSpeedKmh = ReadDouble(current, "wind_speed_10m"),
            WindDirectionDegrees = ReadDouble(current, "wind_direction_10m"),
            Provider = "open-meteo",
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private async Task<WeatherSnapshot> FetchFromMetNoAsync(Location location)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={location.Latitude:F4}&lon={location.Longitude:F4}");

        var json = await _http.GetStringAsync(url).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("properties").GetProperty("timeseries")[0];
        var data = entry.GetProperty("data");
        var details = data.GetProperty("instant").GetProperty("details");

        var forecast = data.TryGetProperty("next_1_hours", out var next1) ? next1 : data.GetProperty("next_6_hours");
        var symbol = forecast.TryGetProperty("summary", out var summary) &&
                     summary.TryGetProperty("symbol_code", out var symbolCode)
            ? symbolCode.GetString() ?? string.Empty
            : string.Empty;

        // met.no reports wind speed in m/s.
        var windMs = ReadDouble(details, "wind_speed");

        return new WeatherSnapshot
        {
            State = WeatherState.Ready,
            City = location.Name,
            TemperatureC = ReadDouble(details, "air_temperature"),
            Condition = ConditionFromSymbol(symbol),
            IsDay = !symbol.EndsWith("_night", StringComparison.OrdinalIgnoreCase),
            WindSpeedKmh = windMs is null ? null : windMs * 3.6,
            WindDirectionDegrees = ReadDouble(details, "wind_from_direction"),
            Provider = "met.no",
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>WMO weather code → normalised condition.</summary>
    public static WeatherCondition ConditionFromWeatherCode(int code) => code switch
    {
        0 => WeatherCondition.Clear,
        1 => WeatherCondition.MainlyClear,
        2 => WeatherCondition.PartlyCloudy,
        3 => WeatherCondition.Overcast,
        45 or 48 => WeatherCondition.Fog,
        51 or 53 or 55 => WeatherCondition.Drizzle,
        56 or 57 => WeatherCondition.FreezingRain,
        61 or 63 or 65 => WeatherCondition.Rain,
        66 or 67 => WeatherCondition.FreezingRain,
        71 or 73 or 75 or 77 => WeatherCondition.Snow,
        80 or 81 or 82 => WeatherCondition.RainShowers,
        85 or 86 => WeatherCondition.SnowShowers,
        95 or 96 or 99 => WeatherCondition.Thunderstorm,
        _ => WeatherCondition.Unknown
    };

    /// <summary>met.no symbol code ("clearsky_day", "lightrain", "snow", …) → normalised condition.</summary>
    public static WeatherCondition ConditionFromSymbol(string symbol)
    {
        var code = (symbol ?? string.Empty).ToLowerInvariant();

        if (code.Contains("thunder")) return WeatherCondition.Thunderstorm;
        if (code.Contains("sleet")) return WeatherCondition.FreezingRain;
        if (code.Contains("snow")) return code.Contains("shower") ? WeatherCondition.SnowShowers : WeatherCondition.Snow;
        if (code.Contains("rain") || code.Contains("drizzle"))
        {
            return code.Contains("shower") ? WeatherCondition.RainShowers : WeatherCondition.Rain;
        }

        if (code.Contains("fog")) return WeatherCondition.Fog;
        if (code.Contains("cloudy")) return code.Contains("partly") ? WeatherCondition.PartlyCloudy : WeatherCondition.Overcast;
        if (code.Contains("clear")) return WeatherCondition.Clear;
        if (code.Contains("fair")) return WeatherCondition.MainlyClear;

        return WeatherCondition.Unknown;
    }

    /// <summary>Normalised condition → keyboard colour.</summary>
    public static ColorRGB MapCondition(WeatherCondition condition, bool isDay) => condition switch
    {
        WeatherCondition.Clear or WeatherCondition.MainlyClear =>
            isDay ? new ColorRGB(255, 214, 90) : ClearNight,
        WeatherCondition.PartlyCloudy => new ColorRGB(200, 205, 215),
        WeatherCondition.Overcast => new ColorRGB(165, 172, 185),
        WeatherCondition.Fog => new ColorRGB(150, 155, 165),
        WeatherCondition.Drizzle or WeatherCondition.Rain or WeatherCondition.RainShowers
            or WeatherCondition.FreezingRain => new ColorRGB(60, 140, 255),
        WeatherCondition.Snow or WeatherCondition.SnowShowers => new ColorRGB(235, 240, 255),
        WeatherCondition.Thunderstorm => new ColorRGB(170, 110, 255),
        _ => new ColorRGB(180, 185, 200)
    };

    /// <summary>WMO weather code → keyboard colour.</summary>
    public static ColorRGB MapWeatherCode(int code, bool isDay) =>
        MapCondition(ConditionFromWeatherCode(code), isDay);

    /// <summary>met.no symbol code ("clearsky_day", "lightrain", "snow", …) → keyboard colour.</summary>
    public static ColorRGB MapSymbolCode(string symbol) =>
        MapCondition(
            ConditionFromSymbol(symbol),
            !(symbol ?? string.Empty).EndsWith("_night", StringComparison.OrdinalIgnoreCase));

    private sealed record Location(string Query, double Latitude, double Longitude, string Name);
}
