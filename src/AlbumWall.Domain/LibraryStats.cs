// AlbumWall — what the library is made of.
//
// Worked out from a finished scan and nothing else, so it costs nothing to keep
// current: every number here was already in memory for the wall. It opens no
// files and keeps no state of its own.
//
// His ask, 2026-09-20: "detailed library stats like x number file types and
// bitrates etc."

namespace AlbumWall.Domain;

public sealed record LibraryStats(
    int Albums, int Tracks, int Artists, TimeSpan PlayingTime, long Bytes,
    IReadOnlyList<LibraryStats.TypeRow> Types,
    IReadOnlyList<LibraryStats.Band> LossyBitrates,
    IReadOnlyList<LibraryStats.Band> LosslessFormats,
    LibraryStats.ArtCounts Art)
{
    /// One file type. `Bitrate` is the MEDIAN of its tracks, in kbit/s: a mean
    /// is dragged about by a handful of spoken-word files at 32 and says nothing
    /// true about a collection that is nearly all 256.
    public sealed record TypeRow(string Type, int Tracks, long Bytes, TimeSpan PlayingTime, int Bitrate);

    /// A labeled count — a bitrate band, or a sample-rate-and-depth pair.
    public sealed record Band(string Label, int Tracks);

    /// Albums, not tracks: art belongs to the album. `Small` is a cover whose
    /// shorter side is under 300 px, the same line the wall's `art:small` search
    /// draws; one whose size could not be read is not counted as small.
    public sealed record ArtCounts(int Embedded, int Sidecar, int Missing, int Small);

    public const int SmallCover = 300;

    // The bands people actually encode at. A file lands in the band it has
    // reached: a 191 kbit/s VBR average is "128-191", not "192".
    private static readonly (int From, string Label)[] Bands =
    [
        (320, "320 and up"), (256, "256 – 319"), (192, "192 – 255"),
        (128, "128 – 191"), (1, "under 128")
    ];

    public static LibraryStats From(IReadOnlyList<Album> albums)
    {
        var tracks = albums.SelectMany(a => a.Tracks).ToList();

        // The extension and not the codec, for the reason WallRows.TypeLine gives:
        // it is what a file manager shows and the one answer that cannot be wrong.
        var types = tracks
            .GroupBy(t => Path.GetExtension(t.Path).TrimStart('.').ToUpperInvariant())
            .Select(g => new TypeRow(
                g.Key.Length > 0 ? g.Key : "(none)",
                g.Count(),
                g.Sum(t => t.Size),
                new TimeSpan(g.Sum(t => t.Duration.Ticks)),
                Median(g.Where(t => t.Bitrate > 0).Select(t => t.Bitrate))))
            .OrderByDescending(r => r.Tracks)
            .ToList();

        // Lossy and lossless by the convention the rest of the app already uses:
        // a lossy file has no bit depth to report, a lossless one does.
        var lossy = tracks.Where(t => t.BitDepth == 0 && t.Bitrate > 0).ToList();
        var bitrates = Bands
            .Select((b, i) => new Band(b.Label, lossy.Count(t =>
                t.Bitrate >= b.From && (i == 0 || t.Bitrate < Bands[i - 1].From))))
            .Where(b => b.Tracks > 0)
            .ToList();

        var lossless = tracks
            .Where(t => t.BitDepth > 0)
            .GroupBy(t => (t.SampleRate, t.BitDepth))
            .OrderByDescending(g => g.Count())
            .Select(g => new Band($"{g.Key.SampleRate / 1000.0:0.#} kHz / {g.Key.BitDepth}-bit", g.Count()))
            .ToList();

        var art = new ArtCounts(
            Embedded: albums.Count(a => a.ArtEmbeddedIn is not null),
            Sidecar: albums.Count(a => a.ArtEmbeddedIn is null && a.ArtPath is not null),
            Missing: albums.Count(a => !a.HasArt),
            Small: albums.Count(a => a.HasArt && a.ArtWidth > 0 && a.ArtHeight > 0
                                     && Math.Min(a.ArtWidth, a.ArtHeight) < SmallCover));

        return new LibraryStats(
            albums.Count, tracks.Count,
            albums.Select(a => a.AlbumArtist).Distinct().Count(),
            new TimeSpan(tracks.Sum(t => t.Duration.Ticks)),
            tracks.Sum(t => t.Size),
            types, bitrates, lossless, art);
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
