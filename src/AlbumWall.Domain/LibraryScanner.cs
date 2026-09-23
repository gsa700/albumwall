// AlbumWall — library scanner.
//
// Walks a folder tree, reads tags, and groups tracks into albums.
//
// THE RULE THAT MATTERS: the files on disk are the truth. Asked plainly, this
// scanner opens every file and reads its tags, every time. Given a LibraryIndex
// AND told to trust it, it skips the files the index still recognizes — that is
// the only shortcut, it is the caller's decision each time, and an honest
// re-read is always one argument away. See the spec, "The index is a cache",
// and LibraryIndex for exactly what trusting it can miss. A player that trusted
// mtime with no way out is how a previous tool silently ignored every tag edit
// made with `metaflac --preserve-modtime`.

using System.Text.RegularExpressions;

namespace AlbumWall.Domain;

public sealed partial class LibraryScanner
{
    // "1978 - Toto", "2014 - Led Zeppelin I (deluxe edition) (Disc 1 of 2): ..."
    [GeneratedRegex(@"^(\d{4}) - ")]
    private static partial Regex FolderYear();

    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav"];
    private static readonly string[] ArtNames = ["cover.jpg", "cover.jpeg", "cover.png",
                                                 "folder.jpg", "folder.jpeg", "front.jpg"];

    /// The back of the sleeve. `music-backart` writes back.jpg and `music-audit`
    /// blesses exactly that spelling; the other two are here for the same reason
    /// ArtNames carries more than one, and cost nothing.
    private static readonly string[] BackNames = ["back.jpg", "back.jpeg", "back.png"];

    /// `Total` is known before the first tag is read, so a bar driven by this is
    /// an honest fraction rather than a spinner with a number beside it.
    public sealed record Progress(int FilesSeen, int Total, int AlbumsFound, string? Current);

    /// Whether a change to `path` could change what a scan returns. The watcher
    /// asks, so that the two can never disagree about what a library file is.
    internal static bool IsLibraryFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || ArtNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        || BackNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// How many files the last scan actually opened. With a trusted index on an
    /// unchanged library this is zero, and that is the point of the index.
    public int FilesOpened { get; private set; }

