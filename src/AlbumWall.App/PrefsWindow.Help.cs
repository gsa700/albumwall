// AlbumWall - the Help tab of Preferences: the keys, and what the search box understands.
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
            ("Ctrl + →   Ctrl + ←", "Next track, previous track."),
            ("→   ←", "Forward or back ten seconds."),
            ("Ctrl + ↑   Ctrl + ↓", "Louder, quieter. This app only, not the computer."),
            ("Ctrl + F   or   /", "Go to the search box."),
            ("Esc", "Clear the search; or, with nothing searched for, fold the open album."),
            ("Ctrl + L", "Go to the album that is playing, and open it."),
            ("Ctrl + ,", "Preferences."),
            ("F1", "This page."));
        Note("The arrow keys alone, Page Up, Page Down, Home and End scroll the wall. Tab moves from cover to "
           + "cover and Enter opens the one it is on. The keyboard's own media keys work too, whether or not "
           + "this window is in front.");

        Heading("Searching");
        Note("Type part of an artist's name or an album's title and the wall narrows as you type. Three "
           + "searches look at the cover art instead:");
        Rows(
            ("art:missing", "Albums with no cover at all."),
            ("art:small", "Albums whose cover is smaller than 300 pixels."),
            ("art:nonsquare", "Albums whose cover is not square."));
        Note("Covers are never fetched from the internet. Better art belongs in the files themselves, put "
           + "there with a tagger; these searches say which albums to look at.");
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

    private void Rows(params (string Keys, string Does)[] rows)
    {
        foreach (var (keys, does) in rows)
        {
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
