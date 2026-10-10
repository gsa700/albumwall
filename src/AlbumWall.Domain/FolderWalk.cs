// AlbumWall — every file under a folder, each real folder once.

namespace AlbumWall.Domain;

/// <summary>
/// The library walk (security review 2026-10-10). .NET's own recursion follows a link to a folder
/// and never notices it has been there: a `loop -> ..` link inside a library listed the same track
/// 42 times, once per level, until the system's link limit stopped it. Links ARE followed here, since
/// an album linked into the library on purpose is part of it, but a folder reached a second time,
/// by any route, is not walked again.
/// </summary>
public static class FolderWalk
{
    public static IEnumerable<FileInfo> Files(string root, string pattern, FileAttributes skip,
                                              MatchCasing casing = MatchCasing.PlatformDefault)
    {
        var flat = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            MatchCasing = casing,
            AttributesToSkip = skip,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(DirectoryInfo Dir, string Real)>();
        var start = new DirectoryInfo(root);
        pending.Push((start, Real(start, null)));

        while (pending.Count > 0)
        {
            var (dir, real) = pending.Pop();
            if (!seen.Add(real)) continue;

            IEnumerable<FileInfo> files;
            List<DirectoryInfo> subdirs;
            try
            {
                files = dir.EnumerateFiles(pattern, flat).ToList();
                subdirs = dir.EnumerateDirectories("*", flat).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in files) yield return f;
            for (var i = subdirs.Count - 1; i >= 0; i--)
                pending.Push((subdirs[i], Real(subdirs[i], real)));
        }
    }

    /// The folder's path with every link in it resolved, by whatever route it was reached: the
    /// system's realpath() where there is one. Resolving one link at a time is not enough: a
    /// relative `..` reached through another link resolves against the route, and the route grows.
    private static string Real(DirectoryInfo dir, string? parentReal)
    {
        if (!OperatingSystem.IsWindows())
        {
            var p = realpath(dir.FullName, IntPtr.Zero);
            if (p != IntPtr.Zero)
            {
                try { return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p)!; }
                finally { free(p); }
            }
        }
        try
        {
            if (dir.LinkTarget is not null && dir.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return Path.GetFullPath(target.FullName);
        }
        catch (IOException) { /* a broken link is walked as itself, and is empty */ }
        return parentReal is null ? Path.GetFullPath(dir.FullName) : Path.Combine(parentReal, dir.Name);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern void free(IntPtr p);
}
