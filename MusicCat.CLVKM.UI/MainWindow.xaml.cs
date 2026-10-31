using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MusicCat.CLVKM.UI.Interop;
using MusicCat.CLVKM.UI.Localization;

using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Slider = System.Windows.Controls.Slider;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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

    /// <summary>Arrow keys walk the city suggestions, Enter takes the highlighted one.</summary>
    private void CityBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when vm.CitySuggestions.Count > 0:
                vm.MoveCitySuggestionSelection(1);
                e.Handled = true;
                break;

            case Key.Up when vm.CitySuggestions.Count > 0:
                vm.MoveCitySuggestionSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                // The city is chosen from the list: Enter takes the highlighted suggestion.
                if (vm.IsCitySuggestionsOpen && vm.SelectedCitySuggestion is not null)
                {
                    vm.SelectCitySuggestionCommand.Execute(vm.SelectedCitySuggestion);
                }

                e.Handled = true;
                break;

            case Key.Escape:
                vm.CloseCitySuggestions();
                e.Handled = true;
                break;
        }
    }

    private void CitySuggestions_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && vm.SelectedCitySuggestion is not null)
        {
            vm.SelectCitySuggestionCommand.Execute(vm.SelectedCitySuggestion);
        }
    }

    private void CityBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm)
        {
            return;
        }

        // A click on a suggestion moves focus into the popup; only close when focus
        // really left both the box and the list (Tab, a click on another control, …).
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!CityPopup.IsOpen || CityPopup.IsKeyboardFocusWithin || CityBox.IsKeyboardFocusWithin)
            {
                return;
            }

            vm.CloseCitySuggestions();
        }));
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
