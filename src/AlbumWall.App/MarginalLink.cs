// AlbumWall — the way to Marginal, the family's tag, art and lyrics editor.
//
// AlbumWall is for listening; editing an album is Marginal's (decided
// 2026-10-21: "I don't want one huge app that tries to do it all"). Joined,
// not merged: the album menu has "Edit in Marginal…" when Marginal is
// installed, and "Get Marginal…" (its releases page) when it is not (Dave's
// idea, 2026-10-31). Each app works without the other.
//
// Marginal is found where the family installs (the same layout as
// AlbumWall's and Deadwax's): /Applications/Marginal.app (or ~/Applications)
// on the Mac, %LOCALAPPDATA%\Programs\Marginal on Windows,
// ~/.local/share/marginal on Linux. MARGINAL_PATH may name a build instead,
// for trying one: a full path to a file named Marginal, nothing else.
//
// It is started with the album's folder and "--from albumwall", by its full
// path and never through a shell (ToolPath's lesson: nothing found in the
// current folder is ever run).

using System.Diagnostics;

namespace AlbumWall.App;

internal static class MarginalLink
{
    public const string ReleasesPage = "https://github.com/gsa700/marginal/releases";

    /// Marginal's executable, or null when it is not installed.
    public static string? Find()
    {
        var exe = OperatingSystem.IsWindows() ? "Marginal.exe" : "Marginal";
        if (Environment.GetEnvironmentVariable("MARGINAL_PATH") is { Length: > 0 } custom
            && Path.IsPathFullyQualified(custom) && Path.GetFileName(custom).Equals(exe, StringComparison.Ordinal)
            && File.Exists(custom))
            return custom;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        IEnumerable<string> candidates =
            OperatingSystem.IsMacOS() ? ["/Applications/Marginal.app/Contents/MacOS/Marginal", Path.Combine(home, "Applications", "Marginal.app", "Contents", "MacOS", "Marginal")]
            : OperatingSystem.IsWindows() ? [Path.Combine(local, "Programs", "Marginal", exe)]
            : [Path.Combine(local, "marginal", exe)];
        return candidates.FirstOrDefault(p => Path.IsPathFullyQualified(p) && File.Exists(p));
    }

    /// The folder to hand Marginal for an album: its one folder, or the folder
    /// its disc folders share ("Album/CD1", "Album/CD2" -> "Album").
    public static string? FolderOf(IReadOnlyCollection<string> directories)
    {
        if (directories.Count == 0) return null;
        if (directories.Count == 1) return directories.First();
        var parts = directories.Select(d => Path.GetFullPath(d).Split(Path.DirectorySeparatorChar)).ToList();
        var common = new List<string>();
        for (var i = 0; i < parts.Min(p => p.Length); i++)
        {
            var part = parts[0][i];
            if (!parts.All(p => string.Equals(p[i], part, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))) break;
            common.Add(part);
        }
        var shared = string.Join(Path.DirectorySeparatorChar, common);
        return shared.Length > 0 && Path.IsPathFullyQualified(shared + Path.DirectorySeparatorChar) ? shared : null;
    }

    /// Opens `folder` in Marginal. False if it could not be started.
    public static bool Open(string exe, string folder)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            start.ArgumentList.Add("--from");
            start.ArgumentList.Add("albumwall");
            using var process = Process.Start(start);
            Console.WriteLine($"[menu] opened {folder} in Marginal");
            return process is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[menu] Marginal would not start: {ex.Message}");
            return false;
        }
    }
}
