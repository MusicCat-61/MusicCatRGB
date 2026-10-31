using System;
using System.Windows;
using Application = System.Windows.Application;

namespace MusicCat.CLVKM.UI.Theming;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Swaps the Frost palette dictionary at runtime. All brushes in XAML are referenced
/// with DynamicResource, so the whole window re-colours without a restart.
/// </summary>
public static class Theme
{
    private static readonly ResourceDictionary DarkDictionary = Load("Theme.Dark.xaml");
    private static readonly ResourceDictionary LightDictionary = Load("Theme.Light.xaml");

    private static ResourceDictionary? _active;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static bool IsLight => Current == AppTheme.Light;

    public static event Action? Changed;

    public static AppTheme Parse(string? code) =>
        string.Equals(code, "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    public static string CurrentCode => Current == AppTheme.Light ? "light" : "dark";

    private static ResourceDictionary Load(string fileName)
    {
        var assembly = typeof(Theme).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Theming/{fileName}",
                UriKind.Absolute)
        };
    }

    public static void Apply(AppTheme theme, bool notify = true)
    {
        var app = Application.Current;
        if (app is not null)
        {
            var dictionary = theme == AppTheme.Light ? LightDictionary : DarkDictionary;
            if (!ReferenceEquals(_active, dictionary))
            {
                var merged = app.Resources.MergedDictionaries;
                if (_active is not null)
                {
                    merged.Remove(_active);
                }

                merged.Insert(0, dictionary);
                _active = dictionary;
            }
        }

        Current = theme;

        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public static void Toggle()
    {
        Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
    }
}
