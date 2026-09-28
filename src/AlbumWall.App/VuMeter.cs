// AlbumWall - one analog VU meter, drawn and moved like the real thing.
//
// His pick from the front-panel mockups, 2026-09-28: the pair of analog meters
// with "a vintage yellow(ed) look", and "lets lose the L and R only on the analog
// meters". The face is the mockup's (cream-to-amber glow, dark brown ink, a brick
// red zone past 0, a black needle, a dark lip over the pivot), drawn at the
// mockup's 190 x 124 and scaled by whoever lays it out.
//
// A REAL VU SCALE IS PRINTED IN VOLTS. The needle's deflection is proportional to
// the signal's voltage, so the dB marks crowd toward the top: +3 dB is full
// scale, 0 VU sits at 71% of the arc (1 / 1.413), -20 at 7%. The marks here are
// placed by that rule rather than spaced by eye, which is why the scale looks the
// way meters have always looked.
//
// AND ITS NEEDLE HAS WEIGHT. A VU meter is specified to reach 99% of a step in
// 300 ms with a little over 1% overshoot. The needle here is a spring and a
// damper chasing the level (natural frequency 13.5 rad/s, damping 0.77, which
// simulates to ~300 ms and ~1.3%), stepped on every frame. That, not the number
// fed in, is what makes it look like a meter rather than a bar graph.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AlbumWall.App;

public sealed class VuMeter : Control
{
    private const double FaceW = 190, FaceH = 124;
    private static readonly Point Pivot = new(95, 132);
    private const double ArcR = 98;
    private const double NeedleR = 112;

    /// Where 0 VU sits, in dBFS of RMS. -18 is the broadcast alignment and pins
    /// every modern master in the red; measured on his library (2026-09-28),
    /// average levels run -17 to -10 dBFS with bursts to -5, so -10 lets the
    /// loud records swing around 0 and the quiet ones sit lower, as a meter
    /// should show them. One number to tune by ear.
    public const double ZeroVuDbfs = -10;

    private const double FullScale = 1.4125;           // +3 dB, as a voltage ratio

    // Ballistics.
    private const double Omega = 13.5;
    private const double Damping = 0.77;

    private double _target;     // 0..~1.05 of the arc
    private double _x;          // needle position, same units
    private double _v;
    private long _lastTick;
    private bool _animating;

    public VuMeter()
    {
        Width = FaceW;
        Height = FaceH;
    }

    /// The level to chase, in dBFS of RMS. Negative infinity (silence, paused,
    /// nothing loaded) lets the needle fall back to its stop.
    public void SetLevel(double dbfs)
    {
        var volts = double.IsNegativeInfinity(dbfs) || double.IsNaN(dbfs)
            ? 0
            : Math.Pow(10, (dbfs - ZeroVuDbfs) / 20);
        // The needle hits the pin a little past +3 and goes no further.
        _target = Math.Min(volts / FullScale, 1.05);
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _animating = true;
        _lastTick = 0;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan now)
    {
        var t = (long)now.TotalMilliseconds;
        var dt = _lastTick == 0 ? 1 / 60.0 : Math.Clamp((t - _lastTick) / 1000.0, 0.001, 0.05);
        _lastTick = t;

        // Semi-implicit Euler, in small steps so a slow frame cannot throw the
        // needle past where a real one could go.
        var steps = (int)Math.Ceiling(dt / 0.005);
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var a = Omega * Omega * (_target - _x) - 2 * Damping * Omega * _v;
            _v += a * h;
            _x += _v * h;
            if (_x < -0.02) { _x = -0.02; _v = 0; }    // the rest pin
            if (_x > 1.05) { _x = 1.05; _v = 0; }      // the end pin
        }
        InvalidateVisual();

