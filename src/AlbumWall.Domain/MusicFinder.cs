// AlbumWall - "Look in the usual places", on the first run's welcome.
//
// Asked for, never done unasked: "lets not scan at all, instead offer to scan
// for common locations" (2026-10-04). So nothing here runs until the button is
// pressed, and what it does then is COUNT, from directory listings, and open no
// file. On Windows that is the difference between seconds and Defender opening
// every track; anywhere, it is the difference between a glance and a scan.
//
// The usual places are the ones a person would name without thinking: the
// Music folder, OneDrive's, a Music folder at the top of a drive; on Linux the
// mounted drives as well. NOT network shares - mapped drives and the like - which
// can take minutes to list and are better chosen with the folder picker.

namespace AlbumWall.Domain;

public static class MusicFinder
{
    /// A place that holds music, as counted: `Albums` is the number of folders
    /// with audio in them, which is what an album usually is and why the welcome
    /// says "about".
    public sealed record Found(string Path, string Label, int Albums, int Tracks);

    /// Below this many tracks a folder is not offered: a Music folder holding a
    /// couple of system sounds is not a library.
    public const int MinTracks = 3;

    /// Where to look, each with what to call it. Each place once, however many
    /// ways there are to reach it (the Music folder is often OneDrive's).
    public static IReadOnlyList<(string Path, string Label)> UsualPlaces()
    {
        var places = new List<(string, string)>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar); } catch { return; }
            if (seen.Add(path) && Directory.Exists(path)) places.Add((path, label));
        }

        // A test run can name its own places, so the button can be tried
        // without whatever this machine happens to hold.
        if (Environment.GetEnvironmentVariable("ALBUMWALL_USUAL_PLACES") is { Length: > 0 } forTest)
        {
            foreach (var path in forTest.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                Add(path, System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
            return places;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music");
        Add(System.IO.Path.Combine(home, "Music"), "Music");

        if (OperatingSystem.IsWindows())
        {
            foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                Add(Environment.GetEnvironmentVariable(variable) is { Length: > 0 } od ? System.IO.Path.Combine(od, "Music") : null,
                    "OneDrive Music");
            foreach (var drive in SafeDrives())
                Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "Music"),
                    $"Music on {drive.Name.TrimEnd('\\', '/')}");
        }
        else
        {
            // Where desktops mount a drive that is plugged in, and where people
            // mount the ones they put in themselves.
            var user = Environment.UserName;
            foreach (var parent in new[] { $"/media/{user}", $"/run/media/{user}", "/media", "/mnt" })
                foreach (var mount in SafeDirectories(parent))
                    Add(System.IO.Path.Combine(mount, "Music"), $"Music on {System.IO.Path.GetFileName(mount)}");
        }
        return places;
    }

    /// Counts what is under one place without opening anything. Null when it
    /// holds too little to be a library, or cannot be listed.
    public static Found? Count(string path, string label, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };
        var albums = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var tracks = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!LibraryScanner.AudioExtensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                tracks++;
                albums.Add(System.IO.Path.GetDirectoryName(file) ?? "");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        return tracks >= MinTracks ? new Found(path, label, albums.Count, tracks) : null;
    }

    /// Every usual place that holds music, counted side by side.
    public static async Task<IReadOnlyList<Found>> LookAsync(CancellationToken ct = default)
    {
        var places = UsualPlaces();
        var counted = await Task.WhenAll(places.Select(p => Task.Run(() => Count(p.Path, p.Label, ct), ct)));
        return counted.OfType<Found>().ToList();
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var drive in drives)
        {
            bool usable;
            try { usable = drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady; }
            catch { usable = false; }
            if (usable) yield return drive;
        }
    }

    private static IEnumerable<string> SafeDirectories(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : []; }
        catch { return []; }
    }
}
