// AlbumWall — per-album colour, for the expansion panel.
//
// This is the point of the interaction. Opening an album has to visibly recolour
// that part of the window, so it reads as THAT ALBUM opening rather than a themed
// box appearing. See the spec, "Panel tint".
//
// Related to but distinct from the wall's ground (Ground.cs): the ground takes a
// single hue from the WHOLE library and holds saturation deliberately flat,
// because it sits behind everything permanently. A panel is temporary, occupies
// a bounded area, and is the subject while it is open, so it can afford real
// saturation. What both share is the refusal to use an honest average as-is:
// the mean pixel of a cover is mud, and mud is not a colour worth showing.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace AlbumWall.App;

public sealed record AlbumPalette(
    double Hue,
    double Saturation,
    IBrush Panel,      // the panel's own ground
    IBrush Surface,    // the track list sitting on the panel
    IBrush Accent,     // disc headers, the selection ring, active state
    IBrush Text,       // body text on Panel/Surface
    IBrush TextDim);

public static class Palette
{
    private static readonly ConcurrentDictionary<string, AlbumPalette> Cache = new();

    /// A neutral warm palette for art that yields no usable colour — greyscale
    /// covers, or an album with no art at all.
    public static AlbumPalette Neutral { get; } = Build(28, 0.10);

    public static AlbumPalette For(Domain.Album album)
    {
        var key = album.ArtPath ?? album.ArtEmbeddedIn;
        if (key is null) return Neutral;
        return Cache.GetOrAdd(key, static k =>
        {
            var (hue, sat) = Analyse(k);
            return sat <= 0.02 ? Neutral : Build(hue, sat);
        });
    }

    /// Builds the whole palette from one hue and one saturation.
    ///
    /// Lightness is NOT taken from the art. It is assigned here, so that panel
    /// text clears 4.5:1 against the panel for every album in the library rather
    /// than for the ones that happen to have dark covers. A bright cover would
    /// otherwise produce a panel its own text could not be read on.
    private static AlbumPalette Build(double hue, double sat)
    {
        // Saturation is allowed to matter, but within bounds: below ~0.18 the
        // tint reads as a mistake rather than a choice, and above ~0.45 the panel
        // starts competing with the artwork it is describing.
        var s = Math.Clamp(sat, 0.18, 0.45);

        return new AlbumPalette(
            Hue: hue,
            Saturation: s,
            Panel:   Solid(hue, s,          0.15),
            Surface: Solid(hue, s * 0.85,   0.20),
            // The accent carries the album's colour at full strength. It is used
            // on small elements only, where saturation is legible rather than
            // overwhelming.
            Accent:  Solid(hue, Math.Clamp(s * 1.9, 0.40, 0.70), 0.62),
            // Text is tinted a few points toward the album hue rather than pure
            // white: on a coloured panel, neutral white reads as a foreign layer.
            Text:    Solid(hue, 0.08, 0.95),
            TextDim: Solid(hue, 0.10, 0.68));
    }

    private static IBrush Solid(double h, double s, double l) =>
        new ImmutableSolidColorBrush(FromHsl(h, s, l));

    /// Saturation- and population-weighted colour of one cover.
    ///
    /// The hue is a circular mean, for the same reason as in Ground: hue wraps,
    /// so a plain average of 350 and 10 degrees returns their opposite. The
    /// saturation returned is the weighted mean, which is what tells us whether
    /// this album has a colour at all or is a black-and-white sleeve.
    private static (double Hue, double Saturation) Analyse(string source)
    {
        Avalonia.Media.Imaging.Bitmap? bmp = null;
        try
        {
            bmp = ArtCache.DecodeTiny(source, 16);
            if (bmp is null) return (0, 0);

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
                var bgr = bmp.Format != PixelFormat.Rgba8888;

                double sx = 0, sy = 0, wsum = 0, satsum = 0;
                for (var i = 0; i < size; i += 4)
                {
                    double r = bgr ? bytes[i + 2] : bytes[i];
                    double g = bytes[i + 1];
                    double b = bgr ? bytes[i] : bytes[i + 2];
                    var (hh, ss, ll) = ToHsl(r / 255, g / 255, b / 255);

                    var weight = ss * (1 - Math.Abs(2 * ll - 1));
                    if (weight <= 0) continue;

                    var a = hh * Math.PI / 180;
                    sx += weight * Math.Cos(a);
                    sy += weight * Math.Sin(a);
                    wsum += weight;
                    satsum += ss * weight;
                }

                if (wsum <= 0) return (0, 0);
                var hue = Math.Atan2(sy, sx) * 180 / Math.PI;
                if (hue < 0) hue += 360;
                return (hue, satsum / wsum);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch
        {
            return (0, 0);
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

    private static Color FromHsl(double h, double s, double l)
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
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
