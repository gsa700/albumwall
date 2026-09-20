// AlbumWall — the ground the wall hangs on.
//
// The background hue is DERIVED FROM THE LIBRARY'S OWN COVER ART rather than
// chosen. A hand-picked value is the designer's taste imposed on someone else's
// records; a derived one is the collection's own color, and it is right for
// every library instead of just the one it was tuned against.
//
// Only the HUE is taken from the art. Saturation and lightness are held at fixed
// low values, because an honest average of album covers is muddy — mean pixel
// saturation across this library is 0.27 and mean lightness 0.41, which as a
// background would be a gray-brown sludge that fights every cover on the wall.
// Taking the hue and imposing our own restraint keeps the covers the brightest,
// most saturated thing on screen, which is the whole point of the view.
//
// DELIBERATELY LIBRARY-WIDE, NOT PER-ALBUM. A ground that re-tinted as tiles
// scrolled past would be in constant motion behind static art — the background
// would become the thing you watch. Per-album color belongs to the expansion
// panel, where it is tied to a deliberate click and to one album at a time; see
// the spec, "Panel tint".

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AlbumWall.App;

public static class Ground
{
    /// Warm amber, used only when a library yields no usable hue at all — an
    /// empty library, or art that is entirely grayscale.
    public const double FallbackHue = 30.0;

    /// Held constant across the ramp.
    ///
    /// SATURATION IS THE LEVER THAT DECIDES WHETHER THIS READS AS BROWN. A dark,
    /// saturated orange simply IS brown, so at 0.14 the wall came out mud
    /// regardless of what the lightness did.
    ///
    /// At 0.02 the hue is barely present — a warm cast on a neutral rather than a
    /// color in its own right. The library still chooses it, and on a wall of
    /// album art that is the point: anything more competes with the covers.
    ///
    /// NOW ADJUSTABLE, because it is the lever that decides the brown question
    /// and the answer was only ever found by looking. Default stays 0.02.
    public static double Saturation { get; set; } = DefaultTint / 100.0;

    /// The same number as a percentage, which is how the control and the
    /// settings file count it.
    public const int DefaultTint = 2;

    /// Ground lightness, in percent, as a CONTINUOUS range the person sets.
    ///
    /// This started as five fixed rungs topping out at 22, chosen against the
    /// real wall on a 6K panel in 2026-09-18. He lived with the top rung and
    /// still found the app too dark — "is there a way to have a sliding scale
    /// against the library color palette?" — so the ladder became a slider and
    /// the range opened upward.
    ///
    /// THE CEILING IS SET BY TEXT CONTRAST, NOT BY TASTE. The body text is
    /// #F3EEE6, and against a ground of 40% lightness that is about 4.9:1, which
    /// still clears the 4.5:1 floor; by 46% it is under it. Going lighter than
    /// this is not a bigger number, it is a different theme — the text has to
    /// turn dark — so the slider stops where the current palette stops working.
    public const int MinLightness = 10;
    public const int MaxLightness = 40;
    /// 21, not 22: where he settled after living with the slider on two very
    /// different screens (a 6K panel at 2x, then a pair of 4K panels at 150%).
    /// One point is visible. A default, not a verdict — see DefaultChrome.
    public const int DefaultLightness = 21;

    /// How the app's own furniture separates itself from the wall.
    ///
    /// The bars were originally the ground plus four points of lightness, which
    /// is a difference you can measure but barely see. Chrome should read as a
    /// DIFFERENT MATERIAL from the wall, not a slightly different shade of it,
    /// and the strongest lever is saturation, not lightness: pulling the hue out
    /// of the furniture makes it read as metal against the warm wall, which is
    /// the distinction the eye picks up instantly.
    ///
    /// `BarDelta` is lightness relative to the ground, in points.
    public sealed record Chrome(string Name, double BarDelta, double BarSat, double EdgeDelta);

    public static readonly Chrome[] Chromes =
    [
        // Furniture sinks below the wall and drains of color: the wall becomes
        // the lit object in the room. Closest to what Firefox's dark theme does.
        new("recede", -5, 0.05, +11),
        // Furniture rises as a neutral panel — a toolbar laid over the wall.
        new("lift",    +9, 0.05, +20),
        // Near-black, fully neutral: maximum separation, the wall floats.
        new("ink",    -14, 0.03,  +9),
        // Keeps the library's warmth in the chrome but pushes it much further
        // apart in lightness than the original four points.
        new("warm",   +11, 0.16, +22),
    ];

    /// "ink". His choice once the bench had done its job: "I think I've settled
    /// on the window colors I like best: Light 21, Tint 2, INK". With that the
    /// bench is hidden, as its own comment in the markup always said it would
    /// be. These are only the values a machine STARTS from — anything already
    /// saved in settings.json wins, so the other machine keeps what it has —
    /// and he expects the right numbers to differ per screen, so the levers are
    /// meant to come back in Preferences rather than be deleted.
    public const int DefaultChrome = 2;

    public sealed record Ramp(double Hue, double Concentration, int Lightness, Chrome Chrome)
    {
        private double BarL  => Math.Clamp((Lightness + Chrome.BarDelta) / 100.0, 0.03, 0.95);
        private double EdgeL => Math.Clamp((Lightness + Chrome.EdgeDelta) / 100.0, 0.03, 0.95);

