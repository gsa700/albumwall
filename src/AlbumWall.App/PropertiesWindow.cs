// AlbumWall — Properties: what an album or a track is, from the right-click menu.
//
// It shows what the library already knows and, for a track that is a file on
// this machine, what the file itself says: every tag, and the lyrics if they
// are embedded. Nothing is kept from that reading; it is done when the window
// is asked for and forgotten when it closes. A track on a server gets the same
// window from what the server says of it, lyrics included (Domain/Navidrome.cs,
// Describe).
//
// ONE THING HERE WRITES: the lyrics of a track, in a library he has allowed
// editing in (Library.Editable). The writing itself is Domain/TagWriter.cs;
// this window only offers a box to type or paste into. Everything else is
// read-only, and tag editing proper is still to come (docs/roadmap.md).
//
// A track's window can step to the tracks either side of it in the album,
// keeping the tab it is on: lyrics are read a song after a song, and pasted in
// an album at a time.
//
// Its colors are its own: a light, neutral window in a dark program. See the
// constructor.
//
// In the format of Preferences, as he asked when that was built ("we'll use
// the same format with the file properties window when we get to it"): a
// proper decorated window of a fixed size, a tab for each part, one instance,
// owned by the window that opened it, closed from its
// title bar. Every value can be selected and copied, since a path or
// an ID is what one comes here to get. Built in code because it is one column
// of labelled lines whose number is not known until the file has been read.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AlbumWall.App;

public sealed class PropertiesWindow : Window
{
    /// One labelled line.
    public sealed record Row(string Label, string Value);

    /// Lines under a heading, or a body of text under one, with any pictures
    /// that belong there shown above them.
    public sealed record Section(string Heading, IReadOnlyList<Row> Rows, string? Body = null,
                                 IReadOnlyList<Picture>? Pictures = null);

    /// A picture to show, from a file or from bytes already read out of one,
    /// with what it is called underneath.
    public sealed record Picture(string Caption, string? File, byte[]? Data = null);

    /// A track's lyrics and how they may be changed. `WhyNot` is the reason
    /// they cannot be, shown on the greyed button; `Save` is given the new
    /// text and answers with what went wrong, or null.
    public sealed record Lyrics(string Current, string? WhyNot, Func<string, Task<string?>>? Save);

    /// The tracks either side of this one in its album, and where it stands
    /// among them ("3 of 11"). A way is null at the album's end.
    public sealed record Around(Action? Previous, Action? Next, string Where);

    private static PropertiesWindow? _open;

    /// The one that is up, if one is: the snapshot rig photographs it.
    public static PropertiesWindow? Current => _open;

    /// Shows the properties of one thing. There is one such window: asking for
    /// another's refills it where it stands, on the tab it was on.
    public static void ShowFrom(Window owner, string title, string subtitle, IEnumerable<Section> sections,
                                Lyrics? lyrics = null, Picture? cover = null, Around? around = null)
    {
        if (_open is { } up && ReferenceEquals(up.Owner, owner))
        {
            up.Fill(title, subtitle, sections, lyrics, cover, around);
            up.Activate();
            return;
        }
        _open?.Close();
        _open = new PropertiesWindow();
        _open.Fill(title, subtitle, sections, lyrics, cover, around);
        var mine = _open;
        mine.Closed += (_, _) => { if (ReferenceEquals(_open, mine)) _open = null; };
        if (owner is MainWindow main) mine.RememberPlace(main.AppSettings);
        mine.Show(owner);
        mine.Activate();
    }

