using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MusicCat.CLVKM.Core.Settings;
using MusicCat.CLVKM.UI.Localization;

namespace MusicCat.CLVKM.UI;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _menuOpen;
    private System.Windows.Forms.ToolStripMenuItem? _menuExit;

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

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Ensure the working directory is the app folder (DllImport needs InsydeDCHU.dll).
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        var startupSettings = SettingsManager.Load();
        Loc.Initialize(startupSettings.Language);
        Theming.Theme.Apply(Theming.Theme.Parse(startupSettings.Theme), notify: false);
        Loc.LanguageChanged += OnLanguageChanged;

        const string appName = "MusicCat.CLVKM_SingleInstance_Mutex";
        bool createdNew;
        _mutex = new Mutex(true, appName, out createdNew);

        if (!createdNew)
        {
            if (e.Args.Length == 0 || e.Args[0] != "--tray")
            {
                System.Windows.MessageBox.Show(Loc.T("Msg_AlreadyRunning"), "KeyGlowMaster");
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

        if (!startInTray)
        {
            CheckAndAskStartup();
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

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
    }

    private void CheckAndAskStartup()
    {
        try
        {
            bool isConfigured = SettingsManager.Load().IsStartupEnabled;

            if (isConfigured)
            {
                return;
            }

            bool enable = ModernMessageBox.Show(
                Loc.T("Msg_ServiceMode"),
                Loc.T("Msg_ServiceModeTitle"));

            if (!enable)
            {
                return;
            }

            RegisterStartup();

            var settings = SettingsManager.Load();
            settings.IsStartupEnabled = true;
            SettingsManager.Save(settings);
        }
        catch
        {
            // Never block startup because of the optional auto-start helper.
        }
    }

    public static void RemoveDataAndStartup()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = "/delete /tn MusicCat.CLVKM /f",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit();

            // Legacy registry entry cleanup.
            try
            {
                Registry.CurrentUser
                    .OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true)
                    ?.DeleteValue("MusicCat.CLVKM", false);
            }
            catch
            {
                // Ignore.
            }

            try
            {
                string settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MusicCat.CLVKM");

                if (Directory.Exists(settingsPath))
                {
                    Directory.Delete(settingsPath, true);
                }
            }
            catch
            {
                // Ignore.
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
        try
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            // Scheduled task so the app can start elevated at logon without a UAC prompt.
            string cmd = $"/create /tn MusicCat.CLVKM /tr \"'\\\"{exePath}\\\"' --tray\" /sc onlogon /rl HIGHEST /f";

            Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = cmd,
                UseShellExecute = true,
                CreateNoWindow = true
            })?.WaitForExit();
        }
        catch
        {
            // Optional feature.
        }
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
