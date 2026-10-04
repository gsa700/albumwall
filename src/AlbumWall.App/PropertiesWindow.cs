// AlbumWall — Properties: what an album or a track is, from the right-click menu.
//
// READ-ONLY. It shows what the library already knows and, for a track that is
// a file on this machine, what the file itself says: every tag, and the lyrics
// if they are embedded. Nothing is kept from that reading; it is done when the
// window is asked for and forgotten when it closes. Tag editing, when it is
// built, is done from here (docs/roadmap.md).
//
// In the format of Preferences and What's inside: a proper decorated window,
// one instance, owned by the window that opened it, the sheet's colors, closed
// from its title bar. Every value can be selected and copied, since a path or
// an ID is what one comes here to get. Built in code because it is one column
// of labelled lines whose number is not known until the file has been read.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace AlbumWall.App;

public sealed class PropertiesWindow : Window
{
    /// One labelled line.
    public sealed record Row(string Label, string Value);

    /// Lines under a heading, or a body of text under one (lyrics).
    public sealed record Section(string Heading, IReadOnlyList<Row> Rows, string? Body = null);

    private static PropertiesWindow? _open;

    /// The one that is up, if one is: the snapshot rig photographs it.
    public static PropertiesWindow? Current => _open;

    /// Shows the properties of one thing. There is one such window: asking for
    /// another's replaces it.
    public static void ShowFrom(Window owner, string title, string subtitle, IEnumerable<Section> sections)
    {
        _open?.Close();
        _open = new PropertiesWindow(title, subtitle, sections);
        var mine = _open;
        mine.Closed += (_, _) => { if (ReferenceEquals(_open, mine)) _open = null; };
        mine.Show(owner);
        mine.Activate();
    }

    private PropertiesWindow(string title, string subtitle, IEnumerable<Section> sections)
    {
        Title = $"{App.DisplayName} — Properties";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Foreground = SolidColorBrush.Parse("#F3EEE6");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };

        var column = new StackPanel { Spacing = 4, Margin = new Thickness(24, 20, 28, 24) };
        column.Children.Add(new SelectableTextBlock
        {
            Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        if (subtitle.Length > 0)
            column.Children.Add(Dim(new SelectableTextBlock { Text = subtitle, FontSize = 13, TextWrapping = TextWrapping.Wrap }));

        foreach (var section in sections)
        {
            if (section.Rows.Count == 0 && string.IsNullOrEmpty(section.Body)) continue;
            column.Children.Add(new TextBlock
            {
                Text = section.Heading, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 16, 0, 4),
            });

            if (section.Rows.Count > 0)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16, RowSpacing = 5 };
                foreach (var row in section.Rows)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var at = grid.RowDefinitions.Count - 1;
                    var label = Dim(new TextBlock { Text = row.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Top });
                    var value = new SelectableTextBlock { Text = row.Value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
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
                    Text = section.Body, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
                });
        }

        var panel = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = column,
            },
        };
        panel[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetField");
        panel[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");
        Content = panel;
    }

    private static T Dim<T>(T text) where T : TextBlock
    {
        text.Classes.Add("dim");
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
