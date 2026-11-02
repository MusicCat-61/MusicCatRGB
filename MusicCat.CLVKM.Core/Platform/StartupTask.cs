using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace MusicCat.CLVKM.Core.Platform;

/// <summary>What the Task Scheduler actually holds for the application.</summary>
public enum StartupTaskState
{
    /// <summary>The query could not be run at all (policy, missing schtasks, another failure).</summary>
    Unknown = 0,

    /// <summary>No task with that name: nothing starts at logon.</summary>
    Missing = 1,

    /// <summary>The task exists but starts a different executable — the app was moved or renamed.</summary>
    PointsElsewhere = 2,

    /// <summary>The task exists and starts this executable at logon.</summary>
    Registered = 3
}

/// <summary>
/// The logon task, read from and written to the Task Scheduler.
///
/// It exists because starting the app with a UAC prompt at every logon would be useless: the
/// task runs with the highest privileges the user already granted, so the lighting comes back
/// without any dialog. <c>schtasks</c> is used directly — the COM API would need another
/// dependency for a job this small.
/// </summary>
public static class StartupTask
{
    /// <summary>
    /// Reads the current state. <paramref name="executablePath"/> is compared with the command
    /// the task runs, so a moved executable is reported instead of being called "registered".
    /// </summary>
    public static StartupTaskState Query(string taskName, string? executablePath, out string? taskCommand)
    {
        taskCommand = null;

        if (!TryRunSchTasks($"/query /tn \"{taskName}\" /xml ONE", out var output, out var exitCode))
        {
            return StartupTaskState.Unknown;
        }

        if (exitCode != 0)
        {
            // "The system cannot find the file specified" is how schtasks says "no such task".
            return StartupTaskState.Missing;
        }

        try
        {
            var document = XDocument.Parse(output.Trim());

            taskCommand = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Command")
                ?.Value
                .Trim()
                .Trim('"');

            if (string.IsNullOrEmpty(taskCommand))
            {
                return StartupTaskState.Unknown;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return StartupTaskState.Registered;
            }

            return PathsMatch(taskCommand, executablePath)
                ? StartupTaskState.Registered
                : StartupTaskState.PointsElsewhere;
        }
        catch (Exception)
        {
            // Unreadable XML: the task exists but we cannot say anything useful about it.
            return StartupTaskState.Unknown;
        }
    }

    /// <summary>
    /// Creates or repoints the task. Recreating an existing one is safe: <c>/f</c> makes it
    /// idempotent, which is exactly what repairs a task that points at an old executable.
    /// </summary>
    /// <param name="runLevel">
    /// "HIGHEST" from the application: the lighting needs the driver, and the task is the only
    /// way to start elevated at logon without a UAC prompt. Tests pass "LIMITED" to exercise
    /// the same code without an administrator token.
    /// </param>
    public static bool TryRegister(
        string taskName,
        string executablePath,
        out string error,
        string runLevel = "HIGHEST")
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = "the executable path is unknown";
            return false;
        }

        // ArgumentList keeps Windows' own escaping rules out of our hands: the /tr value
        // contains quotes and a switch, and building it by concatenation broke it before.
        var startInfo = new ProcessStartInfo("schtasks")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("/create");
        startInfo.ArgumentList.Add("/tn");
        startInfo.ArgumentList.Add(taskName);
        startInfo.ArgumentList.Add("/tr");
        startInfo.ArgumentList.Add($"\"{executablePath}\" --tray");
        startInfo.ArgumentList.Add("/sc");
        startInfo.ArgumentList.Add("onlogon");
        startInfo.ArgumentList.Add("/rl");
        startInfo.ArgumentList.Add(runLevel);
        startInfo.ArgumentList.Add("/f");

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                error = "schtasks could not be started";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (process.ExitCode == 0)
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(output)
                ? $"schtasks exited with {process.ExitCode}"
                : output.Trim();

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the task; a task that is not there is not a failure.</summary>
    public static bool TryDelete(string taskName)
    {
        TryRunSchTasks($"/delete /tn \"{taskName}\" /f", out _, out var exitCode);
        return exitCode == 0;
    }

    private static bool TryRunSchTasks(string arguments, out string output, out int exitCode)
    {
        output = string.Empty;
        exitCode = -1;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (output.Length == 0)
            {
                output = errors;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch
        {
            // No schtasks, no rights, a policy — the caller reports "unknown".
            return false;
        }
    }

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
