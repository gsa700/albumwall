// AlbumWall - the words of the song playing, in the open album.
//
// Over the track list when "Lyrics" is pressed in the playing album's panel
// (MainWindow.Lyrics.cs says when), centred on it, the tracks frosted behind
// (the markup). A window onto the lines, as tall as the tracks or five lines,
// that the song moves through by itself; nothing to scroll by hand, as the
// panel has no scroll region of its own.
//
// TIMED lyrics (LRC, Domain/Lyrics.cs): the line being sung sits in the
// middle, bright and a little heavier; the others are the panel's dim text.
// A click on a line takes the song there. A gap in the singing shows a note.
// PLAIN lyrics, the most of them for now ("they don't have timing YET but will
// via Marginal"): every line in the bright text, and the window drifts down
// through them as the song goes, so what is being sung is roughly in view.
//
// It glides, a fraction of the way each tick; with motion reduced (Motion.cs)
// it jumps. Not a live region: a screen reader announcing every line would
// talk over the music. The lines are there to be read on demand.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AlbumWall.App;

public sealed class LyricsView : Border
{
    public static readonly StyledProperty<Domain.Lyrics?> LyricsProperty =
        AvaloniaProperty.Register<LyricsView, Domain.Lyrics?>(nameof(Lyrics));
    public static readonly StyledProperty<IBrush?> BrightProperty =
        AvaloniaProperty.Register<LyricsView, IBrush?>(nameof(Bright));
    public static readonly StyledProperty<IBrush?> DimProperty =
        AvaloniaProperty.Register<LyricsView, IBrush?>(nameof(Dim));
    /// The size of a line, following the text size (Text14 in the markup).
    public static readonly StyledProperty<double> LineSizeProperty =
        AvaloniaProperty.Register<LyricsView, double>(nameof(LineSize), 14);

    public Domain.Lyrics? Lyrics { get => GetValue(LyricsProperty); set => SetValue(LyricsProperty, value); }
    public IBrush? Bright { get => GetValue(BrightProperty); set => SetValue(BrightProperty, value); }
    public IBrush? Dim { get => GetValue(DimProperty); set => SetValue(DimProperty, value); }
    public double LineSize { get => GetValue(LineSizeProperty); set => SetValue(LineSizeProperty, value); }

    /// At least this many lines are shown; more when the track list it sits
    /// over is taller (the markup puts both in one cell).
    private const int MinLines = 5;
    private double Size => LineSize * 1.2;
    private double Pitch => Math.Ceiling(Size * 1.75);

    private readonly StackPanel _lines = new();
    private readonly List<TextBlock> _blocks = [];
    private readonly DispatcherTimer _timer;
    private int _current = -1;
    private double _y;
    private bool _attached;
    private bool _fresh;

    public LyricsView()
    {
        ClipToBounds = true;
        // The lines fade in at the foot and out at the head rather than being cut
        // off by the edge of the window: each line's own opacity, from where it
        // is in the window (Fade). It was an OpacityMask on this border, and on
        // the screen (not in the app's own snapshots) the mask followed the
        // moving column of lines instead of the window, so a song's first lines
        // were always faded, wherever they were ("the top line is always faded
        // out even though it is nowhere near the top", 2026-11-01).
        // The lines are a column taller than the window, moved by a render
        // transform: no layout pass is needed for them to move, and none is
        // waited for (see Tick).
        _lines.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Child = _lines;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Tick());
        AttachedToVisualTree += (_, _) => { _attached = true; Run(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; _timer.Stop(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) Run();
            else if (e.Property == LyricsProperty || e.Property == LineSizeProperty) Rebuild();
            else if (e.Property == BrightProperty || e.Property == DimProperty) Paint();
        };
        Rebuild();
    }