        public string GroundHex => Hex(Hue, Saturation, Lightness / 100.0);
        public string BarHex    => Hex(Hue, Chrome.BarSat, BarL);
        public string EdgeHex   => Hex(Hue, Chrome.BarSat, EdgeL);

        /// Control surfaces sitting ON the bars (the search field, buttons) have
        /// to separate from the bar, not from the wall, or they vanish into it.
        public string FieldHex  => Hex(Hue, Chrome.BarSat, Math.Clamp(BarL + 0.05, 0.03, 0.95));
        public string FieldEdgeHex => Hex(Hue, Chrome.BarSat, Math.Clamp(BarL + 0.11, 0.03, 0.95));

        /// The alpha in the wordmark: the collection's own hue at a strength
        /// nothing else in the window uses, because it is one letter and has to
        /// be seen as a different color at 21 px. Light on a dark bar and dark on
        /// a light one — it sits on the bars and in the Preferences panel,
        /// which both follow BarL, not on the wall.
        public string WordmarkHex => Hex(Hue, 0.62, BarL < 0.5 ? 0.64 : 0.36);
    }

    /// Saturation-weighted circular mean hue across the library's covers.
    ///
    /// Circular, because hue wraps: a plain mean of 350 and 10 degrees gives 180,
    /// the exact opposite of the right answer. Weighted, because a near-black or
    /// near-white pixel has a hue that is arithmetically defined and visually
    /// meaningless, and a few thousand of them would drown the real signal.
    ///
    /// Returns concentration alongside the hue: 0 means the covers agree on
    /// nothing, 1 means they are all one color. Below ~0.2 there is no real
    /// consensus and the caller should not pretend otherwise.
    public static (double Hue, double Concentration) HueOf(IEnumerable<Domain.Album> albums,
                                                           int maxCovers = 200)
    {
        var sources = albums
            .Select(a => a.ArtPath ?? a.ArtEmbeddedIn)
            .Where(p => p is not null)
            .Distinct()
            .ToList();

        // Spread the sample across the library rather than taking the first N:
        // albums arrive sorted by artist, so a prefix would be a handful of
        // artists rather than a picture of the collection.
        if (sources.Count > maxCovers)
        {
            var step = (double)sources.Count / maxCovers;
            sources = Enumerable.Range(0, maxCovers)
                                .Select(i => sources[(int)(i * step)])
                                .ToList();
        }

        double sx = 0, sy = 0, wsum = 0;
        var lock_ = new object();

        Parallel.ForEach(sources, src =>
        {
            var (lx, ly, lw) = Accumulate(src!);
            if (lw <= 0) return;
            lock (lock_) { sx += lx; sy += ly; wsum += lw; }
        });

        if (wsum <= 0) return (FallbackHue, 0);

        var hue = Math.Atan2(sy, sx) * 180 / Math.PI;
        if (hue < 0) hue += 360;
        return (hue, Math.Sqrt(sx * sx + sy * sy) / wsum);
    }

    private static (double X, double Y, double W) Accumulate(string source)
    {
        Bitmap? bmp = null;
        try
        {
            // 16 px wide is plenty: we want the average color of the artwork,
            // not its detail, and the downscale is itself the averaging.
            bmp = ArtCache.DecodeTiny(source, 16);
            if (bmp is null) return (0, 0, 0);

            var w = bmp.PixelSize.Width;
            var h = bmp.PixelSize.Height;
            var stride = w * 4;
            var size = stride * h;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                bmp.CopyPixels(new PixelRect(0, 0, w, h), buf, size, stride);
                var bytes = new byte[size];
                Marshal.Copy(buf, bytes, 0, size);

                // Avalonia hands back premultiplied BGRA on every desktop
                // backend; the channel order is what matters here, and alpha is
                // opaque for album art.
                var bgr = bmp.Format != PixelFormat.Rgba8888;

                double sx = 0, sy = 0, sw = 0;
                for (var i = 0; i < size; i += 4)
                {
                    double r = bgr ? bytes[i + 2] : bytes[i];
                    double g = bytes[i + 1];
                    double b = bgr ? bytes[i] : bytes[i + 2];
                    var (hue, sat, lig) = ToHsl(r / 255, g / 255, b / 255);

                    // Discard the hue-blind ends of the lightness range and
                    // scale by saturation: what is left is pixels that actually
                    // carry color.
                    var weight = sat * (1 - Math.Abs(2 * lig - 1));
                    if (weight <= 0) continue;

                    var a = hue * Math.PI / 180;
                    sx += weight * Math.Cos(a);
                    sy += weight * Math.Sin(a);
                    sw += weight;
                }
                return (sx, sy, sw);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch
        {
            // One unreadable cover must not cost us the ground color.
            return (0, 0, 0);
        }
        finally { bmp?.Dispose(); }
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d <= 0) return (0, 0, l);

        var s = d / (1 - Math.Abs(2 * l - 1));
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, s, l);
    }

    private static string Hex(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return $"#{Clamp(r + m):X2}{Clamp(g + m):X2}{Clamp(b + m):X2}";
    }

    private static int Clamp(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
