using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using MusicCat.CLVKM.UI.Localization;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MusicCat.CLVKM.UI;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/gutsv-project/Clevo-Monster-RGB-Controller";
    private const string MyRepositoryUrl = "https://github.com/MusicCat-61/KeyGlowMaster";
    private const string LicenseUrl = "https://opensource.org/license/mit";

    public AboutWindow()
    {
        InitializeComponent();
        Title = Loc.T("About_Title");
        VersionText.Text = Loc.F("About_Version", AppVersion);
    }

    /// <summary>Assembly informational version without the "+commit" build metadata.</summary>
    private static string AppVersion
    {
        get
        {
            var assembly = typeof(AboutWindow).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

    private void MyRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(MyRepositoryUrl);

    private void License_Click(object sender, RoutedEventArgs e) => OpenUrl(LicenseUrl);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening the browser is best-effort.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }

        base.OnKeyDown(e);
    }
}
