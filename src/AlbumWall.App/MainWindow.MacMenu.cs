// AlbumWall — the menu bar, on macOS.
//
// Every Mac app has one, and an app without it is the odd one out: the
// application menu with About, Preferences and Quit, and menus naming what the
// app can do, each with its shortcut beside it, which is how a Mac user finds
// the keys at all (the Help tab is the other way). Asked for on 2026-10-14,
// after the Cmd keys.
//
// macOS ONLY. Elsewhere there is no menu bar to fill, and on Linux Avalonia
// would export this to a desktop's global menu, which nobody asked for.
//
// The items run the same code the keys and buttons do. A shortcut shown here
// is a menu key equivalent: macOS hands it to the menu BEFORE the window sees
// it, so OnShortcutKey never gets these and nothing runs twice. Play/Pause has
// no shortcut here on purpose: Space as a menu key would be taken from the
// search box, where it is a character.

using Avalonia.Controls;
using Avalonia.Input;

namespace AlbumWall.App;

public partial class MainWindow
{
    private void SetUpMacMenu()
    {
        if (!OperatingSystem.IsMacOS()) return;

        const KeyModifiers Cmd = KeyModifiers.Meta;

        static NativeMenuItem Item(string header, Action run, Key? key = null, KeyModifiers mods = Cmd)
        {
            var item = new NativeMenuItem(header);
            if (key is { } k) item.Gesture = new KeyGesture(k, mods);
            item.Click += (_, _) => run();
            return item;
        }

        static NativeMenuItem Menu(string header, params NativeMenuItemBase[] items)
        {
            var menu = new NativeMenu();
            foreach (var i in items) menu.Items.Add(i);
            return new NativeMenuItem(header) { Menu = menu };
        }

        // The application menu, the one named AlbumWall, in full. Avalonia's
        // defaults are off (Program.cs) because they began with "About Avalonia"
        // ("About avalonia?", 2026-10-14), so Hide and Quit are ours too.
        // The menu App.Initialize made, so that Avalonia never made its own.
        var app = NativeMenu.GetMenu(Avalonia.Application.Current!) ?? new NativeMenu();
        app.Items.Clear();
        app.Items.Add(Item("About AlbumWall", () => ShowPrefs(PrefsWindow.Tab.About)));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Preferences…", () => ShowPrefs(PrefsWindow.Tab.Library), Key.OemComma));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Hide AlbumWall", () => MacWindow.AppAction("hide:"), Key.H));
        app.Items.Add(Item("Hide Others", () => MacWindow.AppAction("hideOtherApplications:"), Key.H, Cmd | KeyModifiers.Alt));
        app.Items.Add(Item("Show All", () => MacWindow.AppAction("unhideAllApplications:")));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Quit AlbumWall", Close, Key.Q));
        NativeMenu.SetMenu(Avalonia.Application.Current!, app);

        var bar = new NativeMenu();
        bar.Items.Add(Menu("File",
            Item("Libraries…", OpenLibraryPicker),
            Item("Rescan Library", () => ScanLibrary(asked: true)),
            new NativeMenuItemSeparator(),
            Item("Close Window", Close, Key.W)));
        bar.Items.Add(Menu("Edit",
            Item("Find", () => { SearchBox.Focus(); SearchBox.SelectAll(); }, Key.F)));
        bar.Items.Add(Menu("Controls",
            Item("Play / Pause", () => MprisCommand("PlayPause")),
            Item("Next Track", () => MprisCommand("Next"), Key.Right),
            Item("Previous Track", () => MprisCommand("Previous"), Key.Left),
            new NativeMenuItemSeparator(),
            Item("Louder", () => VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + VolumeStep), Key.Up),
            Item("Quieter", () => VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - VolumeStep), Key.Down),
            new NativeMenuItemSeparator(),
            Item("Go to the Playing Album", () => OnRevealPlaying(null, new Avalonia.Interactivity.RoutedEventArgs()), Key.L)));
        bar.Items.Add(Menu("View",
            Item("Compact", ToggleCompact, Key.M, Cmd | Shortcut.CompactExtra)));
        bar.Items.Add(Menu("Window",
            Item("Minimize", () => WindowState = WindowState.Minimized, Key.M),
            Item("Zoom", ToggleMaximized)));
        bar.Items.Add(Menu("Help",
            Item("AlbumWall Help", () => ShowPrefs(PrefsWindow.Tab.Help), Key.OemQuestion, Cmd | KeyModifiers.Shift),
            Item("What's Inside", () => NoticesWindow.ShowFrom(this))));
        NativeMenu.SetMenu(this, bar);
    }
}
