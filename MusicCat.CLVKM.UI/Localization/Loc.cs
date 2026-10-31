using System;
using System.Windows;

using Application = System.Windows.Application;

namespace MusicCat.CLVKM.UI.Localization;

public enum AppLanguage
{
    En,
    Ru
}

/// <summary>
/// Lightweight runtime localization: swaps a merged ResourceDictionary with string
/// resources, so every <c>{DynamicResource L_...}</c> in XAML updates instantly.
/// </summary>
public static class Loc
{
    private static readonly ResourceDictionary EnDictionary = Load("Strings.en.xaml");

    private static readonly ResourceDictionary RuDictionary = Load("Strings.ru.xaml");

    private static ResourceDictionary? _active;

    public static AppLanguage Current { get; private set; } = AppLanguage.En;

    public static string CurrentCode => Current == AppLanguage.Ru ? "ru" : "en";

    /// <summary>Raised after the active dictionary changed.</summary>
    public static event Action? LanguageChanged;

    public static AppLanguage Parse(string? code) =>
        string.Equals(code, "ru", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Ru : AppLanguage.En;

    private static ResourceDictionary Load(string fileName)
    {
        var assembly = typeof(Loc).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Localization/{fileName}",
                UriKind.Absolute)
        };
    }

    /// <summary>
    /// Language used when the user did not pick one yet. The Windows display language
    /// wins; a Russian system/format locale also selects Russian, because on machines
    /// with mixed settings the UI language alone is not a reliable hint.
    /// </summary>
    public static AppLanguage DetectSystemLanguage()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        if (IsRussian(culture))
        {
            return AppLanguage.Ru;
        }

        var system = System.Globalization.CultureInfo.InstalledUICulture;
        if (IsRussian(system) || IsRussian(System.Globalization.CultureInfo.CurrentCulture))
        {
            return AppLanguage.Ru;
        }

        return AppLanguage.En;
    }

    private static bool IsRussian(System.Globalization.CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

    public static void Initialize(string? savedCode)
    {
        var language = string.IsNullOrWhiteSpace(savedCode) ? DetectSystemLanguage() : Parse(savedCode);
        SetLanguage(language, notify: false);
    }

    public static void SetLanguage(AppLanguage language, bool notify = true)
    {
        var app = Application.Current;
        if (app is null)
        {
            Current = language;
            return;
        }

        var dictionary = language == AppLanguage.Ru ? RuDictionary : EnDictionary;
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

        Current = language;

        if (notify)
        {
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Localized string for a resource key (the key itself when missing).</summary>
    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}
