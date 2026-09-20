#:project ../src/AlbumWall.Domain/AlbumWall.Domain.csproj
#:package TagLibSharp@2.3.0

// AlbumWall — does a scan through the index say what an honest scan says?
//
//   dotnet run tools/index-check.cs -- <library root> <scratch dir> [edit]
//
// Scans the library four ways — no index, an empty index, that index read back
// from disk as a second launch would, and an honest scan that ignores it — and
// compares everything the wall or a panel would show. All four must agree, and
// the third must open no files. It only reads the library, and keeps its
// index.db in the scratch directory, never the app's.
//
// WHY THIS EXISTS. The index means an unchanged file is never opened again, so
// if the scanner is changed to read something differently and
// LibraryIndex.Version is not bumped, every indexed file keeps the old answer
// and nothing on screen says so. Run this after touching the scanner: "same as
// plain: False" on the reloaded line is that mistake.
//
// With `edit` it also rewrites one MP3's title at the same length and puts the
// modified time back — the edit the index cannot see from size and time — and
// shows the three outcomes that are by design: a trusting scan misses it, a
// scan after Touch() (what the watcher does) sees it, an honest scan sees it.
// THAT WRITES TO THE LIBRARY. Give it a scratch copy of a few files, never the
// real thing.

using System.Diagnostics;
using AlbumWall.Domain;

if (args.Length < 2)
{
    Console.WriteLine("usage: dotnet run tools/index-check.cs -- <library root> <scratch dir> [edit]");
    return 2;
}

var root = args[0];
var scratch = args[1];
Directory.CreateDirectory(scratch);
var db = Path.Combine(scratch, "index.db");
File.Delete(db);

static string Dump(IReadOnlyList<Album> albums) => string.Join("\n", albums.Select(a =>
    $"{a.AlbumArtist}|{a.Title}|{a.Year}|{a.ArtPath}|{a.ArtEmbeddedIn}|{a.ArtWidth}x{a.ArtHeight}|"
    + string.Join(",", a.Directories.OrderBy(d => d, StringComparer.Ordinal)) + "\n  "
    + string.Join("\n  ", a.Tracks.Select(t => t.ToString()))));

(string Dump, int Opened, long Ms) Run(LibraryIndex? index, bool trust)
{
    var s = new LibraryScanner();
    var sw = Stopwatch.StartNew();
    var albums = s.Scan(root, null, default, index, trust);
    return (Dump(albums), s.FilesOpened, sw.ElapsedMilliseconds);
}

var plain = Run(null, true);
Console.WriteLine($"no index      : {plain.Ms,7} ms, opened {plain.Opened}");

var first = Run(LibraryIndex.Open(db), true);
Console.WriteLine($"empty index   : {first.Ms,7} ms, opened {first.Opened}   same as plain: {first.Dump == plain.Dump}");

// A NEW index object: what a second launch sees, read back from disk.
var second = Run(LibraryIndex.Open(db), true);
Console.WriteLine($"reloaded index: {second.Ms,7} ms, opened {second.Opened}   same as plain: {second.Dump == plain.Dump}");

var honest = Run(LibraryIndex.Open(db), false);
Console.WriteLine($"honest        : {honest.Ms,7} ms, opened {honest.Opened}   same as plain: {honest.Dump == plain.Dump}");
Console.WriteLine($"index.db      : {new FileInfo(db).Length / 1024} KB");

var ok = first.Dump == plain.Dump && second.Dump == plain.Dump && honest.Dump == plain.Dump && second.Opened == 0;

if (args.Length > 2 && args[2] == "edit")
{
    var victim = Directory.EnumerateFiles(root, "*.mp3", SearchOption.AllDirectories).First();
    var time = new FileInfo(victim).LastWriteTimeUtc;

    string Retitle(int n)
    {
        using var f = TagLib.File.Create(victim);
        var title = $"EDIT{n}".PadRight(f.Tag.Title.Length, 'x');     // same length, never seen before
        f.Tag.Title = title;
        f.Save();
        File.SetLastWriteTimeUtc(victim, time);
        return title;
    }

    // Once first, so that TagLib's own rewrite of the tag settles the file's size.
    Retitle(0);
    var size = new FileInfo(victim).Length;
    var index = LibraryIndex.Open(db);
    Run(index, true);

    var title = Retitle(1);
    var now = new FileInfo(victim);
    Console.WriteLine($"\nedited {Path.GetFileName(victim)}; size kept: {now.Length == size}, time kept: {now.LastWriteTimeUtc == time}");

    bool Sees((string Dump, int, long) r) => r.Dump.Contains($"Title = {title},");
    var trusting = Run(index, true);
    Console.WriteLine($"trusting scan : opened {trusting.Opened}, sees the edit: {Sees(trusting)}   <- False is the known limit");
    index.Touch(victim);
    var touched = Run(index, true);
    var sawTouched = Sees(touched);
    Console.WriteLine($"after Touch() : opened {touched.Opened}, sees the edit: {sawTouched}");
    title = Retitle(2);
    var again = Run(index, false);
    Console.WriteLine($"honest scan   : opened {again.Opened}, sees the next edit: {Sees(again)}");

    ok = ok && sawTouched && Sees(again);
}

Console.WriteLine(ok ? "\nPASS" : "\nFAIL");
return ok ? 0 : 1;
