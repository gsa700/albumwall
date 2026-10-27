#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj

// AlbumWall - the two pieces of the updater that are pure logic, checked.
//
//   dotnet run tools/update-check.cs
//
// 1. VersionOrder: which of two versions is newer. The cases are the family's unit tests
//    (FlexPad.Core.Tests/VersionOrderTests.cs), this repository having check tools where the
//    family has a test project. Getting this wrong has two shapes and both are bad: nobody is
//    ever offered the update, or everybody is offered a downgrade.
// 2. UpdateApplyScript: the helpers that put a new build in place. The Windows one READ for what it
//    must contain; the Linux one RUN, against stand-in programs in a temp folder (a package goes in
//    whole; one that falls over is taken out again; one that cannot be copied leaves the old files),
//    and the kiosk launcher's rollback run the same way. The end-to-end test is a published build
//    against a local feed; docs/windows-notes.md says how.
//
// Exit code 0 when everything holds.

using AlbumWall.Domain;

var failures = 0;
void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("FAIL  " + what); } }
void Newer(string newer, string older)
{
    Check(VersionOrder.IsNewer(newer, older), $"{newer} should be newer than {older}");
    Check(!VersionOrder.IsNewer(older, newer), $"{older} should NOT be newer than {newer}");
}
void Same(string a, string b)
{
    Check(VersionOrder.Compare(a, b) == 0, $"{a} and {b} should be the same version");
    Check(!VersionOrder.IsNewer(a, b) && !VersionOrder.IsNewer(b, a), $"neither of {a} / {b} is newer");
}

// ---- higher numbers win, as numbers
Newer("0.10.0-beta", "0.9.0-beta");         // the numeric trap: 10 > 9, not "1" < "9"
Newer("1.0.0-beta1", "0.10.0-beta");
Newer("0.9.1-beta", "0.9.0-beta");
Newer("2.0.0", "1.9.9");
Newer("0.1.1", "0.1.0");                     // the pair the end-to-end test uses

// ---- pre-release suffixes order by their trailing number
Newer("1.0.0-beta2", "1.0.0-beta1");
Newer("1.0.0-beta10", "1.0.0-beta2");        // lexically "beta10" < "beta2"; numerically it is not
Newer("1.0.0-beta.2", "1.0.0-beta.1");
Newer("1.0.0-beta1", "1.0.0-alpha9");
Newer("1.0.0-beta1", "1.0.0-beta");          // a bare suffix precedes a numbered one

// ---- the real release beats its own pre-releases: the case a truncating comparer gets wrong
Newer("1.0.0", "1.0.0-beta9");
Newer("0.1.0", "0.1.0-test3");               // this project's own test builds
Newer("0.1.0-test4", "0.1.0-test3");

// ---- equivalent spellings
Same("1.0.0", "1.0.0");
Same("v1.0.0", "1.0.0");                     // tags carry a leading v, assemblies do not
Same("1.0.0-beta1", "v1.0.0-beta1");
Same("1.0", "1.0.0");                        // a missing field is zero
Same("1.0.0+abc123", "1.0.0");               // build metadata is not part of the order
Same("0.1.0+50302c7f44b416e6b53163a9e8a1a810c4208acf", "v0.1.0");   // what an assembly really reports

// ---- unparseable is "don't know", never "newer"
foreach (var (a, b) in new (string?, string?)[] { (null, "1.0.0"), ("1.0.0", null), ("", "1.0.0"),
                                                   ("not-a-version", "1.0.0"), ("1.0.0", "garbage"),
                                                   ("libmpv-0.41.0-3", "0.1.0") })
{
    Check(VersionOrder.Compare(a, b) is null, $"'{a}' vs '{b}' should be unknown");
    Check(!VersionOrder.IsNewer(a, b), $"'{a}' must not count as newer than '{b}'");
}

