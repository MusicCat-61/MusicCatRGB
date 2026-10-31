using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using MusicCat.CLVKM.UI.Localization;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MusicCat.CLVKM.UI;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/gutsv-project/Clevo-Monster-RGB-Controller";
    private const string LicenseUrl = "https://opensource.org/license/mit";

    public AboutWindow()
    {
        InitializeComponent();
        Title = Loc.T("About_Title");
    }

    private void Link_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

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
