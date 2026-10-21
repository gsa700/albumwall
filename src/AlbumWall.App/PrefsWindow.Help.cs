// AlbumWall - the Help tab of Preferences: the keys, what the search box understands,
// and where the command line's list is.
//
// The two things in this app that cannot be found by looking at it. The keys are
// handled in MainWindow.OnShortcutKey, and THE TWO LISTS MUST AGREE: a key added
// there and not here is a key nobody knows about, and one listed here that does
// nothing is worse. The searches are MainWindow.ArtQuery.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AlbumWall.App;

public partial class PrefsWindow
{
    private void FillHelp()
    {
        Heading("Keys", first: true);
        Rows(
            ("Space", "Play or pause."),
            ($"{Shortcut.Name("→")}   {Shortcut.Name("←")}", "Next track, previous track."),
            (OperatingSystem.IsMacOS() ? "⇧→   ⇧←" : "Shift+→   Shift+←", "Forward or back ten seconds."),
            ($"{Shortcut.Name("↑")}   {Shortcut.Name("↓")}", "Louder, quieter. This app only, not the computer."),
            ($"{Shortcut.Name("F")}   or   /", "Go to the search box."),
            ("Esc", OperatingSystem.IsMacOS()
                ? "Clear the search; or, with nothing searched for, fold the open album."
                : "Leave full screen; or clear the search; or, with nothing searched for, fold the open album."),
            (Shortcut.Name("L"), "Go to the album that is playing, and open it."),
            (Shortcut.Compact, "Compact: the window becomes just what is playing. Again, or Esc, for the wall."),
            (OperatingSystem.IsMacOS() ? null : "F11", "Full screen, and back."),     // none on the Mac
            (Shortcut.Name(","), "Preferences."),
            (OperatingSystem.IsMacOS() ? "F1   or   ⌘?" : "F1", "This page."));
        Note("The arrow keys move from cover to cover; Page Up and Page Down by a screen, Home and End to "
           + "either end. Enter opens an album, ↓ from the open one goes into its tracks, Enter plays one and "
           + "Esc comes back out. From the search box, ↓ goes to the first album found. The keyboard's own "
           + "media keys work too, whether or not this window is in front.");

        Heading("Searching");
        Note("Type part of an artist's name or an album's title and the wall narrows as you type. Three "
           + "searches look at the cover art instead:");
        Rows(
            ("art:missing", "Albums with no cover at all."),
            ("art:small", "Albums whose cover is smaller than 300 pixels."),
            ("art:nonsquare", "Albums whose cover is not square."));
        Note("Covers are never fetched from the internet. Better art belongs in the files themselves, put "
           + "there with a tagger; these searches say which albums to look at.");

        // One line, not the list: few will use the commands, and those who do
        // are in a terminal already (his call, 2026-10-21). --help has them all.
        Heading("Command line");
        Note($"Only one {App.DisplayName} runs at a time: starting it again brings this window forward. "
           + $"Scripts can play, pause, skip and more; run {App.DisplayName} --help for the commands.");
    }

    private void Heading(string text, bool first = false) =>
        HelpBody.Children.Add(new TextBlock
        {
            Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, first ? 0 : 12, 0, 5)
        });

    private void Note(string text) =>
        HelpBody.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, LineHeight = 20, Classes = { "dim" },
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 3)
        });

    /// A row whose keys are null is left out: a key this platform does not have.
    private void Rows(params (string? Keys, string Does)[] rows)
    {
        foreach (var (keys, does) in rows)
        {
            if (keys is null) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), MinHeight = 22 };
            row.Children.Add(new TextBlock
            {
                Text = keys, FontSize = 13, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Top
            });
            var what = new TextBlock
            {
                Text = does, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Opacity = 0.86
            };
            Grid.SetColumn(what, 1);
            row.Children.Add(what);
            HelpBody.Children.Add(row);
        }
    }
}
