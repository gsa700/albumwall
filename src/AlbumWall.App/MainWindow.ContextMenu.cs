// AlbumWall — the right-click menu on albums and tracks.
//
// One menu, the same on every library, and what a library cannot do is greyed
// out with the reason on it rather than left off: a missing item looks like a
// fault, a greyed one says why. A track on a Navidrome server has no file on
// this machine, so there is nothing to show in a file manager and no path to
// copy.
//
// NOTHING HERE WRITES. Play, show, copy, and Properties, which only reads
// (PropertiesWindow.cs). Tag editing comes to Properties when it is built
// (docs/roadmap.md); the disabled "Edit tags" button the album panel used to
// carry is gone, because this is where that will live.

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
            pathItem: "Copy folder path",
            properties: () => ShowAlbumProperties(album.Album));
    }

    private MenuFlyout TrackMenu(TrackLine line, Domain.Track track)
    {
        var file = Domain.Navidrome.IsTrack(track.Path) ? null : track.Path;
        return Menu(
            play: line.PlayCommand is { } play ? () => play.Execute(null) : null,
            show: file is null ? null : () => ShowFile(file),
            path: file,
            pathItem: "Copy file path",
            properties: () => ShowTrackProperties(track));
    }

    private MenuFlyout Menu(Action? play, Action? show, string? path, string pathItem, Action properties)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", play, "Playback is not available."));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show in file manager", show, OnAServer));
        menu.Items.Add(Item(pathItem, path is null ? null : () => _ = Clipboard?.SetTextAsync(path), OnAServer));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Properties…", properties, ""));
        return menu;
    }

    // ---- Properties ----------------------------------------------------------

    /// Where a thing in the current library is, for one who cannot be shown a
    /// folder: "albumwall on http://nas:4533, FLAC".
    private string ServerWhere => $"On a server: {_settings.Current().Root}";

    private void ShowAlbumProperties(Domain.Album album)
    {
        var library = _settings.Current();
        var onServer = album.Tracks.Count > 0 && Domain.Navidrome.IsTrack(album.Tracks[0].Path);
        var rows = new List<PropertiesWindow.Row>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(label, value!)); }

        Add("Year", album.Year > 0 ? album.Year.ToString() : null);
        Add("Tracks", album.DiscCount > 1 ? $"{album.Tracks.Count} on {album.DiscCount} discs" : album.Tracks.Count.ToString());
        Add("Length", PanelRow.Duration(album.TotalTime));
        Add("Format", string.Join(", ", new[] { PanelRow.TypeLine(album), PanelRow.FormatLine(album) }.Where(s => s.Length > 0)));
        Add("Size", PropertiesWindow.Bytes(album.Tracks.Sum(t => t.Size)));
        Add("Library", library.Name);
        Add(album.Directories.Count > 1 ? "Folders" : "Folder",
            onServer ? ServerWhere : string.Join("\n", album.Directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)));

        var art = new List<PropertiesWindow.Row>();
        art.Add(new("Front", album.ArtPath is { } front
            ? Picture(onServer ? "kept from the server" : Path.GetFileName(front), album.ArtWidth, album.ArtHeight, album.ArtSize)
            : album.ArtEmbeddedIn is not null ? Picture("inside the tracks", album.ArtWidth, album.ArtHeight, 0)
            : "none"));
        if (!onServer)
            art.Add(new("Back", album.BackPath is { } back
                ? Picture(Path.GetFileName(back), album.BackWidth, album.BackHeight, album.BackSize)
                : "none"));

        var sections = new List<PropertiesWindow.Section> { new("Album", rows), new("Sleeve", art) };
        if (!onServer && RipLog(album) is { Count: > 0 } rip) sections.Add(new("Rip", rip));
        PropertiesWindow.ShowFrom(this, album.Title, album.AlbumArtist, sections);
    }

    private static string Picture(string where, int width, int height, long size) =>
        string.Join(", ", new[] { where, width > 0 ? $"{width} × {height}" : "", PropertiesWindow.Bytes(size) }.Where(s => s.Length > 0));

    /// What the ripper's log beside the album concluded, if there is one: a
    /// rip made by whipper or Deadwax says how it went in the same words.
    private static List<PropertiesWindow.Row> RipLog(Domain.Album album)
    {
        var rows = new List<PropertiesWindow.Row>();
        try
        {
            foreach (var log in album.Directories.SelectMany(d => Directory.EnumerateFiles(d, "*.log")).Take(4))
                foreach (var line in File.ReadLines(log))
                {
                    var text = line.Trim();
                    foreach (var (key, label) in new[] { ("Log created by:", "Ripped with"), ("AccurateRip summary:", "AccurateRip"), ("Health status:", "Health") })
                        if (text.StartsWith(key, StringComparison.Ordinal))
                            rows.Add(new(label, text[key.Length..].Trim()));
                }
        }
        catch (Exception)
        {
            // A log that cannot be read is a section that is not shown.
        }
        return rows;
    }

    private async void ShowTrackProperties(Domain.Track track)
    {
        var onServer = Domain.Navidrome.IsTrack(track.Path);
        var rows = new List<PropertiesWindow.Row>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(label, value!)); }

        Add("Track", track.Number > 0 ? $"{track.Number}{(track.Disc > 1 ? $", disc {track.Disc}" : "")}" : null);
        Add("Length", track.DurationText);
        Add("Size", PropertiesWindow.Bytes(track.Size > 0 || onServer ? track.Size : FileSize(track.Path)));

        var sections = new List<PropertiesWindow.Section>();
        if (onServer)
        {
            // What the server said when the library was fetched: the file is
            // there, not here, and is not asked for just to be described.
            Add("Kind", Path.GetExtension(track.Path).TrimStart('.').ToUpperInvariant());
            Add("Sample rate", track.SampleRate > 0
                ? $"{track.SampleRate / 1000.0:0.###} kHz{(track.BitDepth > 0 ? $", {track.BitDepth} bit" : "")}" : null);
            Add("Bit rate", track.Bitrate > 0 ? $"{track.Bitrate:N0} kbps" : null);
            Add("File", ServerWhere);
            sections.Add(new("Track", rows));
            sections.Add(new("Tags", [], "The tags and lyrics are in the file, which is on the server."));
        }
        else
        {
            var facts = await Task.Run(() => PropertiesWindow.Read(track.Path));
            rows.AddRange(facts.Audio);
            Add("File", track.Path);
            sections.Add(new("Track", rows));
            if (facts.Problem is { } problem)
                sections.Add(new("Tags", [], $"The file could not be read: {problem}"));
            else
            {
                sections.Add(new("Tags", facts.Tags, facts.Tags.Count == 0 ? "None." : null));
                sections.Add(new("Lyrics", [], facts.Lyrics ?? "None in the file."));
            }
        }
        PropertiesWindow.ShowFrom(this, track.Title, track.Artist, sections);
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
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
