// AlbumWall — Properties: what an album or a track is, from the right-click menu.
//
// It shows what the library already knows and, for a track that is a file on
// this machine, what the file itself says: every tag, and the lyrics if they
// are embedded. Nothing is kept from that reading; it is done when the window
// is asked for and forgotten when it closes.
//
// ONE THING HERE WRITES: the lyrics of a track, in a library he has allowed
// editing in (Library.Editable). The writing itself is Domain/TagWriter.cs;
// this window only offers a box to type or paste into. Everything else is
// read-only, and tag editing proper is still to come (docs/roadmap.md).
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

    private static PropertiesWindow? _open;

    /// The one that is up, if one is: the snapshot rig photographs it.
    public static PropertiesWindow? Current => _open;

    /// Shows the properties of one thing. There is one such window: asking for
    /// another's replaces it.
    public static void ShowFrom(Window owner, string title, string subtitle, IEnumerable<Section> sections,
                                Lyrics? lyrics = null, Picture? cover = null)
    {
        _open?.Close();
        _open = new PropertiesWindow(title, subtitle, sections, lyrics, cover);
        var mine = _open;
        mine.Closed += (_, _) => { if (ReferenceEquals(_open, mine)) _open = null; };
        mine.Show(owner);
        mine.Activate();
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

    private PropertiesWindow(string title, string subtitle, IEnumerable<Section> sections, Lyrics? lyrics, Picture? cover)
    {
        Closed += (_, _) => { foreach (var p in _pictures) p.Dispose(); _pictures.Clear(); };

        Title = $"{App.DisplayName} — Properties";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        // The Preferences format: a fixed size, so it does not jump from tab
        // to tab, and a tab for each part.
        Width = 660;
        Height = 620;
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
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && e.Source is not TextBox) Close(); };

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
        if (lyrics is not null) tabs.Items.Add(Tab("Lyrics", LyricsPart(lyrics)));

        var all = new DockPanel();
        all.Children.Add(header);
        all.Children.Add(tabs);
        Content = all;
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

    /// The lyrics, and under them the way to change them: a button that turns
    /// the text into a box to type or paste into, with Save and Cancel.
    private static Control LyricsPart(Lyrics lyrics)
    {
        var current = lyrics.Current;
        var part = new StackPanel { Spacing = 10 };

        var shown = new SelectableTextBlock { FontSize = LyricsText, LineHeight = 28, TextWrapping = TextWrapping.Wrap };
        // The whitest thing in the window: the page the words are printed on.
        var page = new Border
        {
            Background = Brushes.White, BorderBrush = SolidColorBrush.Parse("#D6D6DA"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(18, 14), Child = shown,
        };
        // The box keeps to the tab's height and scrolls inside itself, so Save
        // and Cancel stay in sight under a long song.
        var box = new TextBox
        {
            FontSize = LyricsText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 350,
            IsVisible = false,
        };
        var said = Dim(new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false });
        var edit = new Button { Content = "Edit lyrics…" };
        var save = new Button { Content = "Save", MinWidth = 84, IsVisible = false };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, IsVisible = false };
        foreach (var b in new[] { edit, save, cancel }) b.Classes.Add("panel");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        buttons.Children.Add(edit);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        void Show() => shown.Text = current.Length > 0 ? current : "None in the file.";
        void Editing(bool on)
        {
            page.IsVisible = edit.IsVisible = !on;
            box.IsVisible = save.IsVisible = cancel.IsVisible = on;
            if (on) { box.Text = current; box.Focus(); }
        }
        void Say(string? text)
        {
            said.Text = text ?? "";
            said.IsVisible = !string.IsNullOrEmpty(text);
        }

        Show();
        if (lyrics.Save is null || lyrics.WhyNot is not null)
        {
            edit.IsEnabled = false;
            ToolTip.SetTip(edit, lyrics.WhyNot ?? "These cannot be changed.");
            ToolTip.SetShowOnDisabled(edit, true);
        }
        else
        {
            edit.Click += (_, _) => { Say(null); Editing(true); };
            cancel.Click += (_, _) => Editing(false);
            save.Click += async (_, _) =>
            {
                save.IsEnabled = cancel.IsEnabled = false;
                var text = Domain.TagWriter.Tidy(box.Text);
                var problem = await lyrics.Save(text);
                save.IsEnabled = cancel.IsEnabled = true;
                if (problem is not null) { Say(problem); return; }
                current = text;
                Show();
                Editing(false);
                Say("Saved to the file.");
            };
        }

        part.Children.Add(page);
        part.Children.Add(box);
        part.Children.Add(buttons);
        part.Children.Add(said);
        return part;
    }

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
