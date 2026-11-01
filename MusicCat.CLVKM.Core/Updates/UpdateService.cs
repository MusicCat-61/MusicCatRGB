using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MusicCat.CLVKM.Core.Updates;

public enum UpdateStatus
{
    /// <summary>Nothing was checked yet in this run.</summary>
    Unknown,

    /// <summary>The installed build is the newest release.</summary>
    UpToDate,

    /// <summary>A newer release exists and carries a downloadable executable.</summary>
    UpdateAvailable,

    /// <summary>A newer release exists, but it has no .exe asset to download.</summary>
    NoDownload,

    /// <summary>The check itself failed (offline, rate limit, API change).</summary>
    Failed
}

/// <summary>Result of one update check.</summary>
public sealed class UpdateCheckResult
{
    public UpdateStatus Status { get; init; } = UpdateStatus.Unknown;

    /// <summary>Version currently installed.</summary>
    public ReleaseVersion? Current { get; init; }

    /// <summary>Version of the newest release, when one was read successfully.</summary>
    public ReleaseVersion? Latest { get; init; }

    /// <summary>Direct download URL of the release executable.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>Name of the release asset, for the log.</summary>
    public string? AssetName { get; init; }

    /// <summary>Size of the asset in bytes (0 when the API did not report it).</summary>
    public long AssetSize { get; init; }

    /// <summary>Release page, for a manual download.</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>Reason the check failed, or why there is nothing to download.</summary>
    public string? Error { get; init; }

    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>
/// Checks GitHub Releases for a newer build and downloads it next to the running
/// executable. The check is deliberately tolerant: no network, a rate limit or a release
/// without a binary must never disturb the lighting.
/// </summary>
public static class UpdateService
{
    /// <summary>Repository the releases are read from.</summary>
    public const string Repository = "MusicCat-61/MusicCatRGB";

    private const string LatestReleaseUrl = "https://github.com/" + Repository + "/releases/latest";

    /// <summary>Name of the executable every release carries (see the release workflow).</summary>
    public const string AssetFileName = "MCRGB.exe";

    /// <summary>File name the downloaded build is parked under until the app restarts.</summary>
    public const string StagedFileName = "MCRGB.update.exe";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient? _http;
    private static HttpClient? _downloadHttp;
    private static ReleaseVersion? _currentVersion;

    /// <summary>
    /// Used for the release pages, where the redirect itself is the answer. Following it would
    /// lose the <c>Location</c> header that carries the version.
    /// </summary>
    private static HttpClient Http
    {
        get
        {
            if (_http is not null)
            {
                return _http;
            }

            lock (Gate)
            {
                return _http ??= CreateClient(allowRedirects: false);
            }
        }
    }

    /// <summary>Asset downloads answer with a redirect to the CDN, so this one follows them.</summary>
    private static HttpClient DownloadHttp
    {
        get
        {
            if (_downloadHttp is not null)
            {
                return _downloadHttp;
            }

            lock (Gate)
            {
                return _downloadHttp ??= CreateClient(allowRedirects: true);
            }
        }
    }

    /// <summary>
    /// The running build. Read from the <em>entry</em> assembly, not from this library:
    /// the version lives in the UI project, so reading it here would always answer 1.0.0
    /// and the app would offer itself an update on every launch.
    /// </summary>
    public static ReleaseVersion CurrentVersion
    {
        get => _currentVersion ??= ReadCurrentVersion();
        set => _currentVersion = value;
    }

    /// <summary>Result of the most recent check in this run; null before the first one.</summary>
    public static UpdateCheckResult? LastResult { get; private set; }

    /// <summary>When the most recent check finished (UTC); null before the first one.</summary>
    public static DateTime? LastCheckUtc { get; private set; }

    /// <summary>Raised after every finished check. May run on a worker thread.</summary>
    public static event EventHandler<UpdateCheckResult>? Checked;

    /// <summary>
    /// True when asking GitHub again is worth it. A successful check is left alone for
    /// <paramref name="interval"/>; a failed one is retried much sooner, because the usual
    /// reason is a missing network rather than a missing release.
    /// </summary>
    public static bool IsCheckDue(TimeSpan interval, TimeSpan retryAfterFailure)
    {
        if (LastCheckUtc is not { } last)
        {
            return true;
        }

        var needs = LastResult is { Status: UpdateStatus.Failed } ? retryAfterFailure : interval;
        return DateTime.UtcNow - last >= needs;
    }

