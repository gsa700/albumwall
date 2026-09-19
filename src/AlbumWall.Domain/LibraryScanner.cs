// AlbumWall — library scanner.
//
// Walks a folder tree, reads tags, and groups tracks into albums.
//
// THE RULE THAT MATTERS: the files on disk are the truth. This scanner reads
// tags unconditionally every time it is asked; it never decides a file is
// unchanged from a timestamp. The cache layer above may skip work, but it must
// always be able to ask for an honest re-read — see the spec, "The index is a
// cache". A player that trusted mtime is exactly how a previous tool silently
// ignored every tag edit made with `metaflac --preserve-modtime`.

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

    /// `Total` is known before the first tag is read, so a bar driven by this is
    /// an honest fraction rather than a spinner with a number beside it.
    public sealed record Progress(int FilesSeen, int Total, int AlbumsFound, string? Current);

    /// Whether a change to `path` could change what a scan returns. The watcher
    /// asks, so that the two can never disagree about what a library file is.
    internal static bool IsLibraryFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || ArtNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// Scans `root` and returns albums sorted the way the wall shows them.
    public IReadOnlyList<Album> Scan(string root, Action<Progress>? onProgress = null,
                                     CancellationToken ct = default)
    {
        // Key is (AlbumArtist, Title) — the album identity rule.
        var albums = new Dictionary<(string, string), Album>();
        var files = 0;

        // Listed first, read second. Walking the tree is a fraction of a second
        // for 18,000 files where reading their tags is minutes on a cold disk,
        // and knowing the total up front is what lets the wait be shown as
        // progress. The first scan of a freshly copied library took 160 s on
        // Windows behind a bare "scanning…", and was reported as a hang.
        var paths = EnumerateAudio(root).ToList();
        onProgress?.Invoke(new Progress(0, paths.Count, 0, null));

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            files++;

            TagLib.File tf;
            try { tf = TagLib.File.Create(path); }
            catch { continue; }          // unreadable or not really audio; skip quietly

            using (tf)
            {
                var tag = tf.Tag;

                // AlbumArtist can be absent on loose files; fall back to the
                // performer so such tracks still group sensibly rather than
                // vanishing from the library.
                var albumArtist = First(tag.AlbumArtists) ?? First(tag.Performers) ?? "Unknown Artist";
                var title = string.IsNullOrWhiteSpace(tag.Album) ? "Unknown Album" : tag.Album.Trim();

                var key = (albumArtist, title);
                if (!albums.TryGetValue(key, out var album))
                {
                    album = new Album { AlbumArtist = albumArtist, Title = title };
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
                var year = m.Success ? int.Parse(m.Groups[1].Value) : (int)tag.Year;
                if (year > 0 && (album.Year == 0 || year < album.Year))
                    album.Year = year;

                var props = tf.Properties;
                album.Tracks.Add(new Track(
                    Disc: (int)Math.Max(1, tag.Disc),
                    Number: (int)tag.Track,
                    Title: string.IsNullOrWhiteSpace(tag.Title)
                             ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim(),
                    Artist: First(tag.Performers) ?? albumArtist,
                    Duration: props?.Duration ?? TimeSpan.Zero,
                    Path: path,
                    SampleRate: props?.AudioSampleRate ?? 0,
                    BitDepth: props?.BitsPerSample ?? 0));

                if (album.ArtPath is null && album.ArtEmbeddedIn is null)
                    ResolveArt(album, dir, tf, path);

                if (files % 25 == 0)
                    onProgress?.Invoke(new Progress(files, paths.Count, albums.Count, albumArtist));
            }
        }

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

    /// Art resolution order: EMBEDDED first, then a sidecar beside the file.
    /// Both paths exist because real libraries use both — this FLAC library has
    /// zero embedded art and only cover.jpg, while the mp3/aac collection has
    /// only embedded art. Supporting one would fail half the time.
    private static void ResolveArt(Album album, string dir, TagLib.File tf, string filePath)
    {
        try
        {
            var pics = tf.Tag.Pictures;
            if (pics is { Length: > 0 })
            {
                var front = pics.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pics[0];
                if (front.Data.Count > 0)
                {
                    album.ArtEmbeddedIn = filePath;
                    Measure(album, front.Data.Data);
                    return;
                }
            }
        }
        catch { /* malformed picture block shouldn't kill the scan */ }

        foreach (var name in ArtNames)
        {
            var candidate = Path.Combine(dir, name);
            if (!File.Exists(candidate)) continue;

            album.ArtPath = candidate;
            try
            {
                // The head of the file, not all of it. A sidecar can be several
                // megabytes and a FLAC library has one per album; the frame
                // header is past the EXIF block but well inside this.
                using var fs = File.OpenRead(candidate);
                var head = new byte[(int)Math.Min(fs.Length, 256 * 1024)];
                fs.ReadExactly(head);
                Measure(album, head);
            }
            catch { /* art we cannot measure is still art */ }
            return;
        }
    }

    private static void Measure(Album album, ReadOnlySpan<byte> data)
    {
        if (ImageSize.Read(data) is not var (w, h)) return;
        album.ArtWidth = w;
        album.ArtHeight = h;
    }

    private static string? First(string[]? xs) =>
        xs is { Length: > 0 } && !string.IsNullOrWhiteSpace(xs[0]) ? xs[0].Trim() : null;

    private static IEnumerable<string> EnumerateAudio(string root)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };
        foreach (var p in Directory.EnumerateFiles(root, "*", opts))
            if (AudioExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                yield return p;
    }
}
