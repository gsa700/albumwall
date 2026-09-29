// AlbumWall - the front panel: the now-playing view in a window of its own, for a
// second screen on a dedicated player (the stereo component: a Pi, the wall on its
// main screen, a 1480 x 320 bar display on the front).
//
// Roadmap, "Compact view, and the same view as a hardware front panel", step 2.
// Only ever opened by --front-panel on the command line, which only a kiosk's
// launcher passes: the desktop app never has this window.
//
// IT DOES NOT PLACE ITSELF. On Wayland an application cannot choose the output a
// window goes to, so the kiosk's compositor does: a labwc window rule matches this
// window by its TITLE (FrontPanelWindow.WindowTitle), moves it to the panel's
// output and makes it full screen. Keep the title and that rule in step.
//
// BY TITLE ONLY, not identifier="albumwall": the app_id is set after the window
// appears, and rules are applied when it appears.
//
// IT IS A DISPLAY. No controls, no window buttons, never takes the keyboard (the
// wall keeps it, and with it the remote's keys). The view is drawn at its design
// size and scaled to the window; on the panel that is exactly 1:1.
//
// IN THE ALBUM'S COLOR, like the compact window. His call, 2026-09-28: "lets have
// the window color match the album art like compact does, it looks sharp and gets
// rid of all the blackness". Standby is still black: a clock read across a room.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AlbumWall.App;

public sealed class FrontPanelWindow : Window
{
    /// The kiosk's window rule matches this exact title. Change both or neither.
    public const string WindowTitle = "AlbumWall Front Panel";

    public NowPlayingPanel View { get; } = new() { Interactive = false };

    public FrontPanelWindow()
    {
        Title = WindowTitle;
        WindowDecorations = WindowDecorations.None;
        ShowActivated = false;
        Focusable = false;
        CanResize = false;
        ShowInTaskbar = false;
        Width = 1480;
        Height = 320;
        Background = Brushes.Black;

        var host = new Border { Background = Brushes.Black, Child = new Viewbox { Stretch = Stretch.Uniform, Child = View } };
        // The bands a screen of another shape leaves around the view take its color,
        // so a tinted view never sits in a black frame.
        View.GroundChanged += ground => { host.Background = ground; Background = ground; };
        Content = host;

        // Its Wayland app_id, as the main window claims its own. It had none, and the kiosk's
        // window rule then matched on identifier="albumwall" never saw it (2026-09-28: the panel
        // landed wherever the pointer was). The kiosk rules now match by TITLE alone, which does
        // not wait for this; the app_id is still right to have.
        Opened += (_, _) => WaylandShell.ClaimIdentity(this);
    }
}
