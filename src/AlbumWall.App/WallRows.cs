// AlbumWall — the wall's row model.
//
// WHY THE WALL IS A LIST OF ROWS AND NOT A GRID.
//
// The first build used ItemsRepeater + UniformGridLayout, which handled the grid
// for us. It cannot do the one interaction this app is built around: opening an
// album has to unfold a full-width panel BELOW THAT ROW and push everything after
// it down. A uniform grid has no concept of a row, so there is nothing to insert
// between and nothing to push.
//
// So the wall is a vertical list whose items are rows, and a row is either a run
// of albums or an open panel. This costs us the column arithmetic — we now compute
// how many covers fit and re-chunk when that changes — and it is arithmetic we
// already owned, because "cover size is the control and the column count is a
// consequence" was decided at design time.
//
// Virtualisation survives, and improves: StackLayout virtualises by row, so a
// realised row realises its own covers. Roughly the same number of covers decode
// as before, which the status bar counter confirms.

namespace AlbumWall.App;

/// One item in the wall. Sealed hierarchy of exactly two shapes.
public abstract class WallItem;

/// A run of albums across one row of the wall.
public sealed class AlbumRow : WallItem
{
    public required IReadOnlyList<AlbumVm> Albums { get; init; }
}

/// The open album, occupying a full-width row of its own directly beneath the
/// row that holds the clicked cover.
public sealed class PanelRow : WallItem
{
    public required AlbumVm Album { get; init; }

    /// Which column of the row above was clicked, so the arrow can point at it.
    public required int ArrowColumn { get; init; }

    /// Geometry of the row above, needed to place the arrow. Passed in rather
    /// than recomputed so the arrow cannot drift from the tile it points at.
    public required double CoverPx { get; init; }
    public required double Spacing { get; init; }

    /// Required rather than defaulted: the wall always knows the album, so there
    /// is no case where a panel should quietly open in the neutral palette.
    public required AlbumPalette Palette { get; init; }

    /// Supplied by the wall: (album, first track index, shuffle).
    public Action<AlbumVm, int, bool>? PlayFrom { get; init; }

    /// False when libmpv is missing, which is the only reason the transport is
    /// ever unavailable. The buttons say why in their tooltip rather than being
    /// quietly dead.
    public bool CanPlay => PlayFrom is not null;

    public System.Windows.Input.ICommand PlayCommand =>
        new Relay(() => PlayFrom?.Invoke(Album, 0, false));

    public System.Windows.Input.ICommand ShuffleCommand =>
        new Relay(() => PlayFrom?.Invoke(Album, 0, true));

    internal sealed class Relay(Action run) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    /// True only for a panel the user just opened. A rebuild caused by a resize
    /// or a filter recreates the PanelRow, and re-running the unfold every time
    /// the window is dragged a pixel wider would be seasick-making.
    public bool Unfold { get; init; }

    /// Set once the unfold has run, so scrolling the panel out of view and back
    /// does not replay it.
    public bool Unfolded { get; set; }

    /// Left offset of the arrow's centre, measured from the panel's left edge.
    public double ArrowOffset => ArrowColumn * (CoverPx + Spacing) + CoverPx / 2;

    public string HeaderLine
    {
        get
        {
            var a = Album.Album;
            var parts = new List<string>();
            if (a.Year > 0) parts.Add(a.Year.ToString());
            parts.Add(a.Tracks.Count == 1 ? "1 track" : $"{a.Tracks.Count} tracks");
            if (a.DiscCount > 1) parts.Add($"{a.DiscCount} discs");
            parts.Add(Duration(a.TotalTime));
            var fmt = FormatLine(a);
            if (fmt.Length > 0) parts.Add(fmt);
            return string.Join("   ·   ", parts);
        }
    }

    /// Reports what the files ACTUALLY are, per track, rather than assuming the
    /// library is uniform. A mixed album should say so.
    private static string FormatLine(Domain.Album a)
    {
        var specs = a.Tracks
            .Where(t => t.SampleRate > 0)
            .Select(t => t.BitDepth > 0
                ? $"{t.BitDepth}/{t.SampleRate / 1000.0:0.#}"
                : $"{t.SampleRate / 1000.0:0.#} kHz")
            .Distinct()
            .ToList();

        return specs.Count switch
        {
            0 => "",
            1 => specs[0] + " kHz",
            _ => "mixed " + string.Join(", ", specs)
        };
    }

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";

    /// The track list, split into a left and a right column.
    ///
    /// Reading order is DOWN the left column and then down the right, not left
    /// to right across the pair. A track list is a sequence, and a two-column
    /// layout that alternates sides turns following that sequence into work.
    /// Disc headers are rows in the flow, so a disc boundary lands where it
    /// falls rather than forcing a new column.
    public IReadOnlyList<TrackLine> LeftColumn => Split().Left;
    public IReadOnlyList<TrackLine> RightColumn => Split().Right;

    /// Offset of the arrow, as a margin. Half the arrow's 22 px width is taken
    /// off so its POINT lands on the cover's centre, not its left corner.
    public Avalonia.Thickness ArrowMargin => new(ArrowOffset - 11, 0, 0, 0);

    private (IReadOnlyList<TrackLine> Left, IReadOnlyList<TrackLine> Right)? _split;

    private (IReadOnlyList<TrackLine> Left, IReadOnlyList<TrackLine> Right) Split()
    {
        if (_split is not null) return _split.Value;
        {
            var lines = new List<TrackLine>();
            var multi = Album.Album.DiscCount > 1;
            var disc = -1;

            // The index is the track's position in the ALBUM, not in this column,
            // because that is what the player's queue is built from.
            for (var i = 0; i < Album.Album.Tracks.Count; i++)
            {
                var t = Album.Album.Tracks[i];
                if (multi && t.Disc != disc)
                {
                    disc = t.Disc;
                    lines.Add(TrackLine.Header($"Disc {disc} of {Album.Album.DiscCount}"));
                }

                var from = i;
                lines.Add(TrackLine.Of(t, () => PlayFrom?.Invoke(Album, from, false)));
            }

            // Split so the left column is never shorter than the right, and never
            // ends on a disc header with its tracks stranded in the next column.
            var half = (lines.Count + 1) / 2;
            if (half > 0 && half < lines.Count && lines[half - 1].IsHeader) half--;
            _split = (lines.Take(half).ToList(), lines.Skip(half).ToList());
            return _split.Value;
        }
    }
}

/// One row of the panel's track list: either a disc header or a track.
public sealed class TrackLine
{
    public bool IsHeader { get; private init; }
    public string Text { get; private init; } = "";
    public string Number { get; private init; } = "";
    public string Duration { get; private init; } = "";
    public Domain.Track? Source { get; private init; }

    public static TrackLine Header(string text) => new() { IsHeader = true, Text = text };

    public static TrackLine Of(Domain.Track t, Action play) => new()
    {
        Source = t,
        Number = t.Number > 0 ? t.Number.ToString() : "",
        Text = t.Title,
        Duration = t.DurationText,
        PlayCommand = new PanelRow.Relay(play)
    };

    /// Clicking a track plays the album FROM that track — the album stays the
    /// queue, so playing track 4 still carries on into track 5.
    public System.Windows.Input.ICommand? PlayCommand { get; private init; }
}
