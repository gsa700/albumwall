// AlbumWall - a song's lyrics as lines, timed or not.
//
// For the Lyrics view in the open album (App/LyricsView.cs). His ask,
// 2026-11-01: "what about the lyrics display? ... Many of them have timing so a
// scrolling display could be cool", and then "they don't have timing YET but
// will via Marginal". So plain lyrics are the common case for now and must
// read well alone; timed ones (LRC: "[01:23.45]words") light up line by line
// as Marginal adds them.
//
// WHERE THEY COME FROM, first found wins:
//  1. a .lrc file beside the track, same name (Marginal writes one when it has
//     timing; other taggers do too);
//  2. the track's own tag (LYRICS / UNSYNCEDLYRICS in FLAC and Ogg, USLT in
//     MP3, ©lyr in M4A: TagWriter.ReadLyrics), timed or plain;
//  3. for a Navidrome library, the server's structured lyrics
//     (Navidrome.LyricsOf), timed ones preferred.
//
// LRC as found in the wild: several stamps on one line ("[00:12.00][01:30.00]
// chorus"), minutes past 99, hundredths or thousandths or none, a colon where
// the dot should be, an [offset:+250] in milliseconds (positive = earlier),
// and [ar:]/[ti:]/[al:]/[by:] headers to skip. A line with no stamp inside
// timed lyrics is a header or a credit and is dropped.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AlbumWall.Domain;

/// One line: when it starts (null in plain lyrics) and its words ("" for a gap).
public sealed record LyricLine(TimeSpan? At, string Text);

public sealed partial class Lyrics
{
    public required IReadOnlyList<LyricLine> Lines { get; init; }

    /// Every line has a time, in order.
    public bool Timed { get; init; }

    public static Lyrics? Parse(string? text)
    {
        text = TagWriter.Tidy(text);
        if (text.Length == 0) return null;

        var offset = TimeSpan.Zero;
        var timed = new List<LyricLine>();
        var plain = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (Header().Match(line) is { Success: true } h)
            {
                if (h.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(h.Groups[2].Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                    offset = TimeSpan.FromMilliseconds(ms);
                continue;
            }
            var stamps = new List<TimeSpan>();
            var rest = line;
            while (Stamp().Match(rest) is { Success: true } m)
            {
                stamps.Add(TimeOf(m));
                rest = rest[m.Length..];
            }
            if (stamps.Count > 0)
                foreach (var at in stamps) timed.Add(new LyricLine(at, rest.Trim()));
            else
                plain.Add(line);
        }

        if (timed.Count > 0)
        {
            // [offset:+n] means the lyrics come n ms EARLIER.
            var lines = timed.Select(l => l with { At = l.At - offset < TimeSpan.Zero ? TimeSpan.Zero : l.At - offset })
                             .OrderBy(l => l.At).ToList();
            return new Lyrics { Lines = lines, Timed = true };
        }

        // Plain: keep the verses' blank lines, but not a run of them.
        var kept = new List<LyricLine>();
        foreach (var l in plain)
            if (l.Length > 0 || (kept.Count > 0 && kept[^1].Text.Length > 0))
                kept.Add(new LyricLine(null, l));
        while (kept.Count > 0 && kept[^1].Text.Length == 0) kept.RemoveAt(kept.Count - 1);
        return kept.Count == 0 ? null : new Lyrics { Lines = kept, Timed = false };
    }

    /// The lyrics of a track on this machine: the .lrc beside it, else its tag.
    /// Never throws; not for the UI thread.
    public static Lyrics? ForFile(string path)
    {
        try
        {
            var lrc = Path.ChangeExtension(path, ".lrc");
            if (File.Exists(lrc) && Parse(File.ReadAllText(lrc)) is { } fromFile) return fromFile;
            using var file = TagLib.File.Create(path);
            return Parse(TagWriter.ReadLyrics(file));
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan TimeOf(Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups[3].Success ? m.Groups[3].Value : "";
        var ms = fraction.Length switch
        {
            0 => 0,
            1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
            2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
            _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture),
        };
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }

    // [mm:ss], [mm:ss.xx], [mm:ss.xxx], [mm:ss:xx]
    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    // [ar:Artist], [offset:+250] ... a tag of letters, not a time.
    [GeneratedRegex(@"^\[([A-Za-z#]+):([^\]]*)\]\s*$")]
    private static partial Regex Header();
}