    /// As tall as it is given, or MinLines: never as tall as all the lines,
    /// which would make the album as long as the song.
    protected override Size MeasureOverride(Size available)
    {
        _lines.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, MinLines * Pitch);
    }

    /// The column of lines at its whole length, from the top; Tick moves it.
    protected override Size ArrangeOverride(Size final)
    {
        _lines.Arrange(new Rect(0, 0, final.Width, _lines.DesiredSize.Height));
        return final;
    }

    private void Run()
    {
        if (IsVisible && _attached) _timer.Start(); else _timer.Stop();
    }

    private void Rebuild()
    {
        _blocks.Clear();
        _lines.Children.Clear();
        _current = -1;
        _y = 0;
        _fresh = true;
        MinHeight = MinLines * Pitch;
        var lyrics = Lyrics;
        if (lyrics is null) return;
        foreach (var line in lyrics.Lines)
        {
            var block = new TextBlock
            {
                Text = line.Text.Length > 0 ? line.Text : lyrics.Timed ? "♪" : " ",
                FontSize = Size,
                // One line each, of one height: a line's place is its number
                // times the pitch, known before anything is laid out. A line too
                // long for the window ends in an ellipsis rather than wrapping.
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                Height = Pitch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (lyrics.Timed && line.At is { } at)
            {
                block.Cursor = new Cursor(StandardCursorType.Hand);
                block.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) return;
                    (DataContext as PanelRow)?.Seek?.Invoke(at);
                    e.Handled = true;
                };
            }
            _blocks.Add(block);
            _lines.Children.Add(block);
        }
        _lines.RenderTransform = null;
        Paint();
    }

    /// Timed: the current line bright, the rest dim. Plain: all bright.
    private void Paint()
    {
        var timed = Lyrics?.Timed == true;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var now = timed && i == _current;
            _blocks[i].Foreground = !timed || now ? Bright : Dim;
            _blocks[i].FontWeight = now ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void Tick()
    {
        if (Lyrics is not { } lyrics || _blocks.Count == 0 || DataContext is not PanelRow { Position: { } where }) return;
        // Nothing is placed until the window has its real height. Placed by a
        // guess at the moment it appeared (the least height, a third of the
        // real one), the first line went near the top, into the fade, and the
        // song's opening was missed ("the first line was totally missed",
        // 2026-11-01).
        if (Bounds.Height <= 0) return;
        var (position, duration) = where();
        var view = Bounds.Height;
        double target;

        if (lyrics.Timed)
        {
            // A touch early, so the line lights as it is sung, not after.
            var at = position + TimeSpan.FromMilliseconds(150);
            // The first line is the current one from the start, lit, through an
            // intro before its time ("start at the first line whether or not
            // the time is yet synced", 2026-11-01): the song has begun, and the
            // words it begins with are the ones to show.
            var now = 0;
            for (var i = 0; i < lyrics.Lines.Count; i++)
                if (lyrics.Lines[i].At <= at) now = i; else break;
            if (now != _current) { _current = now; Paint(); }
            // The line being sung is held in the middle, the first one too
            // before its time, so nothing moves when it starts (it used to wait
            // at the top and snap down: "it's like the lyrics don't sync until
            // the first time code").
            // By arithmetic, not by where layout has put the line so far: read
            // from the layout, the first line of a new song was placed before
            // its lines had been measured and sat off the top of the window, dim,
            // until the second line came ("line 1 text was off the screen on
            // both systems", 2026-11-01).
            target = now * Pitch + Pitch / 2 - view / 2;
        }
        else
        {
            var travel = Math.Max(0, _blocks.Count * Pitch - view);
            var share = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
            target = share * travel;
        }

        // New words (another song) start where they belong, not gliding in
        // from wherever the last song's were.
        if (_fresh) { _y = target; _fresh = false; }
        _y = Motion.Reduced || Math.Abs(target - _y) < 0.5 ? target : _y + (target - _y) * 0.18;
        _lines.RenderTransform = new TranslateTransform(0, -_y);
        Fade(view);
    }

    /// The share of the window, top and bottom, over which lines fade out.
    private const double FadeShare = 0.14;

    private void Fade(double view)
    {
        var edge = Math.Max(1, view * FadeShare);
        for (var i = 0; i < _blocks.Count; i++)
        {
            var middle = i * Pitch + Pitch / 2 - _y;
            var fromEdge = Math.Min(middle, view - middle);
            _blocks[i].Opacity = Math.Clamp(fromEdge / edge, 0, 1);
        }
    }
}
