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
using MusicCat.CLVKM.Core.Ipc;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.Core.Services;
using MusicCat.CLVKM.Core.Settings;
using MusicCat.CLVKM.Drivers;
using MusicCat.CLVKM.UI.Localization;
using MusicCat.CLVKM.UI.Logging;

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

    // --- Keypress and notification flashes (the events arrive from the watcher process) ---

    /// <summary>Flash the keyboard on every key while typing, in a new random colour each time.</summary>
    [ObservableProperty] private bool _typingFlashEnabled;

    /// <summary>Flash the keyboard when Windows shows a notification, in <see cref="NotificationFlashColor"/>.</summary>
    [ObservableProperty] private bool _notificationFlashEnabled;

    [ObservableProperty] private byte _notificationFlashRed = 255;
    [ObservableProperty] private byte _notificationFlashGreen = 255;
    [ObservableProperty] private byte _notificationFlashBlue = 255;
    [ObservableProperty] private string _notificationFlashHexColor = "#FFFFFF";

    /// <summary>Raised when either flash is switched, so the watcher process can follow.</summary>
    public event EventHandler? FlashSettingsChanged;

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
        ColorSlot.Flash => FlashColor,
        _ => StaticColor
    };

    /// <summary>The colour a Windows notification flashes the keyboard with.</summary>
    public ColorRGB NotificationFlashColor => new(NotificationFlashRed, NotificationFlashGreen, NotificationFlashBlue);

    public Color FlashColor => Color.FromRgb(NotificationFlashRed, NotificationFlashGreen, NotificationFlashBlue);

    public SolidColorBrush FlashColorBrush => new(FlashColor);

    public bool IsFlashActive => ActiveSlot == ColorSlot.Flash;

    /// <summary>The flash colour chip is only worth showing once the notification flash is on.</summary>
    public bool ShowFlashColor => NotificationFlashEnabled;

    public SolidColorBrush ActiveColorBrush => new(ActiveColor);

    public byte ActiveRed
    {
        get => ActiveSlot switch
        {
            ColorSlot.Secondary => SecondaryRed,
            ColorSlot.Tertiary => TertiaryRed,
            ColorSlot.Primary => Red,
            ColorSlot.Flash => NotificationFlashRed,
            _ => StaticRed
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryRed = value; break;
                case ColorSlot.Tertiary: TertiaryRed = value; break;
                case ColorSlot.Primary: Red = value; break;
                case ColorSlot.Flash: NotificationFlashRed = value; break;
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
            ColorSlot.Flash => NotificationFlashGreen,
            _ => StaticGreen
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryGreen = value; break;
                case ColorSlot.Tertiary: TertiaryGreen = value; break;
                case ColorSlot.Primary: Green = value; break;
                case ColorSlot.Flash: NotificationFlashGreen = value; break;
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
            ColorSlot.Flash => NotificationFlashBlue,
            _ => StaticBlue
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryBlue = value; break;
                case ColorSlot.Tertiary: TertiaryBlue = value; break;
                case ColorSlot.Primary: Blue = value; break;
                case ColorSlot.Flash: NotificationFlashBlue = value; break;
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
            ColorSlot.Flash => NotificationFlashHexColor,
            _ => StaticHexColor
        };
        set
        {
            switch (ActiveSlot)
            {
                case ColorSlot.Secondary: SecondaryHexColor = value; break;
                case ColorSlot.Tertiary: TertiaryHexColor = value; break;
                case ColorSlot.Primary: HexColor = value; break;
                case ColorSlot.Flash: NotificationFlashHexColor = value; break;
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

        _typingFlashEnabled = _currentSettings.TypingFlashEnabled;
        _notificationFlashEnabled = _currentSettings.NotificationFlashEnabled;
        _notificationFlashRed = _currentSettings.NotificationFlashRed;
        _notificationFlashGreen = _currentSettings.NotificationFlashGreen;
        _notificationFlashBlue = _currentSettings.NotificationFlashBlue;
        _driverType = _currentSettings.DriverType;
        _brightness = Math.Clamp(_currentSettings.Brightness, 0, 100);
        _sleepTimerEnabled = _currentSettings.SleepTimerEnabled;
        _animationSpeed = _currentSettings.AnimationSpeed < 0.1 ? 1.0 : _currentSettings.AnimationSpeed;

        UpdateHexFromRgb();
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        UpdateNotificationFlashHexFromRgb();
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
        DriverDiagnostics.NoteReported += OnDriverNote;

        InitializeAsync();
    }

    private bool _loggingActive;

    /// <summary>One line describing the state restored from settings.json.</summary>
    private void LogRestoredState()
    {
        AppLog.Event(
            "mode",
            $"Restored: effect={SelectedAnimation?.Animation.Name ?? "-"}, " +
            $"driverMode={DescribeDriverType()}, zone={SelectedZone}, sleepTimer={SleepTimerEnabled}, " +
            $"brightness={Brightness:0.#}%, speed={AnimationSpeed:0.##}x, running={IsAnimationRunning}, " +
            $"primary={HexColor}, secondary={SecondaryHexColor}, tertiary={TertiaryHexColor}, static={StaticHexColor}");
    }

    private string DescribeDriverType() => DriverType switch
    {
        1 => "Insyde",
        2 => "WMI",
        _ => "Auto"
    };

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
                AppLog.Warn(
                    "Device not found after {Attempts} attempts (driver mode {Mode})",
                    attempt,
                    DescribeDriverType());
                return;
            }

            SetStatus("St_AntiLag");
            _queue = new AsyncDriverQueue(baseDriver);
            await _queue.InitializeAsync();

            IsMultiZoneCapable = baseDriver.GetType().Name.Contains("Wmi");
            AppLog.Info(
                "Driver connected: {Driver} ({Zones}) after {Attempts} attempt(s)",
                baseDriver.GetType().Name,
                IsMultiZoneCapable ? "multi-zone" : "single-zone",
                attempt + 1);

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
            AppLog.Error("Driver initialisation failed: {Message}", ex, ex.Message);
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
        // Per-change logging starts here, once the saved values are being applied: the
        // constructor writes the same properties from settings, and logging those would
        // look like a burst of user edits.
        _loggingActive = true;

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

        LogRestoredState();
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
        AppLog.Event(
            "effect",
            $"Animation started: {SelectedAnimation.Name} ({SelectedAnimation.Animation.Name}), " +
            $"brightness={Brightness:0.#}%, speed={AnimationSpeed:0.##}x, zone={SelectedZone}");
        SaveSettings();
    }

    private void StopAnimation()
    {
        _animationEngine?.Stop();
        IsAnimationRunning = false;
        SetStatus("St_Stopped");
        AppLog.Event("effect", $"Animation stopped: {SelectedAnimation?.Animation.Name ?? "-"}");

        SaveSettings();

        // Stopping an effect always returns the keyboard to the single static colour.
        _ = ApplyColorCore(announceStatus: false);
    }

    [RelayCommand]
    private void ToggleAnimation()
    {
        // An explicit switch takes the keyboard back from a flash that is still holding it.
        CancelFlashHold();

        if (IsAnimationRunning)
        {
            StopAnimation();
        }
        else
        {
            StartAnimation();
        }
    }

    /// <summary>Cycles the effect list from a global hotkey.</summary>
    public bool CycleAnimation(int direction)
    {
        if (!IsReady || _animationEngine is null || IsAsleep || Animations.Count == 0 || direction == 0)
        {
            return false;
        }

        int step = Math.Sign(direction);
        int currentIndex = SelectedAnimation is null ? -1 : Animations.IndexOf(SelectedAnimation);
        if (currentIndex < 0)
        {
            currentIndex = step < 0 ? 0 : -1;
        }

        int nextIndex = (currentIndex + step + Animations.Count) % Animations.Count;
        bool wasRunning = IsAnimationRunning;
        SelectedAnimation = Animations[nextIndex];

        // A shortcut should immediately apply the chosen effect, even from static-light mode.
        if (!wasRunning)
        {
            StartAnimation();
        }

        return true;
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
        AppLog.Event("mode", $"Weather location: {city} ({latitude:0.####}, {longitude:0.####})");
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
        AppLog.Warn("Driver: {Message}", message);

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

    /// <summary>Benign driver answers (expected status codes) go to the log once each.</summary>
    private void OnDriverNote(string message) => AppLog.Event("driver", message);

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
            AppLog.Event(
                "color",
                $"Static colour sent to the keyboard: #{applied.R:X2}{applied.G:X2}{applied.B:X2} " +
                $"(zone {zone}, brightness {Brightness:0.#}%)");
        }
    }

    [RelayCommand]
    private void SelectPrimaryColor() => ActiveSlot = ColorSlot.Primary;

    [RelayCommand]
    private void SelectSecondaryColor() => ActiveSlot = ColorSlot.Secondary;

    [RelayCommand]
    private void SelectTertiaryColor() => ActiveSlot = ColorSlot.Tertiary;

    [RelayCommand]
    private void SelectFlashColor() => ActiveSlot = ColorSlot.Flash;

    partial void OnActiveSlotChanged(ColorSlot value)
    {
        OnPropertyChanged(nameof(IsPrimaryActive));
        OnPropertyChanged(nameof(IsSecondaryActive));
        OnPropertyChanged(nameof(IsTertiaryActive));
        OnPropertyChanged(nameof(IsStaticActive));
        OnPropertyChanged(nameof(IsFlashActive));
        OnPropertyChanged(nameof(ActiveRed));
        OnPropertyChanged(nameof(ActiveGreen));
        OnPropertyChanged(nameof(ActiveBlue));
        OnPropertyChanged(nameof(ActiveHexColor));
        UpdateColorPreview();

        if (_loggingActive)
        {
            AppLog.Event("mode", $"Colour slot selected: {value}");
        }
    }

    public bool IsLightTheme => Theming.Theme.IsLight;

    [RelayCommand]
    private void ToggleTheme()
    {
        Theming.Theme.Toggle();
        OnPropertyChanged(nameof(IsLightTheme));
        _currentSettings.Theme = Theming.Theme.CurrentCode;
        AppLog.Event("mode", $"Theme: {Theming.Theme.CurrentCode}");
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

        if (_loggingActive)
        {
            AppLog.Event("effect", $"Effect selected: {value.Name} ({value.Animation.Name})");
        }

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
        LogColorSlot("primary", Red, Green, Blue);
    }

    partial void OnGreenChanged(byte value)
    {
        UpdateHexFromRgb();
        SyncAnimationColors();
        LogColorSlot("primary", Red, Green, Blue);
    }

    partial void OnBlueChanged(byte value)
    {
        UpdateHexFromRgb();
        SyncAnimationColors();
        LogColorSlot("primary", Red, Green, Blue);
    }

    partial void OnSecondaryRedChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveRed));
        LogColorSlot("secondary", SecondaryRed, SecondaryGreen, SecondaryBlue);
    }

    partial void OnSecondaryGreenChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveGreen));
        LogColorSlot("secondary", SecondaryRed, SecondaryGreen, SecondaryBlue);
    }

    partial void OnSecondaryBlueChanged(byte value)
    {
        UpdateSecondaryHexFromRgb();
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsSecondaryActive) OnPropertyChanged(nameof(ActiveBlue));
        LogColorSlot("secondary", SecondaryRed, SecondaryGreen, SecondaryBlue);
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
                LogColorSlot("primary", Red, Green, Blue);
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

        if (_loggingActive)
        {
            AppLog.Coalesce("mode", "mode.brightness", () => $"Brightness: {Brightness:0.#}%");
        }
    }

    partial void OnAnimationSpeedChanged(double value)
    {
        if (_animationEngine is not null)
        {
            _animationEngine.Speed = value;
        }

        if (_loggingActive)
        {
            AppLog.Coalesce("mode", "mode.speed", () => $"Animation speed: {AnimationSpeed:0.##}x");
        }

        SaveSettings();
    }

    partial void OnSelectedZoneChanged(Zone value)
    {
        if (_loggingActive)
        {
            AppLog.Event("mode", $"Active zone: {value}");
        }
    }

    partial void OnDriverTypeChanged(int value)
    {
        if (_loggingActive)
        {
            AppLog.Event("mode", $"Driver mode: {DescribeDriverType()} (applies after a restart)");
        }
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
        AppLog.Event("mode", $"Language: {Loc.CurrentCode}");
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

    /// <summary>
    /// Queues a colour-slot change. Slider drags change the value on every pixel, so the
    /// entry is coalesced and only the value the user settles on is written to the log.
    /// </summary>
    private void LogColorSlot(string slot, byte red, byte green, byte blue)
    {
        if (!_loggingActive)
        {
            return;
        }

        AppLog.Coalesce("color", $"color.{slot}", () => $"Colour {slot}: #{red:X2}{green:X2}{blue:X2}");
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
            LogColorSlot("secondary", SecondaryRed, SecondaryGreen, SecondaryBlue);
        }
        catch
        {
            // Ignore incomplete input.
        }
    }

    private void SyncAnimationColors()
    {
        if (SelectedAnimation?.Animation is not { } animation)
        {
            return;
        }

        // Zone mode applies to effects that are not colour-aware as well.
        var singleZone = !IsMultiZoneCapable;

        if (animation is RainbowAnimation rainbow)
        {
            rainbow.SingleZone = singleZone;
        }

        if (animation is GarlandAnimation garland)
        {
            garland.SingleZone = singleZone;
        }

        if (animation is not IColorAwareAnimation aware)
        {
            return;
        }

        aware.Primary = new ColorRGB(Red, Green, Blue);
        aware.Secondary = new ColorRGB(SecondaryRed, SecondaryGreen, SecondaryBlue);

        if (aware is ZoneGradientAnimation gradient)
        {
            gradient.SingleZone = singleZone;
            gradient.Tertiary = new ColorRGB(TertiaryRed, TertiaryGreen, TertiaryBlue);
        }

        if (aware is ColorShiftAnimation wave)
        {
            wave.SingleZone = singleZone;
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
        LogColorSlot("static", StaticRed, StaticGreen, StaticBlue);
    }

    partial void OnStaticGreenChanged(byte value)
    {
        UpdateStaticHexFromRgb();
        if (IsStaticActive) OnPropertyChanged(nameof(ActiveGreen));
        LogColorSlot("static", StaticRed, StaticGreen, StaticBlue);
    }

    partial void OnStaticBlueChanged(byte value)
    {
        UpdateStaticHexFromRgb();
        if (IsStaticActive) OnPropertyChanged(nameof(ActiveBlue));
        LogColorSlot("static", StaticRed, StaticGreen, StaticBlue);
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
            LogColorSlot("static", StaticRed, StaticGreen, StaticBlue);
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
        LogColorSlot("tertiary", TertiaryRed, TertiaryGreen, TertiaryBlue);
    }

    partial void OnTertiaryGreenChanged(byte value)
    {
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveGreen));
        LogColorSlot("tertiary", TertiaryRed, TertiaryGreen, TertiaryBlue);
    }

    partial void OnTertiaryBlueChanged(byte value)
    {
        UpdateTertiaryHexFromRgb();
        UpdateStaticHexFromRgb();
        if (IsTertiaryActive) OnPropertyChanged(nameof(ActiveBlue));
        LogColorSlot("tertiary", TertiaryRed, TertiaryGreen, TertiaryBlue);
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
            LogColorSlot("tertiary", TertiaryRed, TertiaryGreen, TertiaryBlue);
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

        if (_loggingActive)
        {
            AppLog.Event("mode", $"Sleep timer (idle backlight off): {(value ? "on" : "off")}");
        }

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

    // --- Keypress and notification flashes ------------------------------------------------

    /// <summary>
    /// Ten neon tones a keypress flash picks from. One is chosen per keypress and never the
    /// same one twice in a row, so fast typing reads as a colour ripple instead of a blink.
    /// </summary>
    private static readonly ColorRGB[] KeypressFlashColors =
    {
        new(0x00, 0xD4, 0xFF), // cyan
        new(0xFF, 0x3C, 0xC8), // magenta
        new(0x4A, 0xDE, 0x80), // green
        new(0xFF, 0x8C, 0x00), // orange
        new(0x9B, 0x5C, 0xFF), // violet
        new(0xFF, 0xE0, 0x3C), // yellow
        new(0x00, 0xFF, 0xC8), // aqua
        new(0xFF, 0x4D, 0x4D), // red
        new(0x3C, 0x7C, 0xFF), // blue
        new(0xFF, 0x9A, 0xE8)  // pink
    };

    /// <summary>Fast typing must not turn the flashes into one long glow.</summary>
    private static readonly TimeSpan FlashCooldown = TimeSpan.FromMilliseconds(90);

    /// <summary>The effect steps aside while the user types and comes back after this pause.</summary>
    private static readonly TimeSpan TypingHold = TimeSpan.FromSeconds(5);

    /// <summary>How long a notification keeps the keyboard to itself.</summary>
    private static readonly TimeSpan NotificationHold = TimeSpan.FromMilliseconds(700);

    /// <summary>The return to the effect is a glide, never a jump.</summary>
    private static readonly TimeSpan FlashFadeBack = TimeSpan.FromMilliseconds(350);

    private static readonly Random FlashRandom = new();

    private long _lastFlashTicks;
    private long _typingHoldUntilTicks;
    private long _notificationHoldUntilTicks;
    private bool _animationPausedForFlash;
    private bool _endingFlashSession;
    private int _lastPaletteIndex = -1;
    private bool _updatingFlashHex;
    private ColorRGB _lastFlashColor = ColorRGB.Black;
    private DispatcherTimer? _flashTimer;

    /// <summary>
    /// Called on the UI thread for every event the watcher sends over the pipe. The switches
    /// are re-checked here: the watcher may still be delivering a queued keypress from the
    /// moment before the user unticked the box.
    /// </summary>
    public void HandleWatcherEvent(WatcherEventKind kind)
    {
        switch (kind)
        {
            case WatcherEventKind.Keypress when TypingFlashEnabled:
                FlashForKeypress();
                break;

            case WatcherEventKind.Notification when NotificationFlashEnabled:
                FlashForNotification();
                break;
        }
    }

    /// <summary>
    /// A keypress: the flash owns the keyboard and the effect waits. The hold is extended on
    /// every key, so the animation comes back five seconds after the typing stops instead of
    /// flickering in and out between keystrokes.
    /// </summary>
    private void FlashForKeypress()
    {
        if (!CanFlash())
        {
            return;
        }

        BeginFlashSession();
        Interlocked.Exchange(ref _typingHoldUntilTicks, Environment.TickCount64 + (long)TypingHold.TotalMilliseconds);

        var now = Environment.TickCount64;

        if (now - Interlocked.Read(ref _lastFlashTicks) < FlashCooldown.TotalMilliseconds)
        {
            // The hold was extended above; the colour simply did not change this time.
            return;
        }

        Interlocked.Exchange(ref _lastFlashTicks, now);
        PaintFlash(NextKeypressColor());
    }

    /// <summary>
    /// A toast: one colour for a moment, then the effect returns on its own. A notification
    /// neither cancels typing flashes nor is cancelled by them — each one only extends the
    /// hold it owns, so the two can never cut each other short.
    /// </summary>
    private void FlashForNotification()
    {
        if (!CanFlash())
        {
            return;
        }

        BeginFlashSession();
        Interlocked.Exchange(
            ref _notificationHoldUntilTicks,
            Environment.TickCount64 + (long)NotificationHold.TotalMilliseconds);

        PaintFlash(NotificationFlashColor);
    }

    /// <summary>True when a flash may be shown now: the driver is ready and the keyboard awake.</summary>
    private bool CanFlash() => IsReady && !IsAsleep && _driver is not null;

    /// <summary>A random palette colour that is never the same as the previous one.</summary>
    private ColorRGB NextKeypressColor()
    {
        var index = FlashRandom.Next(KeypressFlashColors.Length);

        if (index == _lastPaletteIndex)
        {
            index = (index + 1) % KeypressFlashColors.Length;
        }

        _lastPaletteIndex = index;
        return KeypressFlashColors[index];
    }

    /// <summary>
    /// Freezes the effect the first time a flash arrives — otherwise its frames would land on
    /// top of the flash colour — and starts the timer that ends the hold.
    /// </summary>
    private void BeginFlashSession()
    {
        if (!_animationPausedForFlash && IsAnimationRunning && _animationEngine is not null)
        {
            _animationEngine.Pause();
            _animationPausedForFlash = true;
            AppLog.Event("flash", "Animation paused: a flash takes the keyboard");
        }

        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _flashTimer.Tick += OnFlashTick;
        }

        _flashTimer.Start();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        var holdUntil = Math.Max(
            Interlocked.Read(ref _typingHoldUntilTicks),
            Interlocked.Read(ref _notificationHoldUntilTicks));

        if (holdUntil > Environment.TickCount64)
        {
            return;
        }

        _ = EndFlashSessionAsync();
    }

    /// <summary>
    /// Gives the keyboard back: fades from the last flash colour to whatever should be shown
    /// and only then lets the effect run again, so the switch reads as a breath, not a jump.
    /// </summary>
    private async Task EndFlashSessionAsync()
    {
        if (_endingFlashSession)
        {
            return;
        }

        _endingFlashSession = true;

        try
        {
            _flashTimer?.Stop();
            Interlocked.Exchange(ref _typingHoldUntilTicks, 0);
            Interlocked.Exchange(ref _notificationHoldUntilTicks, 0);

            var resumeAnimation = _animationPausedForFlash;
            _animationPausedForFlash = false;

            if (_driver is null || IsAsleep)
            {
                // While asleep the engine stays paused; the sleep timer resumes it on wake.
                if (resumeAnimation && !IsAsleep)
                {
                    _animationEngine?.Resume();
                }

                return;
            }

            await FadeAsync(_lastFlashColor, OutputColor, (int)FlashFadeBack.TotalMilliseconds);

            if (resumeAnimation)
            {
                _animationEngine?.Resume();
                AppLog.Event("flash", "Animation resumed after the flash");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not end the flash: {Message}", ex.Message);
        }
        finally
        {
            _endingFlashSession = false;
        }
    }

    /// <summary>Drops the hold without painting anything — the user took control back.</summary>
    private void CancelFlashHold()
    {
        _flashTimer?.Stop();
        Interlocked.Exchange(ref _typingHoldUntilTicks, 0);
        Interlocked.Exchange(ref _notificationHoldUntilTicks, 0);
        _animationPausedForFlash = false;
    }

    private void PaintFlash(ColorRGB color)
    {
        var painted = color.ApplyBrightness((float)(Brightness / 100.0));
        _lastFlashColor = painted;
        _ = WriteFlashColorAsync(painted);
    }

    private async Task WriteFlashColorAsync(ColorRGB color)
    {
        try
        {
            if (_driver is not null)
            {
                await _driver.SetColorAsync(Zone.All, color);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flash could not be painted: {Message}", ex.Message);
        }
    }

    private void UpdateNotificationFlashHexFromRgb()
    {
        // The hex box mirrors the sliders; the guard keeps the two from updating each other.
        _updatingFlashHex = true;

        try
        {
            NotificationFlashHexColor = $"#{NotificationFlashRed:X2}{NotificationFlashGreen:X2}{NotificationFlashBlue:X2}";
        }
        finally
        {
            _updatingFlashHex = false;
        }

        OnPropertyChanged(nameof(NotificationFlashColor));
        OnPropertyChanged(nameof(FlashColor));
        OnPropertyChanged(nameof(FlashColorBrush));
        OnPropertyChanged(nameof(ActiveColor));
        OnPropertyChanged(nameof(ActiveColorBrush));

        if (IsFlashActive)
        {
            OnPropertyChanged(nameof(ActiveRed));
            OnPropertyChanged(nameof(ActiveGreen));
            OnPropertyChanged(nameof(ActiveBlue));
            OnPropertyChanged(nameof(ActiveHexColor));
        }
    }

    partial void OnNotificationFlashRedChanged(byte value) => UpdateNotificationFlashHexFromRgb();

    partial void OnNotificationFlashGreenChanged(byte value) => UpdateNotificationFlashHexFromRgb();

    partial void OnNotificationFlashBlueChanged(byte value) => UpdateNotificationFlashHexFromRgb();

    /// <summary>The hex box stays an alternative input for the sliders, as in every other slot.</summary>
    partial void OnNotificationFlashHexColorChanged(string value)
    {
        if (_updatingFlashHex)
        {
            return;
        }

        if (value is not { Length: 7 } || value[0] != '#')
        {
            return;
        }

        var span = value.AsSpan(1);

        if (!byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return;
        }

        NotificationFlashRed = red;
        NotificationFlashGreen = green;
        NotificationFlashBlue = blue;
    }

    partial void OnTypingFlashEnabledChanged(bool value)
    {
        _currentSettings.TypingFlashEnabled = value;
        SaveSettings();
        FlashSettingsChanged?.Invoke(this, EventArgs.Empty);

        if (_loggingActive)
        {
            AppLog.Event("flash", $"Typing flash: {(value ? "on" : "off")}");
        }
    }

    partial void OnNotificationFlashEnabledChanged(bool value)
    {
        _currentSettings.NotificationFlashEnabled = value;
        SaveSettings();
        OnPropertyChanged(nameof(ShowFlashColor));
        FlashSettingsChanged?.Invoke(this, EventArgs.Empty);

        if (_loggingActive)
        {
            AppLog.Event("flash", $"Notification flash: {(value ? "on" : "off")}");
        }

        if (!value && ActiveSlot == ColorSlot.Flash)
        {
            ActiveSlot = ColorSlot.Primary;
        }
    }

    private async void Sleep()
    {
        if (_isSleeping || _sleepTransition || _driver is null || !IsReady)
        {
            return;
        }

        // The sleep timer owns the keyboard now; a pending flash hold must not fight it.
        CancelFlashHold();

        _sleepTransition = true;
        _isSleeping = true;
        IsAsleep = true;
        SetStatus("St_Sleeping");
        AppLog.Event("mode", $"Idle sleep: backlight off ({SleepTimeout.TotalMinutes:0} min without input)");

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
        AppLog.Event("mode", "Input detected: backlight restored");

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
        _currentSettings.TypingFlashEnabled = TypingFlashEnabled;
        _currentSettings.NotificationFlashEnabled = NotificationFlashEnabled;
        _currentSettings.NotificationFlashRed = NotificationFlashRed;
        _currentSettings.NotificationFlashGreen = NotificationFlashGreen;
        _currentSettings.NotificationFlashBlue = NotificationFlashBlue;
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
