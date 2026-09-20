// AlbumWall — library watcher.
//
// Says "the library changed, scan again" and nothing more. It does not say WHAT
// changed, on purpose: the scanner's rule is that the files on disk are the
// truth and every scan is an honest re-read, and a watcher that tried to patch
// the album list from individual events would be a second, weaker source of
// truth that drifts the first time an event is dropped. Events are dropped —
// see OnError.
//
// The one thing it does pass on is the NAME of a music or art file it saw
// something happen to, through `touched`, and only ever to make the next scan
// more honest: the index forgets that file, so it is opened and read again
// whatever its size and modified time claim afterwards. That is what catches a
// tag edit that preserves both, while the app is open. A dropped event costs
// nothing here that it did not cost before — the file is then judged by size
// and time like any other.
//
// First found on Windows: music copied into the folder while the app was open
// simply never appeared, and nothing on screen suggested pressing Rescan.

namespace AlbumWall.Domain;

public sealed class LibraryWatcher : IDisposable
{
    /// How long the folder has to stay still before a rescan is asked for.
    ///
    /// A copy is not one event, it is thousands: every file is created, grows
    /// through a run of size changes, and is touched once more at the end. A
    /// scan in the middle of that reads half-written files and shows albums with
    /// tracks missing. So this waits for QUIET rather than reacting to change,
    /// and every event pushes the deadline back. The cost is that a long copy
    /// shows nothing until it finishes, which is the honest answer anyway.
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    private readonly FileSystemWatcher _fsw;
    private readonly Timer _timer;
    private readonly Action _changed;
    private readonly Action<string>? _touched;

    public string Root { get; }

    /// `changed` is raised on a thread-pool thread, once per burst of activity.
    /// `touched` is raised at once, for every event that names a library file.
    public LibraryWatcher(string root, Action changed, Action<string>? touched = null)
    {
        Root = root;
        _changed = changed;
        _touched = touched;
        _timer = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);

        _fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,

            // The default is 8 KB, which a bulk copy overruns in moments. 64 KB
            // is the most Windows will accept for a network path and costs
            // nothing worth counting; elsewhere the value is ignored.
            InternalBufferSize = 64 * 1024
        };

        _fsw.Created += OnEvent;
        _fsw.Changed += OnEvent;
        _fsw.Deleted += OnEvent;
        _fsw.Renamed += (_, e) =>
        {
            Touched(e.OldFullPath);
            Touched(e.FullPath);
            if (Matters(e.OldFullPath, true) || Matters(e.FullPath, true)) Nudge();
        };
        _fsw.Error += OnError;
        _fsw.EnableRaisingEvents = true;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        Touched(e.FullPath);
        if (Matters(e.FullPath, e.ChangeType == WatcherChangeTypes.Deleted)) Nudge();
    }

    private void Touched(string path)
    {
        if (_touched is not null && LibraryScanner.IsLibraryFile(path)) _touched(path);
    }

    /// Music, cover art, and anything that is or might have been a folder.
    ///
    /// A deleted or renamed-away path can no longer be asked what it was, and
    /// losing an artist folder is exactly the event that must not be missed —
    /// sending one to the Recycle Bin is a SINGLE rename of the top folder, with
    /// nothing reported for the files inside. The name is no guide either:
    /// "Mr. Mister" has an extension as far as Path is concerned. So anything
    /// that has gone counts. What this keeps out is the noise of things
    /// arriving and changing — thumbnail caches, desktop.ini, a tagger's temp
    /// files — and a wasted scan costs little, because the app drops a result
    /// that matches what it is already showing.
    private static bool Matters(string path, bool gone)
    {
        if (gone || LibraryScanner.IsLibraryFile(path)) return true;
        try { return Directory.Exists(path); } catch { return false; }
    }

    /// An overrun buffer means events were lost, and there is no knowing which.
    /// That is not a failure here, only a louder "something changed" — which is
    /// the one thing this class ever says.
    private void OnError(object sender, ErrorEventArgs e)
    {
        Console.WriteLine($"[watch] {e.GetException().Message}");
        Nudge();
    }

    private void Nudge()
    {
        try { _timer.Change(Quiet, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* an event in flight as we were torn down */ }
    }

    public void Dispose()
    {
        _fsw.Dispose();
        _timer.Dispose();
    }
}
