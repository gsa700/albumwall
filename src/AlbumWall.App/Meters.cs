// AlbumWall - the meters: the VU pair, the LED bars and the 31-band analyzer, one
// of them at a time in a strip of one fixed size, changed by a click.
//
// Agreed 2026-10-27 (Hambench's mockup, his calls on Control): the same three in
// compact mode and at the right end of the play bar, cycled VU, LED, analyzer and
// back as Winamp did, the style's name showing for a moment so a click does not
// look like a fault. ONE WIDTH FOR ALL THREE, set by the VU pair, so nothing
// beside the strip moves when the style changes. Each place keeps its own choice
// (Settings.BarMeter, CompactMeter); one setting says whether every meter reads
// before ReplayGain or after it (Settings.MetersAfterGain).
//
// The mockup: https://claude.ai/artifact/4bE3KuLx29wzJbM5F4xKbv
//
// THE STRIP IS A DISPLAY, as NowPlayingPanel is: whoever owns it feeds it readings
// (Feed) on the meter timer's tick, hears the click (Cycled) and says which style
// to show. It is drawn at its real size rather than scaled from one design, so a
// segment's 1 px gap stays a pixel on the play bar.

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AlbumWall.Playback;

namespace AlbumWall.App;

public enum MeterStyle { Vu, Led, Rta }

public static class MeterStyles
{
    public static MeterStyle Parse(string? s) => s switch
    {
        "led" => MeterStyle.Led,
        "rta" => MeterStyle.Rta,
        _ => MeterStyle.Vu,
    };

    public static string Key(this MeterStyle s) => s switch
    {
        MeterStyle.Led => "led",
        MeterStyle.Rta => "rta",
        _ => "vu",
    };

    public static MeterStyle Next(this MeterStyle s) => s switch
    {
        MeterStyle.Vu => MeterStyle.Led,
        MeterStyle.Led => MeterStyle.Rta,
        _ => MeterStyle.Vu,
    };

    /// What the name flash says, and the strip's name for a screen reader.
    public static string Name(this MeterStyle s) => s switch
    {
        MeterStyle.Led => "LED",
        MeterStyle.Rta => "31-BAND",
        _ => "VU",
    };
}

/// The colors the LED bars and the analyzer are drawn in. The VU keeps its own
/// yellow faces wherever it is.
public sealed record MeterInk(IBrush Lit, IBrush Unlit, IBrush Amber, IBrush Red, IBrush Label)
{
    /// On the dark now-playing view.
    public static readonly MeterInk OnDark = new(
        new ImmutableSolidColorBrush(Color.Parse("#E4DED4")),
        new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF")),
        new ImmutableSolidColorBrush(Color.Parse("#E8A23A")),
        new ImmutableSolidColorBrush(Color.Parse("#E0533F")),
        new ImmutableSolidColorBrush(Color.Parse("#9C968C")));

    /// On the play bar's light tone: the bar's own dark text, its unlit segments the
    /// same ink faint, the mockup's amber and red at the top.
    public static MeterInk OnLight(IBrush ink, IBrush dim)
    {
        var c = (ink as ISolidColorBrush)?.Color ?? Color.Parse("#15202B");
        return new MeterInk(ink, new ImmutableSolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B)),
                            new ImmutableSolidColorBrush(Color.Parse("#A35F00")),
                            new ImmutableSolidColorBrush(Color.Parse("#B0302A")), dim);
    }
}

/// Where a strip sits, which sets its size.
public enum MeterPlace { Bar, Panel }

public sealed class MeterStrip : Panel
{
    /// A click (or a tap) on the strip: the owner moves the style on.
    public event Action? Cycled;

    private readonly StackPanel _vu;
    private readonly VuMeter _left, _right;
    private readonly LedMeter _led;
    private readonly RtaMeter _rta;
    private readonly Border _flash;
    private readonly TextBlock _flashText;
    private DispatcherTimer? _flashTimer;
    private MeterStyle _style = MeterStyle.Vu;

    public MeterStrip(MeterPlace place)
    {
        // The VU pair sets the width: two faces and the gap between them. On the
        // panel they are the faces' own 190 x 124; on the play bar 100 x 65 (the
        // mockup's two faces and gap came to about 214), which fits the bar's 82.
        var (faceW, faceH, gap) = place == MeterPlace.Panel ? (190.0, 124.0, 14.0) : (100.0, 65.0, 14.0);
        Width = 2 * faceW + gap;
        Height = faceH;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;       // the gaps between the faces take the click too

        _left = new VuMeter { Width = faceW, Height = faceH };
        _right = new VuMeter { Width = faceW, Height = faceH };
        _vu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap, Children = { _left, _right } };
        _led = new LedMeter(place) { IsVisible = false };
        _rta = new RtaMeter(place) { IsVisible = false };

