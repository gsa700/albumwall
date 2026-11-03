using System.Diagnostics;

namespace AlbumWall.Tests;

/// The kiosk launcher's rollback (kiosk/albumwall-kiosk, his call 2026-10-27),
/// RUN: the installed build exits at once and previous/ holds the one before it
/// (as a supervised update leaves it, Spindle's UpdateService.InstallBeside);
/// after two quick exits the launcher puts that back, sets the marker the app
/// reads (.albumwall-update-failed, Spindle's name for it), and runs it. From
/// tools/update-check.cs, whose other checks are Spindle's tests now.
public sealed class KioskLauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("albumwall-kiosk-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string? Launcher()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "kiosk", "albumwall-kiosk");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private string Stand(string dir, string say, int exit = 0, int lasts = 0) =>
        Write(dir, "AlbumWall", $"#!/bin/sh\necho {say} >> \"{_root}/ran\"\n{(lasts > 0 ? $"sleep {lasts}\n" : "")}exit {exit}\n", exec: true);

    private static string Write(string dir, string name, string text, bool exec = false)
    {
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, name);
        File.WriteAllText(f, text);
        if (exec && !OperatingSystem.IsWindows()) File.SetUnixFileMode(f, (UnixFileMode)0b111_101_101);
        return f;
    }

    private static string Read(string f) => File.Exists(f) ? File.ReadAllText(f) : "(none)";

    [Fact]
    public void Two_quick_exits_put_the_previous_build_back()
    {
        if (OperatingSystem.IsWindows()) return;          // a shell launcher, for the Pi
        var kiosk = Launcher();
        Assert.NotNull(kiosk);

        var inst = Path.Combine(_root, "kiosk", "albumwall");
        Stand(inst, "broken", exit: 1);
        Thread.Sleep(1100);                                              // previous/ newer than the install
        Stand(Path.Combine(inst, "previous"), "previous", lasts: 30);
        Write(inst, "libSkiaSharp.so", "new lib");
        Write(Path.Combine(inst, "previous"), "libSkiaSharp.so.absent", "");

        var k = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        k.ArgumentList.Add(kiosk!);
        k.ArgumentList.Add("--inside");
        k.Environment["ALBUMWALL_KIOSK_APP"] = Path.Combine(inst, "AlbumWall");
        k.Environment["HOME"] = Path.Combine(_root, "home");             // no kiosk.conf: no screens to set
        using var loop = Process.Start(k)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !Read(Path.Combine(_root, "ran")).Contains("previous")) Thread.Sleep(200);

            Assert.Contains("echo previous", Read(Path.Combine(inst, "AlbumWall")));
            Assert.False(File.Exists(Path.Combine(inst, "libSkiaSharp.so")));      // a library the previous build did not have
            Assert.True(File.Exists(Path.Combine(inst, ".albumwall-update-failed")));
            Assert.False(Directory.Exists(Path.Combine(inst, "previous")));        // used up
            Assert.Equal("broken broken previous", Read(Path.Combine(_root, "ran")).Replace("\n", " ").Trim());
        }
        finally
        {
            Process.Start("pkill", ["-f", Path.Combine(inst, "AlbumWall")])!.WaitForExit();
            try { loop.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