// ---- a whole series, the way a person would order it
string[] series = ["0.9.0-beta", "0.10.0-beta", "1.0.0-alpha1", "1.0.0-beta", "1.0.0-beta1", "1.0.0-beta2",
                   "1.0.0-beta10", "1.0.0-rc1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
for (var i = 1; i < series.Length; i++) Newer(series[i], series[i - 1]);

// ---- the Windows helper: what it must contain, and must not (it runs on Windows only)
const string Odd = @"C:\Users\Sean O’Brien\AppData\Local\Programs\AlbumWall";
var win = UpdateApplyScript.Windows(4242, @"C:\Temp\AlbumWall-update\ex", Odd, "AlbumWall.exe",
                                    ["libSkiaSharp.dll", "e_sqlite3.dll"], Odd + @"\.failed", @"C:\Temp\AlbumWall-update",
                                    @"C:\Temp\.net\AlbumWall\AbC123", @"C:\Temp\albumwall-apply-x", ["--front-panel", "a b"]);
Check(win.Contains("Get-Process -Id 4242"), "windows: waits for the pid");
Check(win.Contains("$i -lt 40") && win.Contains("-ErrorAction Stop"), "windows: the copy is retried, and its failure is catchable");
Check(win.Contains("O’’Brien") && !win.Contains("O’Brien"), "windows: a curly apostrophe in a path is doubled, never raw");
Check(win.Contains("@('libSkiaSharp.dll', 'e_sqlite3.dll', 'AlbumWall.exe')"), "windows: the libraries first, the program last");
Check(win.Contains("function Restore") && win.Contains("$p.ExitCode -ne 0"), "windows: falls back when the new program falls over");
Check(win.Contains(@"*\AbC123\*") && win.Contains("$inUse"), "windows: the unpacked folder is removed only if no running copy uses it");
Check(!win.Contains(@"-LiteralPath 'C:\Temp\.net\AlbumWall' "), "windows: never removes the unpacking ROOT");
Check(win.Contains("-ArgumentList '--front-panel \"a b\"'"), "windows: relaunches with its arguments, one with a space kept whole");
Check(win.Contains("-WorkingDirectory $to"), "windows: the relaunch is given the install folder to start in");
// Written as UTF-8 WITH its byte-order mark: Windows PowerShell 5.1 reads a file without one as
// ANSI, and a curly apostrophe in the path came out as three characters (Mobilenode, 2026-10-27).
Check(PowerShellText.FileEncoding.GetPreamble() is [0xEF, 0xBB, 0xBF], "windows: a .ps1 is written with the UTF-8 byte-order mark");
var none = UpdateApplyScript.Windows(1, "a", "b", "c", [], "d", "e", null, "f", []);
Check(!none.Contains("$inUse") && !none.Contains(".net") && !none.Contains("-ArgumentList"), "windows: unpacked nothing, removes nothing; no arguments, passes none");

// ---- the Linux helper, RUN, against stand-in programs (a shell script named AlbumWall)
if (!OperatingSystem.IsWindows())
{
    var root = Directory.CreateTempSubdirectory("update-check-").FullName;
    string Stand(string dir, string say, int exit = 0, int lasts = 0) =>
        Write(dir, "AlbumWall", $"#!/bin/sh\necho {say} >> \"{root}/ran\"\n{(lasts > 0 ? $"sleep {lasts}\n" : "")}exit {exit}\n", exec: true);
    string Write(string dir, string name, string text, bool exec = false)
    {
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, name);
        File.WriteAllText(f, text);
        if (exec) File.SetUnixFileMode(f, (UnixFileMode)0b111_101_101);
        return f;
    }
    string Read(string f) => File.Exists(f) ? File.ReadAllText(f) : "(none)";
    void Run(string name, string from, string to, string[] libs)
    {
        File.Delete(Path.Combine(root, "ran"));
        var stage = Directory.CreateDirectory(Path.Combine(root, "stage-" + name)).FullName;
        var helper = Directory.CreateDirectory(Path.Combine(root, "helper-" + name)).FullName;
        var sh = Write(helper, "apply-update.sh", UpdateApplyScript.Unix + "\n");
        using var gone = System.Diagnostics.Process.Start("/bin/sh", "-c true")!;
        gone.WaitForExit();
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var a in UpdateApplyScript.UnixArguments(sh, gone.Id, from, to, "AlbumWall", libs,
                                                          Path.Combine(to, ".failed"), stage, null, helper, ["--front-panel"], 2))
            start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!;
        p.WaitForExit(30_000);
        Thread.Sleep(500);
        Check(!Directory.Exists(stage) && !Directory.Exists(helper), $"unix {name}: the staging and helper folders are gone");
    }

    // A package goes in whole: the libraries and the program, the new program started and still up.
    var to = Path.Combine(root, "install");
    Stand(to, "old"); Write(to, "libSkiaSharp.so", "old lib");
    var from = Path.Combine(root, "new");
    Stand(from, "new", lasts: 4); Write(from, "libSkiaSharp.so", "new lib"); Write(from, "libe_sqlite3.so", "new sqlite");
    Run("good", from, to, ["libSkiaSharp.so", "libe_sqlite3.so"]);
    Check(Read(Path.Combine(to, "AlbumWall")).Contains("echo new"), "unix good: the new program is in place");
    Check(Read(Path.Combine(to, "libSkiaSharp.so")) == "new lib" && Read(Path.Combine(to, "libe_sqlite3.so")) == "new sqlite",
          "unix good: the libraries are, the one that was not there before too");
    Check(Read(Path.Combine(root, "ran")).Trim() == "new", "unix good: the new program was started, once");
    Check(!File.Exists(Path.Combine(to, ".failed")), "unix good: no failure marker");

    // A program that falls over at once is taken out again: the old files back, the old program started.
    to = Path.Combine(root, "install2");
    Stand(to, "old", lasts: 1); Write(to, "libSkiaSharp.so", "old lib");
    from = Path.Combine(root, "new2");
    Stand(from, "broken", exit: 3); Write(from, "libSkiaSharp.so", "new lib"); Write(from, "libe_sqlite3.so", "new sqlite");
    Run("falls-over", from, to, ["libSkiaSharp.so", "libe_sqlite3.so"]);
    Check(Read(Path.Combine(to, "AlbumWall")).Contains("echo old"), "unix falls-over: the old program is back");
    Check(Read(Path.Combine(to, "libSkiaSharp.so")) == "old lib", "unix falls-over: the old library is back");
    Check(!File.Exists(Path.Combine(to, "libe_sqlite3.so")), "unix falls-over: a library that was not there is gone again");
    Check(Read(Path.Combine(root, "ran")).Replace("\n", " ").Trim() == "broken old", "unix falls-over: the new one tried, then the old one started");
    Check(File.Exists(Path.Combine(to, ".failed")), "unix falls-over: the failure marker is set");

    // A package that cannot be copied leaves the old files, and never tries the new program.
    to = Path.Combine(root, "install3");
    Stand(to, "old", lasts: 1); Write(to, "libSkiaSharp.so", "old lib");
    from = Path.Combine(root, "new3");
    Stand(from, "new"); Write(from, "libSkiaSharp.so", "new lib");     // libe_sqlite3.so is missing
    Run("cannot-copy", from, to, ["libSkiaSharp.so", "libe_sqlite3.so"]);
    Check(Read(Path.Combine(to, "AlbumWall")).Contains("echo old") && Read(Path.Combine(to, "libSkiaSharp.so")) == "old lib",
          "unix cannot-copy: the old files stay");
    Check(Read(Path.Combine(root, "ran")).Trim() == "old", "unix cannot-copy: only the old program started");
    Check(File.Exists(Path.Combine(to, ".failed")), "unix cannot-copy: the failure marker is set");

    // ---- the kiosk launcher's rollback (kiosk/albumwall-kiosk, his call 2026-10-27), RUN: the
    // installed build exits at once, previous/ holds the one before; after two quick exits the
    // launcher puts that back, sets the marker, and runs it.
    var kiosk = Path.Combine(AppContext.GetData("EntryPointFilePath") is string me ? Path.GetDirectoryName(me)! : ".", "..", "kiosk", "albumwall-kiosk");
    if (File.Exists(kiosk))
    {
        var inst = Path.Combine(root, "kiosk", "albumwall");
        Stand(inst, "broken", exit: 1);
        Thread.Sleep(1100);                                              // previous/ newer than the install
        Stand(Path.Combine(inst, "previous"), "previous", lasts: 30);
        Write(inst, "libSkiaSharp.so", "new lib");
        Write(Path.Combine(inst, "previous"), "libSkiaSharp.so.absent", "");
        File.Delete(Path.Combine(root, "ran"));
        var k = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true };
        k.ArgumentList.Add(kiosk); k.ArgumentList.Add("--inside");
        k.Environment["ALBUMWALL_KIOSK_APP"] = Path.Combine(inst, "AlbumWall");
        k.Environment["HOME"] = Path.Combine(root, "home");               // no kiosk.conf: no screens to set
        using var loop = System.Diagnostics.Process.Start(k)!;
        Thread.Sleep(5000);
        Check(Read(Path.Combine(inst, "AlbumWall")).Contains("echo previous"), "kiosk: the previous build is back");
        Check(!File.Exists(Path.Combine(inst, "libSkiaSharp.so")), "kiosk: a library the previous build did not have is gone");
        Check(File.Exists(Path.Combine(inst, ".albumwall-update-failed")), "kiosk: the marker tells the app");
        Check(!Directory.Exists(Path.Combine(inst, "previous")), "kiosk: previous/ is used up");
        Check(Read(Path.Combine(root, "ran")).Replace("\n", " ").Trim() == "broken broken previous", "kiosk: two quick exits, then the previous build runs");
        System.Diagnostics.Process.Start("pkill", ["-f", Path.Combine(inst, "AlbumWall")])!.WaitForExit();
        loop.Kill(entireProcessTree: true);
    }
    else Console.WriteLine($"(kiosk launcher not found at {kiosk}; its check skipped)");
    Directory.Delete(root, recursive: true);
}