        _flashText = new TextBlock
        {
            FontFamily = App.Mono,
            FontSize = place == MeterPlace.Panel ? 22 : 11,
            FontWeight = FontWeight.Medium,
            LetterSpacing = place == MeterPlace.Panel ? 2.4 : 1.2,
            Foreground = new ImmutableSolidColorBrush(Color.Parse("#F3EEE6")),
        };
        _flash = new Border
        {
            Child = _flashText,
            Background = new ImmutableSolidColorBrush(Color.Parse("#CC15120D")),
            CornerRadius = new CornerRadius(4),
            Padding = place == MeterPlace.Panel ? new Thickness(14, 6) : new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0,
            Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
        };

        Children.Add(_vu);
        Children.Add(_led);
        Children.Add(_rta);
        Children.Add(_flash);

        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;           // not the window's "move me", nor the view's background
            Cycle();
        };
        Avalonia.Automation.AutomationProperties.SetName(this, "Meters: VU. Click to change their style");
    }

    public MeterStyle Style => _style;

    /// What a click does: asks the owner to move the style on.
    public void Cycle() => Cycled?.Invoke();

    /// Which meter shows. `flash` puts its name up for a moment, for a change a
    /// person asked for; a style restored from the settings comes up quietly.
    public void Show(MeterStyle style, bool flash)
    {
        if (style != _style)
        {
            _style = style;
            _vu.IsVisible = style == MeterStyle.Vu;
            _led.IsVisible = style == MeterStyle.Led;
            _rta.IsVisible = style == MeterStyle.Rta;
            Avalonia.Automation.AutomationProperties.SetName(this, $"Meters: {style.Name()}. Click to change their style");
        }
        if (!flash) return;
        _flashText.Text = style.Name();
        _flash.Opacity = 1;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _flashTimer.Tick += (_, _) => { _flashTimer?.Stop(); _flashTimer = null; _flash.Opacity = 0; };
        _flashTimer.Start();
    }

    public MeterInk Ink
    {
        set { _led.Ink = value; _rta.Ink = value; }
    }

    /// Whether the analyzer is up, and so whether there is any point in taking bands.
    public bool WantsBands => IsEffectivelyVisible && _style == MeterStyle.Rta;

    /// The readings for this tick, and how long since the last. `bands` is null
    /// when none were taken; the analyzer's bars fall on their own then.
    public void Feed(MeterLevels m, float[]? bands, double seconds)
    {
        switch (_style)
        {
            case MeterStyle.Vu:
                _left.SetLevel(m.RmsLeft);
                _right.SetLevel(m.RmsRight);
                _left.Step(seconds);
                _right.Step(seconds);
                break;
            case MeterStyle.Led:
                _led.Step(m.PeakLeft, m.PeakRight, seconds);
                break;
            case MeterStyle.Rta:
                _rta.Step(bands, seconds);
                break;
        }
    }
}

/// Two rows of 32 segments, left and right, reading the PEAK of each block: fast
/// attack, slow release, and a held peak that waits before it falls (the mockup's
/// ballistics, in segments per second rather than per 40 ms frame).
public sealed class LedMeter : Control
{
    private const int Segments = 32;
    /// The scale, in dBFS: -30 at the first segment, +3 past the last (a level
    /// after ReplayGain can pass 0). Amber from the 24th (about -6), red from the
    /// 30th (0 dBFS: the top of the record).
    private const double FloorDb = -30, TopDb = 3;
    private const int AmberFrom = 23, RedFrom = 29;
    private const double Release = 20;          // segments a second
    private const double PeakHold = 0.9;        // seconds
    private const double PeakFall = 12.5;       // segments a second

    private readonly double _rowH, _rowGap, _labelW;
    private readonly double _labelSize;
    private readonly Bar _l = new(), _r = new();
    private MeterInk _ink = MeterInk.OnDark;
    private FormattedText? _lText, _rText;

    private sealed class Bar
    {
        public double Level, Peak, Held;

        public bool Step(double db, double seconds)
        {
            var target = double.IsNegativeInfinity(db) || double.IsNaN(db)
                ? 0 : Math.Clamp((db - FloorDb) / (TopDb - FloorDb) * Segments, 0, Segments);
            var (level, peak) = (Level, Peak);
            Level = target > Level ? target : Math.Max(target, Level - Release * seconds);
            if (Level >= Peak) { Peak = Level; Held = 0; }
            else if ((Held += seconds) > PeakHold) Peak = Math.Max(Level, Peak - PeakFall * seconds);
            return Lit(level) != Lit(Level) || Lit(peak) != Lit(Peak);
        }

        public static int Lit(double v) => (int)Math.Round(v);
    }

