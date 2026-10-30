// AlbumWall - text size: how large the light windows are drawn.
//
// Accessibility in Preferences (his ask, 2026-10-29, "text size in v1"). The
// windows' type sizes are written into them, 12 and 13 and 14 where the design
// wants them, about 140 places in all, so a larger size is not a font setting:
// each window that follows it is drawn through a scale, text, spacing and
// buttons together, and the window is made that much larger to hold it. The
// same thing a browser's zoom does, which is what people who need larger text
// already know.
//
// THE LIGHT WINDOWS ONLY: Preferences, Properties, a library's settings, the
// statistics, the server sign-in and the questions the app asks. Not yet the
// main window, where a scale would make the covers larger as well as the text,
// fewer to a row and decoded too small for their new size; how text there
// should grow is its own decision.
//
// Applies at once to every window that follows it, open or not.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AlbumWall.App;

public static class TextSize
{
    /// The choices, as percentages of the designed size.
    public static readonly int[] Choices = [100, 115, 130, 150];

    public static int Percent { get; private set; } = 100;

    private static event Action? Changed;

    public static void Choose(int? percent)
    {
        var p = percent is { } v && Choices.Contains(v) ? v : 100;
        if (p == Percent) return;
        Percent = p;
        Console.WriteLine($"[text] {Percent}%");
        Changed?.Invoke();
    }

    /// Called at the end of a window's constructor, once its content is in place.
    /// The window is drawn at the chosen size from then on, and follows any
    /// change until it closes. A window that sizes itself to its content needs
    /// nothing more; one with a fixed size is made larger by the same amount,
    /// but never larger than the screen it opens on.
    /// A window that puts new content in later (Properties for the next album, a
    /// statistics page rebuilt) is followed too: whatever it sets is wrapped in
    /// turn.
    public static void Follow(Window window)
    {
        var host = new LayoutTransformControl();
        var content = window.Content as Control;
        double width = window.Width, height = window.Height;
        var swapping = false;

        void Wrap()
        {
            swapping = true;
            try
            {
                var f = Percent / 100.0;
                if (f == 1)
                {
                    if (window.Content == host) { host.Child = null; window.Content = content; }
                }
                else if (content is not null)
                {
                    host.LayoutTransform = new ScaleTransform(f, f);
                    if (window.Content != host) { window.Content = null; host.Child = content; window.Content = host; }
                }
            }
            finally { swapping = false; }
        }

        window.PropertyChanged += (_, e) =>
        {
            if (swapping || e.Property != ContentControl.ContentProperty || e.NewValue == host) return;
            content = e.NewValue as Control;
            if (Percent != 100) Wrap();
        };

        void Apply()
        {
            var f = Percent / 100.0;
            Wrap();
            if (window.SizeToContent != SizeToContent.Manual) return;
            var w = double.IsNaN(width) ? double.NaN : Math.Ceiling(width * f);
            var h = double.IsNaN(height) ? double.NaN : Math.Ceiling(height * f);
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen)
            {
                var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
                if (!double.IsNaN(w)) w = Math.Min(w, area.Width - 48);
                if (!double.IsNaN(h)) h = Math.Min(h, area.Height - 48);
            }
            if (!double.IsNaN(w)) window.Width = w;
            if (!double.IsNaN(h)) window.Height = h;
        }

        Apply();
        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
    }
}
