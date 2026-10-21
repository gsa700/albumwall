// AlbumWall - the contrast floor: text never under 4.5:1 against what is behind it.
//
// Roadmap, keyboard and screen-reader access: "contrast, which depends on his
// Light and Tint". Measured 2026-10-21 (WCAG 2 ratios, every Light 10-40, Tint
// 0-14, hue, chrome and palette): at his own settings only the artist line under
// the covers fell short (4.21); at the light end the dim text went down to 1.7,
// disc headers on blue albums to 2.4, the play controls' second line on yellow
// ones to 2.3, and light text on the Lift and Warm bars to 2.6. His call: keep
// every color as designed and move only what falls short, just far enough
// (Floor), keeping its hue, so the look stays and only the weakest cases change.

using Avalonia.Media;

namespace AlbumWall.App;

public static class Contrast
{
    /// WCAG 2's floor for ordinary text.
    public const double Text = 4.5;

    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Luminance(Color c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// `fg` as it is if it already reads against `bg`; otherwise moved in
    /// lightness, away from the ground and keeping its hue and saturation, until
    /// it does, or as far as it can go (white or black).
    public static Color Floor(Color fg, Color bg, double min = Text)
    {
        if (Ratio(fg, bg) >= min) return fg;
        var hsl = fg.ToHsl();
        var up = Luminance(fg) >= Luminance(bg);
        for (var l = hsl.L; up ? l <= 1 : l >= 0; l += up ? 0.01 : -0.01)
        {
            var c = HslColor.ToRgb(hsl.H, hsl.S, Math.Clamp(l, 0, 1), fg.A / 255.0);
            if (Ratio(c, bg) >= min) return c;
        }
        return up ? Colors.White : Colors.Black;
    }

    /// The lightest a ground of this hue and saturation may be while `text`
    /// still reads on it: how far a bar may rise before light text fails.
    public static double MaxGroundLightness(Color text, double hue, double sat, double min = Text)
    {
        for (var l = 1.0; l > 0; l -= 0.005)
            if (Ratio(text, HslColor.ToRgb(hue, sat, l, 1)) >= min) return l;
        return 0;
    }

    public static IBrush Brush(Color c) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c);
}
