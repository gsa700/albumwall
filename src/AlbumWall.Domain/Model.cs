// AlbumWall — domain model.
//
// Album identity is (AlbumArtist, Album), deliberately matching the rule the
// music-audit tool uses, so the player and the audit always agree on what an
// album is. Consequences, all intended:
//   * a multi-disc set is ONE album, ordered by (Disc, TrackNumber)
//   * folder names are not identity; the "YEAR - Album" folder is only the
//     authoritative source of the YEAR
//   * an artist folder may differ from the tag where the filesystem forbids a
//     character (AC/DC is stored as AC_DC)

namespace AlbumWall.Domain;

public sealed record Track(
    int Disc,
    int Number,
    string Title,
    string Artist,
    TimeSpan Duration,
    string Path,
    int SampleRate,
    int BitDepth,
    // kbit/s as the file's own header reports it, 0 when it does not. For a
    // VBR file this is the average. Only the Statistics tab reads it.
    int Bitrate = 0,
    // Bytes on disk, from the directory listing. The whole file, art included.
    long Size = 0)
{
    public string DurationText =>
        Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
            : $"{Duration.Minutes}:{Duration.Seconds:00}";
}

public sealed class Album
{
    public required string AlbumArtist { get; init; }
    public required string Title { get; init; }

    /// Original release year, taken from the "YEAR - Album" folder name, which
    /// is authoritative in this library — FLAC DATE tags often carry a reissue
    /// date instead.
    public int Year { get; set; }

    public List<Track> Tracks { get; } = [];

    /// Resolved cover image: a sidecar path, or null when the art is embedded
    /// (in which case ArtEmbeddedIn names the file to extract it from).
    public string? ArtPath { get; set; }
    public string? ArtEmbeddedIn { get; set; }

    /// The cover's real size in pixels, read from its header at scan time, or
    /// zero when there is no art or the format was not recognized. Zero means
    /// UNKNOWN and must never be read as "small".
    public int ArtWidth { get; set; }
    public int ArtHeight { get; set; }

    public bool HasArt => ArtPath is not null || ArtEmbeddedIn is not null;

    /// Every directory this album's tracks live in. A multi-disc set spans
    /// several, which is why art lookup has to consider more than one.
    public HashSet<string> Directories { get; } = [];

    public int DiscCount => Tracks.Count == 0 ? 0 : Tracks.Select(t => t.Disc).Distinct().Count();
    public TimeSpan TotalTime => new(Tracks.Sum(t => t.Duration.Ticks));

    /// Ignores a leading article so "The Doobie Brothers" files under D.
    public string SortKey
    {
        get
        {
            var a = AlbumArtist;
            foreach (var article in (string[])["The ", "A ", "An "])
                if (a.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                { a = a[article.Length..]; break; }
            return $"{a} {Year:0000} {Title}";
        }
    }

    /// Cheap, allocation-free-ish haystack for the live search box.
    public string SearchText { get; set; } = "";

    public override string ToString() => $"{AlbumArtist} — {Title} ({Year}) [{Tracks.Count} tracks]";
}
