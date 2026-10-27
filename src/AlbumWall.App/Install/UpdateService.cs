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

    /// <summary>The release offered is an Edge build (a GitHub pre-release).</summary>
    public bool IsPrerelease { get; set; }

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
///   it; get-libmpv.ps1 checked a DLL the same way while there was one. A release with no SHA256SUMS is not installed.
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
    ///
    /// On the Mac, by replacing AlbumWall.app whole (MacUpdate), since 0.7.12: the program sits
    /// inside a signed bundle, and swapping only it broke the seal, which is why the Mac did not
    /// update itself from 0.7.9 (security review 2026-10-26, finding 6). Not from the disk image,
    /// from Downloads, or where this account cannot write: <see cref="MacBlocker"/> says which.
    /// </summary>
    public static bool CanUpdate => InstallService.IsSingleFile && MacBlocker is null;

    /// <summary>Why a Mac copy cannot update itself, as a sentence; null when it can, and on every
    /// other system.</summary>
    public static string? MacBlocker =>
        OperatingSystem.IsMacOS() && InstallService.IsSingleFile ? MacUpdate.Blocker(InstallService.ExePath) : null;

    /// <summary>Runtime identifier used in the release asset name: win-x64, linux-x64, linux-arm64.</summary>
    public static string Rid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    /// <summary>
    /// Stable: the latest full release. Edge: the release list, newest app release taken
    /// (<see cref="ReleaseFeed"/>). ALBUMWALL_UPDATE_FEED replaces either and must serve the
    /// matching shape (one release for Stable, a list for Edge), from a place <see cref="MayFetch"/>
    /// allows: anything else is ignored.
    /// </summary>
    private static string FeedUrl(bool edge)
    {
        var github = edge ? $"https://api.github.com/repos/{InstallService.Repo}/releases?per_page=30"
                          : $"https://api.github.com/repos/{InstallService.Repo}/releases/latest";
        var other = Environment.GetEnvironmentVariable("ALBUMWALL_UPDATE_FEED");
        if (string.IsNullOrWhiteSpace(other)) return github;
        if (!Uri.TryCreate(other, UriKind.Absolute, out var u) || !MayFetch(u))
        {
            Console.WriteLine($"[update] ALBUMWALL_UPDATE_FEED ignored (GitHub over https, or this computer, only): {other}");
            return github;
        }
        Console.WriteLine($"[update] FEED OVERRIDDEN by ALBUMWALL_UPDATE_FEED: {other}");
        return other;
    }

    /// <summary>
    /// Where the updater may fetch from: GitHub over https (the API, the release pages, and
    /// githubusercontent.com, where a download is redirected), or this computer, over http or
    /// https, for a test feed. ALBUMWALL_UPDATE_FEED worked in every build and took any address,
    /// and the asset links in a feed were followed wherever they pointed: an environment that
    /// set it could point a copy at any server, which then only had to agree with itself
    /// (security review 2026-10-26, INFO). SHA256SUMS comes from the same release, so GitHub
    /// stays the root of trust.
    /// </summary>
    internal static bool MayFetch(Uri u)
    {
        if (u.IsLoopback) return u.Scheme is "http" or "https";
        if (u.Scheme != "https") return false;
        var host = u.IdnHost.ToLowerInvariant();
        return host is "github.com" or "api.github.com"
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    /// <summary>Refuses a reply that a redirect brought from somewhere <see cref="MayFetch"/> does not allow.</summary>
    private static void CheckLanded(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri is { } u && !MayFetch(u))
            throw new InvalidOperationException($"The download was redirected to {u.Host}, not GitHub. Nothing was installed.");
    }

    /// <summary>
    /// On Linux .NET does its TLS and hashing through the system's OpenSSL, loaded by name the first
    /// time either is used. If no version it knows is there, the runtime does not throw - it prints
    /// "No usable version of libssl was found" and ABORTS THE PROCESS, and no catch can stop it. Fedora
    /// 45 did exactly that (2026-09-21): it ships only libssl.so.4, and the check six seconds after
    /// launch took the player down mid-song. So look first, with the names the runtime itself tries
    /// (dotnet/runtime opensslshim.c), and treat "none" as a reason not to update rather than a crash.
    /// <c>CLR_OPENSSL_VERSION_OVERRIDE</c> is the runtime's own escape hatch and is honoured the same way.
    /// Cached: a library appearing mid-session would not help, the runtime has already looked.
    /// </summary>
    private static readonly Lazy<string?> MissingTls = new(() =>
    {
        if (!OperatingSystem.IsLinux()) return null;
        var names = new List<string>();
        if (Environment.GetEnvironmentVariable("CLR_OPENSSL_VERSION_OVERRIDE") is { Length: > 0 } v)
            names.Add($"libssl.so.{v}");
        names.AddRange(["libssl.so.3", "libssl.so.1.1", "libssl.so.1.0.2", "libssl.so.1.0.0", "libssl.so.10"]);
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return null;
            }
        Console.WriteLine($"[update] no libssl .NET can use (tried {string.Join(", ", names)}); updates are off");
        return "This computer has no version of OpenSSL that .NET can use, so AlbumWall cannot check for "
             + "updates. On Fedora, installing openssl3-libs fixes it.";
    });

    public static async Task<UpdateInfo> CheckAsync(bool edge = false)
    {
        var info = new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            ReleaseUrl = App.ProjectUrl + "/releases/latest"
        };
        if (MissingTls.Value is { } why)
        {
            info.Error = why;
            return info;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl(edge));
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
            JsonElement root;
            if (!edge) root = doc.RootElement;
            else if (ReleaseFeed.Newest(doc.RootElement) is { } newest) root = newest;
            else
            {
                info.NothingPublished = true;
                Console.WriteLine("[update] edge: no app releases listed");
                return info;
            }
            if (edge) info.IsPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            info.LatestTag = root.GetProperty("tag_name").GetString() ?? "";
            if (root.TryGetProperty("html_url", out var hu) && hu.GetString() is { Length: > 0 } url)
                info.ReleaseUrl = url;

            // The Native AOT package first, the name 0.8 ships under (a program with its
            // libraries beside it), else the single file. Copies before the bridge release do
            // not look for the -aot name, and say the release has no build for them.
            string[] wanted = [$"AlbumWall-{Rid()}-aot.zip", $"AlbumWall-{Rid()}.zip"];
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var link = a.GetProperty("browser_download_url").GetString();
                    var rank = Array.IndexOf(wanted, name);
                    if (rank >= 0 && (info.AssetName is null || rank < Array.IndexOf(wanted, info.AssetName)))
                    { info.AssetUrl = link; info.AssetName = name; }
                    else if (name == "SHA256SUMS") info.SumsUrl = link;
                }
            }

            // Unparseable on either side is "not newer", never "newer": see VersionOrder.
            info.UpdateAvailable = VersionOrder.IsNewer(info.LatestTag, CurrentVersion);
            Console.WriteLine($"[update] {(edge ? "edge" : "stable")}: latest {info.LatestTag}{(info.IsPrerelease ? " (edge build)" : "")}, have {CurrentVersion}, "
                            + $"newer={info.UpdateAvailable}, asset={info.AssetName ?? "none for " + Rid()}");
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
    /// A new private folder each time (mode 0700 on Linux, unique name), never a fixed name in a
    /// shared temp directory, where another account could make it first and swap the program
    /// between the hash check and the copy (security review 2026-10-10; Deadwax 0.2.10 likewise).
    private static string? _stageRoot;

    private static string StageRoot => _stageRoot ?? throw new InvalidOperationException("Nothing has been staged.");

    /// <summary>
    /// Downloads the release's zip, checks it against the release's SHA256SUMS, unpacks it, and returns
    /// the staged executable. <paramref name="progress"/> is told the fraction downloaded, 0 to 1.
    /// </summary>
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (MissingTls.Value is { } why) throw new InvalidOperationException(why);
        if (info.AssetUrl is null || info.AssetName is null)
            throw new InvalidOperationException($"This release has no build for {Rid()}.");
        if (info.SumsUrl is null)
            throw new InvalidOperationException("This release publishes no SHA256SUMS, so its download "
                                              + "cannot be checked. It has not been installed.");
        // Both links checked before anything is made on disk, so a refusal leaves nothing behind.
        Request(info.SumsUrl).Dispose();
        Request(info.AssetUrl).Dispose();

        var tmp = Directory.CreateTempSubdirectory("AlbumWall-update-").FullName;
        _stageRoot = tmp;
        // None left by a download that failed: every failure but a bad hash left its zip in
        // temp, one more per try (security review 2026-10-26, INFO; Deadwax's the same).
        try
        {
            return await StageAsync(info, tmp, progress);
        }
        catch
        {
            _stageRoot = null;
            try { Directory.Delete(tmp, recursive: true); } catch { /* temp's to tidy */ }
            throw;
        }
    }

    /// <summary>The download proper; AssetUrl, AssetName and SumsUrl are checked above.</summary>
    private static async Task<string> StageAsync(UpdateInfo info, string tmp, IProgress<double>? progress)
    {

        // The list first: it is 300 bytes, and without the zip's line in it there is no point
        // fetching fifty megabytes.
        string sums;
        using (var req = Request(info.SumsUrl))
        using (var resp = await Http.SendAsync(req))
        {
            resp.EnsureSuccessStatusCode();
            CheckLanded(resp);
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
            CheckLanded(resp);
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
        if (OperatingSystem.IsMacOS())
        {
            // The whole bundle, checked for our signature, Apple's notarization and its version.
            var app = MacUpdate.UnpackAndVerify(zip, unpacked, info.LatestTag, CurrentVersion);
            File.Delete(zip);
            Console.WriteLine($"[update] {app}: signed by {MacUpdate.TeamId}, notarized, version {info.LatestTag}");
            return app;
        }
        ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
        File.Delete(zip);

        var staged = FindProgram(unpacked, InstallService.ExeFileName)
            ?? throw new FileNotFoundException($"{InstallService.ExeFileName} is not in the downloaded package.");

        // A last look before it is given the keys: the right kind of file, and not a stub; and the
        // same for each native library a Native AOT build has beside it.
        if (!await IsBinaryForThisSystemAsync(staged) || new FileInfo(staged).Length < 1_000_000)
            throw new InvalidOperationException("The downloaded program is not an executable for this system.");
        var folder = Path.GetDirectoryName(staged)!;
        foreach (var lib in InstallLayout.NativeLibrariesIn(folder))
            if (!await IsBinaryForThisSystemAsync(Path.Combine(folder, lib)))
                throw new InvalidOperationException($"The downloaded {lib} is not a library for this system.");

        return staged;
    }

    /// <summary>The program at the top of the package, else the first file named exactly so below
    /// it: a search that ignores case (Windows, the Mac) could take another file of a similar name
    /// for it (Deadwax 836b46f).</summary>
    public static string? FindProgram(string unpacked, string exeName)
    {
        bool Named(string f) => Path.GetFileName(f) == exeName;
        return Directory.GetFiles(unpacked, exeName).FirstOrDefault(Named)
            ?? Directory.GetFiles(unpacked, exeName, SearchOption.AllDirectories).FirstOrDefault(Named);
    }

    /// <summary>ELF on Linux, PE ("MZ") on Windows, by the first bytes.</summary>
    private static async Task<bool> IsBinaryForThisSystemAsync(string path)
    {
        var head = new byte[4];
        await using (var fs = File.OpenRead(path)) _ = await fs.ReadAsync(head);
        return OperatingSystem.IsWindows() ? head[0] == 'M' && head[1] == 'Z'
                                           : head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
    }

    private static HttpRequestMessage Request(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !MayFetch(u))
            throw new InvalidOperationException($"The release points its download at {url}, not at GitHub. Nothing was installed.");
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
        // Relaunch as launched: a kiosk's copy runs with --front-panel and must come back with it.
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // The Mac: the whole bundle, by renames, in a helper of its own (MacUpdate).
        if (OperatingSystem.IsMacOS())
        {
            var app = MacUpdate.AppBundle(target) ?? throw new InvalidOperationException("This copy is not inside AlbumWall.app.");
            MacUpdate.Start(pid, stagedExe, app, marker, StageRoot, Directory.CreateTempSubdirectory("albumwall-apply-").FullName);
            Console.WriteLine($"[update] helper started; replacing {app} once pid {pid} has gone");
            return;
        }

        var from = Path.GetDirectoryName(stagedExe)!;
        var exeName = InstallService.ExeFileName;
        var libraries = InstallLayout.NativeLibrariesIn(from);
        var oldx = InstallService.OwnExtractionDir;
        // The helper has a private folder of its own, not the staging folder: it deletes that one
        // first, and its own last (a script cannot sit in the folder it is removing).
        var helperDir = Directory.CreateTempSubdirectory("albumwall-apply-").FullName;

        // SUPERVISED (ALBUMWALL_SUPERVISED=1, set by a kiosk's launcher): something else restarts the
        // app when it exits, so the helper must not, or two copies run - which is what the Pi kiosk
        // got from the first update it took (2026-09-28: two walls). And the new build has to be in
        // place BEFORE this process exits, because the supervisor starts whatever is there a second
        // later. Linux lets a running program's files be replaced: each is copied beside itself and
        // renamed over the old one, which is atomic; this process keeps running from the old ones
        // until it exits. What it replaces is kept in previous/, which the launcher puts back if
        // the new build keeps falling over (kiosk/albumwall-kiosk; his call, 2026-10-27).
        if (Supervised && !OperatingSystem.IsWindows())
        {
            InstallBeside(from, targetDir, libraries.Append(exeName).ToList(), exeName);
            if (File.Exists(marker)) File.Delete(marker);
            var tidy = Path.Combine(helperDir, "tidy.sh");
            File.WriteAllText(tidy, UpdateApplyScript.UnixSupervised + "\n");
            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { tidy, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), oldx ?? "", StageRoot, helperDir },
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            Console.WriteLine($"[update] supervised: {exeName}{(libraries.Count > 0 ? " and " + string.Join(", ", libraries) : "")} "
                            + $"put in place in {targetDir}, the old ones in {Path.Combine(targetDir, PreviousFolder)}; the supervisor restarts it");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(helperDir, "apply-update.ps1");
            File.WriteAllText(ps1, UpdateApplyScript.Windows(pid, from, targetDir, exeName, libraries, marker,
                                                             StageRoot, oldx, helperDir, args), Domain.PowerShellText.FileEncoding);
            Process.Start(new ProcessStartInfo(InstallService.PowerShellExe)
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", ps1 },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),      // not ours: see the class comment
            });
        }
        else
        {
            var sh = Path.Combine(helperDir, "apply-update.sh");
            File.WriteAllText(sh, UpdateApplyScript.Unix + "\n");
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            foreach (var a in UpdateApplyScript.UnixArguments(sh, pid, from, targetDir, exeName, libraries, marker,
                                                              StageRoot, oldx, helperDir, args))
                start.ArgumentList.Add(a);
            Process.Start(start);
        }
        Console.WriteLine($"[update] helper started; installing {exeName}{(libraries.Count > 0 ? " and " + string.Join(", ", libraries) : "")} "
                        + $"into {targetDir} once pid {pid} has gone; then removing {oldx ?? "(nothing unpacked)"}");
    }

    /// <summary>Where a supervised update keeps the build it replaced, beside the program.</summary>
    public const string PreviousFolder = "previous";

    /// <summary>
    /// A supervised update, done by the app itself before it exits: the files it will replace are
    /// copied into previous/ (a file not there before is noted as name.absent, so putting the old
    /// build back removes it), then the libraries and last the program are copied in, each beside
    /// itself and renamed over the old one. Anything failing puts the old files back and throws,
    /// and the app keeps running the build it has.
    /// </summary>
    private static void InstallBeside(string from, string to, IReadOnlyList<string> names, string exeName)
    {
        var previous = Path.Combine(to, PreviousFolder);
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        Directory.CreateDirectory(previous);
        foreach (var n in names)
        {
            var t = Path.Combine(to, n);
            if (File.Exists(t)) File.Copy(t, Path.Combine(previous, n));
            else File.WriteAllText(Path.Combine(previous, n + ".absent"), "");
        }

        void Put(string source, string n)
        {
            var fresh = Path.Combine(to, "." + n + ".new");
            File.Copy(source, fresh, overwrite: true);
            if (n == exeName)
                File.SetUnixFileMode(fresh, File.GetUnixFileMode(fresh) | UnixFileMode.UserExecute
                                           | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            File.Move(fresh, Path.Combine(to, n), overwrite: true);
        }

        try
        {
            foreach (var n in names) Put(Path.Combine(from, n), n);     // the program is last in names
        }
        catch
        {
            foreach (var n in names)
            {
                try
                {
                    var t = Path.Combine(to, n);
                    if (File.Exists(Path.Combine(previous, n + ".absent"))) File.Delete(t);
                    else if (File.Exists(Path.Combine(previous, n))) Put(Path.Combine(previous, n), n);
                    File.Delete(Path.Combine(to, "." + n + ".new"));
                }
                catch { /* the rest still goes back */ }
            }
            throw;
        }
    }

    /// <summary>Set by a kiosk's launcher: something restarts the app when it exits.</summary>
    public static bool Supervised => Environment.GetEnvironmentVariable("ALBUMWALL_SUPERVISED") == "1";

    /// <summary>Beside the program; on the Mac beside the settings instead, since a file put inside
    /// the signed bundle would break its seal.</summary>
    private static string FailedMarkerPath(string targetExe) => OperatingSystem.IsMacOS()
        ? Path.Combine(InstallService.DataDir, ".albumwall-update-failed")
        : Path.Combine(Path.GetDirectoryName(targetExe) ?? ".", ".albumwall-update-failed");

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
