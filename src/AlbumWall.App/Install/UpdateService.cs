using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AlbumWall.Domain;

namespace AlbumWall.App.Install;

/// <summary>What a check found. <see cref="Error"/> is a sentence that can be shown as it is.</summary>
public sealed class UpdateInfo
{
    public string CurrentVersion { get; init; } = "";
    public string LatestTag { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string ReleaseUrl { get; set; } = "";
    public string? AssetUrl { get; set; }
    public string? AssetName { get; set; }
    public string? SumsUrl { get; set; }

    /// <summary>
    /// The feed answered "no such thing": no full release exists yet, or the repository is private.
    /// Not an error - it is the state this project is in until its first public release.
    /// </summary>
    public bool NothingPublished { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// The in-app updater, ported from the family (LP-100A, by way of W2 and FlexPad): ask GitHub for the
/// latest release, download the build for this platform, and - since a running executable cannot
/// overwrite itself - hand over to a helper that waits for exit, swaps the exe and relaunches.
///
/// WHAT IS DIFFERENT FROM THE FAMILY'S, and why:
///
/// - THE DOWNLOAD IS VERIFIED. Every AlbumWall release carries SHA256SUMS, and the zip must be listed
///   in it and match before anything is unpacked. This is a program fetching a program and running
///   it; get-libmpv.ps1 checks a DLL the same way. A release with no SHA256SUMS is not installed.
/// - The helper retries the swap and clears the extraction cache; see <see cref="UpdateApplyScript"/>.
/// - The helper runs with the temp folder as its working directory, not ours - the second of the
///   three rules the uninstall learned (c7b545f).
/// - <c>ALBUMWALL_UPDATE_FEED</c> points the check at another URL serving the same JSON. It exists so
///   the whole path - check, download, verify, swap, relaunch - can be run against a local server,
///   which is the only way to test it at all while the repository is private. Whoever can set an
///   environment variable for this process can already replace the exe by hand; it is logged loudly.
///
/// WHAT GITHUB'S "latest" MEANS. <c>/releases/latest</c> never returns a pre-release, which is the
/// point: test builds and the libmpv-* releases are pre-releases, so neither is ever offered. It also
/// answers 404 for a private repository, so until the public snapshot this finds nothing, honestly.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string CurrentVersion => InstallService.CurrentVersion;

    /// <summary>
    /// Only a single-file build can be updated: the update IS one file, swapped for another. A
    /// development build is a folder of assemblies and says so instead of offering.
    /// </summary>
    public static bool CanUpdate => InstallService.IsSingleFile;

    /// <summary>Runtime identifier used in the release asset name: win-x64, linux-x64, linux-arm64.</summary>
    public static string Rid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    private static string FeedUrl
    {
        get
        {
            var other = Environment.GetEnvironmentVariable("ALBUMWALL_UPDATE_FEED");
            if (string.IsNullOrWhiteSpace(other))
                return $"https://api.github.com/repos/{InstallService.Repo}/releases/latest";
            Console.WriteLine($"[update] FEED OVERRIDDEN by ALBUMWALL_UPDATE_FEED: {other}");
            return other;
        }
    }

    public static async Task<UpdateInfo> CheckAsync()
    {
        var info = new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            ReleaseUrl = App.ProjectUrl + "/releases/latest"
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("AlbumWall-UpdateCheck", "1.0"));
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                info.NothingPublished = true;
                Console.WriteLine("[update] nothing published (404)");
                return info;
            }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            info.LatestTag = root.GetProperty("tag_name").GetString() ?? "";
            if (root.TryGetProperty("html_url", out var hu) && hu.GetString() is { Length: > 0 } url)
                info.ReleaseUrl = url;

            var wanted = $"AlbumWall-{Rid()}.zip";
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var link = a.GetProperty("browser_download_url").GetString();
                    if (name == wanted) { info.AssetUrl = link; info.AssetName = name; }
                    else if (name == "SHA256SUMS") info.SumsUrl = link;
                }
            }

            // Unparseable on either side is "not newer", never "newer": see VersionOrder.
            info.UpdateAvailable = VersionOrder.IsNewer(info.LatestTag, CurrentVersion);
            Console.WriteLine($"[update] latest {info.LatestTag}, have {CurrentVersion}, "
                            + $"newer={info.UpdateAvailable}, asset={(info.AssetUrl is null ? "none for " + Rid() : wanted)}");
        }
        catch (Exception ex)
        {
            info.Error = ex is HttpRequestException or TaskCanceledException
                ? "Could not reach the update server. Check the network and try again."
                : ex.Message;
            Console.WriteLine($"[update] check failed: {ex.Message}");
        }
        return info;
    }

    /// <summary>
    /// Where the update is downloaded and unpacked. The relaunched app must never have this as its
    /// working directory: a directory in use as one cannot be deleted, and the next update's
    /// clean-up of it would throw.
    /// </summary>
    private static string StageRoot => Path.Combine(Path.GetTempPath(), "AlbumWall-update");

    /// <summary>
    /// Downloads the release's zip, checks it against the release's SHA256SUMS, unpacks it, and returns
    /// the staged executable. <paramref name="progress"/> is told the fraction downloaded, 0 to 1.
    /// </summary>
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (info.AssetUrl is null || info.AssetName is null)
            throw new InvalidOperationException($"This release has no build for {Rid()}.");
        if (info.SumsUrl is null)
            throw new InvalidOperationException("This release publishes no SHA256SUMS, so its download "
                                              + "cannot be checked. It has not been installed.");

        var tmp = StageRoot;
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        // The list first: it is 300 bytes, and without the zip's line in it there is no point
        // fetching fifty megabytes.
        string sums;
        using (var req = Request(info.SumsUrl))
        using (var resp = await Http.SendAsync(req))
        {
            resp.EnsureSuccessStatusCode();
            sums = await resp.Content.ReadAsStringAsync();
        }
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && f[1].TrimStart('*', '.', '/') == info.AssetName)
            .Select(f => f[0].ToLowerInvariant())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"SHA256SUMS does not list {info.AssetName}. Not installed.");

        var zip = Path.Combine(tmp, "update.zip");
        using (var req = Request(info.AssetUrl))
        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var from = await resp.Content.ReadAsStreamAsync();
            await using var to = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (actual != expected)
        {
            Directory.Delete(tmp, recursive: true);
            throw new InvalidOperationException("The download does not match the release's SHA-256 and has been "
                                              + "thrown away. Nothing was installed.");
        }
        Console.WriteLine($"[update] {info.AssetName} matches SHA256SUMS ({actual[..12]}...)");

        var unpacked = Path.Combine(tmp, "ex");
        ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
        File.Delete(zip);

        var staged = Directory.GetFiles(unpacked, InstallService.ExeFileName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{InstallService.ExeFileName} is not in the downloaded package.");

        // A last look before it is given the keys: the right kind of file, and not a stub.
        var head = new byte[4];
        await using (var fs = File.OpenRead(staged)) _ = await fs.ReadAsync(head);
        var looksRight = OperatingSystem.IsWindows() ? head[0] == 'M' && head[1] == 'Z'
                                                     : head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
        if (!looksRight || new FileInfo(staged).Length < 1_000_000)
            throw new InvalidOperationException("The downloaded program is not an executable for this system.");

        return staged;
    }

    private static HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("AlbumWall-UpdateInstall", "1.0"));
        return req;
    }

    /// <summary>
    /// Launches the detached helper that waits for this process to exit, replaces the current
    /// executable with the staged one, and relaunches it. THE CALLER MUST THEN EXIT THE APP.
    /// </summary>
    public static void ApplyAndRestart(string stagedExe)
    {
        var target = InstallService.ExePath;
        var targetDir = Path.GetDirectoryName(target)!;
        var marker = FailedMarkerPath(target);
        var pid = Environment.ProcessId;

        // The helper lives in the temp root, not in the staging folder: it deletes that folder, and a
        // script cannot sit in the folder it is removing.
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(Path.GetTempPath(), "albumwall-apply-update.ps1");
            File.WriteAllText(ps1, UpdateApplyScript.Windows(pid, stagedExe, target, marker, targetDir,
                                                             StageRoot, InstallService.OwnExtractionDir, ps1));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),      // not ours: see the class comment
            });
        }
        else
        {
            var sh = Path.Combine(Path.GetTempPath(), "albumwall-apply-update.sh");
            File.WriteAllText(sh, UpdateApplyScript.Unix(pid, stagedExe, target, marker, targetDir,
                                                         StageRoot, InstallService.OwnExtractionDir, sh));
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{sh}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
        Console.WriteLine($"[update] helper started; swapping {target} once pid {pid} has gone; "
                        + $"then removing {InstallService.OwnExtractionDir ?? "(nothing unpacked)"}");
    }

    private static string FailedMarkerPath(string targetExe) =>
        Path.Combine(Path.GetDirectoryName(targetExe) ?? ".", ".albumwall-update-failed");

    /// <summary>
    /// True, once, if the last helper could not swap the exe (it relaunched the old one). Clears the
    /// marker, so the warning shows on the next start and not on every start.
    /// </summary>
    public static bool ConsumeUpdateFailed()
    {
        try
        {
            var p = FailedMarkerPath(Environment.ProcessPath ?? "");
            if (File.Exists(p)) { File.Delete(p); return true; }
        }
        catch { /* a marker we cannot read is a warning we do not show */ }
        return false;
    }
}
