using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicCat.CLVKM.Core.Animations;
using MusicCat.CLVKM.Core.Models;
using MusicCat.CLVKM.UI.Localization;

namespace MusicCat.CLVKM.UI.ViewModels;

/// <summary>Which colour slot the sliders and hex box are editing right now.</summary>
public enum ColorSlot
{
    /// <summary>The applied colour shown when no animation is running.</summary>
    Static,
    Primary,
    Secondary,
    Tertiary,

    /// <summary>The colour a Windows notification flashes the keyboard with.</summary>
    Flash
}

/// <summary>Localizable wrapper around an animation engine implementation.</summary>
public sealed class AnimationItem : ObservableObject
{
    private static readonly Dictionary<string, (string Name, string Description)> ResourceKeys = new(StringComparer.Ordinal)
    {
        ["Breathing"] = ("Anim_Breathing", "Desc_Breathing"),
        ["Neon Pulse"] = ("Anim_NeonPulse", "Desc_NeonPulse"),
        ["Spectrum Cycle"] = ("Anim_SpectrumCycle", "Desc_SpectrumCycle"),
        ["Rainbow"] = ("Anim_Rainbow", "Desc_Rainbow"),
        ["Strobe Blink"] = ("Anim_StrobeBlink", "Desc_StrobeBlink"),
        ["Wave"] = ("Anim_Wave", "Desc_Wave"),
        ["Audio Sync (Spectrum)"] = ("Anim_AudioSync", "Desc_AudioSync"),
        ["System Monitor (Heatmap)"] = ("Anim_SystemMonitor", "Desc_SystemMonitor"),
        ["Ambient (Screen Sync)"] = ("Anim_Ambient", "Desc_Ambient"),
        ["Day / Night"] = ("Anim_DayNight", "Desc_DayNight"),
        ["Zone Gradient"] = ("Anim_ZoneGradient", "Desc_ZoneGradient"),
        ["Random Neon"] = ("Anim_RandomNeon", "Desc_RandomNeon"),
        ["Two-Color Fade"] = ("Anim_TwoColorFade", "Desc_TwoColorFade"),
        ["Weather Sync"] = ("Anim_WeatherSync", "Desc_WeatherSync"),
        ["Dual Breathe"] = ("Anim_DualBreathe", "Desc_DualBreathe"),
        ["Soviet Garland"] = ("Anim_Garland", "Desc_Garland")
    };

    public AnimationItem(IAnimation animation) => Animation = animation;

    public IAnimation Animation { get; }

    public string Name =>
        ResourceKeys.TryGetValue(Animation.Name, out var keys) ? Loc.T(keys.Name) : Animation.Name;

    public string Description =>
        ResourceKeys.TryGetValue(Animation.Name, out var keys) ? Loc.T(keys.Description) : string.Empty;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
    }
}

/// <summary>Localizable keyboard zone entry for the zone picker.</summary>
public sealed class ZoneOption : ObservableObject
{
    public ZoneOption(Zone zone, string resourceKey)
    {
        Zone = zone;
        ResourceKey = resourceKey;
    }

    public Zone Zone { get; }

    public string ResourceKey { get; }

    public string Name => Loc.T(ResourceKey);

    public void Refresh() => OnPropertyChanged(nameof(Name));
}