        // At rest and staying there: stop asking for frames.
        if (Math.Abs(_x - _target) < 0.0005 && Math.Abs(_v) < 0.0005)
        {
            _animating = false;
            return;
        }
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(Frame);
        else _animating = false;
    }

    // ---- drawing -----------------------------------------------------------

    private static readonly IBrush Face = new RadialGradientBrush
    {
        Center = new RelativePoint(0.5, 0.78, RelativeUnit.Relative),
        GradientOrigin = new RelativePoint(0.5, 0.78, RelativeUnit.Relative),
        RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#F2DFA6"), 0),
            new GradientStop(Color.Parse("#E2C47E"), 0.6),
            new GradientStop(Color.Parse("#C9A45C"), 1),
        }
    }.ToImmutable();

    private static readonly IPen Rim = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#5A4A2C")), 1);
    private static readonly ImmutableSolidColorBrush InkBrush = new ImmutableSolidColorBrush(Color.Parse("#2A2318"));
    private static readonly ImmutableSolidColorBrush RedBrush = new ImmutableSolidColorBrush(Color.Parse("#B3362B"));
    private static readonly IPen Ink = new ImmutablePen(InkBrush, 1.5);
    private static readonly IPen RedThin = new ImmutablePen(RedBrush, 1.5);
    private static readonly IPen RedZone = new ImmutablePen(RedBrush, 5);
    private static readonly IPen Needle = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#15120D")), 2,
                                                           lineCap: PenLineCap.Round);
    private static readonly IBrush Lip = new ImmutableSolidColorBrush(Color.Parse("#2B2418"));
    private static readonly IBrush VuInk = new ImmutableSolidColorBrush(Color.Parse("#4A3E28"));

    /// The dB marks, with which get a longer tick and a number.
    private static readonly (double Db, bool Major, string? Label)[] Marks =
    [
        (-20, true, "20"), (-10, true, "10"), (-7, true, "7"), (-5, true, "5"), (-3, true, "3"),
        (-2, false, null), (-1, false, null), (0, true, "0"), (1, false, null), (2, false, null), (3, true, "+3"),
    ];

    private static double Position(double db) => Math.Pow(10, db / 20) / FullScale;
    private static double Angle(double pos) => (-48 + 96 * pos) * Math.PI / 180;
    private static Point At(double pos, double r)
    {
        var a = Angle(pos);
        return new Point(Pivot.X + r * Math.Sin(a), Pivot.Y - r * Math.Cos(a));
    }

    private static Geometry Arc(double from, double to, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(At(from, r), false);
        c.ArcTo(At(to, r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.EndFigure(false);
        return g;
    }

    private static readonly Geometry ScaleArc = Arc(Position(-20), Position(0), ArcR);
    private static readonly Geometry RedArc = Arc(Position(0), 1, ArcR + 3);

    private static readonly Typeface Numbers = new("Consolas, DejaVu Sans Mono, monospace", FontStyle.Normal, FontWeight.Medium);
    private static readonly Typeface Caption = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public override void Render(DrawingContext ctx)
    {
        var scale = Math.Min(Bounds.Width / FaceW, Bounds.Height / FaceH);
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));
        var face = new Rect(0.5, 0.5, FaceW - 1, FaceH - 1);
        using var clip = ctx.PushClip(new RoundedRect(new Rect(0, 0, FaceW, FaceH), 10));

        ctx.DrawRectangle(Face, Rim, new RoundedRect(face, 10));

        ctx.DrawGeometry(null, Ink, ScaleArc);
        ctx.DrawGeometry(null, RedZone, RedArc);

        foreach (var (db, major, label) in Marks)
        {
            var p = Position(db);
            var red = db > 0;
            ctx.DrawLine(red ? RedThin : Ink, At(p, ArcR), At(p, ArcR + (major ? 11 : 7)));
            if (label is null) continue;
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight, Numbers, 11, red ? RedBrush : InkBrush);
            var at = At(p, ArcR + 20);
            // Kept inside the face: at the ends of the arc a centred label would
            // hang off the edge (the "+3" did, first light).
            var x = Math.Clamp(at.X - text.Width / 2, 4, FaceW - 4 - text.Width);
            ctx.DrawText(text, new Point(x, at.Y - text.Height / 2));
        }

        var vu = new FormattedText("VU", System.Globalization.CultureInfo.InvariantCulture,
                                   FlowDirection.LeftToRight, Caption, 16, VuInk);
        ctx.DrawText(vu, new Point(Pivot.X - vu.Width / 2, 84));

        ctx.DrawLine(Needle, Pivot, At(_x, NeedleR));

        // The lip over the pivot, drawn last so the needle's foot is under it.
        var lip = new StreamGeometry();
        using (var c = lip.Open())
        {
            c.BeginFigure(new Point(0.5, 106), true);
            c.LineTo(new Point(FaceW - 0.5, 106));
            c.LineTo(new Point(FaceW - 0.5, FaceH));
            c.LineTo(new Point(0.5, FaceH));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(Lip, null, lip);
    }
}