// 3. ReleaseFeed: what the Edge channel offers from GitHub's /releases list (2026-10-10).
string? Edge(string json)
{
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    return ReleaseFeed.Newest(doc.RootElement)?.GetProperty("tag_name").GetString();
}
Check(Edge("""
    [{"tag_name":"v0.6.6","prerelease":false},{"tag_name":"v0.6.7","prerelease":true},{"tag_name":"v0.6.5"}]
    """) == "v0.6.7", "edge: the newest version, pre-release or not");
Check(Edge("""
    [{"tag_name":"libmpv-0.41.0-4","prerelease":true},{"tag_name":"v0.6.6","prerelease":false}]
    """) == "v0.6.6", "edge: a libmpv engine release is never offered, however its numbers compare");
Check(Edge("""
    [{"tag_name":"v0.6.8","prerelease":true,"draft":true},{"tag_name":"v0.6.6"}]
    """) == "v0.6.6", "edge: never a draft");
Check(Edge("""[{"tag_name":"v0.6.9"},{"tag_name":"v0.6.10"}]""") == "v0.6.10", "edge: numbers compare as numbers");
Check(Edge("[]") is null && Edge("""{"message":"Not Found"}""") is null, "edge: nothing listed is null");
Check(!ReleaseFeed.IsAppTag("v0.6.7-beta1") && !ReleaseFeed.IsAppTag("0.6.7") && ReleaseFeed.IsAppTag("v0.6.7"),
      "edge: only vX.Y.Z is an app release");

Console.WriteLine(failures == 0 ? "PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