    /// <summary>
    /// Reads the newest release. Never throws: every failure comes back as
    /// <see cref="UpdateStatus.Failed"/> with a reason.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            LastResult = result;
            LastCheckUtc = DateTime.UtcNow;

            try
            {
                Checked?.Invoke(null, result);
            }
            catch
            {
                // A faulty subscriber must not break the updater.
            }

            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A plain web request, not the REST API. GitHub answers /releases/latest with a
            // redirect to /releases/tag/<version>, so the newest version is in the Location
            // header — and a page request is not counted against the API rate limit at all.
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.Headers.Location is { } location)
            {
                return await FromRedirectAsync(location, cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"HTTP {(int)response.StatusCode}");
            }

            // A successful answer without a redirect means the repository has no release yet.
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Current = CurrentVersion,
                Latest = CurrentVersion
            };
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, timeout, cancellation — all just mean "no answer".
            return Failed(ex.Message);
        }
    }

    /// <summary>
    /// Turns the redirect target into a result. The executable is not listed anywhere in a
    /// page response, so its address is derived from the tag by the release convention and
    /// then confirmed with a HEAD request: a release without the file says so instead of
    /// failing later at download time.
    /// </summary>
    private static async Task<UpdateCheckResult> FromRedirectAsync(Uri location, CancellationToken cancellationToken)
    {
        var tag = TagFromLocation(location);

        if (tag is null || !ReleaseVersion.TryParse(tag, out var latest))
        {
            return Failed($"«/releases/latest» redirected to «{location}», which is not a release tag");
        }

        var releaseUrl = $"https://github.com/{Repository}/releases/tag/{tag}";

        if (latest <= CurrentVersion)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Current = CurrentVersion,
                Latest = latest,
                ReleaseUrl = releaseUrl
            };
        }

        var assetUrl = $"https://github.com/{Repository}/releases/download/{tag}/{AssetFileName}";

        if (!await AssetExistsAsync(assetUrl, cancellationToken).ConfigureAwait(false))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.NoDownload,
                Current = CurrentVersion,
                Latest = latest,
                ReleaseUrl = releaseUrl,
                Error = $"the release carries no {AssetFileName}"
            };
        }

        return new UpdateCheckResult
        {
            Status = UpdateStatus.UpdateAvailable,
            Current = CurrentVersion,
            Latest = latest,
            DownloadUrl = assetUrl,
            AssetName = AssetFileName,
            ReleaseUrl = releaseUrl
        };
    }

    /// <summary>
    /// The version at the end of a <c>/releases/tag/&lt;version&gt;</c> address; null for
    /// anything else, including the redirect to a bare <c>/releases</c>.
    /// </summary>
    public static string? TagFromLocation(Uri location)
    {
        var segments = location.AbsolutePath.Trim('/').Split('/');

        return segments.Length >= 2 && segments[^2].Equals("tag", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[^1])
            : null;
    }

    private static async Task<bool> AssetExistsAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // A release asset answers with a redirect to the download host; 404 means missing.
            return (int)response.StatusCode is 200 or 301 or 302;
        }
        catch
        {
            // A flaky probe must not cost the user an update: let the download try.
            return true;
        }
    }

    /// <summary>
    /// Saves the release build into <paramref name="targetDirectory"/> — normally the
    /// folder the application was started from, so the update replaces it in place.
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateCheckResult result,
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            throw new InvalidOperationException("There is no download URL for this release.");
        }

        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, StagedFileName);

        // The download host sits behind a redirect of its own, so this is the client that
        // follows redirects.
        using var response = await DownloadHttp
            .GetAsync(result.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"The download answered HTTP {(int)response.StatusCode}.");
        }

        long total = response.Content.Headers.ContentLength ?? result.AssetSize;
        var buffer = new byte[81920];

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (total > 0)
                {
                    progress?.Report(Math.Clamp(written / (double)total, 0, 1));
                }
            }
        }

        // A half-downloaded file must never be applied.
        if (total > 0 && new FileInfo(target).Length != total)
        {
            File.Delete(target);
            throw new IOException("The download was incomplete.");
        }

        // A proxy error page or an HTML sign-in form saved as ".exe" would otherwise be
        // copied over the running build and destroy the installation.
        if (!LooksLikeExecutable(target))
        {
            File.Delete(target);
            throw new IOException("The downloaded file is not a Windows executable.");
        }

        return target;
    }

    /// <summary>True when the file starts with the "MZ" signature every PE image has.</summary>
    private static bool LooksLikeExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[2];
            return stream.Read(header, 0, header.Length) == header.Length
                && header[0] == (byte)'M'
                && header[1] == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hands the staged file to a detached script that replaces the executable and starts
    /// it again. Windows locks a running executable against being overwritten, so the swap
    /// has to happen after this process is gone.
    /// </summary>
    public static bool TryApplyOnExit(string stagedFile, string executablePath, out string error)
    {
        error = string.Empty;

        try
        {
            if (!File.Exists(stagedFile))
            {
                error = "The downloaded file is gone.";
                return false;
            }

            Directory.CreateDirectory(AppPaths.DataDirectory);

            // The script text stays pure ASCII and takes both paths from the environment.
            // cmd.exe reads a .cmd file in the OEM code page, so an install path like
            // "C:\Users\Маksim\..." would be mangled if it were written into the file.
            //
            // The swap waits for this process to disappear, retries the move, and only then
            // falls back to a copy. Every step and every failure lands in a log, so a failed
            // update is no longer silent.
            File.WriteAllLines(ApplyScriptPath, ApplyScriptLines);

            // A report left behind by an earlier attempt would be mistaken for this one's.
            TryDelete(ApplyLogPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{ApplyScriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.Environment["MCRGB_STAGED"] = stagedFile;
            startInfo.Environment["MCRGB_TARGET"] = executablePath;
            startInfo.Environment["MCRGB_LOG"] = ApplyLogPath;
            startInfo.Environment["MCRGB_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["MCRGB_EXPECT"] = new FileInfo(stagedFile).Length.ToString(CultureInfo.InvariantCulture);

            Process.Start(startInfo);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Script that performs the swap after this process is gone.</summary>
    private static string ApplyScriptPath => Path.Combine(AppPaths.DataDirectory, "apply-update.cmd");

    /// <summary>Where the apply script records what it did; read on the next start.</summary>
    public static string ApplyLogPath => Path.Combine(AppPaths.LogDirectory, "update-apply.log");

    /// <summary>Full path the download is parked under until the app restarts.</summary>
    public static string StagedFilePath => Path.Combine(ExecutableDirectory, StagedFileName);

    /// <summary>
    /// The staged build, when one is on disk and newer than the running one. A leftover of
    /// an already installed release is deleted: it would be offered for ever otherwise, and
    /// applying it would quietly downgrade the app.
    /// </summary>
    public static string? FindStagedUpdate()
    {
        try
        {
            var staged = StagedFilePath;

            if (!File.Exists(staged))
            {
                return null;
            }

            if (new FileInfo(staged).Length == 0 || IsNotNewer(staged))
            {
                File.Delete(staged);
                return null;
            }

            return staged;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the staged build is not newer than the running one.</summary>
    private static bool IsNotNewer(string staged)
    {
        // A file without a readable version is kept: refusing a real update would be worse
        // than offering it twice.
        var text = FileVersionInfo.GetVersionInfo(staged).FileVersion;
        return ReleaseVersion.TryParse(text, out var stagedVersion) && stagedVersion <= CurrentVersion;
    }

    /// <summary>What the detached apply script reported the last time it ran.</summary>
    public sealed record UpdateApplyReport(bool Succeeded, string Detail);

    /// <summary>
    /// Outcome of the last apply attempt, or null when the script never got as far as
    /// writing one. This is how a failed update stops being silent.
    /// </summary>
    public static UpdateApplyReport? ReadLastApplyReport()
    {
        try
        {
            if (!File.Exists(ApplyLogPath))
            {
                return null;
            }

            const string marker = "RESULT=";
            var line = File.ReadLines(ApplyLogPath)
                .LastOrDefault(entry => entry.Contains(marker, StringComparison.Ordinal));

            if (line is null)
            {
                return null;
            }

            var text = line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();

            if (text.StartsWith("OK", StringComparison.Ordinal))
            {
                return new UpdateApplyReport(true, string.Empty);
            }

            return new UpdateApplyReport(
                false,
                text.StartsWith("FAILED", StringComparison.Ordinal) ? text["FAILED".Length..].Trim() : text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Drops everything the last apply attempt left behind: the report and the script.
    /// A failing attempt must be announced once, not on every start.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        TryDelete(ApplyLogPath);
        TryDelete(ApplyScriptPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover file is harmless.
        }
    }

    /// <summary>
    /// The replacement script. It decides nothing on its own: it waits for the app to exit,
    /// swaps the file, checks the result, starts the app again and records every step in
    /// <see cref="ApplyLogPath"/>, errors included.
    /// </summary>
    private static readonly string[] ApplyScriptLines =
    {
        "@echo off",
        "setlocal",
        "set \"STAGED=%MCRGB_STAGED%\"",
        "set \"TARGET=%MCRGB_TARGET%\"",
        "set \"LOG=%MCRGB_LOG%\"",
        "set \"APPPID=%MCRGB_PID%\"",
        "set \"EXPECT=%MCRGB_EXPECT%\"",
        "call :note \"apply start: pid=%APPPID% expected bytes=%EXPECT%\"",
        "call :note \"staged: %STAGED%\"",
        "call :note \"target: %TARGET%\"",
        "",
        "rem Windows keeps a running executable locked, so retry the move itself: that is the",
        "rem condition we actually care about, and it cannot be fooled by a process id that",
        "rem shows up in unrelated tasklist output.",
        "set /a tries=0",
        ":swap",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 30 goto stuck",
        "call :sleep",
        "goto swap",
        "",
        ":stuck",
        "rem Still locked after half a minute, so the process holding the file did not go",
        "rem away by itself. The lock it is still holding proves that the id is ours.",
        "call :note \"still locked after 30s, ending pid %APPPID%\"",
        "taskkill /PID %APPPID% /T /F >nul 2>nul",
        "call :sleep",
        "set /a tries=0",
        ":swapagain",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 15 goto copyfile",
        "call :sleep",
        "goto swapagain",
        "",
        ":copyfile",
        "rem A rename can also fail for reasons no retry can fix (another volume, a filter",
        "rem driver holding the file). Copying fails differently, so give it one chance.",
        "call :note \"rename failed, trying a copy\"",
        "copy /Y /B \"%STAGED%\" \"%TARGET%\" >>\"%LOG%\" 2>&1",
        "if errorlevel 1 goto failed",
        "del /F /Q \"%STAGED%\" >nul 2>nul",
        "",
        ":swapped",
        "call :verify",
        "if errorlevel 1 goto failed",
        "call :note \"RESULT=OK\"",
        "call :note \"the new build is in place, starting it\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":failed",
        "call :note \"RESULT=FAILED the downloaded file could not replace the running one\"",
        "call :note \"kept for another try: %STAGED%\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":done",
        "rem Nothing left to do here: the app deletes this script when it starts again.",
        "exit /b 0",
        "",
        ":verify",
        "if not exist \"%TARGET%\" exit /b 1",
        "for %%A in (\"%TARGET%\") do if %%~zA LSS 1024 exit /b 1",
        "if \"%EXPECT%\"==\"\" exit /b 0",
        "for %%A in (\"%TARGET%\") do if not \"%%~zA\"==\"%EXPECT%\" exit /b 1",
        "exit /b 0",
        "",
        ":note",
        ">>\"%LOG%\" echo [%DATE% %TIME%] %~1",
        "exit /b 0",
        "",
        ":sleep",
        "rem A wait that works without a console window.",
        "ping -n 2 127.0.0.1 >nul 2>nul",
        "exit /b 0"
    };

    /// <summary>Folder the application was started from — where the update is written.</summary>
    public static string ExecutableDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    /// <summary>Full path of the running executable.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MCRGB.exe");

    /// <summary>True when the folder can actually be written to (Program Files usually cannot).</summary>
    public static bool CanWriteToInstallFolder()
    {
        try
        {
            var probe = Path.Combine(ExecutableDirectory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="allowRedirects">
    /// False for the release pages, where the redirect target is the answer; true for asset
    /// downloads, which are redirected to the CDN.
    /// </param>
    private static HttpClient CreateClient(bool allowRedirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MusicCatRGB/{CurrentVersion.Display}");
        return client;
    }

    private static UpdateCheckResult Failed(string reason) => new()
    {
        Status = UpdateStatus.Failed,
        Current = CurrentVersion,
        Error = reason
    };

    private static ReleaseVersion ReadCurrentVersion()
    {
        // The informational version carries the full tag-derived number, including a
        // fourth component; the assembly version is the fallback.
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;

        var informational = assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (informational is not null)
        {
            int plus = informational.IndexOf('+');
            if (plus >= 0)
            {
                informational = informational[..plus];
            }

            if (ReleaseVersion.TryParse(informational, out var fromAttribute))
            {
                return fromAttribute;
            }
        }

        if (ReleaseVersion.TryParse(assembly.GetName().Version?.ToString(), out var fallback))
        {
            return fallback;
        }

        return ReleaseVersion.TryParse("0.0.0", out var zero) ? zero : throw new InvalidOperationException();
    }
}
