using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MusicCat.CLVKM.Core.Updates;
using MusicCat.CLVKM.UI.Localization;
using MusicCat.CLVKM.UI.Logging;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MusicCat.CLVKM.UI;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/gutsv-project/Clevo-Monster-RGB-Controller";
    private const string MyRepositoryUrl = "https://github.com/MusicCat-61/MusicCatRGB";
    private const string DonateUrl = "https://dalink.to/musiccat61";
    private const string TelegramUrl = "https://t.me/music_cat1";
    private const string LicenseUrl = "https://opensource.org/license/mit";

    private UpdateCheckResult? _result;

    /// <summary>Path of the downloaded build, once it is on disk and ready to be applied.</summary>
    private string? _stagedFile;

    private bool _busy;

    public AboutWindow()
    {
        InitializeComponent();
        Title = Loc.T("About_Title");
        VersionText.Text = Loc.F("About_Version", AppVersion);

        // A silent check runs at startup, so the dialog usually opens already knowing
        // whether an update exists — no need to hit the network again.
        if (UpdateService.LastResult is { } known)
        {
            ShowResult(known);
        }
        else
        {
            ShowIdle();
        }
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
                var text = plus >= 0 ? informational[..plus] : informational;

                // "1.0.6.0" should read "1.0.6", but "1.0.6.1" must keep its fourth digit.
                if (ReleaseVersion.TryParse(text, out var parsed))
                {
                    return parsed.Display;
                }

                return text;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    // ------------------------------------------------------------------ state

    private void ShowIdle()
    {
        UpdateStatusText.Text = Loc.F("About_Version", AppVersion);
        SetButtons(check: true, download: false, finish: false, release: false);
        SetProgress(null);
    }

    private void ShowResult(UpdateCheckResult result)
    {
        _result = result;

        switch (result.Status)
        {
            case UpdateStatus.UpToDate:
                UpdateStatusText.Text = Loc.T("Upd_UpToDate");
                SetButtons(check: true, download: false, finish: false, release: false);
                SetProgress(null);
                break;

            case UpdateStatus.UpdateAvailable:
                UpdateStatusText.Text = Loc.F("Upd_Available", result.Latest!.Display);
                UpdateDownloadButton.Content = Loc.F("Upd_Download", result.Latest.Display);
                SetButtons(check: true, download: true, finish: false, release: false);
                SetProgress(null);
                break;

            case UpdateStatus.NoDownload:
                UpdateStatusText.Text = Loc.F("Upd_NoDownload", result.Latest?.Display ?? "?");
                SetButtons(check: true, download: false, finish: false, release: result.ReleaseUrl is not null);
                SetProgress(null);
                break;

            case UpdateStatus.Failed:
                UpdateStatusText.Text = Loc.F("Upd_Failed", result.Error ?? "?");
                SetButtons(check: true, download: false, finish: false, release: false);
                SetProgress(null);
                break;

            default:
                ShowIdle();
                break;
        }
    }

    private void SetButtons(bool check, bool download, bool finish, bool release)
    {
        UpdateCheckButton.Visibility = check ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadButton.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
        UpdateFinishButton.Visibility = finish ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleaseButton.Visibility = release ? Visibility.Visible : Visibility.Collapsed;

        UpdateCheckButton.IsEnabled = !_busy;
        UpdateDownloadButton.IsEnabled = !_busy;
        UpdateFinishButton.IsEnabled = !_busy;
    }

    /// <summary>Pass a fraction 0..1 to show the bar, or null to hide it.</summary>
    private void SetProgress(double? fraction)
    {
        if (fraction is not { } value)
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateProgressPanel.Visibility = Visibility.Visible;

        // The track has no explicit width, so size the fill from its parent.
        var track = UpdateProgressPanel.ActualWidth;
        UpdateProgressBar.Width = Math.Max(0, track * Math.Clamp(value, 0, 1));
    }

    // ---------------------------------------------------------------- actions

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateStatusText.Text = Loc.T("Upd_Checking");
        UpdateCheckButton.IsEnabled = false;
        UpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateFinishButton.Visibility = Visibility.Collapsed;
        UpdateReleaseButton.Visibility = Visibility.Collapsed;
        AppLog.Event("update", "Manual update check requested");

        try
        {
            var result = await UpdateService.CheckAsync();
            AppLog.Event("update", Describe(result));
            _busy = false;
            ShowResult(result);
        }
        finally
        {
            _busy = false;
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _result is not { } result || !result.HasUpdate)
        {
            return;
        }

        var folder = UpdateService.ExecutableDirectory;

        if (!UpdateService.CanWriteToInstallFolder())
        {
            UpdateStatusText.Text = Loc.F("Upd_NotWritable", folder);
            AppLog.EventWarning("update", $"Install folder is not writable: {folder}");
            return;
        }

        _busy = true;
        UpdateCheckButton.IsEnabled = false;
        UpdateDownloadButton.IsEnabled = false;
        SetProgress(0);

        var progress = new Progress<double>(fraction =>
        {
            SetProgress(fraction);
            UpdateDownloadButton.Content = Loc.F("Upd_Downloading", (int)Math.Round(fraction * 100));
        });

        try
        {
            AppLog.Event("update", $"Downloading {result.Latest!.Display} into {folder}");

            _stagedFile = await UpdateService.DownloadAsync(result, folder, progress);

            UpdateStatusText.Text = Loc.F("Upd_Ready", UpdateService.StagedFileName);
            SetProgress(1);
            SetButtons(check: false, download: false, finish: true, release: false);
            AppLog.Event("update", $"Update {result.Latest.Display} ready: {_stagedFile}");
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = Loc.F("Upd_DownloadFailed", ex.Message);
            SetProgress(null);
            SetButtons(check: true, download: true, finish: false, release: false);
            AppLog.EventError("update", $"Download of {result.Latest!.Display} failed", ex);
        }
        finally
        {
            _busy = false;
            UpdateCheckButton.IsEnabled = true;
            UpdateDownloadButton.IsEnabled = true;
        }
    }

    private void UpdateFinish_Click(object sender, RoutedEventArgs e)
    {
        if (_stagedFile is null)
        {
            return;
        }

        var executable = UpdateService.ExecutablePath;

        if (!UpdateService.TryApplyOnExit(_stagedFile, executable, out var error))
        {
            UpdateStatusText.Text = Loc.F("Upd_RestartFailed", error);
            AppLog.EventError("update", $"Could not start the replacement script: {error}");
            return;
        }

        AppLog.Event("update", $"Restarting to apply {Path.GetFileName(_stagedFile)} over {executable}");

        // Flush the log before the process disappears; the batch swaps the file as soon
        // as this PID is gone and then starts the new build.
        App.ShutdownForUpdate();
    }

    private void UpdateRelease_Click(object sender, RoutedEventArgs e)
    {
        var url = _result?.ReleaseUrl ?? $"https://github.com/{UpdateService.Repository}/releases/latest";
        OpenUrl(url);
    }

    /// <summary>One-line description of a check result for the log.</summary>
    private static string Describe(UpdateCheckResult result) => result.Status switch
    {
        UpdateStatus.UpToDate => $"Up to date ({UpdateService.CurrentVersion.Display})",
        UpdateStatus.UpdateAvailable => $"Update available: {result.Latest!.Display} ({result.AssetName}, {result.AssetSize / 1024 / 1024} MB)",
        UpdateStatus.NoDownload => $"Release {result.Latest?.Display} has no executable asset",
        UpdateStatus.Failed => $"Check failed: {result.Error}",
        _ => "Unknown update state"
    };

    private void Link_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

    private void MyRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(MyRepositoryUrl);

    private void Donate_Click(object sender, RoutedEventArgs e) => OpenUrl(DonateUrl);

    private void Telegram_Click(object sender, RoutedEventArgs e) => OpenUrl(TelegramUrl);

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

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // Keep the progress fill correct if the dialog is laid out after a download has
        // already started.
        if (UpdateProgressPanel.Visibility == Visibility.Visible)
        {
            var track = Math.Max(1, UpdateProgressPanel.ActualWidth);
            SetProgress(UpdateProgressBar.Width / track);
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
