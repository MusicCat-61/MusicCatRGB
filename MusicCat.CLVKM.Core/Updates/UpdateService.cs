using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
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

    private const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>File name the downloaded build is parked under until the app restarts.</summary>
    public const string StagedFileName = "MCRGB.update.exe";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient? _http;
    private static ReleaseVersion? _currentVersion;

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
                return _http ??= CreateClient();
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

    /// <summary>Raised after every finished check. May run on a worker thread.</summary>
    public static event EventHandler<UpdateCheckResult>? Checked;

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
            using var response = await Http.GetAsync(ApiUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                var hint = code is 403 or 429
                    ? $"GitHub API rate limit (HTTP {code})"
                    : $"HTTP {code}";

                return Failed(hint);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, timeout, cancellation — all just mean "no answer".
            return Failed(ex.Message);
        }
    }

    /// <summary>Separated from the network call so it can be tested against fixed JSON.</summary>
    public static UpdateCheckResult Parse(string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (!ReleaseVersion.TryParse(tag, out var latest))
            {
                return Failed($"release tag \"{tag}\" is not a version");
            }

            var releaseUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() : null;

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

            if (FindExecutableAsset(root) is not { } asset)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.NoDownload,
                    Current = CurrentVersion,
                    Latest = latest,
                    ReleaseUrl = releaseUrl,
                    Error = "the release has no .exe asset"
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable,
                Current = CurrentVersion,
                Latest = latest,
                DownloadUrl = asset.Url,
                AssetName = asset.Name,
                AssetSize = asset.Size,
                ReleaseUrl = releaseUrl
            };
        }
        catch (JsonException ex)
        {
            return Failed($"unreadable API answer: {ex.Message}");
        }
    }

    private static (string Name, string Url, long Size)? FindExecutableAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The release ships "MCRGB.exe" and "MCRGB.exe.sha256"; only the first is wanted.
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                ? parsed
                : 0;

            return (name, url, size);
        }

        return null;
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

        using var response = await Http
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

        return target;
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

            var script = Path.Combine(Path.GetTempPath(), $"musiccatrgb-update-{Guid.NewGuid():N}.cmd");

            // The script text stays pure ASCII and takes both paths from the environment.
            // cmd.exe reads a .cmd file in the OEM code page, so an install path like
            // "C:\Users\Маksim\..." would be mangled if it were written into the file.
            //
            // Waiting is done by retrying the move itself rather than by watching the PID:
            // that is the condition we actually care about, and it cannot be fooled by a
            // process id appearing in unrelated tasklist output.
            var lines = new[]
            {
                "@echo off",
                "setlocal",
                "set /a tries=0",
                ":retry",
                "move /Y \"%MCRGB_STAGED%\" \"%MCRGB_TARGET%\" >nul 2>nul",
                "if not errorlevel 1 goto swapped",
                "set /a tries+=1",
                "if %tries% geq 120 goto giveup",
                "rem ping is a sleep that works without a console window",
                "ping -n 2 127.0.0.1 >nul",
                "goto retry",
                ":swapped",
                "start \"\" \"%MCRGB_TARGET%\"",
                "goto cleanup",
                ":giveup",
                "rem Give up quietly, but do not leave the user without an app.",
                "start \"\" \"%MCRGB_TARGET%\"",
                ":cleanup",
                "del \"%~f0\""
            };

            File.WriteAllLines(script, lines);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            startInfo.Environment["MCRGB_STAGED"] = stagedFile;
            startInfo.Environment["MCRGB_TARGET"] = executablePath;

            Process.Start(startInfo);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MusicCatRGB/{CurrentVersion.Display}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
