// AlbumWall — all the numbers about one library, on a window of its own.
//
// File types, bitrates, lossless formats and cover art, from the library's
// last scan (Domain.LibraryStats), reached from "All the numbers…" under the
// totals on Preferences > Library. "detailed library stats like x number file
// types and bitrates etc." was the ask that made the Statistics tab
// (2026-09-20); the tab became the lower half of Library (2026-10-10) and
// made it too tall, so the totals stayed there and this is the rest. The
// rows are built here because how many there are depends on whose music it
// is; a section with nothing in it is left out.
//
// The Properties format: light, a fixed width, the system's own title bar
// and no Close button, nothing to change. One window, refilled as the
// selection in Preferences changes.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using AlbumWall.Domain;

namespace AlbumWall.App;

public sealed class StatisticsWindow : Window
{
    private readonly MainWindow _host;

    private static readonly IBrush Ink = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#1D1D20"));
    private static readonly IBrush DimInk = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#62626A"));

    public StatisticsWindow(MainWindow host)
    {
        _host = host;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        Width = 580;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;        // a 1080p laptop at 125 %; past it the page scrolls
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = ThemeVariant.Light;
        Foreground = Ink;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Resources["SheetBg"] = SolidColorBrush.Parse("#E4E4E2");
        Resources["SheetField"] = SolidColorBrush.Parse("#F6F6F4");
        Resources["SheetEdge"] = SolidColorBrush.Parse("#C2C2C6");
        Resources["WordmarkInk"] = Ink;
        Style Restyle<T>(Func<Selector?, Selector> which, params (AvaloniaProperty Property, object Value)[] set) where T : Control
        {
            var style = new Style(which);
            foreach (var (property, value) in set) style.Setters.Add(new Setter(property, value));
            return style;
        }
        Styles.Add(Restyle<TextBlock>(x => x.OfType<TextBlock>().Class("dim"), (TextBlock.ForegroundProperty, DimInk)));
        Styles.Add(Restyle<Button>(x => x.OfType<Button>().Class("link"), (Button.ForegroundProperty, Ink)));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    public void Refill(Library library, LibraryStats? s, string asOf)
    {
        Title = $"{App.DisplayName} — {library.Name}";
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(18, 14, 18, 18) };
        page.Children.Add(new TextBlock { Text = $"{library.Name}  —  {asOf}", FontSize = 15, FontWeight = FontWeight.SemiBold });
        if (s is not { Tracks: > 0 })
        {
            page.Children.Add(Dim("Nothing to count yet. This fills in once the library has been read.", 13));
            Content = page;
            return;
        }

        // ---- file types: one row each, a bar for its share of the tracks
        var types = Section(page, "File types");
        types.Children.Add(Row(TypeColumns, null,
            Dim("", 11), Dim("tracks", 11, right: true), Dim("share", 11, right: true),
            Dim("size", 11, right: true), Dim("time", 11, right: true), Dim("typical", 11, right: true)));
        foreach (var t in s.Types)
            types.Children.Add(Row(TypeColumns, (double)t.Tracks / s.Tracks,
                new TextBlock { Text = t.Type, FontSize = 13, FontWeight = FontWeight.SemiBold },
                Figure($"{t.Tracks:N0}"), Figure(Share(t.Tracks, s.Tracks)),
                Figure(Bytes(t.Bytes)), Figure(Spoken(t.PlayingTime)),
                Figure(t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—")));

        // ---- bitrates of the lossy files
        if (s.LossyBitrates.Count > 0)
        {
            var lossy = s.LossyBitrates.Sum(b => b.Tracks);
            var bitrates = Section(page, "Bitrates");
            var about = Dim(
                $"The {lossy:N0} lossy tracks, by the rate each file reports for itself in kbps. "
              + "For a variable-rate file that is its average. “Typical” above is the middle track of its type, "
              + "not the mean, so a few low-rate files do not drag it down.", 12);
            about.TextWrapping = TextWrapping.Wrap;
            bitrates.Children.Add(about);
            foreach (var b in s.LossyBitrates)
                bitrates.Children.Add(Row(BandColumns, (double)b.Tracks / lossy,
                    new TextBlock { Text = b.Label, FontSize = 13 },
                    Figure($"{b.Tracks:N0}"), Figure(Share(b.Tracks, lossy))));
        }

        // ---- sample rate and depth of the lossless ones
        if (s.LosslessFormats.Count > 0)
        {
            var lossless = s.LosslessFormats.Sum(b => b.Tracks);
            var formats = Section(page, "Lossless");
            foreach (var b in s.LosslessFormats)
                formats.Children.Add(Row(BandColumns, (double)b.Tracks / lossless,
                    new TextBlock { Text = b.Label, FontSize = 13 },
                    Figure($"{b.Tracks:N0}"), Figure(Share(b.Tracks, lossless))));
        }

        // ---- cover art, per album; the two that want attention can be SHOWN
        var art = Section(page, "Cover art");
        // One album is not "1 albums have": with 1,034 of them, some count here is
        // going to be exactly one, and on this library the first one shown was.
        static string N(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

        // A server's covers are files the server handed over, kept here; whether
        // they were inside the music or beside it is the server's business, so
        // "beside the tracks" would be a guess dressed as a fact.
        if (library.IsNavidrome)
            art.Children.Add(ArtLine(N(s.Art.Embedded + s.Art.Sidecar, "album has its", "albums have their") + " cover from the server", null));
        else
        {
            art.Children.Add(ArtLine(N(s.Art.Embedded, "album carries its", "albums carry their") + " cover inside the files", null));
            if (s.Art.Sidecar > 0)
                art.Children.Add(ArtLine(N(s.Art.Sidecar, "album uses", "albums use") + " a cover file beside the tracks", null));
        }
        art.Children.Add(ArtLine(
            s.Art.Missing == 0 ? "No album is missing a cover" : N(s.Art.Missing, "album has", "albums have") + " no cover at all",
            s.Art.Missing == 0 ? null : "art:missing"));
        if (s.Art.Small > 0)
            art.Children.Add(ArtLine(N(s.Art.Small, "album has", "albums have") + $" a cover smaller than {LibraryStats.SmallCover} px", "art:small"));

        Content = new ScrollViewer
        {
            Content = page,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    private static StackPanel Section(StackPanel page, string heading)
    {
        var section = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        section.Children.Add(new TextBlock { Text = heading, FontSize = 14, FontWeight = FontWeight.SemiBold });
        page.Children.Add(section);
        return section;
    }

    // type | bar | tracks | share | size | time | typical
    private const string TypeColumns = "58,*,62,52,74,92,84";
    // label | bar | tracks | share. 120 so "44.1 kHz / 16-bit" fits: at 96 it
    // read "16-bi" (seen in the README screenshot, 2026-10-08).
    private const string BandColumns = "120,*,62,52";

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
            // Track and fill, like the scan bars elsewhere. The fill is a
            // star-sized column inside, so it needs no measuring.
            var bar = new Grid
            {
                Height = 6, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                ColumnDefinitions = new ColumnDefinitions($"{Math.Max(f, 0.004):0.####}*,{Math.Max(1 - f, 0):0.####}*")
            };
            var track = new Border { CornerRadius = new CornerRadius(3) };
            track[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");
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
        if (search is not null)
        {
            var show = new Button
            {
                Classes = { "link" },
                Content = new TextBlock { Text = "show on the wall", FontSize = 13, TextDecorations = TextDecorations.Underline },
                VerticalAlignment = VerticalAlignment.Center
            };
            show.Click += (_, _) => _host.SearchFor(search);
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

    internal static string Bytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : $"{bytes / 1024.0:0} KB";

    /// "44 d 19 h", "7 h 12 min", "48 min" — two units, because nobody wants the
    /// seconds of a record collection.
    internal static string Spoken(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{t.Minutes} min";
}
