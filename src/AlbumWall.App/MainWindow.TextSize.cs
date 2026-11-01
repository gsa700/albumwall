// AlbumWall - the main window at the chosen text size.
//
// His call, 2026-11-01: "go ahead with text only". The text grows and the
// covers do not. The wall is there to show the collection at once; zooming
// all of it would take away about a third of the covers in each row at 150%,
// for text that was the only hard part to read. The setting is called Text
// size, and this way it does what its name says. (Marginal, which is lists and
// forms, zooms its whole window, which suits it.)
//
// Two ways, by what the text sits in:
//  - ON THE WALL (the labels under the covers, the open album, its track
//    list): the type sizes are resources (Text10 ... Text21 in the markup),
//    set here to the designed size times the text size, and the rows they
//    sit in follow (LabelHeight, TrackLineHeight). The covers, the panel's
//    260 pt sleeve and the words printed on an art-less sleeve keep their
//    size: they are the pictures.
//  - THE PLAY CONTROLS: the same, text only (the song, the artist, the two
//    times). Scaled whole, as the bars are, the strip outgrew a 1444 window
//    at 150% and the second meter went off its end (photographed 2026-11-01).
//    At 150% the text still fits the 82 pt strip, so its height stays.
//  - IN THE BARS (top and bottom) and the empty, welcome and scan screens:
//    drawn through a scale, text, buttons and spacing together, the way the
//    light windows are (TextSize.Follow), with the bars made that much taller
//    to hold it. Their buttons are targets for the text beside them; scaling
//    the text alone would leave a 15 pt label crowding a 27 pt button.
//
// Not yet: the compact view and the kiosk's front panel (NowPlayingPanel),
// which size their windows to themselves; context menus and tooltips, which
// are the system's.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AlbumWall.App;

public partial class MainWindow
{
    /// The chosen text size as a factor: 1, 1.15, 1.3 or 1.5.
    private double _textScale = 1;

    /// The designed heights, at 100%.
    private const double BarDesignHeight = 44;
    private const double TransportDesignHeight = 82;
    private const int LabelDesignHeight = 40;
    private const double SearchDesignWidth = 320;

    /// Under each cover: its title and artist, at the text size.
    private int LabelHeight => (int)Math.Ceiling(LabelDesignHeight * _textScale);

    /// The play controls' strip: its text grows inside it (see above).
    private const double TransportHeight = TransportDesignHeight;

    /// The type sizes the wall's templates use (Text10 ... in the markup).
    private static readonly int[] WallTypeSizes = [10, 11, 12, 14, 21];

    /// From the constructor, and again whenever the size is changed (Preferences,
    /// or the "textsize" command), with the window open.
    private void FollowTextSize()
    {
        ApplyTextSize();
        Action changed = () => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyTextSize);
        TextSize.Changed += changed;
        Closed += (_, _) => TextSize.Changed -= changed;
    }

    private void ApplyTextSize()
    {
        var f = TextSize.Percent / 100.0;
        var first = !_textSizeApplied;
        if (!first && f == _textScale) return;
        _textSizeApplied = true;
        _textScale = f;

        foreach (var size in WallTypeSizes) Resources[$"Text{size}"] = size * f;
        Resources["TrackLineHeight"] = Math.Ceiling(28 * f);
        Resources["TrackNumberWidth"] = new GridLength(Math.Ceiling(30 * f));

        foreach (var chrome in new[] { TopBar, BottomBar, EmptyState, WelcomeState, ScanState })
            Scale(chrome, f);
        TransportTitle.FontSize = 16 * f;
        TransportArtist.FontSize = ElapsedText.FontSize = RemainingText.FontSize = 11 * f;
        ElapsedText.Width = Math.Ceiling(46 * f);
        RemainingText.Width = Math.Ceiling(52 * f);
        TopBar.Height = BottomBar.Height = Math.Ceiling(BarDesignHeight * f);
        // The search box keeps its width on screen (320) while its text grows:
        // scaled with the rest it took half again as much of the bar, and at
        // 150% in a 1444 window the library's name ran into it.
        SearchBox.Width = Math.Round(SearchDesignWidth / f);

        if (first) return;      // the constructor goes on to lay all of this out

        Console.WriteLine($"[text] main window at {TextSize.Percent}%: bars {TopBar.Height}, labels {LabelHeight}");
        Wall.RowHeight = CoverPx + LabelHeight;
        ApplyTransportPosition();       // it parks against a bar, whose height changed
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaTitleBarHeightHint = TopBar.Height;
            RecenterTrafficLights();
        }
        if (_open is not null) SchedulePanelRefresh();
    }

    private bool _textSizeApplied;

    /// Draws a border's content through a scale, wrapping it once.
    private static void Scale(Border border, double f)
    {
        if (border.Child is not LayoutTransformControl host)
        {
            if (f == 1) return;
            var child = border.Child;
            border.Child = null;
            host = new LayoutTransformControl { Child = child };
            border.Child = host;
        }
        // 1 said explicitly: a null transform leaves the old scale on the content
        // (TextSize.Follow says how that was found).
        host.LayoutTransform = new ScaleTransform(f, f);
    }

    /// A border's own content, whether or not Scale has wrapped it.
    private static Control? Inner(Border border) =>
        border.Child is LayoutTransformControl host ? host.Child : border.Child;
}
