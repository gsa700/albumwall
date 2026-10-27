#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj

// AlbumWall - "Show in file manager" on the Mac, checked (security review 2026-10-26, finding 1).
//
//   dotnet run tools/reveal-check.cs
//
// In a scratch folder: an album folder named "Proof Album.app", laid out as an app whose program
// only writes a marker file. Showing the folder, and a track in it, the way the menu does
// (FileManager.Show) must never run it; the old way (opening the folder) did. Leaves a Finder window
// or two open on the scratch folder; they go when it is deleted. Exit code 0 when everything holds.

using AlbumWall.Domain;

if (!OperatingSystem.IsMacOS()) { Console.WriteLine("reveal-check: the Mac only"); return 0; }

var failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("FAIL  " + what); } else Console.WriteLine("ok    " + what); }

var tmp = Directory.CreateTempSubdirectory("reveal-check-").FullName;
var bundle = Path.Combine(tmp, "Artist", "Proof Album.app");
var marker = Path.Combine(tmp, "MARKER");
try
{
    var macos = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS")).FullName;
    File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"),
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>CFBundleExecutable</key><string>run</string>
        <key>CFBundleIdentifier</key><string>io.github.gsa700.AlbumWall.reveal-check</string>
        <key>CFBundlePackageType</key><string>APPL</string>
        </dict></plist>
        """);
    var run = Path.Combine(macos, "run");
    File.WriteAllText(run, $"#!/bin/sh\necho ran > '{marker}'\n");
    File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var track = Path.Combine(bundle, "01 Track.wav");
    File.WriteAllText(track, "");

    Check(!FileManager.MayOpenFolders, "folders are never handed to the desktop to open on the Mac");
    Check(FileManager.Show(bundle), "the album folder is shown");
    Check(FileManager.Show(track), "a track in it is shown");
    Thread.Sleep(3000);   // a launched app would have written its marker by now
    Check(!File.Exists(marker), "showing an album folder named .app does not run it");
}
finally
{
    // Forget the scratch "app" again, then delete it.
    try
    {
        using var ls = System.Diagnostics.Process.Start(
            "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
            ["-u", bundle]);
        ls?.WaitForExit(5000);
    }
    catch (Exception) { }
    Directory.Delete(tmp, recursive: true);
}

Console.WriteLine(failures == 0 ? "reveal-check: all good" : $"reveal-check: {failures} FAILED");
return failures == 0 ? 0 : 1;
