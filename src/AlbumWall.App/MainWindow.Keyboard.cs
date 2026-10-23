// AlbumWall - moving about the wall with the keyboard.
//
// Roadmap, "Arrow-key navigation of the wall" (his go, 2026-10-21: "yes,
// shift+arrows for seek, go ahead"). The arrows move from cover to cover, ↓ from
// the open album goes into its tracks, Enter opens an album or plays a track, Esc
// comes back out. Seeking moved to Shift+← →: the arrows have to MOVE, here and on
// a kiosk's remote, which sends nothing else.
//
// WHICH WAY THE TRACKS ARE. The panel usually opens under its album, but opening
// an album in the row under the open one swaps in place and leaves the panel
// where it was, above (_panelAbove). Then the arrows follow the screen: ↑ from the
// cover goes into the tracks, ↓ past the last track comes back to the cover, and
// ↑ past the first goes on up to the row above the panel (found by Dave on
// Hambench, 2026-10-22: "you press ↓ to reach a track list that's above you").
//
// THE CURSOR IS AN ALBUM, NOT A CONTROL. The wall is virtualized, so the cover to
// move to may not exist yet as an element: the album is chosen first, the wall
// scrolled to it by arithmetic (WallView knows every row's offset), and only then,
// once it has been laid out, its tile is given real keyboard focus. That focus is
// what a screen reader announces and what draws the ring (Button.tile:focus-visible
// in the markup), and a click never shows the ring, because a click's focus is not
// "visible" focus.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AlbumWall.App;

public partial class MainWindow
{
    /// The album the keyboard is on, and the track within it (-1: the cover).
    private AlbumVm? _keyAlbum;
    private int _trackCursor = -1;

    /// The wall's keys. True when the key was used.
    private bool WallKey(Key key, bool typing)
    {
        if (_visible.Count == 0) return false;

        // From the search box only ↓ leaves it, onto the first album found.
        if (typing)
        {
            if (key != Key.Down) return false;
            MoveTo(_visible[0]);
            return true;
        }

        if (_trackCursor >= 0 && _keyAlbum is not null && ReferenceEquals(_keyAlbum, _open))
            return TrackKey(key);

        var at = _keyAlbum is not null ? _visible.IndexOf(_keyAlbum) : -1;
        if (at < 0)
        {
            // The first key lands somewhere sensible before it moves anything: the
            // open album, else the first one on screen.
            if (key is Key.Enter) return false;
            MoveTo(_open is not null && _visible.Contains(_open) ? _open : TopAlbumInView() ?? _visible[0]);
            return true;
        }

        var perScreen = Math.Max(1, (int)(WallScroller.Viewport.Height / (CoverPx + LabelHeight + RowSpacing)));
        int To(int i) => Math.Clamp(i, 0, _visible.Count - 1);
        switch (key)
        {
            case Key.Left: MoveTo(_visible[To(at - 1)]); return true;
            case Key.Right: MoveTo(_visible[To(at + 1)]); return true;
            case Key.Up:
                // The open album's tracks are over its cover when the panel is above.
                if (_panelAbove && ReferenceEquals(_keyAlbum, _open) && TrackButtons().Count > 0) { MoveToTrack(0); return true; }
                MoveTo(_visible[To(at - _columns)]);
                return true;
            case Key.Home: MoveTo(_visible[0]); return true;
            case Key.End: MoveTo(_visible[^1]); return true;
            case Key.PageUp: MoveTo(_visible[To(at - _columns * perScreen)]); return true;
            case Key.PageDown: MoveTo(_visible[To(at + _columns * perScreen)]); return true;

            case Key.Down:
                // The open album's tracks are under its cover: ↓ goes into them.
                if (!_panelAbove && ReferenceEquals(_keyAlbum, _open) && TrackButtons().Count > 0) { MoveToTrack(0); return true; }
                MoveTo(_visible[To(at + _columns)]);
                return true;

            case Key.Enter:
                // Open it; Enter on the open one goes into its tracks.
                if (!ReferenceEquals(_keyAlbum, _open)) { SetOpen(_keyAlbum); Dispatcher.UIThread.Post(FocusCursor, DispatcherPriority.Background); }
                else if (TrackButtons().Count > 0) MoveToTrack(0);
                return true;
        }
        return false;
    }

