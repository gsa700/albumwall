// AlbumWall — asking the desktop's file manager to show a file or a folder.

using System.Diagnostics;

namespace AlbumWall.Domain;

/// <summary>
/// "Show in file manager", as each desktop does it: Explorer's /select, the Mac's `open -R`, the
/// freedesktop FileManager1 call. Each shows the item picked out in its folder and never opens it.
///
/// On the Mac that is the only safe way (security review 2026-10-26, finding 1). Opening a folder
/// there goes through Launch Services, which RUNS a folder whose name ends in .app and holds an
/// app's layout instead of showing it: an album folder "Some Album.app" from a copied library was a
/// program one click away. `open -R` reveals it, whatever its name (tools/reveal-check.cs).
/// </summary>
public static class FileManager
{
    /// Whether a folder may be handed to the desktop to open. Not on the Mac (see above): there it
    /// is always shown with <see cref="Show"/>.
    public static bool MayOpenFolders => !OperatingSystem.IsMacOS();

    /// Asks the file manager to show one file or folder selected. False if there was nobody to ask.
    public static bool Show(string path)
    {
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (OperatingSystem.IsWindows())
            {
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                start.Arguments = $"/select,\"{path}\"";
                using var explorer = Process.Start(start);
                return explorer is not null;        // its exit code says nothing either way
            }
            if (OperatingSystem.IsMacOS())
            {
                start.FileName = "/usr/bin/open";
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(path);
            }
            else
            {
                // The freedesktop way, which Files, Dolphin, Nemo and COSMIC Files
                // all answer. dbus-send splits an array on commas, and a song is
                // allowed one in its name.
                if (ToolPath.Find("dbus-send") is not { } dbusSend) return false;
                start.FileName = dbusSend;
                foreach (var arg in new[]
                {
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    "array:string:" + new Uri(path).AbsoluteUri.Replace(",", "%2C"), "string:",
                })
                    start.ArgumentList.Add(arg);
            }
            using var p = Process.Start(start);
            return p is not null && p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;       // no such program here: the folder alone will do
        }
    }
}
