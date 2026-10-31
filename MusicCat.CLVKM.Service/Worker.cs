using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using MusicCat.CLVKM.Core.Animations;
using MusicCat.CLVKM.Core.Interfaces;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.Core.Settings;
using MusicCat.CLVKM.Drivers;
using MusicCat.CLVKM.Drivers.Wmi;

namespace MusicCat.CLVKM.Service;

[SupportedOSPlatform("windows")]
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private IKeyboardController? _driver;
    private AnimationEngine? _engine;
    private IAnimation? _animation;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("KeyGlowMaster Service starting...");

        void OnDriverError(string message) => _logger.LogWarning("Driver error: {Message}", message);
        DriverDiagnostics.ErrorReported += OnDriverError;

        var settings = SettingsManager.Load();

        try
        {
            // DeviceManager initialises the driver it returns (WMI first, then Insyde).
            _driver = await DeviceManager.CreateDriverAsync(settings.DriverType);
            if (_driver is null)
            {
                _logger.LogError("No supported device found.");
                return;
            }

            await RestoreLightingAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in KeyGlowMaster Service");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }

        StopAnimation();
        DriverDiagnostics.ErrorReported -= OnDriverError;

        if (_driver is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async Task RestoreLightingAsync(AppSettings settings)
    {
        var brightness = (float)Math.Clamp(settings.Brightness / 100.0, 0.0, 1.0);
        var animation = ResolveAnimation(settings.LastAnimation);

        if (animation is not null)
        {
            ApplyAnimationColors(animation, settings);
            ApplyAnimationZoneMode(animation);

            _engine = new AnimationEngine(_driver!)
            {
                Speed = settings.AnimationSpeed <= 0.1 ? 1.0 : settings.AnimationSpeed,
                Brightness = brightness
            };

            _animation = animation;
            _engine.Start(animation);

            _logger.LogInformation("Restored animation: {Animation}", animation.Name);
            return;
        }

        // No animation (or one the service cannot run): fall back to the applied colour.
        var color = settings.HasStaticColor
            ? new ColorRGB(settings.StaticRed, settings.StaticGreen, settings.StaticBlue)
            : new ColorRGB(settings.Red, settings.Green, settings.Blue);

        color = color.ApplyBrightness(brightness);

        _logger.LogInformation("Restored static colour: R{Red} G{Green} B{Blue}", color.R, color.G, color.B);
        await _driver!.SetColorAsync(Zone.All, color);
    }

    /// <summary>
    /// Resolves the effect saved by the UI through the shared catalog, so effects added
    /// later (Garland, Weather, …) are restored without touching this service.
    /// </summary>
    private IAnimation? ResolveAnimation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (AnimationCatalog.RequiresInteractiveSession(name))
        {
            _logger.LogWarning(
                "Animation '{Animation}' needs an interactive desktop session; restoring the static colour instead.",
                name);
            return null;
        }

        var animation = AnimationCatalog.CreateByName(name);
        if (animation is null)
        {
            _logger.LogWarning("Unknown animation '{Animation}'; restoring the static colour instead.", name);
        }

        return animation;
    }

    private static void ApplyAnimationColors(IAnimation animation, AppSettings settings)
    {
        if (animation is not IColorAwareAnimation aware)
        {
            return;
        }

        aware.Primary = new ColorRGB(settings.Red, settings.Green, settings.Blue);
        aware.Secondary = new ColorRGB(settings.SecondaryRed, settings.SecondaryGreen, settings.SecondaryBlue);

        if (animation is ZoneGradientAnimation gradient)
        {
            gradient.Tertiary = new ColorRGB(settings.TertiaryRed, settings.TertiaryGreen, settings.TertiaryBlue);
        }
    }

    private void ApplyAnimationZoneMode(IAnimation animation)
    {
        var singleZone = _driver is not WmiDriver;

        switch (animation)
        {
            case ColorShiftAnimation wave:
                wave.SingleZone = singleZone;
                break;
            case GarlandAnimation garland:
                garland.SingleZone = singleZone;
                break;
            case ZoneGradientAnimation gradient:
                gradient.SingleZone = singleZone;
                break;
        }
    }

    private void StopAnimation()
    {
        _engine?.Stop();
        _engine = null;

        if (_animation is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _animation = null;
    }
}