    private bool TrackKey(Key key)
    {
        var count = TrackButtons().Count;
        if (count == 0) { _trackCursor = -1; return false; }
        switch (key)
        {
            case Key.Up:
                if (_trackCursor > 0) MoveToTrack(_trackCursor - 1);
                else if (_panelAbove) Leave(-_columns);             // off the top: on to the row above the panel
                else MoveTo(_keyAlbum!);                            // back up onto the cover
                return true;
            case Key.Down:
                if (_trackCursor < count - 1) MoveToTrack(_trackCursor + 1);
                else if (_panelAbove) MoveTo(_keyAlbum!);           // back down onto the cover
                else Leave(_columns);                               // off the end: on to the row under the album
                return true;
            case Key.Home: MoveToTrack(0); return true;
            case Key.End: MoveToTrack(count - 1); return true;
            case Key.Enter:
                if (TrackButtons() is { } tracks && _trackCursor < tracks.Count)
                    tracks[_trackCursor].Command?.Execute(tracks[_trackCursor].CommandParameter);
                return true;
            case Key.Escape:
                MoveTo(_keyAlbum!);
                return true;
        }
        return false;

        void Leave(int by) => MoveTo(_visible[Math.Clamp(_visible.IndexOf(_keyAlbum!) + by, 0, _visible.Count - 1)]);
    }

    /// Puts the keyboard on an album: scrolled into view, then focused.
    private void MoveTo(AlbumVm album)
    {
        _keyAlbum = album;
        _trackCursor = -1;
        ScrollIntoView(album);
        Dispatcher.UIThread.Post(FocusCursor, DispatcherPriority.Background);
    }

    private void MoveToTrack(int index)
    {
        _trackCursor = index;
        var tracks = TrackButtons();
        if (index < tracks.Count) tracks[index].Focus(NavigationMethod.Directional);
        tracks.ElementAtOrDefault(index)?.BringIntoView();
    }

    /// The open album's track buttons, in track order (the left column, then
    /// the right). The panel is always realized, so these always exist.
    private List<Button> TrackButtons() =>
        _panelAt >= 0 && Wall.TryGetElement(_panelAt) is { } panel
            ? panel.GetVisualDescendants().OfType<Button>()
                   .Where(b => b.Classes.Contains("track") && b.IsVisible).ToList()
            : [];

    /// Gives the cursor's tile real focus, now that it has been laid out.
    private void FocusCursor()
    {
        if (_keyAlbum is null) return;
        var row = RowIndexOf(_keyAlbum);
        if (Wall.TryGetElement(row) is not { } el) return;
        el.GetVisualDescendants().OfType<Button>()
          .FirstOrDefault(b => b.Classes.Contains("tile") && ReferenceEquals(b.DataContext, _keyAlbum))
          ?.Focus(NavigationMethod.Directional);
    }

    /// Scrolls just enough for the album's row to be wholly in view, and not at
    /// all if it already is: walking along a row must not move the wall.
    private void ScrollIntoView(AlbumVm album)
    {
        var top = Wall.OffsetOf(RowIndexOf(album));
        var height = CoverPx + LabelHeight;
        var view = WallScroller.Viewport.Height - CoveredByTransport;
        var at = WallScroller.Offset.Y;
        var max = Math.Max(0, WallScroller.Extent.Height - WallScroller.Viewport.Height);
        double? to = top < at ? top - RowSpacing
                   : top + height > at + view ? top + height - view + RowSpacing
                   : null;
        if (to is { } y) WallScroller.Offset = WallScroller.Offset.WithY(Math.Clamp(y, 0, max));
    }
}
