// AlbumWall — the right-click menu on albums and tracks.
//
// One menu, the same on every library, and what a library cannot do is greyed
// out with the reason on it rather than left off: a missing item looks like a
// fault, a greyed one says why. A track on a Navidrome server has no file on
// this machine, so there is nothing to show in a file manager and no path to
// copy.
//
// THE MENU ITSELF WRITES NOTHING. Play, show, copy, and Properties. Properties
// can change one thing, a track's lyrics, and only in a library he has allowed
// editing in (PropertiesWindow.cs, Domain/TagWriter.cs). The rest of tag
// editing comes to Properties when it is built (docs/roadmap.md); the disabled
// "Edit tags" button the album panel used to carry is gone, because this is
// where that lives.

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace AlbumWall.App;

public partial class MainWindow
{
    private const string OnAServer = "This is on a server: there is no file on this computer.";

    private void WireContextMenu()
    {
        AddHandler(ContextRequestedEvent, OnContextRequested);
        // Every right press is written down, whoever handles it, so that a
        // menu that did not come can be told apart afterwards: the press never
        // arrived, it arrived and asked for no menu, or the menu was asked for
        // and the desktop closed it at once. He reported all three shapes at
        // once on 2026-10-04 ("sometimes I have to click the file even when
        // albumwall IS in focus before the context menu will appear").
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                Console.WriteLine($"[menu] right press on {Described(e.Source)}, window {(IsActive ? "active" : "NOT active")}");
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        // A second button going down while another is held is not a press to
        // Avalonia, only a move. So a right click that arrives while the left
        // button is still believed to be down (it was let go inside a window
        // move the desktop took over, and nobody said) shows up here and
        // nowhere else.
        var saidStuck = false;
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(this).Properties;
            var both = p.IsRightButtonPressed && p.IsLeftButtonPressed;
            if (both && !saidStuck)
                Console.WriteLine($"[menu] right button down over {Described(e.Source)} with the LEFT still counted as held: no press, no menu");
            saidStuck = both;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// What was pressed, for the log: the control and what it stands for.
    private static string Described(object? source) =>
        source is Control c ? $"{c.GetType().Name} ({c.DataContext?.GetType().Name ?? "nothing"})" : source?.GetType().Name ?? "nothing";

    /// A right click, or the keyboard's menu key, on a tile, on the open
    /// panel, or on one of its tracks.
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source)
        {
            Console.WriteLine($"[menu] asked for on {Described(e.Source)}: not a control, no menu");
            return;
        }

        MenuFlyout? menu = source.DataContext switch
        {
            TrackLine { IsHeader: false, Source: { } track } line => TrackMenu(line, track, PanelOf(source)?.Album.Album),
            AlbumVm album => AlbumMenu(album),
            PanelRow panel => AlbumMenu(panel.Album),
            // A disc heading belongs to the panel it is in.
            TrackLine => PanelOf(source) is { } panel ? AlbumMenu(panel.Album) : null,
            _ => null,
        };
        if (menu is null)
        {
            Console.WriteLine($"[menu] asked for on {Described(source)}: nothing there has a menu");
            return;
        }

