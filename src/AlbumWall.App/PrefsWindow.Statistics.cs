// AlbumWall — the Statistics tab of Preferences.
//
// Its own file because it is the one tab that is BUILT rather than filled in:
// how many rows there are depends on whose music it is. The numbers come from
// Domain.LibraryStats, which works them out from the last scan; nothing here
// counts anything, it only lays the counts out.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumWall.Domain;

namespace AlbumWall.App;

public partial class PrefsWindow
{
    /// Called from Fill(), so it follows every scan as the rest of the window does.
    private void FillStatistics()
    {
        var s = _host?.LibraryStatistics;
        var any = s is { Tracks: > 0 };

        StatsEmpty.IsVisible = !any;
        StatsTotals.IsVisible = StatsTypesSection.IsVisible = any;
        StatsBitratesSection.IsVisible = any && s!.LossyBitrates.Count > 0;
        StatsLosslessSection.IsVisible = any && s!.LosslessFormats.Count > 0;
        StatsArtSection.IsVisible = any;
        if (!any) return;

        // ---- the five figures across the top
        StatsTotals.Children.Clear();
        (string Figure, string Label)[] totals =
        [
            ($"{s!.Albums:N0}", "albums"),
            ($"{s.Tracks:N0}", "tracks"),
            ($"{s.Artists:N0}", "artists"),
            (Spoken(s.PlayingTime), "of music"),
            (Bytes(s.Bytes), "on disk")
        ];
        for (var i = 0; i < totals.Length; i++)
        {
            var cell = new StackPanel { Spacing = 1 };
            cell.Children.Add(new TextBlock { Text = totals[i].Figure, FontSize = 20, FontWeight = FontWeight.SemiBold });
            cell.Children.Add(Dim(totals[i].Label, 12));
            Grid.SetColumn(cell, i);
            StatsTotals.Children.Add(cell);
        }

        // ---- file types: one row each, a bar for its share of the tracks
        StatsTypes.Children.Clear();
        StatsTypes.Children.Add(Row(TypeColumns, null,
            Dim("", 11), Dim("tracks", 11, right: true), Dim("share", 11, right: true),
            Dim("size", 11, right: true), Dim("time", 11, right: true), Dim("typical", 11, right: true)));
        foreach (var t in s.Types)
            StatsTypes.Children.Add(Row(TypeColumns, (double)t.Tracks / s.Tracks,
                new TextBlock { Text = t.Type, FontSize = 13, FontWeight = FontWeight.SemiBold },
                Figure($"{t.Tracks:N0}"), Figure(Share(t.Tracks, s.Tracks)),
                Figure(Bytes(t.Bytes)), Figure(Spoken(t.PlayingTime)),
                Figure(t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—")));

        // ---- bitrates of the lossy files
        var lossy = s.LossyBitrates.Sum(b => b.Tracks);
        StatsBitratesAbout.Text =
            $"The {lossy:N0} lossy tracks, by the rate each file reports for itself in kbps. "
          + "For a variable-rate file that is its average. “Typical” above is the middle track of its type, "
          + "not the mean, so a few low-rate files do not drag it down.";
        StatsBitrates.Children.Clear();
        foreach (var b in s.LossyBitrates)
            StatsBitrates.Children.Add(Row(BandColumns, (double)b.Tracks / lossy,
                new TextBlock { Text = b.Label, FontSize = 13 },
                Figure($"{b.Tracks:N0}"), Figure(Share(b.Tracks, lossy))));

        // ---- sample rate and depth of the lossless ones
        var lossless = s.LosslessFormats.Sum(b => b.Tracks);
        StatsLossless.Children.Clear();
        foreach (var b in s.LosslessFormats)
            StatsLossless.Children.Add(Row(BandColumns, (double)b.Tracks / lossless,
                new TextBlock { Text = b.Label, FontSize = 13 },
                Figure($"{b.Tracks:N0}"), Figure(Share(b.Tracks, lossless))));

        // ---- cover art, per album; the two that want attention can be SHOWN
        StatsArt.Children.Clear();
        // One album is not "1 albums have": with 1,034 of them, some count here is
        // going to be exactly one, and on this library the first one shown was.
        static string N(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

        StatsArt.Children.Add(ArtLine(
            N(s.Art.Embedded, "album carries its", "albums carry their") + " cover inside the files", null));
        if (s.Art.Sidecar > 0)
            StatsArt.Children.Add(ArtLine(
                N(s.Art.Sidecar, "album uses", "albums use") + " a cover file beside the tracks", null));
        StatsArt.Children.Add(ArtLine(
            s.Art.Missing == 0 ? "No album is missing a cover"
                               : N(s.Art.Missing, "album has", "albums have") + " no cover at all",
            s.Art.Missing == 0 ? null : "art:missing"));
        if (s.Art.Small > 0)
            StatsArt.Children.Add(ArtLine(
                N(s.Art.Small, "album has", "albums have") + $" a cover smaller than {LibraryStats.SmallCover} px",
                "art:small"));
    }

    // type | bar | tracks | share | size | time | typical
    private const string TypeColumns = "58,*,62,52,74,92,84";
    // label | bar | tracks | share
    private const string BandColumns = "96,*,62,52";

    /// One row: the first cell, then a bar in the star column, then the figures.
    /// A null fraction is a header row, which has no bar.
    private static Grid Row(string columns, double? fraction, Control first, params Control[] rest)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), MinHeight = 22 };

        first.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(first, 0);
        grid.Children.Add(first);

        if (fraction is { } f)
        {
            // Track and fill, like the scan bars elsewhere in this window. The
            // fill is a star-sized column inside, so it needs no measuring.
            var bar = new Grid
            {
                Height = 6, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions = new ColumnDefinitions($"{Math.Max(f, 0.004):0.####}*,{Math.Max(1 - f, 0):0.####}*")
            };
            var track = new Border { CornerRadius = new CornerRadius(3) };
            track[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.85 };
            fill[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WordmarkInk");
            bar.Children.Add(track);
            bar.Children.Add(fill);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
        }

        for (var i = 0; i < rest.Length; i++)
        {
            rest[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rest[i], i + 2);
            grid.Children.Add(rest[i]);
        }
        return grid;
    }

    /// A sentence about the covers and, when the wall can show which albums it
    /// means, a link that types that search into the main window.
    private Control ArtLine(string text, string? search)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (search is not null && _host is { } host)
        {
            var show = new Button
            {
                Classes = { "link" },
                Content = new TextBlock { Text = "show on the wall", FontSize = 13, TextDecorations = TextDecorations.Underline },
                VerticalAlignment = VerticalAlignment.Center
            };
            show.Click += (_, _) => host.SearchFor(search);
            line.Children.Add(show);
        }
        return line;
    }

    private static TextBlock Dim(string text, double size, bool right = false) => new()
    {
        Text = text, FontSize = size, Classes = { "dim" },
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left
    };

    private static TextBlock Figure(string text) => new()
    {
        Text = text, FontSize = 12, Classes = { "mono" }, HorizontalAlignment = HorizontalAlignment.Right
    };

    private static string Share(int part, int whole) =>
        whole == 0 ? "" : part * 1000L / whole == 0 ? "<0.1%" : $"{100.0 * part / whole:0.#}%";

    private static string Bytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : $"{bytes / 1024.0:0} KB";

    /// "44 d 19 h", "7 h 12 min", "48 min" — two units, because nobody wants the
    /// seconds of a record collection.
    private static string Spoken(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{t.Minutes} min";
}
