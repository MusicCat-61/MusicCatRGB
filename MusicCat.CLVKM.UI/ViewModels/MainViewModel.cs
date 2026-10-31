using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicCat.CLVKM.Core.Animations;
using MusicCat.CLVKM.Core.Drivers;
using MusicCat.CLVKM.Core.Interfaces;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.Core.Services;
using MusicCat.CLVKM.Core.Settings;
using MusicCat.CLVKM.Drivers;
using MusicCat.CLVKM.UI.Localization;

using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace MusicCat.CLVKM.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private IKeyboardController? _driver;
    private AsyncDriverQueue? _queue;
    private AnimationEngine? _animationEngine;
    private readonly AppSettings _currentSettings;

    /// <summary>Prevents settings from being re-saved while the saved state is being restored.</summary>
    private bool _suppressSettingsSave;

    private string _statusKey = "St_Init";


    private object?[] _statusArgs = Array.Empty<object?>();

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    /// <summary>0 = Auto, 1 = Insyde, 2 = WMI.</summary>
    [ObservableProperty]
    private int _driverType;

    [ObservableProperty] private byte _red;
    [ObservableProperty] private byte _green = 212; // Default neon blue
    [ObservableProperty] private byte _blue = 255;

    [ObservableProperty] private string _hexColor = "#00D4FF";

    [ObservableProperty] private byte _staticRed;
    [ObservableProperty] private byte _staticGreen = 212;
    [ObservableProperty] private byte _staticBlue = 255;

    [ObservableProperty] private string _staticHexColor = "#00D4FF";

    [ObservableProperty] private byte _secondaryRed = 255;
    [ObservableProperty] private byte _secondaryGreen = 60;
    [ObservableProperty] private byte _secondaryBlue = 200;

    [ObservableProperty] private string _secondaryHexColor = "#FF3CC8";

    [ObservableProperty] private byte _tertiaryRed = 255;
    [ObservableProperty] private byte _tertiaryGreen = 140;
    [ObservableProperty] private byte _tertiaryBlue = 0;

    [ObservableProperty] private string _tertiaryHexColor = "#FF8C00";

    /// <summary>Which of the two colour slots the RGB sliders and hex box edit.</summary>
    [ObservableProperty] private ColorSlot _activeSlot = ColorSlot.Static;

    [ObservableProperty] private Zone _selectedZone = Zone.All;
    [ObservableProperty] private bool _isMultiZoneCapable;

    [ObservableProperty] private bool _isAnimationRunning;
    [ObservableProperty] private AnimationItem? _selectedAnimation;

    [ObservableProperty] private double _brightness = 100;
    [ObservableProperty] private double _animationSpeed = 1.0;

    [ObservableProperty] private bool _sleepTimerEnabled;

    /// <summary>True while the sleep timer has switched the backlight off.</summary>
    [ObservableProperty] private bool _isAsleep;

    public bool IsAwake => !IsAsleep;

    partial void OnIsAsleepChanged(bool value) => OnPropertyChanged(nameof(IsAwake));

    private static readonly TimeSpan SleepTimeout = TimeSpan.FromMinutes(5);
    private DispatcherTimer? _idleTimer;
    private bool _isSleeping;
    private bool _sleepTransition;

    // --- Weather effect (only visible while the weather animation is selected) ---

    private static readonly SolidColorBrush WeatherUnknownBrush = FrozenBrush(0x9A, 0xA0, 0xAB);
    private static readonly SolidColorBrush WeatherOnlineBrush = FrozenBrush(0x4A, 0xDE, 0x80);
    private static readonly SolidColorBrush WeatherOfflineBrush = FrozenBrush(0xE8, 0x11, 0x23);
    private static readonly SolidColorBrush WeatherPendingBrush = FrozenBrush(0xF2, 0xC9, 0x4C);

    private static readonly string[] WeatherDirectionKeys =
    {
        "Wx_DirN", "Wx_DirNE", "Wx_DirE", "Wx_DirSE",
        "Wx_DirS", "Wx_DirSW", "Wx_DirW", "Wx_DirNW"
    };

    private DispatcherTimer? _weatherTimer;
    private DispatcherTimer? _citySearchTimer;
    private CancellationTokenSource? _citySearchCts;
    private bool _applyingCity;

    [ObservableProperty] private string _weatherCityInput = string.Empty;
    [ObservableProperty] private string _weatherStatusText = string.Empty;
    [ObservableProperty] private string _weatherUpdatedText = string.Empty;
    [ObservableProperty] private string _weatherTemperatureText = string.Empty;
    [ObservableProperty] private string _weatherConditionText = string.Empty;
    [ObservableProperty] private string _weatherWindText = string.Empty;
    [ObservableProperty] private string _weatherErrorText = string.Empty;
    [ObservableProperty] private bool _hasWeatherData;
    [ObservableProperty] private bool _isCitySuggestionsOpen;
    [ObservableProperty] private CitySuggestion? _selectedCitySuggestion;
    [ObservableProperty] private SolidColorBrush _weatherStatusBrush = WeatherUnknownBrush;
    [ObservableProperty] private string _driverErrorText = string.Empty;

    public ObservableCollection<CitySuggestion> CitySuggestions { get; } = new();

    /// <summary>The weather block is only shown while the weather effect is selected.</summary>
    public bool ShowWeatherPanel => SelectedAnimation?.Animation is WeatherSyncAnimation;

    private static SolidColorBrush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewColorBrush))]
    private Color _previewColor = Colors.Gray;

    public ObservableCollection<AnimationItem> Animations { get; } = new();

    public ObservableCollection<ZoneOption> ZoneOptions { get; } = new()
    {
        new ZoneOption(Zone.All, "L_ZoneAll"),
        new ZoneOption(Zone.Left, "L_ZoneLeft"),
        new ZoneOption(Zone.Center, "L_ZoneCenter"),
        new ZoneOption(Zone.Right, "L_ZoneRight")
    };

    public bool IsEnglish => Loc.Current == AppLanguage.En;

    public bool IsRussian => Loc.Current == AppLanguage.Ru;


    public SolidColorBrush PreviewColorBrush => new(PreviewColor);

    public Color PrimaryColor => Color.FromRgb(Red, Green, Blue);

    public SolidColorBrush PrimaryColorBrush => new(PrimaryColor);

    public Color SecondaryColor => Color.FromRgb(SecondaryRed, SecondaryGreen, SecondaryBlue);

    public SolidColorBrush SecondaryColorBrush => new(SecondaryColor);

    public Color TertiaryColor => Color.FromRgb(TertiaryRed, TertiaryGreen, TertiaryBlue);

    public SolidColorBrush TertiaryColorBrush => new(TertiaryColor);

    public bool IsPrimaryActive => ActiveSlot == ColorSlot.Primary;

    public bool IsSecondaryActive => ActiveSlot == ColorSlot.Secondary;

    public bool IsTertiaryActive => ActiveSlot == ColorSlot.Tertiary;

    public bool IsStaticActive => ActiveSlot == ColorSlot.Static;

    public Color StaticColor => Color.FromRgb(StaticRed, StaticGreen, StaticBlue);

    public SolidColorBrush StaticColorBrush => new(StaticColor);

    /// <summary>The third colour is only meaningful for the zone-gradient effect.</summary>
    public bool ShowTertiaryColor => SelectedAnimation?.Animation is ZoneGradientAnimation;

    public Color ActiveColor => ActiveSlot switch
    {
        ColorSlot.Secondary => SecondaryColor,
        ColorSlot.Tertiary => TertiaryColor,
        ColorSlot.Primary => PrimaryColor,
        _ => StaticColor
    };

    public SolidColorBrush ActiveColorBrush => new(ActiveColor);

    public byte ActiveRed
    {
        get => ActiveSlot switch
        {
            ColorSlot.Secondary => SecondaryRed,
            ColorSlot.Tertiary => TertiaryRed,
            ColorSlot.Primary => Red,
            _ => StaticRed
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryRed = value; break;
                case ColorSlot.Tertiary: TertiaryRed = value; break;
                case ColorSlot.Primary: Red = value; break;
                default: StaticRed = value; break;
            }

            OnPropertyChanged();
        }
    }

    public byte ActiveGreen
    {
        get => ActiveSlot switch
        {
            ColorSlot.Secondary => SecondaryGreen,
            ColorSlot.Tertiary => TertiaryGreen,
            ColorSlot.Primary => Green,
            _ => StaticGreen
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryGreen = value; break;
                case ColorSlot.Tertiary: TertiaryGreen = value; break;
                case ColorSlot.Primary: Green = value; break;
                default: StaticGreen = value; break;
            }

            OnPropertyChanged();
        }
    }

    public byte ActiveBlue
    {
        get => ActiveSlot switch
        {
            ColorSlot.Secondary => SecondaryBlue,
            ColorSlot.Tertiary => TertiaryBlue,
            ColorSlot.Primary => Blue,
            _ => StaticBlue
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryBlue = value; break;
                case ColorSlot.Tertiary: TertiaryBlue = value; break;
                case ColorSlot.Primary: Blue = value; break;
                default: StaticBlue = value; break;
            }

            OnPropertyChanged();
        }
    }

    public string ActiveHexColor
    {
        get => ActiveSlot switch
        {
            ColorSlot.Secondary => SecondaryHexColor,
            ColorSlot.Tertiary => TertiaryHexColor,
            ColorSlot.Primary => HexColor,
            _ => StaticHexColor
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryHexColor = value; break;
                case ColorSlot.Tertiary: TertiaryHexColor = value; break;
                case ColorSlot.Primary: HexColor = value; break;
                default: StaticHexColor = value; break;
            }

            OnPropertyChanged();
        }
    }
    public MainViewModel()
    {
        _currentSettings = SettingsManager.Load();

        _red = _currentSettings.Red;
        _green = _currentSettings.Green;
        _blue = _currentSettings.Blue;
        _secondaryRed = _currentSettings.SecondaryRed;
        _secondaryGreen = _currentSettings.SecondaryGreen;
        _secondaryBlue = _currentSettings.SecondaryBlue;
        _staticRed = _currentSettings.StaticRed;
        _staticGreen = _currentSettings.StaticGreen;
        _staticBlue = _currentSettings.StaticBlue;

        // Migration: settings written before the separate static colour existed keep
        // using the primary colour as the applied one.
        if (!_currentSettings.HasStaticColor)
        {
            _staticRed = _red;
            _staticGreen = _green;
            _staticBlue = _blue;
            _currentSettings.HasStaticColor = true;
        _currentSettings.SleepTimerEnabled = SleepTimerEnabled;
        }
        _tertiaryRed = _currentSettings.TertiaryRed;
        _tertiaryGreen = _currentSettings.TertiaryGreen;
        _tertiaryBlue = _currentSettings.TertiaryBlue;
        _driverType = _currentSettings.DriverType;
        _brightness = Math.Clamp(_currentSettings.Brightness, 0, 100);
        _sleepTimerEnabled = _currentSettings.SleepTimerEnabled;
        _animationSpeed = _currentSettings.AnimationSpeed < 0.1 ? 1.0 : _currentSettings.AnimationSpeed;

        UpdateHexFromRgb();
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        SetStatus("St_Init");
        BuildAnimations();
        UpdateIdleTimer();
        InitializeWeather();

        // Show a selection in the UI even before the driver is ready, without
        // overwriting the animation name we are going to restore later.
        string? savedAnimationName = _currentSettings.LastAnimation;
        _suppressSettingsSave = true;
        SelectedAnimation = Animations[0];
        _suppressSettingsSave = false;
        _currentSettings.LastAnimation = savedAnimationName;

        Loc.LanguageChanged += OnLanguageChanged;
        DriverDiagnostics.ErrorReported += OnDriverError;

        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        await Task.Delay(500); // Let the window render first.

        bool autoStart = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--tray", StringComparison.OrdinalIgnoreCase));

        try
        {
            // The Control Center (FnKey.exe) may be slow to start after logon, so an
            // auto-started instance retries far longer than a manually launched one.
            int maxAttempts = autoStart ? 80 : 10; // ~4 min at boot / ~30 s manually
            int attempt = 0;
            IKeyboardController? baseDriver = null;

            while (attempt < maxAttempts)
            {
                if (attempt == 0)
                {
                    SetStatus("St_Scan");
                }
                else
                {
                    SetStatus("St_Retry", Math.Min(attempt + 1, maxAttempts), maxAttempts);
                }

                try
                {
                    baseDriver = await DeviceManager.CreateDriverAsync(DriverType);
                }
                catch
                {
                    // Transient (driver not ready yet) - keep trying.
                }

                if (baseDriver is not null)
                {
                    break;
                }

                attempt++;
                if (attempt >= maxAttempts)
                {
                    break;
                }

                await Task.Delay(3000);
            }

            if (baseDriver is null)
            {
                SetStatus("St_NotFound");
                return;
            }

            SetStatus("St_AntiLag");
            _queue = new AsyncDriverQueue(baseDriver);
            await _queue.InitializeAsync();

            IsMultiZoneCapable = baseDriver.GetType().Name.Contains("Wmi");
            if (!IsMultiZoneCapable)
            {
                SelectedZone = Zone.All;
            }

            _driver = _queue;
            IsReady = true;

            _animationEngine = new AnimationEngine(_driver)
            {
                Speed = AnimationSpeed,
                Brightness = (float)(Brightness / 100.0)
            };
            _animationEngine.FrameApplied += OnFrameApplied;

            RestoreSavedState();

            if (!IsAnimationRunning)
            {
                SetStatus("St_Ready");
            }
        }
        catch (Exception ex)
        {
            SetStatus("St_Failed", ex.Message);
        }
    }

    private void BuildAnimations()
    {
        Animations.Clear();

        foreach (var animation in AnimationCatalog.CreateAll())
        {
            Animations.Add(new AnimationItem(animation));
        }
    }

    /// <summary>
    /// Restores the saved animation / color and re-applies it. This is what makes the
    /// lighting come back automatically after Windows starts.
    /// </summary>
    private void RestoreSavedState()
    {
        AnimationItem? saved = !string.IsNullOrEmpty(_currentSettings.LastAnimation)
            ? Animations.FirstOrDefault(item => item.Animation.Name == _currentSettings.LastAnimation)
            : null;

        // Selecting an item normally persists settings; suppress that so it cannot
        // overwrite the saved "animation active" flag we are about to read.
        _suppressSettingsSave = true;
        SelectedAnimation = saved ?? Animations.FirstOrDefault();
        _suppressSettingsSave = false;

        if (_currentSettings.IsAnimationActive && SelectedAnimation is not null)
        {
            SetStatus("St_Resuming");
            StartAnimation();
        }
        else
        {
            IsAnimationRunning = false;
            _ = ApplyColorCore(announceStatus: false);
        }
    }

    private void StartAnimation()
    {
        if (_animationEngine is null || SelectedAnimation is null)
        {
            return;
        }

        
        SyncAnimationColors();
        _animationEngine.Start(SelectedAnimation.Animation);
        IsAnimationRunning = true;
        SetStatus("St_Running");
        SaveSettings();
    }

    private void StopAnimation()
    {
        _animationEngine?.Stop();
        IsAnimationRunning = false;
        SetStatus("St_Stopped");

        SaveSettings();

        // Stopping an effect always returns the keyboard to the single static colour.
        _ = ApplyColorCore(announceStatus: false);
    }

    [RelayCommand]
    private void ToggleAnimation()
    {
        if (IsAnimationRunning)
        {
            StopAnimation();
        }
        else
        {
            StartAnimation();
        }
    }

    // ───────────────────────────── Weather effect ─────────────────────────────

    private void InitializeWeather()
    {
        WeatherService.Instance.Language = Loc.CurrentCode;
        WeatherService.Instance.RestoreCity(
            _currentSettings.WeatherCity ?? string.Empty,
            _currentSettings.WeatherLatitude,
            _currentSettings.WeatherLongitude);
        WeatherService.Instance.Updated += OnWeatherUpdated;

        WeatherCityInput = WeatherService.Instance.City;
        ApplyWeatherSnapshot(WeatherService.Instance.Snapshot);

        // Keeps the API-link status and the readings fresh while the panel is open.
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _weatherTimer.Tick += OnWeatherTick;
        _weatherTimer.Start();

        // Typing in the city box is debounced before the suggestion lookup goes out.
        _citySearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _citySearchTimer.Tick += OnCitySearchTick;
    }

    partial void OnWeatherCityInputChanged(string value)
    {
        if (_applyingCity)
        {
            return;
        }

        _citySearchTimer?.Stop();

        if (string.IsNullOrWhiteSpace(value))
        {
            CloseCitySuggestions();
            return;
        }

        _citySearchTimer?.Start();
    }

    private void OnCitySearchTick(object? sender, EventArgs e)
    {
        _citySearchTimer?.Stop();
        _ = SearchCitySuggestionsAsync();
    }

    private async Task SearchCitySuggestionsAsync()
    {
        var query = (WeatherCityInput ?? string.Empty).Trim();
        if (query.Length < 2)
        {
            CloseCitySuggestions();
            return;
        }

        _citySearchCts?.Cancel();
        _citySearchCts = new CancellationTokenSource();
        var token = _citySearchCts.Token;

        try
        {
            var results = await WeatherService.Instance.SearchCitiesAsync(query, 8, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            CitySuggestions.Clear();
            foreach (var suggestion in results)
            {
                CitySuggestions.Add(suggestion);
            }

            SelectedCitySuggestion = CitySuggestions.FirstOrDefault();
            IsCitySuggestionsOpen = CitySuggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
        catch
        {
            CloseCitySuggestions();
        }
    }

    [RelayCommand]
    private void SelectCitySuggestion(CitySuggestion? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        _applyingCity = true;
        WeatherCityInput = suggestion.DisplayName;
        _applyingCity = false;
        CloseCitySuggestions();

        // The exact coordinates from the suggestion are pinned, so the weather comes from
        // the right place even when the name exists in several regions.
        WeatherService.Instance.SelectCity(suggestion);
        PersistWeatherCity(suggestion.DisplayName, suggestion.Latitude, suggestion.Longitude);
        _ = WeatherService.Instance.RefreshAsync();
    }

    /// <summary>Moves the highlighted suggestion (used by the arrow keys in the city box).</summary>
    public void MoveCitySuggestionSelection(int delta)
    {
        if (CitySuggestions.Count == 0)
        {
            return;
        }

        var index = SelectedCitySuggestion is null ? -1 : CitySuggestions.IndexOf(SelectedCitySuggestion);
        index = Math.Clamp(index + delta, 0, CitySuggestions.Count - 1);
        SelectedCitySuggestion = CitySuggestions[index];
        IsCitySuggestionsOpen = true;
    }

    public void CloseCitySuggestions()
    {
        IsCitySuggestionsOpen = false;
        SelectedCitySuggestion = null;
    }

    private void PersistWeatherCity(string city, double latitude, double longitude)
    {
        _currentSettings.WeatherCity = city;
        _currentSettings.WeatherLatitude = latitude;
        _currentSettings.WeatherLongitude = longitude;
        SaveSettings();
    }

    private void OnWeatherTick(object? sender, EventArgs e)
    {
        if (ShowWeatherPanel)
        {
            _ = WeatherService.Instance.RefreshAsync();
        }
    }

    private void OnWeatherUpdated(object? sender, WeatherSnapshot snapshot)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Dispatcher.Invoke(() => ApplyWeatherSnapshot(snapshot));
        }
        catch
        {
            // Dispatcher may be shutting down.
        }
    }

    /// <summary>Driver failures stay non-fatal; the last one is shown in the status tooltip.</summary>
    private void OnDriverError(string message)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Dispatcher.Invoke(() => DriverErrorText = message);
        }
        catch
        {
            // Dispatcher may be shutting down.
        }
    }

    private void ApplyWeatherSnapshot(WeatherSnapshot snapshot)
    {
        WeatherStatusText = DescribeWeatherState(snapshot);
        WeatherStatusBrush = snapshot.State switch
        {
            WeatherState.Ready => WeatherOnlineBrush,
            WeatherState.Offline or WeatherState.CityNotFound => WeatherOfflineBrush,
            WeatherState.Updating => WeatherPendingBrush,
            _ => WeatherUnknownBrush
        };

        WeatherErrorText = snapshot.Error ?? string.Empty;
        HasWeatherData = snapshot.TemperatureC is not null;

        WeatherTemperatureText = snapshot.TemperatureC is double temperature
            ? Loc.F("Wx_TemperatureValue", FormatSigned(temperature))
            : string.Empty;

        WeatherConditionText = snapshot.Condition == WeatherCondition.Unknown
            ? string.Empty
            : Loc.T($"Wx_{snapshot.Condition}");

        WeatherWindText = FormatWind(snapshot);

        WeatherUpdatedText = snapshot.UpdatedUtc == default
            ? string.Empty
            : Loc.F("Wx_Updated", snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
    }

    private static string DescribeWeatherState(WeatherSnapshot snapshot)
    {
        var text = snapshot.State switch
        {
            WeatherState.Ready => Loc.T("Wx_StatusOnline"),
            WeatherState.Offline => Loc.T("Wx_StatusOffline"),
            WeatherState.CityNotFound => Loc.T("Wx_StatusCityNotFound"),
            WeatherState.NoCity => Loc.T("Wx_StatusNoCity"),
            WeatherState.Updating => Loc.T("Wx_StatusUpdating"),
            _ => Loc.T("Wx_StatusConnecting")
        };

        return string.IsNullOrEmpty(snapshot.City) ? text : $"{text} · {snapshot.City}";
    }

    private static string FormatSigned(double value) =>
        string.Format(CultureInfo.CurrentCulture, "{0:+0;-0;0}", (int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static string FormatWind(WeatherSnapshot snapshot)
    {
        if (snapshot.WindSpeedMs is not double speed)
        {
            return string.Empty;
        }

        var direction = snapshot.WindDirectionDegrees is double degrees
            ? Loc.T(WeatherDirectionKey(degrees))
            : string.Empty;

        return direction.Length == 0
            ? Loc.F("Wx_WindSpeedOnly", speed)
            : Loc.F("Wx_WindValue", direction, speed);
    }

    private static string WeatherDirectionKey(double degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        var index = (int)Math.Round(normalized / 45.0, MidpointRounding.AwayFromZero) % 8;
        return WeatherDirectionKeys[index];
    }

    [RelayCommand]
    private async Task ApplyColor() => await ApplyColorCore(announceStatus: true);

    private async Task ApplyColorCore(bool announceStatus)
    {
        if (_driver is null || !IsReady || _isSleeping)
        {
            return;
        }

        if (IsAnimationRunning)
        {
            ToggleAnimation();
        }

        var applied = StaticColor;
        var color = new ColorRGB(applied.R, applied.G, applied.B);
        var finalColor = color.ApplyBrightness((float)(Brightness / 100.0));
        var zone = IsMultiZoneCapable ? SelectedZone : Zone.All;

        await _driver.SetColorAsync(zone, finalColor);
        SaveSettings();

        if (announceStatus)
        {
            SetStatus("St_Applied");
        }
    }

    [RelayCommand]
    private void SelectPrimaryColor() => ActiveSlot = ColorSlot.Primary;

    [RelayCommand]
    private void SelectSecondaryColor() => ActiveSlot = ColorSlot.Secondary;

    [RelayCommand]
    private void SelectTertiaryColor() => ActiveSlot = ColorSlot.Tertiary;

    partial void OnActiveSlotChanged(ColorSlot value)
    {
        OnPropertyChanged(nameof(IsPrimaryActive));
        OnPropertyChanged(nameof(IsSecondaryActive));
        OnPropertyChanged(nameof(IsTertiaryActive));
        OnPropertyChanged(nameof(IsStaticActive));
        OnPropertyChanged(nameof(ActiveRed));
        OnPropertyChanged(nameof(ActiveGreen));
        OnPropertyChanged(nameof(ActiveBlue));
        OnPropertyChanged(nameof(ActiveHexColor));
        UpdateColorPreview();
    }

    public bool IsLightTheme => Theming.Theme.IsLight;

    [RelayCommand]
    private void ToggleTheme()
    {
        Theming.Theme.Toggle();
        OnPropertyChanged(nameof(IsLightTheme));
        _currentSettings.Theme = Theming.Theme.CurrentCode;
        SaveSettings();
    }

    [RelayCommand]
    private void SetLanguage(string? code)
    {
        var language = Loc.Parse(code);
        if (language == Loc.Current)
        {
            return;
        }

        Loc.SetLanguage(language);
    }

    [RelayCommand]
    private async Task Shutdown()
    {
        _idleTimer?.Stop();
        _weatherTimer?.Stop();
        _citySearchTimer?.Stop();
        _citySearchCts?.Cancel();
        SaveSettings();
        if (_queue is not null)
        {
            await _queue.ShutdownAsync();
        }
    }

    partial void OnSelectedAnimationChanged(AnimationItem? value)
    {
        if (value is null)
        {
            return;
        }

        _currentSettings.LastAnimation = value.Animation.Name;

        OnPropertyChanged(nameof(ShowTertiaryColor));
        OnPropertyChanged(nameof(ShowWeatherPanel));

        if (ShowWeatherPanel)
        {
            // Show the current readings immediately and check the API link.
            _ = WeatherService.Instance.RefreshAsync();
        }

        if (!ShowTertiaryColor && ActiveSlot == ColorSlot.Tertiary)
        {
            ActiveSlot = ColorSlot.Primary;
        }

        if (IsAnimationRunning && _animationEngine is not null)
        {
            
            SyncAnimationColors();
            _animationEngine.Start(value.Animation);
            SetStatus("St_Running");
        }

        if (!_suppressSettingsSave)
        {
            SaveSettings();
        }
    }

    partial void OnRedChanged(byte value)
    {
        UpdateHexFromRgb();
        if (IsPrimaryActive) OnPropertyChanged(nameof(ActiveRed));
        SyncAnimationColors();
    }

    partial void OnGreenChanged(byte value)
    {
        UpdateHexFromRgb();
        SyncAnimationColors();
    }

    partial void OnBlueChanged(byte value)
    {
        UpdateHexFromRgb();
        SyncAnimationColors();
    }

    partial void OnSecondaryRedChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveRed));
    }

    partial void OnSecondaryGreenChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveGreen));
    }

    partial void OnSecondaryBlueChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveBlue));
    }

    partial void OnHexColorChanged(string value)
    {
        if (value.Length == 7 && value.StartsWith('#'))
        {
            try
            {
                var color = System.Drawing.ColorTranslator.FromHtml(value);

#pragma warning disable MVVMTK0034
                _red = color.R;
                _green = color.G;
                _blue = color.B;
#pragma warning restore MVVMTK0034

                OnPropertyChanged(nameof(Red));
                OnPropertyChanged(nameof(Green));
                OnPropertyChanged(nameof(Blue));
                
                SyncAnimationColors();
                UpdateColorPreview();
            }
            catch
            {
                // Ignore incomplete input.
            }
        }
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_isSleeping)
        {
            return;
        }

        if (_animationEngine is not null)
        {
            _animationEngine.Brightness = (float)(value / 100.0);
        }

        if (!IsAnimationRunning && IsReady)
        {
            _ = ApplyColorCore(announceStatus: false);
        }
    }

    partial void OnAnimationSpeedChanged(double value)
    {
        if (_animationEngine is not null)
        {
            _animationEngine.Speed = value;
        }

        SaveSettings();
    }

    private void OnLanguageChanged()
    {
        foreach (var item in Animations)
        {
            item.Refresh();
        }

        foreach (var zone in ZoneOptions)
        {
            zone.Refresh();
        }

        StatusText = FormatStatus();

        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsRussian));

        // Localised city names and weather texts follow the UI language.
        WeatherService.Instance.Language = Loc.CurrentCode;
        ApplyWeatherSnapshot(WeatherService.Instance.Snapshot);

        _currentSettings.Language = Loc.CurrentCode;
        SaveSettings();
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusText = FormatStatus();
    }

    private string FormatStatus() =>
        _statusArgs.Length == 0 ? Loc.T(_statusKey) : Loc.F(_statusKey, _statusArgs);

    private void UpdateHexFromRgb()
    {
        

        HexColor = $"#{Red:X2}{Green:X2}{Blue:X2}";
        OnPropertyChanged(nameof(HexColor));
        OnPropertyChanged(nameof(PrimaryColorBrush));
        if (IsPrimaryActive) OnPropertyChanged(nameof(ActiveHexColor));
        UpdateColorPreview();
    }

    private void UpdateColorPreview()
    {
        OnPropertyChanged(nameof(ActiveColor));
        OnPropertyChanged(nameof(ActiveColorBrush));
        OnPropertyChanged(nameof(StaticColor));
        OnPropertyChanged(nameof(StaticColorBrush));

        if (!IsAnimationRunning)
        {
            PreviewColor = StaticColor;
        }
    }

    private void OnFrameApplied(ColorRGB color, Zone zone)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        try
        {
            app.Dispatcher.Invoke(() => PreviewColor = Color.FromRgb(color.R, color.G, color.B));
        }
        catch
        {
            // Dispatcher may be shutting down.
        }
    }

    private void UpdateSecondaryHexFromRgb()
    {
        

        SecondaryHexColor = $"#{SecondaryRed:X2}{SecondaryGreen:X2}{SecondaryBlue:X2}";
        OnPropertyChanged(nameof(SecondaryColorBrush));
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveHexColor));
        SyncAnimationColors();
    }

    partial void OnSecondaryHexColorChanged(string value)
    {
        if (value.Length != 7 || !value.StartsWith('#'))
        {
            return;
        }

        try
        {
            var color = System.Drawing.ColorTranslator.FromHtml(value);

#pragma warning disable MVVMTK0034
            _secondaryRed = color.R;
            _secondaryGreen = color.G;
            _secondaryBlue = color.B;
#pragma warning restore MVVMTK0034

            OnPropertyChanged(nameof(SecondaryRed));
            OnPropertyChanged(nameof(SecondaryGreen));
            OnPropertyChanged(nameof(SecondaryBlue));
            OnPropertyChanged(nameof(SecondaryColorBrush));
            
            SyncAnimationColors();
        }
        catch
        {
            // Ignore incomplete input.
        }
    }

    private void SyncAnimationColors()
    {
        if (SelectedAnimation?.Animation is not IColorAwareAnimation aware)
        {
            return;
        }

        aware.Primary = new ColorRGB(Red, Green, Blue);
        aware.Secondary = new ColorRGB(SecondaryRed, SecondaryGreen, SecondaryBlue);

        if (aware is ZoneGradientAnimation gradient)
        {
            gradient.SingleZone = !IsMultiZoneCapable;
            gradient.Tertiary = new ColorRGB(TertiaryRed, TertiaryGreen, TertiaryBlue);
        }

        if (aware is ColorShiftAnimation wave)
        {
            wave.SingleZone = !IsMultiZoneCapable;
        }

        if (aware is GarlandAnimation garland)
        {
            garland.SingleZone = !IsMultiZoneCapable;
        }
    }

    private void UpdateStaticHexFromRgb()
    {
        StaticHexColor = $"#{StaticRed:X2}{StaticGreen:X2}{StaticBlue:X2}";
        OnPropertyChanged(nameof(StaticColor));
        OnPropertyChanged(nameof(StaticColorBrush));

        if (IsStaticActive)
        {
            OnPropertyChanged(nameof(ActiveRed));
            OnPropertyChanged(nameof(ActiveGreen));
            OnPropertyChanged(nameof(ActiveBlue));
        }

        UpdateColorPreview();
    }

    partial void OnStaticRedChanged(byte value)
    {
        UpdateStaticHexFromRgb();
        if (IsStaticActive) OnPropertyChanged(nameof(ActiveRed));
    }

    partial void OnStaticGreenChanged(byte value)
    {
        UpdateStaticHexFromRgb();
        if (IsStaticActive) OnPropertyChanged(nameof(ActiveGreen));
    }

    partial void OnStaticBlueChanged(byte value)
    {
        UpdateStaticHexFromRgb();
        if (IsStaticActive) OnPropertyChanged(nameof(ActiveBlue));
    }

    partial void OnStaticHexColorChanged(string value)
    {
        if (value.Length != 7 || !value.StartsWith('#'))
        {
            return;
        }

        try
        {
            var color = System.Drawing.ColorTranslator.FromHtml(value);

#pragma warning disable MVVMTK0034
            _staticRed = color.R;
            _staticGreen = color.G;
            _staticBlue = color.B;
#pragma warning restore MVVMTK0034

            OnPropertyChanged(nameof(StaticRed));
            OnPropertyChanged(nameof(StaticGreen));
            OnPropertyChanged(nameof(StaticBlue));
            OnPropertyChanged(nameof(StaticColor));
            OnPropertyChanged(nameof(StaticColorBrush));
            UpdateColorPreview();
        }
        catch
        {
            // Ignore incomplete input.
        }
    }

    private void UpdateTertiaryHexFromRgb()
    {
        

        TertiaryHexColor = $"#{TertiaryRed:X2}{TertiaryGreen:X2}{TertiaryBlue:X2}";
        OnPropertyChanged(nameof(TertiaryColorBrush));
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveHexColor));
        SyncAnimationColors();
    }

    partial void OnTertiaryRedChanged(byte value)
    {
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveRed));
    }

    partial void OnTertiaryGreenChanged(byte value)
    {
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveGreen));
    }

    partial void OnTertiaryBlueChanged(byte value)
    {
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveBlue));
    }

    partial void OnTertiaryHexColorChanged(string value)
    {
        if (value.Length != 7 || !value.StartsWith('#'))
        {
            return;
        }

        try
        {
            var color = System.Drawing.ColorTranslator.FromHtml(value);

#pragma warning disable MVVMTK0034
            _tertiaryRed = color.R;
            _tertiaryGreen = color.G;
            _tertiaryBlue = color.B;
#pragma warning restore MVVMTK0034

            OnPropertyChanged(nameof(TertiaryRed));
            OnPropertyChanged(nameof(TertiaryGreen));
            OnPropertyChanged(nameof(TertiaryBlue));
            OnPropertyChanged(nameof(TertiaryColorBrush));
            if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveHexColor));
            SyncAnimationColors();
            UpdateColorPreview();
        }
        catch
        {
            // Ignore incomplete input.
        }
    }

    partial void OnSleepTimerEnabledChanged(bool value)
    {
        _currentSettings.SleepTimerEnabled = value;
        SaveSettings();
        UpdateIdleTimer();

        if (!value)
        {
            WakeUp();
        }
    }

    private void UpdateIdleTimer()
    {
        if (!SleepTimerEnabled)
        {
            _idleTimer?.Stop();
            return;
        }

        if (_idleTimer is null)
        {
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _idleTimer.Tick += OnIdleTick;
        }

        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_driver is null || !IsReady)
        {
            return;
        }

        var idle = Interop.IdleTime.GetIdleTime();

        if (!_isSleeping && idle >= SleepTimeout)
        {
            Sleep();
        }
        else if (_isSleeping && idle < TimeSpan.FromSeconds(2))
        {
            WakeUp();
        }
    }

    /// <summary>Colour that is currently on the keyboard (animation frame or static colour).</summary>
    private ColorRGB OutputColor
    {
        get
        {
            if (IsAnimationRunning)
            {
                return new ColorRGB(PreviewColor.R, PreviewColor.G, PreviewColor.B);
            }

            var staticColor = new ColorRGB(StaticColor.R, StaticColor.G, StaticColor.B);
            return staticColor.ApplyBrightness((float)(Brightness / 100.0));
        }
    }

    private async void Sleep()
    {
        if (_isSleeping || _sleepTransition || _driver is null || !IsReady)
        {
            return;
        }

        _sleepTransition = true;
        _isSleeping = true;
        IsAsleep = true;
        SetStatus("St_Sleeping");

        var from = OutputColor;
        _animationEngine?.Pause();

        try
        {
            await FadeAsync(from, ColorRGB.Black, 450);
            await _driver.SetColorAsync(Zone.All, ColorRGB.Black);
            PreviewColor = Colors.Black;
        }
        finally
        {
            _sleepTransition = false;
        }
    }

    private async void WakeUp()
    {
        if (!_isSleeping || _sleepTransition || _driver is null)
        {
            return;
        }

        _sleepTransition = true;
        _isSleeping = false;
        IsAsleep = false;

        try
        {
            await FadeAsync(ColorRGB.Black, OutputColor, 350);

            if (IsAnimationRunning && _animationEngine is not null)
            {
                _animationEngine.Resume();
                SetStatus("St_Running");
            }
            else
            {
                PreviewColor = StaticColor;
                SetStatus("St_Ready");
                SaveSettings();
            }
        }
        finally
        {
            _sleepTransition = false;
        }
    }

    private async Task FadeAsync(ColorRGB from, ColorRGB to, int durationMs)
    {
        const int stepMs = 25;
        var steps = Math.Max(1, durationMs / stepMs);

        for (var i = 1; i <= steps; i++)
        {
            var color = ColorRGB.Lerp(from, to, i / (double)steps);
            await _driver!.SetColorAsync(Zone.All, color);
            await Task.Delay(stepMs);
        }
    }

    private void SaveSettings()
    {
        _currentSettings.Red = Red;
        _currentSettings.Green = Green;
        _currentSettings.Blue = Blue;
        _currentSettings.SecondaryRed = SecondaryRed;
        _currentSettings.SecondaryGreen = SecondaryGreen;
        _currentSettings.SecondaryBlue = SecondaryBlue;
        _currentSettings.TertiaryRed = TertiaryRed;
        _currentSettings.TertiaryGreen = TertiaryGreen;
        _currentSettings.TertiaryBlue = TertiaryBlue;
        _currentSettings.StaticRed = StaticRed;
        _currentSettings.StaticGreen = StaticGreen;
        _currentSettings.StaticBlue = StaticBlue;
        _currentSettings.HasStaticColor = true;
        _currentSettings.SleepTimerEnabled = SleepTimerEnabled;
        _currentSettings.DriverType = DriverType;
        _currentSettings.Brightness = Brightness;
        _currentSettings.IsAnimationActive = IsAnimationRunning;
        _currentSettings.AnimationSpeed = AnimationSpeed;
        _currentSettings.Language = Loc.CurrentCode;

        if (SelectedAnimation is not null)
        {
            _currentSettings.LastAnimation = SelectedAnimation.Animation.Name;
        }

        _currentSettings.WeatherCity = WeatherService.Instance.City.Length == 0
            ? null
            : WeatherService.Instance.City;

        SettingsManager.Save(_currentSettings);
    }

    public void ResetState()
    {
        if (IsAnimationRunning)
        {
            ToggleAnimation();
        }

        Red = 0;
        Green = 212;
        Blue = 255;
        Brightness = 100;
        SelectedZone = Zone.All;
        DriverType = 0;
        AnimationSpeed = 1.0;

        _currentSettings.IsStartupEnabled = false;
        _currentSettings.IsAnimationActive = false;
        _currentSettings.LastAnimation = null;

        WeatherCityInput = string.Empty;
        CloseCitySuggestions();
        WeatherService.Instance.City = string.Empty;
        _currentSettings.WeatherCity = null;
        _currentSettings.WeatherLatitude = null;
        _currentSettings.WeatherLongitude = null;

        // Settings file is deleted by App.RemoveDataAndStartup; the reset fields above
        // make sure a later save cannot resurrect the old state.
    }
}
