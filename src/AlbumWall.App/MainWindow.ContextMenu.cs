// AlbumWall — the right-click menu on albums and tracks.
//
// One menu, the same on every library, and what a library cannot do is greyed
// out with the reason on it rather than left off: a missing item looks like a
// fault, a greyed one says why. A track on a Navidrome server has no file on
// this machine, so there is nothing to show in a file manager and no path to
// copy.
//
// NOTHING HERE WRITES. Play, show, copy. Properties and tag editing come to
// this menu when they are built (docs/roadmap.md); the disabled "Edit tags"
// button the album panel used to carry is gone, because this is where that
// will live.

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace AlbumWall.App;

public partial class MainWindow
{
    private const string OnAServer = "This is on a server: there is no file on this computer.";

    private void WireContextMenu() => AddHandler(ContextRequestedEvent, OnContextRequested);

    /// A right click, or the keyboard's menu key, on a tile, on the open
    /// panel, or on one of its tracks.
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;

        MenuFlyout? menu = source.DataContext switch
        {
            TrackLine { IsHeader: false, Source: { } track } line => TrackMenu(line, track),
            AlbumVm album => AlbumMenu(album),
            PanelRow panel => AlbumMenu(panel.Album),
            // A disc heading belongs to the panel it is in.
            TrackLine => source.GetVisualAncestors().OfType<Control>()
                               .Select(c => c.DataContext).OfType<PanelRow>().FirstOrDefault() is { } panel
                ? AlbumMenu(panel.Album) : null,
            _ => null,
        };
        if (menu is null) return;

        menu.ShowAt(source, showAtPointer: e.TryGetPosition(source, out _));
        e.Handled = true;
    }

    private bool CanPlayNow => Playback.Player.IsAvailable && !_playerFailed;

    private MenuFlyout AlbumMenu(AlbumVm album)
    {
        var first = album.Album.Tracks.FirstOrDefault()?.Path;
        var folder = first is null || Domain.Navidrome.IsTrack(first) ? null : Path.GetDirectoryName(first);
        return Menu(
            play: CanPlayNow && first is not null ? () => StartPlayback(album, 0, false) : null,
            show: folder is null ? null : () => OpenFolder(folder),
            path: folder,
            pathItem: "Copy folder path");
    }

    private MenuFlyout TrackMenu(TrackLine line, Domain.Track track)
    {
        var file = Domain.Navidrome.IsTrack(track.Path) ? null : track.Path;
        return Menu(
            play: line.PlayCommand is { } play ? () => play.Execute(null) : null,
            show: file is null ? null : () => ShowFile(file),
            path: file,
            pathItem: "Copy file path");
    }

    private MenuFlyout Menu(Action? play, Action? show, string? path, string pathItem)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", play, "Playback is not available."));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in file manager", show, OnAServer));
        menu.Items.Add(Item(pathItem, path is null ? null : () => _ = Clipboard?.SetTextAsync(path), OnAServer));
        return menu;
    }

    /// An item that does `run`, or, with nothing to run, a greyed one that
    /// says why when pointed at.
    private static MenuItem Item(string header, Action? run, string whyNot)
    {
        var item = new MenuItem { Header = header, IsEnabled = run is not null };
        if (run is not null) item.Click += (_, _) => run();
        else
        {
            ToolTip.SetTip(item, whyNot);
            ToolTip.SetShowOnDisabled(item, true);
        }
        return item;
    }

    /// Opens the album's folder in whatever the desktop opens folders with.
    private void OpenFolder(string folder)
    {
        Console.WriteLine($"[menu] open {folder}");
        _ = Launcher.LaunchUriAsync(new Uri(folder));
    }

    /// Opens the file's folder with the file picked out, where the desktop
    /// can do that, and just the folder where it cannot.
    private void ShowFile(string file)
    {
        Console.WriteLine($"[menu] show {file}");
        var folder = Path.GetDirectoryName(file);
        _ = Task.Run(() =>
        {
            if (Selected(file) || folder is null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenFolder(folder));
        });
    }

    /// Asks the file manager to show one file selected. False if there was
    /// nobody to ask.
    private static bool Selected(string file)
    {
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            if (OperatingSystem.IsWindows())
            {
                start.FileName = "explorer.exe";
                start.Arguments = $"/select,\"{file}\"";
                using var explorer = Process.Start(start);
                return explorer is not null;        // its exit code says nothing either way
            }
            if (OperatingSystem.IsMacOS())
            {
                start.FileName = "open";
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(file);
            }
            else
            {
                // The freedesktop way, which Files, Dolphin, Nemo and COSMIC Files
                // all answer. dbus-send splits an array on commas, and a song is
                // allowed one in its name.
                start.FileName = "dbus-send";
                foreach (var arg in new[]
                {
                    "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                    "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                    "array:string:" + new Uri(file).AbsoluteUri.Replace(",", "%2C"), "string:",
                })
                    start.ArgumentList.Add(arg);
            }
            using var p = Process.Start(start);
            return p is not null && p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;       // no such program here: the folder alone will do
        }
    }
}
