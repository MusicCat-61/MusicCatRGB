using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicCat.CLVKM.Core.Ipc;
using MusicCat.CLVKM.Core.Updates;
using MusicCat.CLVKM.Core.Settings;
using MusicCat.CLVKM.UI.Localization;
using MusicCat.CLVKM.UI.Watcher;

namespace MusicCat.CLVKM.UI;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _menuOpen;
    private System.Windows.Forms.ToolStripMenuItem? _menuExit;
    private EffectToastWindow? _effectToast;

    /// <summary>How long a finished check is trusted before the app may ask GitHub again.</summary>
    private const int CheckIntervalMinutes = 30;

    /// <summary>A failed check (offline, rate limit) is retried much sooner.</summary>
    private const int FailedCheckRetryMinutes = 5;

    /// <summary>How often a long-running tray instance looks for a new release.</summary>
    private const int PeriodicCheckMinutes = 15;

    /// <summary>Set once the update shutdown started, so it cannot run twice.</summary>
    private static int _updateShutdownStarted;

    private readonly object _checkSync = new();
    private bool _checkRunning;
    private bool _startupCheckSettling = true;
    private string? _announcedVersion;
    private DispatcherTimer? _checkTimer;

    /// <summary>Owns the headless event watcher process and the pipe to it.</summary>
    private WatcherClient? _watcher;

    private string _trayKey = "Tray_Ready";
    private object?[] _trayArgs = Array.Empty<object?>();

    public static bool IsExplicitExit { get; set; }

    private static Mutex? _mutex;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is shutting down / logging off.
        IsExplicitExit = true;
        _notifyIcon?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MusicCat.CLVKM.UI.MainWindow mainWindow)
        {
            mainWindow.UnregisterGlobalHotKeys();
        }

        _effectToast?.Close();
        _effectToast = null;
        _watcher?.Dispose();
        _watcher = null;
        _notifyIcon?.Dispose();
        _mutex?.Dispose();
        Logging.AppLog.Shutdown();
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // The headless watcher shares this executable. It never creates a window and never
        // takes the single-instance mutex, so it can run beside the app that started it.
        if (e.Args.Contains(WatcherHost.Switch, StringComparer.OrdinalIgnoreCase))
        {
            Environment.Exit(WatcherHost.Run());
            return;
        }

        Logging.AppLog.Initialize();

        // Ensure the working directory is the app folder (DllImport needs InsydeDCHU.dll).
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        var startupSettings = SettingsManager.Load();
        Loc.Initialize(startupSettings.Language);
        Theming.Theme.Apply(Theming.Theme.Parse(startupSettings.Theme), notify: false);
        Loc.LanguageChanged += OnLanguageChanged;

        Logging.AppLog.Info(
            "Startup: tray={Tray}, language={Language}, theme={Theme}, driverMode={Driver}",
            e.Args.Contains("--tray"),
            Loc.CurrentCode,
            Theming.Theme.CurrentCode,
            startupSettings.DriverType switch { 1 => "Insyde", 2 => "WMI", _ => "Auto" });

        const string appName = "MusicCat.CLVKM_SingleInstance_Mutex";
        bool createdNew;
        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            if (e.Args.Length == 0 || e.Args[0] != "--tray")
            {
                System.Windows.MessageBox.Show(Loc.T("Msg_AlreadyRunning"), "MusicCatRGB");
            }

            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SetupTrayIcon();
        SetTrayText("Tray_Starting");

        bool startInTray = e.Args.Length > 0 && e.Args[0] == "--tray";

        // Keeps UI feedback and lets the driver wait for Control Center to come up.
        await WaitForControlCenter(startInTray);

        if (IsExplicitExit)
        {
            return;
        }

        // The pre-1.0.6 task points at the old executable name; it is dead weight now.
        Core.Platform.StartupTask.TryDelete(Core.AppPaths.LegacyStartupTaskName);

        if (startInTray && startupSettings.IsStartupEnabled)
        {
            // Started by the logon task: repoint it at this executable in case it was renamed.
            SyncStartupTask();
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        // The app may stay hidden in the tray, so create its HWND before registering global hotkeys.
        _ = new WindowInteropHelper(mainWindow).EnsureHandle();
        mainWindow.EnsureGlobalHotKeysRegistered();

        if (startInTray)
        {
            // Created (so the view model starts the saved lighting) but kept hidden.
            SetTrayText("Tray_Running");
        }
        else
        {
            mainWindow.Show();
            SetTrayText("Tray_Ready");
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // Not urgent: let the window and the lighting settle, then look for a new release.
        // A build that starts at logon sits in the tray for days, so the same check also
        // runs whenever the window is opened again and every so often while it stays hidden.
        ReportLastUpdateApply();
        StartPeriodicUpdateChecks();
        StartFlashWatcher();
        _ = CheckForUpdatesAfterStartupAsync();

        // The first run asks about the machine and the logon task; after that the window simply
        // shows whether the task is still there.
        await ShowStartupTaskStateAsync();

        if (!startInTray)
        {
            await RunFirstRunAsync();
        }
    }

    /// <summary>Mirrors the real task state into the window; cheap, so it runs on every show.</summary>
    private static async Task ShowStartupTaskStateAsync()
    {
        if (Current?.MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
        {
            await viewModel.RefreshStartupTaskAsync();
        }
    }

    /// <summary>
    /// Runs once: is this a machine the app can drive, and does the user want the logon task?
    ///
    /// The compatibility answers come first, because offering an autostart on a laptop of another
    /// make would promise a feature that cannot work. The flag that remembers "already asked"
    /// lives in the settings, so declining is remembered too — the old code asked again on every
    /// launch.
    /// </summary>
    private async Task RunFirstRunAsync()
    {
        var settings = SettingsManager.Load();

        if (settings.FirstRunCompleted)
        {
            return;
        }

        SetTrayText("Tray_Checking");

        Core.Platform.CompatibilityReport report;

        try
        {
            report = await Task.Run(() => Core.Platform.CompatibilityCheck.Run());
        }
        catch
        {
            report = Core.Platform.CompatibilityReport.Unknown;
        }

        SetTrayText("Tray_Ready");

        Logging.AppLog.Event(
            "compat",
            $"First run on «{report.BoardDisplay}»: clevo={report.BoardLooksClevo}, driver={report.DriverLoadable}, " +
            $"controlCenter={report.ControlCenterRunning}/{report.ControlCenterInstalled}, compatible={report.IsCompatible}");

        if (report.IsClearlyIncompatible)
        {
            ModernMessageBox.ShowInfo(
                Loc.F("Msg_CompatFailed", report.BoardDisplay),
                Loc.T("Msg_CompatTitle"));

            CompleteFirstRun(settings, startupEnabled: false);
            return;
        }

        var state = await Task.Run(
            () => Core.Platform.StartupTask.Query(Core.AppPaths.StartupTaskName, Environment.ProcessPath, out _));

        if (state == Core.Platform.StartupTaskState.Registered)
        {
            CompleteFirstRun(settings, startupEnabled: true);
            return;
        }

        // The task is gone even though the user agreed to it once, or it was never set up.
        var question = settings.IsStartupEnabled ? Loc.T("Msg_StartupRepair") : Loc.T("Msg_ServiceMode");

        if (!ModernMessageBox.Show(question, Loc.T("Msg_ServiceModeTitle")))
        {
            CompleteFirstRun(settings, startupEnabled: false);
            return;
        }

        var created = await Task.Run(
            () => Core.Platform.StartupTask.TryRegister(
                Core.AppPaths.StartupTaskName,
                Environment.ProcessPath ?? string.Empty,
                out var error)
                ? (string.Empty)
                : error);

        if (created.Length > 0)
        {
            Logging.AppLog.EventWarning("autostart", $"Logon task could not be created: {created}");
            ModernMessageBox.ShowInfo(Loc.T("Msg_StartupFailed"), Loc.T("Msg_ServiceModeTitle"));
        }
        else
        {
            Logging.AppLog.Event("autostart", "Logon task created on the first run");
        }

        CompleteFirstRun(settings, startupEnabled: created.Length == 0);
        await ShowStartupTaskStateAsync();
    }

    private static void CompleteFirstRun(AppSettings settings, bool startupEnabled)
    {
        settings.FirstRunCompleted = true;
        settings.IsStartupEnabled = startupEnabled;
        SettingsManager.Save(settings);
    }

    /// <summary>
    /// Brings up the event watcher and keeps it in step with the two flash switches. The
    /// watcher is a separate process because a keyboard hook that stops answering stalls
    /// input on the whole desktop; it talks to this window over an owner-only named pipe.
    /// </summary>
    private void StartFlashWatcher()
    {
        if (MainWindow?.DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        _watcher = new WatcherClient();
        _watcher.EventReceived += OnWatcherEvent;
        viewModel.FlashSettingsChanged += (_, _) => SyncFlashWatcher(viewModel);

        SyncFlashWatcher(viewModel);
    }

    private void SyncFlashWatcher(ViewModels.MainViewModel viewModel)
    {
        var wanted = viewModel.TypingFlashEnabled || viewModel.NotificationFlashEnabled;

        if (wanted)
        {
            _watcher?.Start();
        }
        else
        {
            _watcher?.Stop();
        }

        Logging.AppLog.Event(
            "watcher",
            wanted
                ? $"Event watcher on (typing={viewModel.TypingFlashEnabled}, notifications={viewModel.NotificationFlashEnabled})"
                : "Event watcher off: both flashes are disabled");
    }

    /// <summary>Events arrive on a worker thread; the flashes belong to the UI thread.</summary>
    private void OnWatcherEvent(WatcherEvent message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.HandleWatcherEvent(message.Kind);
            }
        }));
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(true);
            await CheckForUpdatesAsync("startup", force: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logging.AppLog.EventWarning("update", $"Startup update check failed: {ex.Message}");
        }
        finally
        {
            _startupCheckSettling = false;
        }
    }

    /// <summary>
    /// The window becoming visible is the best moment to mention a new release: the user is
    /// looking at the app anyway. Throttled by <see cref="UpdateService.IsCheckDue"/>, so
    /// opening the window over and over cannot hammer the GitHub API.
    /// </summary>
    internal void OnMainWindowShown()
    {
        // The task may have been removed (or repaired) while the window was hidden, so the
        // status line is re-read every time it comes back.
        _ = ShowStartupTaskStateAsync();

        // The window is shown while the startup check is still waiting for the app to
        // settle; that check owns this moment, so this one would only duplicate it.
        if (_startupCheckSettling)
        {
            return;
        }

        _ = CheckForUpdatesAsync("window opened", force: false);
    }

    /// <summary>Covers the instance that never shows a window for hours on end.</summary>
    private void StartPeriodicUpdateChecks()
    {
        _checkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(PeriodicCheckMinutes)
        };

        _checkTimer.Tick += (_, _) => _ = CheckForUpdatesAsync("timer", force: false);
        _checkTimer.Start();
    }

    /// <summary>
    /// One throttled update check. A failure is logged and nothing else happens — the user
    /// must never learn about a missing network through a dialog, and the manual button in
    /// the About window is always there.
    /// </summary>
    private async Task CheckForUpdatesAsync(string reason, bool force)
    {
        if (!force && !UpdateService.IsCheckDue(
                TimeSpan.FromMinutes(CheckIntervalMinutes),
                TimeSpan.FromMinutes(FailedCheckRetryMinutes)))
        {
            return;
        }

        lock (_checkSync)
        {
            if (_checkRunning)
            {
                return;
            }

            _checkRunning = true;
        }

        try
        {
            var result = await UpdateService.CheckAsync().ConfigureAwait(true);
            ReportCheckResult(result, reason);
        }
        catch (Exception ex)
        {
            Logging.AppLog.EventWarning("update", $"Update check ({reason}) failed: {ex.Message}");
        }
        finally
        {
            lock (_checkSync)
            {
                _checkRunning = false;
            }
        }
    }

    private void ReportCheckResult(UpdateCheckResult result, string reason)
    {
        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable:
                Logging.AppLog.Event(
                    "update",
                    $"Update available: {result.Latest!.Display} (installed {UpdateService.CurrentVersion.Display}, {result.AssetName}) — found by the {reason} check");

                // One balloon per release, not one per check.
                if (!string.Equals(_announcedVersion, result.Latest.Display, StringComparison.Ordinal))
                {
                    _announcedVersion = result.Latest.Display;
                    ShowUpdateBalloon(result.Latest.Display);
                }

                break;

            case UpdateStatus.UpToDate:
                Logging.AppLog.Info(
                    "Update check ({Reason}): up to date ({Version})",
                    reason,
                    UpdateService.CurrentVersion.Display);
                break;

            default:
                Logging.AppLog.EventWarning("update", $"Update check ({reason}): {result.Status} — {result.Error}");
                break;
        }
    }

    /// <summary>
    /// The replacement script writes a report next to the logs. Without this the user saw
    /// an update that closed the app and then simply did nothing.
    /// </summary>
    private void ReportLastUpdateApply()
    {
        try
        {
            var report = UpdateService.ReadLastApplyReport();

            // Whatever happened, the leftovers of that attempt are read once and dropped.
            UpdateService.CleanUpAfterUpdate();

            if (report is not { Succeeded: false } failure)
            {
                return;
            }

            Logging.AppLog.EventWarning("update", $"The last update attempt failed: {failure.Detail}");

            _notifyIcon?.ShowBalloonTip(
                15000,
                Loc.T("Upd_ApplyFailedTitle"),
                Loc.F("Upd_ApplyFailedText", failure.Detail),
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch
        {
            // Reporting must never disturb startup.
        }
    }

    private void ShowUpdateBalloon(string version)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(
                10000,
                Loc.T("Upd_BalloonTitle"),
                Loc.F("Upd_BalloonText", version),
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Balloons are unsupported on some shells; the About window still works.
        }
    }

    /// <summary>Opens the About dialog (used by the update balloon).</summary>
    internal void ShowAboutWindow()
    {
        ShowWindow();

        if (MainWindow is not MainWindow window)
        {
            return;
        }

        var about = new AboutWindow { Owner = window };
        about.ShowDialog();
    }

    /// <summary>
    /// Leaves the process so the detached replacement script can swap the executable.
    /// The log is flushed first: this is the last thing this build ever writes.
    ///
    /// The script waits for this process to disappear, so a shutdown that never finishes
    /// would keep the executable locked and the update would silently do nothing. That is
    /// why the shutdown is bounded and a watchdog ends the process if it lingers anyway.
    /// </summary>
    public static async Task ShutdownForUpdateAsync()
    {
        if (Interlocked.Exchange(ref _updateShutdownStarted, 1) == 1)
        {
            return;
        }

        IsExplicitExit = true;

        if (Current is App app)
        {
            app._notifyIcon?.Dispose();
            app._notifyIcon = null;
        }

        Logging.AppLog.Event("update", "Closing to apply the update and restart");

        // Release the keyboard first: it keeps the lights from being left on, and the
        // script only needs the process to end.
        if (Current?.MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
        {
            try
            {
                await viewModel.ShutdownCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Logging.AppLog.EventWarning("update", $"Shutdown before the update did not finish cleanly: {ex.Message}");
            }
        }

        Logging.AppLog.Shutdown();

        CloseEveryWindow();

        StartExitWatchdog();

        Current?.Shutdown();
    }

    /// <summary>
    /// Closes the window and any dialog on top of it.
    ///
    /// A dialog opened with ShowDialog runs its own dispatcher loop and is not part of
    /// <see cref="Application.Windows"/>, so Shutdown alone would leave it on screen until the
    /// watchdog fires — the update looked frozen for those seconds.
    /// </summary>
    private static void CloseEveryWindow()
    {
        if (Current is not { } app)
        {
            return;
        }

        var windows = app.Windows.OfType<Window>().ToList();

        if (app.MainWindow is Window owner)
        {
            windows.AddRange(owner.OwnedWindows.OfType<Window>());
        }

        foreach (var window in windows)
        {
            try
            {
                window.Close();
            }
            catch
            {
                // A window that refuses to close is left to the watchdog.
            }
        }
    }

    /// <summary>
    /// Ends the process even if the dispatcher refuses to unwind (a modal dialog, a driver
    /// call that never returns). Without it the swap script would wait half a minute, kill
    /// the app itself and only then replace the file.
    /// </summary>
    private static void StartExitWatchdog()
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(8));
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "update-exit-watchdog"
        };

        watchdog.Start();
    }

    public static void RemoveDataAndStartup()
    {
        try
        {
            Core.Platform.StartupTask.TryDelete(Core.AppPaths.StartupTaskName);
            Core.Platform.StartupTask.TryDelete(Core.AppPaths.LegacyStartupTaskName);

            // Legacy registry entry cleanup.
            try
            {
                var runKey = Registry.CurrentUser
                    .OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

                runKey?.DeleteValue(Core.AppPaths.StartupTaskName, false);
                runKey?.DeleteValue(Core.AppPaths.LegacyStartupTaskName, false);
            }
            catch
            {
                // Ignore.
            }

            foreach (var folder in new[] { Core.AppPaths.DataDirectory, Core.AppPaths.LegacyDataDirectory })
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, true);
                    }
                }
                catch
                {
                    // Ignore: a file may still be held open by this process.
                }
            }

            ModernMessageBox.ShowInfo(Loc.T("Msg_CleanupDone"), Loc.T("Msg_CleanupTitle"));
        }
        catch (Exception ex)
        {
            ModernMessageBox.ShowInfo(Loc.F("Msg_CleanupError", ex.Message), Loc.T("Msg_ErrorTitle"));
        }
    }

    private async Task WaitForControlCenter(bool isAutoStart)
    {
        const string targetProcess = "FnKey";

        if (isAutoStart)
        {
            // Auto-start: be patient, keep the user informed through the tray.
            SetTrayText("Tray_WaitingFnKey");

            while (true)
            {
                if (Process.GetProcessesByName(targetProcess).Length > 0)
                {
                    SetTrayText("Tray_FnKeyFound");
                    await Task.Delay(12000); // Give Control Center time to talk to the hardware.
                    return;
                }

                await Task.Delay(1000);
            }
        }

        // Manual start: check quickly and fail with a clear message.
        SetTrayText("Tray_Checking");

        int attempts = 0;
        while (attempts < 10)
        {
            if (Process.GetProcessesByName(targetProcess).Length > 0)
            {
                SetTrayText("Tray_FnKeyFound");
                await Task.Delay(8000);
                return;
            }

            await Task.Delay(500);
            attempts++;
        }

        ModernMessageBox.ShowInfo(Loc.T("Msg_NoControlCenter"), Loc.T("Msg_FatalTitle"));
        Shutdown();
        IsExplicitExit = true;
    }

    private void RegisterStartup()
    {
        var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        // The task is the only way to start the app elevated at logon without a UAC prompt;
        // re-registering is idempotent, which is what repairs a task that points at an old
        // executable after the app was moved or renamed.
        if (!Core.Platform.StartupTask.TryRegister(Core.AppPaths.StartupTaskName, executable, out var error))
        {
            Logging.AppLog.EventWarning("autostart", $"Logon task could not be created: {error}");
        }
    }

    /// <summary>
    /// Repairs autostart after the executable was renamed (1.0.6): without this the logon
    /// task would keep pointing at a file that no longer exists.
    /// </summary>
    private void SyncStartupTask()
    {
        RegisterStartup();
        Logging.AppLog.Event("mode", $"Autostart task repointed at {Core.AppPaths.ProductName}");
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon();

        try
        {
            var uri = Branding.IconUri;
            var stream = GetResourceStream(uri).Stream;
            var bitmap = new System.Drawing.Bitmap(stream);
            _notifyIcon.Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }
        catch
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _notifyIcon.Visible = true;
        SetTrayText("Tray_Ready");

        // Clicking the "update available" balloon goes straight to the About dialog,
        // where the download button lives.
        _notifyIcon.BalloonTipClicked += (_, _) => ShowAboutWindow();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Renderer = new FluentRenderer();
        contextMenu.BackColor = System.Drawing.Color.FromArgb(32, 32, 32);
        contextMenu.ForeColor = System.Drawing.Color.White;

        _menuOpen = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Open"));
        _menuOpen.Click += (_, _) => ShowWindow();

        try
        {
            var uri = Branding.IconUri;
            var stream = GetResourceStream(uri).Stream;
            var original = new System.Drawing.Bitmap(stream);
            _menuOpen.Image = new System.Drawing.Bitmap(original, new System.Drawing.Size(16, 16));
        }
        catch
        {
            // Icon is optional.
        }

        _menuExit = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Tray_Exit"));
        _menuExit.Click += (_, _) => ExitApp();

        contextMenu.Items.Add(_menuOpen);
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add(_menuExit);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    internal void ShowEffectToast(string name, string description)
    {
        _effectToast ??= new EffectToastWindow();
        _effectToast.ShowEffect(name, description);
    }

    private void SetTrayText(string key, params object?[] args)
    {
        _trayKey = key;
        _trayArgs = args;

        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Text = args.Length == 0 ? Loc.T(key) : Loc.F(key, args);
    }

    private void OnLanguageChanged()
    {
        if (_menuOpen is not null)
        {
            _menuOpen.Text = Loc.T("Tray_Open");
        }

        if (_menuExit is not null)
        {
            _menuExit.Text = Loc.T("Tray_Exit");
        }

        SetTrayText(_trayKey, _trayArgs);
    }

    private void ShowWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    private void ExitApp()
    {
        IsExplicitExit = true;
        _notifyIcon?.Dispose();
        Shutdown();
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.AppLog.Error("Unhandled exception: {Message}", e.Exception, e.Exception.Message);

        System.Windows.MessageBox.Show(
            Loc.F("Crash_Message", e.Exception.Message, e.Exception.StackTrace),
            Loc.T("Crash_Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        ExitApp();
    }

    /// <summary>Simple flat, dark menu renderer used for the tray menu.</summary>
    private sealed class FluentRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public FluentRenderer() : base(new FluentColors())
        {
        }
    }

    private sealed class FluentColors : System.Windows.Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(70, 70, 70);
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(70, 70, 70);
        public override System.Drawing.Color MenuItemSelectedGradientBegin => System.Drawing.Color.FromArgb(70, 70, 70);
        public override System.Drawing.Color MenuItemSelectedGradientEnd => System.Drawing.Color.FromArgb(70, 70, 70);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(60, 60, 60);
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(32, 32, 32);
        public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(32, 32, 32);
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(32, 32, 32);
        public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(32, 32, 32);
    }
}