    /// Opens where it was last closed, if that place is still on a screen - a
    /// monitor can be unplugged between runs - and otherwise centred on the
    /// main window, as before. Kept as it closes, like Preferences. Not on
    /// native Wayland, which neither tells an app where a window is nor lets it
    /// choose: there the compositor places it and nothing is written down.
    private void RememberPlace(Settings settings)
    {
        if (Program.NativeWayland) return;
        var remembered = settings is { PropsX: { } x, PropsY: { } y }
                         && Screens.ScreenFromPoint(new PixelPoint(x + 40, y + 20)) is not null;
        if (remembered)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(settings.PropsX!.Value, settings.PropsY!.Value);
        }
        Opened += (_, _) => Console.WriteLine($"[props] opened at {Position.X},{Position.Y} ({(remembered ? "where it was" : "over the main window")})");
        Closing += (_, _) =>
        {
            settings.PropsX = Position.X;
            settings.PropsY = Position.Y;
            settings.Save();
            Console.WriteLine($"[props] closed at {Position.X},{Position.Y}");
        };
    }

    /// The pictures this window decoded, let go of when it closes.
    private readonly List<Avalonia.Media.Imaging.Bitmap> _pictures = [];

    /// The picture as a control `size` wide, or null if there is none or it
    /// cannot be read. Decoded no larger than it is shown: a sleeve scan is
    /// thousands of pixels across and this is a thumbnail of it.
    private Control? Shown(Picture? picture, double size)
    {
        if (picture is null) return null;
        try
        {
            using Stream? from = picture.Data is { } data ? new MemoryStream(data)
                               : picture.File is { } file && File.Exists(file) ? File.OpenRead(file) : null;
            if (from is null) return null;
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(from, (int)(size * 2));
            _pictures.Add(bitmap);
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(3), ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.Uniform },
            };
        }
        catch (Exception)
        {
            return null;        // a picture that will not decode is a window without one
        }
    }

    private PropertiesWindow()
    {
        Closed += (_, _) => { foreach (var p in _pictures) p.Dispose(); _pictures.Clear(); };

        Title = $"{App.DisplayName} — Properties";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        // The Preferences format: a fixed size, so it does not jump from tab
        // to tab, and a tab for each part.
        Width = 660;
        Height = 740;
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Foreground = Ink;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // NOT THE SHEET'S COLORS. Preferences is furniture and takes the ground
        // he chose for the wall; this is a page of facts about a file, and he
        // asked for it light and neutral, "more data like colors vs the colors
        // from the album library", then for paper white. So it is the one
        // light window in a dark program: grey desk, off-white page, and the
        // lyrics on white.
        //
        // The tab chrome in App.axaml asks for these three by name, so naming
        // them here recolors it. Its words and the panel buttons are styled for
        // light-on-dark there, and are restyled below for dark-on-light.
        Resources["SheetBg"] = SolidColorBrush.Parse("#E4E4E2");
        Resources["SheetField"] = SolidColorBrush.Parse("#F6F6F4");
        Resources["SheetEdge"] = SolidColorBrush.Parse("#C2C2C6");
        Avalonia.Styling.Style Restyle<T>(Func<Avalonia.Styling.Selector?, Avalonia.Styling.Selector> which,
                                          params (AvaloniaProperty Property, object Value)[] set) where T : Control
        {
            var style = new Avalonia.Styling.Style(which);
            foreach (var (property, value) in set) style.Setters.Add(new Avalonia.Styling.Setter(property, value));
            return style;
        }
        Styles.Add(Restyle<TabItem>(x => x.OfType<TabItem>(), (TabItem.ForegroundProperty, DimInk)));
        Styles.Add(Restyle<TabItem>(x => x.OfType<TabItem>().Class(":pointerover"), (TabItem.ForegroundProperty, Ink)));
        Styles.Add(Restyle<TabItem>(x => x.OfType<TabItem>().Class(":selected"), (TabItem.ForegroundProperty, Ink)));
        Styles.Add(Restyle<Button>(x => x.OfType<Button>().Class("panel"),
            (Button.BackgroundProperty, SolidColorBrush.Parse("#12000000")),
            (Button.ForegroundProperty, Ink),
            (Button.BorderBrushProperty, SolidColorBrush.Parse("#38000000"))));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");
        // Escape closes it, but not out from under lyrics that are not saved.
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && _unsaved?.Invoke() != true) Close(); };
        // Alt+Left and Alt+Right step through the album. Taken on the way
        // down, because the lyrics box would spend the arrow on its caret.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != Avalonia.Input.KeyModifiers.Alt || _around is null) return;
            if (e.Key == Avalonia.Input.Key.Left) { Go(_around.Previous); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Right) { Go(_around.Next); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // What the filling in the window just now has to say for itself: the ways
    // out of it, its tabs, and the lyrics box's state.
    private Around? _around;
    private TabControl? _tabs;
    private TabItem? _lyricsTab;
    private TextBox? _lyricsBox;
    private Func<bool>? _unsaved;
    private Func<Task>? _save;
    private Action<string?>? _say;
    private bool _warned;

    /// Steps to another track, unless there are lyrics typed and not saved:
    /// then it says so once, and goes at the second asking.
    private void Go(Action? way)
    {
        if (way is null) return;
        if (_unsaved?.Invoke() == true && !_warned)
        {
            _warned = true;
            if (_tabs is not null && _lyricsTab is not null) _tabs.SelectedItem = _lyricsTab;
            _say?.Invoke("These lyrics are not saved. Save them, or press again to move on without them.");
            return;
        }
        way();
    }

    /// Puts one thing's properties in the window, in place of whatever was
    /// there, and stays on the tab of the same name if the new thing has one.
    private void Fill(string title, string subtitle, IEnumerable<Section> sections, Lyrics? lyrics, Picture? cover, Around? around)
    {
        var was = _tabs?.SelectedItem is TabItem { Header: string name } ? name : null;
        var old = _pictures.ToList();
        _pictures.Clear();
        _around = around;
        _lyricsTab = null;
        _lyricsBox = null;
        _unsaved = null;
        _save = null;
        _say = null;
        _warned = false;

        // What it is the properties of, above the tabs and the same on all of
        // them: its sleeve, its name, whose it is.
        var words = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new SelectableTextBlock
        {
            Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        if (subtitle.Length > 0)
            words.Children.Add(Dim(new SelectableTextBlock { Text = subtitle, FontSize = Text, TextWrapping = TextWrapping.Wrap }));
        var header = new DockPanel { Margin = new Thickness(18, 16, 18, 4) };
        if (Shown(cover, 72) is { } thumb)
        {
            thumb.Margin = new Thickness(0, 0, 16, 0);
            DockPanel.SetDock(thumb, Dock.Left);
            header.Children.Add(thumb);
        }
        if (around is not null)
        {
            // The ways to the tracks either side, and where this one stands.
            Button Way(string glyph, string tip, Action? way)
            {
                var b = new Button { Content = glyph, FontSize = 18, Padding = new Thickness(11, 1, 11, 4), IsEnabled = way is not null };
                b.Classes.Add("panel");
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => Go(way);
                return b;
            }
            var ways = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ways.Children.Add(Way("‹", "Previous track (Alt+Left)", around.Previous));
            ways.Children.Add(Dim(new TextBlock { Text = around.Where, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }));
            ways.Children.Add(Way("›", "Next track (Alt+Right)", around.Next));
            DockPanel.SetDock(ways, Dock.Right);
            header.Children.Add(ways);
        }
        header.Children.Add(words);
        DockPanel.SetDock(header, Dock.Top);

        var tabs = new TabControl { Margin = new Thickness(12) };
        foreach (var section in sections)
        {
            if (section.Rows.Count == 0 && string.IsNullOrEmpty(section.Body)) continue;
            var column = new StackPanel { Spacing = 10 };

            if (section.Pictures is { Count: > 0 } pictures)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 0, 0, 8) };
                foreach (var picture in pictures)
                {
                    if (Shown(picture, 272) is not { } image) continue;
                    var one = new StackPanel { Spacing = 6 };
                    one.Children.Add(image);
                    one.Children.Add(Dim(new TextBlock { Text = picture.Caption, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center }));
                    row.Children.Add(one);
                }
                if (row.Children.Count > 0) column.Children.Add(row);
            }

            if (section.Rows.Count > 0)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18, RowSpacing = 7 };
                foreach (var row in section.Rows)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var at = grid.RowDefinitions.Count - 1;
                    var label = Dim(new TextBlock { Text = row.Label, FontSize = Text, VerticalAlignment = VerticalAlignment.Top });
                    var value = new SelectableTextBlock { Text = row.Value, FontSize = Text, TextWrapping = TextWrapping.Wrap };
                    Grid.SetRow(label, at);
                    Grid.SetRow(value, at);
                    Grid.SetColumn(value, 1);
                    grid.Children.Add(label);
                    grid.Children.Add(value);
                }
                column.Children.Add(grid);
            }

            if (!string.IsNullOrEmpty(section.Body))
                column.Children.Add(new SelectableTextBlock
                {
                    Text = section.Body, FontSize = Text, LineHeight = 22, TextWrapping = TextWrapping.Wrap,
                });
            tabs.Items.Add(Tab(section.Heading, column));
        }
        // The lyrics tab does not scroll: the box fills it and scrolls inside
        // itself, so Save stays in sight under a long song.
        if (lyrics is not null)
            tabs.Items.Add(_lyricsTab = new TabItem { Header = "Lyrics", Content = LyricsPart(lyrics) });
        if (tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header is string h && h == was) is { } same)
            tabs.SelectedItem = same;
        // Coming to the lyrics puts the caret in the box, ready for a paste.
        void Ready()
        {
            if (ReferenceEquals(tabs.SelectedItem, _lyricsTab) && _lyricsBox is { IsReadOnly: false } box)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        }
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) Ready(); };
        _tabs = tabs;

        var all = new DockPanel();
        all.Children.Add(header);
        all.Children.Add(tabs);
        Content = all;
        Ready();
        foreach (var p in old) p.Dispose();
    }

    /// The size of the words in the tabs: a step up from Preferences, because
    /// this is a window for reading values off, not for ticking boxes.
    private const double Text = 14;

    /// Lyrics are read, a line at a time, perhaps from across a room: larger
    /// again, and on white so they stand as a page.
    private const double LyricsText = 17;

    /// A tab whose content scrolls when it is longer than the window: a FLAC's
    /// tags and a long song's lyrics both are.
    private static TabItem Tab(string header, Control content) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(0, 0, 14, 0), Child = content },
        },
    };

    /// The lyrics, in a box that is always ready to be typed or pasted into
    /// where the library allows it, with Save under it. Where it does not, the
    /// same box only shows them, and the greyed Save says why.
    private Control LyricsPart(Lyrics lyrics)
    {
        var current = lyrics.Current;
        var can = lyrics.Save is not null && lyrics.WhyNot is null;

        // The whitest thing in the window: the page the words are printed on.
        var box = new TextBox
        {
            Text = current, FontSize = LyricsText, LineHeight = 28, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            IsReadOnly = !can, PlaceholderText = can ? "Paste or type the lyrics here." : "None in the file.",
            Background = Brushes.White, BorderBrush = SolidColorBrush.Parse("#D6D6DA"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(18, 14),
        };
        // And it stays white under the pointer and with the caret in it.
        box.Resources["TextControlBackgroundPointerOver"] = Brushes.White;
        box.Resources["TextControlBackgroundFocused"] = Brushes.White;
        // Nor does it light up in the system's blue for holding the caret,
        // which it nearly always does: a darker edge says as much.
        box.Resources["TextControlBorderBrushFocused"] = SolidColorBrush.Parse("#9A9AA2");
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var said = Dim(new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var save = new Button { Content = "Save", MinWidth = 84, IsEnabled = false };
        save.Classes.Add("panel");
        var under = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(said, 1);
        under.Children.Add(save);
        under.Children.Add(said);

        void Say(string? text) => said.Text = text ?? "";
        bool Unsaved() => can && (box.Text ?? "") != current;

        if (!can)
        {
            ToolTip.SetTip(save, lyrics.WhyNot ?? "These cannot be changed.");
            ToolTip.SetShowOnDisabled(save, true);
        }
        else
        {
            var saving = false;
            async Task Save()
            {
                if (saving || !Unsaved()) return;
                saving = true;
                save.IsEnabled = false;
                box.IsReadOnly = true;
                var text = Domain.TagWriter.Tidy(box.Text);
                var problem = await lyrics.Save!(text);
                box.IsReadOnly = false;
                saving = false;
                if (problem is not null) { save.IsEnabled = true; Say(problem); return; }
                current = text;
                if (box.Text != text) box.Text = text;
                _warned = false;
                Say("Saved to the file.");
            }
            box.TextChanged += (_, _) =>
            {
                if (saving) return;
                save.IsEnabled = Unsaved();
                if (save.IsEnabled) { Say(null); _warned = false; }
            };
            save.Click += async (_, _) => await Save();
            box.KeyDown += async (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.S || e.KeyModifiers != Avalonia.Input.KeyModifiers.Control) return;
                e.Handled = true;
                await Save();
            };
            _unsaved = Unsaved;
            _save = Save;
        }
        _say = Say;
        _lyricsBox = box;

        var part = new DockPanel();
        DockPanel.SetDock(under, Dock.Bottom);
        part.Children.Add(under);
        part.Children.Add(box);
        return part;
    }

#if DEBUG
    /// The snapshot rig's hands in this window: "next", "prev", "tab <name>",
    /// "type <text>" into the lyrics box, "save".
    public async void Drive(string what)
    {
        if (what == "save" && _save is { } save) await save();
        else if (what == "next") Go(_around?.Next);
        else if (what == "prev") Go(_around?.Previous);
        else if (what.StartsWith("tab ") && _tabs is { } tabs)
            tabs.SelectedItem = tabs.Items.OfType<TabItem>()
                .FirstOrDefault(t => t.Header is string h && h.Equals(what[4..].Trim(), StringComparison.OrdinalIgnoreCase)) ?? tabs.SelectedItem;
        else if (what.StartsWith("type ") && _lyricsBox is { IsReadOnly: false } box) box.Text = what[5..].Replace("\\n", "\n");
        Console.WriteLine($"[props] {what}: {Title}, tab {(_tabs?.SelectedItem as TabItem)?.Header}, "
                          + $"unsaved {_unsaved?.Invoke()}, said '{(_lyricsBox?.Parent as DockPanel)?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()?.Text}'");
    }
#endif

    // IMMUTABLE, and that is not a nicety. An ordinary brush belongs to the
    // thread that made it, and a static field is made on whichever thread first
    // touches the class's statics: here that was the worker reading a file's
    // tags (Read, below), so the window drew its text with brushes the UI
    // thread did not own and the program died on the first frame. 0.5.0 shipped
    // that. An immutable brush belongs to no thread.
    private static readonly IBrush Ink = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#1D1D20"));
    private static readonly IBrush DimInk = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#62626A"));

    /// A label, or a second line: the grey ink of this window, not the warm
    /// tan the "dim" class gives everywhere else, which is made for dark.
    private static T Dim<T>(T text) where T : TextBlock
    {
        text.Foreground = DimInk;
        return text;
    }

    // ---- what a file says about itself ---------------------------------------

    /// A file's own account: its audio, every tag, its lyrics. `Problem` is
    /// set, and the rest empty, if it could not be read.
    public sealed record FileFacts(IReadOnlyList<Row> Audio, IReadOnlyList<Row> Tags, string? Lyrics, string? Problem);

    /// Tags that are not lines of text: the lyrics get a section of their own,
    /// and a picture in a tag is megabytes of base64.
    private static readonly string[] NotLines = ["LYRICS", "UNSYNCEDLYRICS", "METADATA_BLOCK_PICTURE", "COVERART", "COVERARTMIME"];

    /// Opens the file and reads it. Not on the UI thread: a file on a share
    /// takes as long as the share does.
    public static FileFacts Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var audio = new List<Row>();
            var p = file.Properties;
            if (p is not null)
            {
                if (!string.IsNullOrWhiteSpace(p.Description)) audio.Add(new Row("Kind", p.Description));
                if (p.AudioSampleRate > 0)
                    audio.Add(new Row("Sample rate", $"{p.AudioSampleRate / 1000.0:0.###} kHz"
                                                     + (p.BitsPerSample > 0 ? $", {p.BitsPerSample} bit" : "")
                                                     + (p.AudioChannels > 0 ? $", {p.AudioChannels} channels" : "")));
                if (p.AudioBitrate > 0) audio.Add(new Row("Bit rate", $"{p.AudioBitrate:N0} kbps"));
            }

            var tags = new List<Row>();
            string? lyrics = null;
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                // A FLAC or an Ogg file names its own tags: show them all, as written.
                foreach (var name in xiph.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    if (NotLines.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    foreach (var value in xiph.GetField(name))
                        tags.Add(new Row(name, value));
                }
                lyrics = new[] { "LYRICS", "UNSYNCEDLYRICS" }
                    .Select(n => xiph.GetFirstField(n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }
            else
            {
                // An MP3's or an M4A's are frames and atoms with codes for
                // names: show what they mean rather than what they are called.
                var t = file.Tag;
                void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) tags.Add(new Row(label, value!)); }
                Add("Title", t.Title);
                Add("Artist", string.Join("; ", t.Performers ?? []));
                Add("Album artist", string.Join("; ", t.AlbumArtists ?? []));
                Add("Album", t.Album);
                Add("Year", t.Year > 0 ? t.Year.ToString() : null);
                Add("Track", t.Track > 0 ? t.TrackCount > 0 ? $"{t.Track} of {t.TrackCount}" : t.Track.ToString() : null);
                Add("Disc", t.Disc > 0 ? t.DiscCount > 0 ? $"{t.Disc} of {t.DiscCount}" : t.Disc.ToString() : null);
                Add("Genre", string.Join("; ", t.Genres ?? []));
                Add("Composer", string.Join("; ", t.Composers ?? []));
                Add("Comment", t.Comment);
                Add("Copyright", t.Copyright);
                Add("MusicBrainz release", t.MusicBrainzReleaseId);
                Add("MusicBrainz release group", t.MusicBrainzReleaseGroupId);
                Add("MusicBrainz recording", t.MusicBrainzTrackId);
                lyrics = t.Lyrics;
            }
            return new FileFacts(audio, tags, string.IsNullOrWhiteSpace(lyrics) ? null : lyrics!.Trim(), null);
        }
        catch (Exception ex)
        {
            return new FileFacts([], [], null, ex.Message);
        }
    }

    public static string Bytes(long n) => n switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{n / 1048576.0:0.#} MB",
        _ => $"{n / 1073741824.0:0.##} GB",
    };
}