    /// Scans `root` and returns albums sorted the way the wall shows them.
    ///
    /// With an `index`, what is read is remembered in it. With `trustIndex` as
    /// well, a file whose path, size and modified time it recognizes is not
    /// opened. Without, every file is — and the index is rebuilt from the result,
    /// which is what makes Rescan the way out when the index is wrong.
    public IReadOnlyList<Album> Scan(string root, Action<Progress>? onProgress = null,
                                     CancellationToken ct = default,
                                     LibraryIndex? index = null, bool trustIndex = true)
    {
        // Key is (AlbumArtist, Title) — the album identity rule.
        var albums = new Dictionary<(string, string), Album>();
        var files = 0;
        FilesOpened = 0;

        var startedAt = index?.Now() ?? 0;
        var seen = new Dictionary<string, LibraryIndex.TrackFacts>(LibraryIndex.PathComparer);
        var seenArt = new Dictionary<string, LibraryIndex.ArtFacts>(LibraryIndex.PathComparer);

        // Listed first, read second. Walking the tree is a fraction of a second
        // for 18,000 files where reading their tags is minutes on a cold disk,
        // and knowing the total up front is what lets the wait be shown as
        // progress. The first scan of a freshly copied library took 160 s on
        // Windows behind a bare "scanning…", and was reported as a hang.
        //
        // The listing carries each file's size and modified time — on Windows
        // they arrive with the directory entry, at no further cost — and those
        // are what the index is asked about. Nothing is opened to find them out.
        var paths = EnumerateAudio(root).ToList();
        onProgress?.Invoke(new Progress(0, paths.Count, 0, null));

        try
        {
            foreach (var info in paths)
            {
                ct.ThrowIfCancellationRequested();
                files++;

                var path = info.FullName;
                long size, modified;
                try { size = info.Length; modified = info.LastWriteTimeUtc.Ticks; }
                catch { continue; }          // gone since it was listed

                var facts = trustIndex ? index?.Find(path, size, modified) : null;
                if (facts is null)
                {
                    FilesOpened++;
                    facts = ReadFacts(path, size, modified);
                }
                seen[path] = facts;

                // Unreadable, or not really audio. Remembered all the same, or
                // it would be opened again on every launch to find that out.
                if (!facts.Readable) continue;

                var key = (facts.AlbumArtist, facts.Album);
                if (!albums.TryGetValue(key, out var album))
                {
                    album = new Album { AlbumArtist = facts.AlbumArtist, Title = facts.Album };
                    albums[key] = album;
                }

                var dir = Path.GetDirectoryName(path)!;
                album.Directories.Add(dir);

                // Year: the folder wins — tag DATE is frequently a reissue date.
                //
                // Take the EARLIEST year across the album's folders. A box set
                // spans several ("1978 - Toto" ... "1984 - Isolation") and
                // whichever the filesystem happened to hand us first is not a
                // stable answer: it made the Toto box report 1982 on one run
                // and would reshuffle the sort order between scans.
                var m = FolderYear().Match(Path.GetFileName(dir));
                var year = m.Success ? int.Parse(m.Groups[1].Value) : facts.TagYear;
                if (year > 0 && (album.Year == 0 || year < album.Year))
                    album.Year = year;

                album.Tracks.Add(new Track(
                    Disc: facts.Disc,
                    Number: facts.Number,
                    Title: facts.Title,
                    Artist: facts.Artist,
                    Duration: new TimeSpan(facts.DurationTicks),
                    Path: path,
                    SampleRate: facts.SampleRate,
                    BitDepth: facts.BitDepth,
                    Bitrate: facts.Bitrate,
                    Size: size));

                // Until a track with a picture turns up, keep looking: a sidecar
                // found on the way is only a fallback. See ResolveArt.
                if (album.ArtEmbeddedIn is null)
                    ResolveArt(album, dir, facts, path, trustIndex ? index : null, seenArt);

                if (files % 25 == 0)
                    onProgress?.Invoke(new Progress(files, paths.Count, albums.Count, facts.AlbumArtist));
            }
        }
        catch (OperationCanceledException)
        {
            // Cut short, but what was read was read, and on a cold first scan
            // that may be minutes of it. Kept, without concluding anything about
            // the files this scan never reached.
            index?.Commit(seen, seenArt, startedAt, complete: false);
            throw;
        }

        index?.Commit(seen, seenArt, startedAt, complete: true);

        foreach (var a in albums.Values)
        {
            a.Tracks.Sort((x, y) => x.Disc != y.Disc
                ? x.Disc.CompareTo(y.Disc)
                : x.Number.CompareTo(y.Number));
            a.SearchText = $"{a.AlbumArtist}\n{a.Title}".ToLowerInvariant();
        }

        onProgress?.Invoke(new Progress(files, paths.Count, albums.Count, null));

        return albums.Values.OrderBy(a => a.SortKey, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// EMBEDDED IN ANY TRACK beats a sidecar, not just embedded in the first.
    ///
    /// This used to stop at the first thing it found, track by track — so an
    /// album whose first track had no picture took the folder's sidecar and
    /// never looked at track two. On Hambench that sidecar was a 1,305-byte
    /// black Folder.jpg some tool had scattered through the library, and four
    /// albums with perfectly good embedded covers showed as black squares. It
    /// only came to light when the placeholders were deleted and the real
    /// covers appeared. A sidecar is now remembered and the search goes on; the
    /// first embedded picture found replaces it.
    ///
    /// Art resolution order: EMBEDDED first, then a sidecar beside the file.
    /// Both paths exist because real libraries use both — this FLAC library has
    /// zero embedded art and only cover.jpg, while the mp3/aac collection has
    /// only embedded art. Supporting one would fail half the time.
    private static void ResolveArt(Album album, string dir, LibraryIndex.TrackFacts facts, string filePath,
                                   LibraryIndex? trusted, Dictionary<string, LibraryIndex.ArtFacts> seenArt)
    {
        if (facts.HasCover)
        {
            album.ArtEmbeddedIn = filePath;
            album.ArtPath = null;
            album.ArtWidth = facts.CoverWidth;          // not the sidecar's
            album.ArtHeight = facts.CoverHeight;
            album.ArtSize = facts.Size;                 // the TRACK's, since that is what would be re-read
            album.ArtModified = facts.Modified;
            ResolveBack(album, dir, trusted, seenArt);
            return;
        }

        // The back is resolved FIRST and on its own, because everything below
        // returns early once a front is settled — and an album whose front is
        // embedded, or already found in an earlier directory, must still get
        // its back.
        ResolveBack(album, dir, trusted, seenArt);

        if (album.ArtPath is not null) return;          // already have a fallback

        foreach (var candidate in Sidecar(dir, ArtNames))
        {
            album.ArtPath = candidate.FullName;
            var (size, modified) = (candidate.Length, candidate.LastWriteTimeUtc.Ticks);

            // Stamped from the directory entry, OUTSIDE the try: art we cannot
            // measure is still art (see the catch), and it must still invalidate
            // its caches when it is replaced.
            album.ArtSize = size;
            album.ArtModified = modified;
            try
            {
                if (!seenArt.TryGetValue(candidate.FullName, out var art))
                    art = trusted?.FindArt(candidate.FullName, size, modified);
                if (art is null)
                {
                    // The head of the file, not all of it. A sidecar can be several
                    // megabytes and a FLAC library has one per album; the frame
                    // header is past the EXIF block but well inside this.
                    using var fs = candidate.OpenRead();
                    var head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                    fs.ReadExactly(head);
                    var (w, h) = ImageSize.Read(head) ?? (0, 0);
                    art = new LibraryIndex.ArtFacts(size, modified, w, h);
                }
                seenArt[candidate.FullName] = art;
                album.ArtWidth = art.Width;
                album.ArtHeight = art.Height;
            }
            catch { /* art we cannot measure is still art */ }
            return;
        }
    }

    /// The first of `names` present in `dir`, in the order `names` gives, WHATEVER ITS CASE.
    ///
    /// Windows never cares about case, so the exact-name look is all it needs.
    /// Linux does, and Windows-era libraries write `Folder.jpg`: every album
    /// copied from the iTunes collection on the NAS has one, and an exact look
    /// for `folder.jpg` walked straight past it. The exact look still goes
    /// first, so a library that spells its art the way we do (this FLAC one)
    /// never lists a directory. Yields at most one file.
    private static IEnumerable<FileInfo> Sidecar(string dir, string[] names)
    {
        foreach (var name in names)
        {
            var exact = new FileInfo(Path.Combine(dir, name));
            if (exact.Exists) { yield return exact; yield break; }
        }

        if (OperatingSystem.IsWindows()) yield break;

        FileInfo[] present;
        try { present = new DirectoryInfo(dir).GetFiles(); }
        catch { yield break; }

        foreach (var name in names)
        {
            var match = present.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) { yield return match; yield break; }
        }
    }

    /// Finds back.jpg beside the tracks, if there is one.
    ///
    /// Measured, not just found: backs are scans of the whole tray card and are
    /// not square, so the view needs the real aspect to letterbox instead of
    /// cropping off the track listing. Measurement goes through the same index
    /// table the front uses — a back is just another art path, which is why
    /// this cost no index version bump.
    private static void ResolveBack(Album album, string dir, LibraryIndex? trusted,
                                    Dictionary<string, LibraryIndex.ArtFacts> seenArt)
    {
        if (album.BackPath is not null) return;         // an earlier disc had one

        foreach (var candidate in Sidecar(dir, BackNames))
        {
            album.BackPath = candidate.FullName;
            var (size, modified) = (candidate.Length, candidate.LastWriteTimeUtc.Ticks);
            album.BackSize = size;
            album.BackModified = modified;
            try
            {
                if (!seenArt.TryGetValue(candidate.FullName, out var art))
                    art = trusted?.FindArt(candidate.FullName, size, modified);
                if (art is null)
                {
                    using var fs = candidate.OpenRead();
                    var head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                    fs.ReadExactly(head);
                    var (w, h) = ImageSize.Read(head) ?? (0, 0);
                    art = new LibraryIndex.ArtFacts(size, modified, w, h);
                }
                seenArt[candidate.FullName] = art;
                album.BackWidth = art.Width;
                album.BackHeight = art.Height;
            }
            catch { /* a back we cannot measure is still a back */ }
            return;
        }
    }

    /// Opens one file and takes everything the scanner wants from it. The only
    /// place a music file is opened, and what the index exists to avoid.
    ///
    /// CHANGE WHAT THIS RETURNS FOR THE SAME FILE AND LibraryIndex.Version MUST
    /// GO UP, or every file already indexed keeps the old answer.
    private static LibraryIndex.TrackFacts ReadFacts(string path, long size, long modified)
    {
        TagLib.File tf;
        try { tf = TagLib.File.Create(path); }
        catch
        {
            return new LibraryIndex.TrackFacts(size, modified, false, "", "", 0, 0, 0, "", "", 0, 0, 0, 0, false, 0, 0);
        }

        using (tf)
        {
            var tag = tf.Tag;
            var props = tf.Properties;

            // AlbumArtist can be absent on loose files; fall back to the
            // performer so such tracks still group sensibly rather than
            // vanishing from the library.
            var albumArtist = First(tag.AlbumArtists) ?? First(tag.Performers) ?? "Unknown Artist";

            var (hasCover, w, h) = (false, 0, 0);
            try
            {
                if (ImageSize.Cover(tag.Pictures) is { } cover)
                {
                    hasCover = true;
                    (w, h) = ImageSize.Read(cover) ?? (0, 0);
                }
            }
            catch { /* malformed picture block shouldn't kill the scan */ }

            return new LibraryIndex.TrackFacts(
                size, modified, true,
                AlbumArtist: albumArtist,
                Album: string.IsNullOrWhiteSpace(tag.Album) ? "Unknown Album" : tag.Album.Trim(),
                TagYear: (int)tag.Year,
                Disc: (int)Math.Max(1, tag.Disc),
                Number: (int)tag.Track,
                Title: string.IsNullOrWhiteSpace(tag.Title)
                         ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim(),
                Artist: First(tag.Performers) ?? albumArtist,
                DurationTicks: (props?.Duration ?? TimeSpan.Zero).Ticks,
                SampleRate: props?.AudioSampleRate ?? 0,
                BitDepth: props?.BitsPerSample ?? 0,
                Bitrate: props?.AudioBitrate ?? 0,
                HasCover: hasCover, CoverWidth: w, CoverHeight: h);
        }
    }

    private static string? First(string[]? xs) =>
        xs is { Length: > 0 } && !string.IsNullOrWhiteSpace(xs[0]) ? xs[0].Trim() : null;

    private static IEnumerable<FileInfo> EnumerateAudio(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            if (AudioExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                yield return f;
    }
}
