// AlbumWall - the keyboard's ring, light or dark to suit what it sits on.
//
// The ring is drawn over the cover's edge (the tile clips anything outside it),
// and a light ring vanished on light sleeves, "leaving you wondering what is
// selected" (Dave, 2026-10-23). His pick of the options: the ring changes color
// to contrast with the cover, light by default, dark where light would not show,
// with a thin light line round the outside of the dark one (his, on seeing it
// plain: "it's too dark by itself"; a line inside it was not obvious enough), and
// the light one the exact inverse, a dark line round it.
//
// What lies under the ring depends on how the cover sits in its tile. One that
// fills it puts its own edge there, measured once with the panel's colors
// (Palette.Analyze: how much of the edge each ring reads against). One that is
// small sits inset on the wall, so the ring is on the wall's ground; an oblong
// one is half and half. A tile still waiting for its cover, or with none,
// shows its placeholder ground.

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AlbumWall.App;

public static class KeyRing
{
    public static readonly Color LightColor = Color.Parse("#F2EDE4");
    public static readonly Color DarkColor = Color.Parse("#1A1612");
    private static readonly IBrush Light = new ImmutableSolidColorBrush(LightColor);
    private static readonly IBrush Dark = new ImmutableSolidColorBrush(DarkColor);

    /// Paints the ring (Border.keyring and the Border.keyline inside it): a thin
    /// line at the very edge of the tile and a band inside it, on the cover, in
    /// the opposite color. Dark: a light line round a dark band. Light: the
    /// inverse, a dark line round a light band, so the two are the same ring with
    /// the colors swapped (his, 2026-10-23).
    public static void Paint(Border ring, bool dark)
    {
        ring.BorderBrush = dark ? Light : Dark;
        if (ring.Child is Border band) band.BorderBrush = dark ? Dark : Light;
    }

    /// WCAG 2's floor for a focus indicator and other non-text marks.
    private const double Floor = 3;

    public static bool Reads(Color ring, Color under) => Contrast.Ratio(ring, under) >= Floor;

    /// Whether this album's tile, `tile` wide, on a wall of `wall`, takes the
    /// dark ring: only where dark reads against more of what is under it.
    public static bool DarkFor(AlbumVm album, double tile, Color wall)
    {
        double light, dark;
        if (album.Cover is null || album.HasNoArt)
        {
            var ground = album.Placeholder is ISolidColorBrush b ? b.Color : wall;
            (light, dark) = (Reads(LightColor, ground) ? 1 : 0, Reads(DarkColor, ground) ? 1 : 0);
        }
        else
        {
            var edge = Palette.For(album.Album);
            double onWall = album.CoverMax < tile - 0.5 ? 1                              // small: inset on the wall
                          : album.CoverStretch == Stretch.Uniform ? 0.5 : 0;             // oblong: wall on two sides
            light = (1 - onWall) * edge.EdgeLight + onWall * (Reads(LightColor, wall) ? 1 : 0);
            dark = (1 - onWall) * edge.EdgeDark + onWall * (Reads(DarkColor, wall) ? 1 : 0);
        }
        return dark > light;
    }
}
