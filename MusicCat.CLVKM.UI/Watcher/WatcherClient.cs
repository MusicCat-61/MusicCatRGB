using System;
using System.Diagnostics;
using System.Threading;
using MusicCat.CLVKM.Core.Ipc;
using MusicCat.CLVKM.UI.Logging;

using Timer = System.Threading.Timer;

namespace MusicCat.CLVKM.UI.Watcher;

/// <summary>
/// App half of the watcher: starts the headless process, keeps it alive, and hands the events
/// it receives from the pipe to whoever listens. The process is only started while at least one
/// flash is switched on, so an app that does not use the feature runs no extra process at all.
/// </summary>
internal sealed class WatcherClient : IDisposable
{
    /// <summary>Do not restart the watcher more often than this (a crashing watcher loop).</summary>
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan GuardInterval = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Timer _guard;

    private Process? _process;
    private WatcherEventClient? _pipe;
    private bool _wanted;
    private DateTime _lastStartUtc = DateTime.MinValue;

    public WatcherClient()
    {
        _guard = new Timer(_ => KeepAlive(), null, GuardInterval, GuardInterval);
    }

    /// <summary>Raised on a worker thread for every event received over the pipe.</summary>
    public event Action<WatcherEvent>? EventReceived;

    /// <summary>True while the watcher process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Starts the watcher; safe to call when it already runs.</summary>
    public void Start()
    {
        lock (_sync)
        {
            _wanted = true;
            EnsureStartedLocked();
        }
    }

    /// <summary>Stops the watcher and closes the pipe.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            _wanted = false;
            StopLocked();
        }
    }

    private void KeepAlive()
    {
        lock (_sync)
        {
            if (_wanted)
            {
                EnsureStartedLocked();
            }
        }
    }

    private void EnsureStartedLocked()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        if (DateTime.UtcNow - _lastStartUtc < RestartBackoff)
        {
            return;
        }

        _lastStartUtc = DateTime.UtcNow;
        EnsurePipeLocked();

        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            AppLog.EventWarning("watcher", "Watcher: the executable path is unknown, cannot start the watcher");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = WatcherHost.Switch,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            // The watcher ends as soon as this process does, even if the pipe is left open.
            startInfo.Environment[WatcherHost.ParentProcessVariable] =
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            _process = Process.Start(startInfo);

            AppLog.Event("watcher", $"Watcher started (pid {_process?.Id})");
        }
        catch (Exception ex)
        {
            AppLog.EventWarning("watcher", $"Watcher could not be started: {ex.Message}");
        }
    }

    private void EnsurePipeLocked()
    {
        if (_pipe is not null)
        {
            return;
        }

        _pipe = new WatcherEventClient();
        _pipe.Received += OnReceived;
        _pipe.Failed += OnPipeFailed;
        _ = _pipe.StartAsync();
    }

    private void OnPipeFailed(string message) =>
        AppLog.EventWarning("watcher", $"Watcher channel problem: {message}");

    private void OnReceived(object? sender, WatcherEvent message)
    {
        try
        {
            EventReceived?.Invoke(message);
        }
        catch (Exception ex)
        {
            AppLog.EventWarning("watcher", $"Watcher event handler failed: {ex.Message}");
        }
    }

    private void StopLocked()
    {
        if (_pipe is not null)
        {
            _pipe.Received -= OnReceived;
            _ = _pipe.DisposeAsync();
            _pipe = null;
        }

        var process = _process;
        _process = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                AppLog.Event("watcher", "Watcher stopped");
            }
        }
        catch
        {
            // It may have exited between the check and the kill: nothing to do either way.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        _guard.Dispose();
        Stop();
    }
}
