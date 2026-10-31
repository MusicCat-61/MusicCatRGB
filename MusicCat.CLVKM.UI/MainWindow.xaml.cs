using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MusicCat.CLVKM.UI.Interop;
using MusicCat.CLVKM.UI.Localization;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace MusicCat.CLVKM.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(Branding.IconUri);
        }
        catch
        {
            // Icon is cosmetic only.
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        WindowBackdrop.TryApplyMica(this, !Theming.Theme.IsLight);

        // Keep the native Mica tint in sync when the user flips the theme.
        Theming.Theme.Changed += OnThemeChanged;
    }

    private void OnThemeChanged() => WindowBackdrop.SetDarkMode(this, !Theming.Theme.IsLight);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase || source is Slider || source is TextBox)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void HexBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // The hex box edits the applied (static) colour, not an animation colour slot.
        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.ActiveSlot = ViewModels.ColorSlot.Static;
        }
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void CleanClick(object sender, RoutedEventArgs e)
    {
        bool confirm = ModernMessageBox.Show(Loc.T("Reset_Message"), Loc.T("Reset_Title"));
        if (!confirm)
        {
            return;
        }

        App.RemoveDataAndStartup();

        if (DataContext is ViewModels.MainViewModel vm)
        {
            vm.ResetState();
        }
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsExplicitExit)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                await vm.ShutdownCommand.ExecuteAsync(null);
            }

            base.OnClosing(e);
        }
        else
        {
            // X button minimizes to tray.
            e.Cancel = true;
            Hide();
        }
    }
}