        var asked = Stopwatch.StartNew();
        menu.Opened += (_, _) => Console.WriteLine($"[menu] opened on {Described(source)} after {asked.ElapsedMilliseconds} ms");
        menu.Closed += (_, _) => Console.WriteLine($"[menu] closed {asked.ElapsedMilliseconds} ms after it was asked for");
        Console.WriteLine($"[menu] asked for on {Described(source)}, window {(IsActive ? "active" : "NOT active")}");
        menu.ShowAt(source, showAtPointer: e.TryGetPosition(source, out _));
        e.Handled = true;
    }

    /// The open album a track line or a disc heading is part of.
    private static PanelRow? PanelOf(Control source) =>
        source.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<PanelRow>().FirstOrDefault();

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

    private MenuFlyout TrackMenu(TrackLine line, Domain.Track track, Domain.Album? album)
    {
        var file = Domain.Navidrome.IsTrack(track.Path) ? null : track.Path;
        return Menu(
            play: line.PlayCommand is { } play ? () => play.Execute(null) : null,
            show: file is null ? null : () => ShowFile(file),
            path: file,
            pathItem: "Copy file path",
            properties: () => ShowTrackProperties(track, album));
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

    /// The album's front, for showing: its cover file, or the picture inside
    /// its tracks, read out now. Null if it has neither.
    private static PropertiesWindow.Picture? FrontOf(Domain.Album? album)
    {
        if (album is null) return null;
        if (album.ArtPath is { } file) return new("Front", file);
        if (album.ArtEmbeddedIn is not { } track) return null;
        try
        {
            using var tf = TagLib.File.Create(track);
            return Domain.ImageSize.Cover(tf.Tag.Pictures) is { } data ? new("Front", null, data) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Counts the askings for Properties, so a file that was slow to read
    /// cannot put itself in the window after a later one has.
    private int _propertiesAsk;

    private async void ShowAlbumProperties(Domain.Album album)
    {
        var ask = ++_propertiesAsk;
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

        var sleeve = await Task.Run(() => FrontOf(album));
        if (ask != _propertiesAsk) return;
        var pictures = new List<PropertiesWindow.Picture>();
        if (sleeve is not null) pictures.Add(sleeve);
        if (album.BackPath is { } backFile) pictures.Add(new("Back", backFile));

        var sections = new List<PropertiesWindow.Section> { new("Album", rows), new("Sleeve", art, Pictures: pictures) };
        if (!onServer && RipLog(album) is { Count: > 0 } rip) sections.Add(new("Rip", rip));
        PropertiesWindow.ShowFrom(this, album.Title, album.AlbumArtist, sections, cover: sleeve);
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

    private async void ShowTrackProperties(Domain.Track track, Domain.Album? album)
    {
        var ask = ++_propertiesAsk;
        var onServer = Domain.Navidrome.IsTrack(track.Path);
        var library = _settings.Current();
        PropertiesWindow.Lyrics? lyrics = null;
        var rows = new List<PropertiesWindow.Row>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new(label, value!)); }

        Add("Track", track.Number > 0 ? $"{track.Number}{(track.Disc > 1 ? $", disc {track.Disc}" : "")}" : null);
        Add("Length", track.DurationText);
        Add("Size", PropertiesWindow.Bytes(track.Size > 0 || onServer ? track.Size : FileSize(track.Path)));

        var sections = new List<PropertiesWindow.Section>();
        if (onServer)
        {
            // The same window as a file's, from what the server says of the
            // song: its tags as the server read them, and its lyrics. Asked
            // now and not kept. The file is there, not here, and is not
            // fetched just to be described.
            var home = Domain.Navidrome.LibraryOf(track.Path) is { } id
                ? _settings.AllLibraries().FirstOrDefault(l => l.Id == id) : null;
            var server = home is null ? null : NavidromeFor(home);
            var song = track.Path;
            var facts = server is null
                ? new Domain.Navidrome.SongFacts([], 0, null, "its sign-in is missing from the settings")
                : await Task.Run(() =>
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    return server.Describe(song, wait.Token);
                });

            Add("Kind", Path.GetExtension(track.Path).TrimStart('.').ToUpperInvariant());
            Add("Sample rate", track.SampleRate > 0
                ? $"{track.SampleRate / 1000.0:0.###} kHz{(track.BitDepth > 0 ? $", {track.BitDepth} bit" : "")}"
                  + (facts.Channels > 0 ? $", {facts.Channels} channels" : "") : null);
            Add("Bit rate", track.Bitrate > 0 ? $"{track.Bitrate:N0} kbps" : null);
            Add("File", ServerWhere);
            sections.Add(new("Track", rows));
            if (facts.Problem is { } problem)
                sections.Add(new("Tags", [], $"The server could not be asked: {problem}."));
            else
            {
                sections.Add(new("Tags", facts.Tags.Select(t => new PropertiesWindow.Row(t.Label, t.Value)).ToList(),
                                 facts.Tags.Count == 0 ? "None." : null));
                // Shown, never changed: a server cannot be written to.
                lyrics = new(facts.Lyrics ?? "",
                             "This is on a server. Lyrics are changed in the file, on the machine that holds the master copy.",
                             null);
            }
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
                var path = track.Path;
                lyrics = new(
                    facts.Lyrics ?? "",
                    library.CanEdit ? null
                        : $"Editing is not allowed in the library “{library.Name}”. It is turned on per library in "
                          + "Preferences › Library, and belongs on the machine that holds the master copy.",
                    async text =>
                    {
                        var result = await Task.Run(() => Domain.TagWriter.SetLyrics(path, text, Settings.TagBackups));
                        Console.WriteLine(result.Ok
                            ? $"[edit] lyrics of {path}: {(result.Backup is null ? "unchanged" : $"written, old kept in {result.Backup}")}"
                            : $"[edit] lyrics of {path}: FAILED, {result.Problem}");
                        return result.Problem;
                    });
            }
        }
        var cover = await Task.Run(() => FrontOf(album));
        if (ask != _propertiesAsk) return;

        // The tracks either side of it, in the album's own order.
        PropertiesWindow.Around? around = null;
        if (album is { Tracks.Count: > 1 } && album.Tracks.IndexOf(track) is >= 0 and var at)
            around = new(
                at > 0 ? () => ShowTrackProperties(album.Tracks[at - 1], album) : null,
                at < album.Tracks.Count - 1 ? () => ShowTrackProperties(album.Tracks[at + 1], album) : null,
                $"{at + 1} of {album.Tracks.Count}");
        PropertiesWindow.ShowFrom(this, track.Title,
                                  album is null ? track.Artist : $"{track.Artist}  ·  {album.Title}", sections, lyrics, cover, around);
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
        if (run is not null) item.Click += (_, _) => { Console.WriteLine($"[menu] chose {header}"); run(); };
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