    public LedMeter(MeterPlace place)
    {
        (_rowH, _rowGap, _labelW, _labelSize) = place == MeterPlace.Panel ? (34.0, 16.0, 22.0, 18.0) : (12.0, 8.0, 12.0, 10.0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public MeterInk Ink
    {
        set { _ink = value; _lText = _rText = null; InvalidateVisual(); }
    }

    public void Step(double leftDb, double rightDb, double seconds)
    {
        var moved = _l.Step(leftDb, seconds);
        moved |= _r.Step(rightDb, seconds);
        if (moved) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        _lText ??= Label("L");
        _rText ??= Label("R");
        var top = (Bounds.Height - 2 * _rowH - _rowGap) / 2;
        Row(ctx, _l, _lText, top);
        Row(ctx, _r, _rText, top + _rowH + _rowGap);
    }

    private FormattedText Label(string s) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(App.Mono), _labelSize, _ink.Label);

    private void Row(DrawingContext ctx, Bar bar, FormattedText label, double y)
    {
        ctx.DrawText(label, new Point(0, y + (_rowH - label.Height) / 2));
        const double gap = 1;
        var x0 = _labelW;
        var w = (Bounds.Width - x0 - (Segments - 1) * gap) / Segments;
        int lit = Bar.Lit(bar.Level), peak = Bar.Lit(bar.Peak) - 1;
        for (var i = 0; i < Segments; i++)
        {
            var on = i < lit || i == peak;
            var brush = !on ? _ink.Unlit : i >= RedFrom ? _ink.Red : i >= AmberFrom ? _ink.Amber : _ink.Lit;
            ctx.FillRectangle(brush, new Rect(x0 + i * (w + gap), y, w, _rowH));
        }
    }
}

/// The 31-band analyzer: third-octave bars from 20 Hz to 20 kHz with peak caps,
/// a few frequencies marked under them.
public sealed class RtaMeter : Control
{
    /// The scale, in dB of a band (a full-scale sine reads 0, Spectrum): one number
    /// to tune by ear, as the VU's zero is. Music spreads its energy over many
    /// bands, so each sits well below the whole: measured 2026-10-27 on AC/DC and
    /// Amy Winehouse, the busy bands -17 to -40, the bottom octave and the top
    /// -60 to -80. -48 at the floor left both ends dark; -60 to -6 shows them,
    /// with the loudest band near the top.
    private const double FloorDb = -60, TopDb = -6;
    private const double Release = 1.0;         // the bar's whole height a second
    private const double PeakHold = 0.6;        // seconds
    private const double PeakFall = 0.55;       // of the height a second

    private static readonly (int Band, string Text)[] Marks = [(0, "20"), (7, "100"), (17, "1k"), (27, "10k")];

    private readonly double _labelH, _labelSize, _gap, _capH;
    private readonly double[] _h = new double[Spectrum.BandCount];      // 0..1
    private readonly double[] _peak = new double[Spectrum.BandCount];
    private readonly double[] _held = new double[Spectrum.BandCount];
    private MeterInk _ink = MeterInk.OnDark;
    private FormattedText[]? _labels;
    private bool _resting = true;

    public RtaMeter(MeterPlace place)
    {
        (_labelH, _labelSize, _gap, _capH) = place == MeterPlace.Panel ? (22.0, 16.0, 3.0, 3.0) : (12.0, 9.0, 2.0, 2.0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public MeterInk Ink
    {
        set { _ink = value; _labels = null; InvalidateVisual(); }
    }

    /// `bands` in dB, or null for none this tick (paused, or the reading was not
    /// to be had): the bars fall as they would to silence.
    public void Step(float[]? bands, double seconds)
    {
        if (bands is null && _resting) return;
        var moving = false;
        for (var b = 0; b < Spectrum.BandCount; b++)
        {
            var db = bands?[b] ?? float.NegativeInfinity;
            var t = float.IsNegativeInfinity(db) || float.IsNaN(db) ? 0 : Math.Clamp((db - FloorDb) / (TopDb - FloorDb), 0, 1);
            _h[b] = t > _h[b] ? t : Math.Max(t, _h[b] - Release * seconds);
            if (_h[b] >= _peak[b]) { _peak[b] = _h[b]; _held[b] = 0; }
            else if ((_held[b] += seconds) > PeakHold) _peak[b] = Math.Max(_h[b], _peak[b] - PeakFall * seconds);
            moving |= _h[b] > 0 || _peak[b] > 0;
        }
        _resting = !moving;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var n = Spectrum.BandCount;
        var w = (Bounds.Width - (n - 1) * _gap) / n;
        var floor = Bounds.Height - _labelH;
        var room = floor - _capH - 1;
        for (var b = 0; b < n; b++)
        {
            var x = b * (w + _gap);
            ctx.FillRectangle(_ink.Unlit, new Rect(x, floor - 1, w, 1));
            var h = Math.Round(_h[b] * room);
            if (h > 0) ctx.FillRectangle(_ink.Lit, new Rect(x, floor - h, w, h));
            var p = Math.Round(_peak[b] * room);
            if (p > 0) ctx.FillRectangle(_peak[b] > 0.92 ? _ink.Red : _ink.Amber, new Rect(x, floor - p - _capH, w, _capH));
        }

        _labels ??= Marks.Select(m => new FormattedText(m.Text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(App.Mono), _labelSize, _ink.Label)).ToArray();
        for (var i = 0; i < Marks.Length; i++)
        {
            var x = Marks[i].Band * (w + _gap);
            ctx.DrawText(_labels[i], new Point(Math.Min(x, Bounds.Width - _labels[i].Width), floor + (_labelH - _labels[i].Height) / 2 + 1));
        }
    }
}
