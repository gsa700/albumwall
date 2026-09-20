// AlbumWall — "What's inside": the third-party notices, shown from the About tab.
//
// The text is THIRD-PARTY-NOTICES.md from the root of the repository, carried
// inside the exe as a resource. Inside the exe and not only on a web page,
// because the licenses ask that the notices travel WITH the program: a copy
// handed on by itself, with no network, still says what it is made of.
//
// In the format of Preferences (see PrefsWindow.axaml): a proper decorated
// window, fixed size, one instance, owned by the window that opened it, the
// sheet's colors. Built in code because it is one scrolling column of text and
// the markup would be longer than this.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace AlbumWall.App;

public sealed class NoticesWindow : Window
{
    private static NoticesWindow? _open;

    /// The one that is up, if one is: the snapshot rig photographs it.
    public static NoticesWindow? Current => _open;

    /// Shows the one instance, bringing it forward if it is already up.
    public static void ShowFrom(Window owner)
    {
        if (_open is null)
        {
            _open = new NoticesWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show(owner);
        }
        _open.Activate();
    }

    /// For the snapshot rig: opens the first fold whose name contains `part` and
    /// brings it into view.
    public void OpenFold(string part)
    {
        var fold = this.GetVisualDescendants().OfType<Expander>()
            .FirstOrDefault(e => (e.Header as TextBlock)?.Text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true);
        if (fold is null) return;
        fold.IsExpanded = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => fold.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private NoticesWindow()
    {
        Title = $"{App.DisplayName} — What's inside";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AlbumWall/Assets/app.ico")));
        Width = 660;
        Height = 700;
        CanResize = false;
        ShowInTaskbar = false;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Foreground = SolidColorBrush.Parse("#F3EEE6");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetBg");

        var column = new StackPanel { Spacing = 4, Margin = new Thickness(26, 20, 30, 26) };

        // The folds in the sheet's colors and at a height meant for a list of
        // twenty-one. The theme's own are near-black slabs 48 px tall, which on
        // this panel read as a wall of buttons rather than a list of documents.
        column.Resources["ExpanderMinHeight"] = 34.0;
        foreach (var key in new[] { "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver",
                                    "ExpanderHeaderBackgroundPressed" })
            column.Resources[key] = Application.Current!.Resources["SheetBg"];
        column.Resources["ExpanderContentBackground"] = Brushes.Transparent;
        foreach (var block in Read())
            column.Children.Add(block);
        foreach (var fold in LicenseTexts())
            column.Children.Add(fold);

        var panel = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = column
            }
        };
        panel[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetField");
        panel[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SheetEdge");
        Content = panel;
    }

    /// The license texts themselves, which the notices end by promising: every file
    /// under Assets/licenses, one fold each, closed. They are other people's words
    /// and are shown exactly as they came — monospaced, because most of them are
    /// laid out for an 80-column page. A text is only read out of the exe when its
    /// fold is first opened: there are twenty-one and the GPL alone is 35 KB.
    private static IEnumerable<Control> LicenseTexts()
    {
        var folder = new Uri("avares://AlbumWall/Assets/licenses");
        List<Uri> files;
        try { files = AssetLoader.GetAssets(folder, null).OrderBy(u => u.AbsolutePath, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { yield break; }

        foreach (var file in files)
        {
            var name = Uri.UnescapeDataString(file.AbsolutePath[(file.AbsolutePath.LastIndexOf('/') + 1)..]);
            var fold = new Expander
            {
                Header = new TextBlock { Text = name, FontSize = 13 },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 0)
            };
            fold.Expanded += (_, _) =>
            {
                if (fold.Content is not null) return;
                string text;
                try
                {
                    using var reader = new StreamReader(AssetLoader.Open(file));
                    text = reader.ReadToEnd();
                }
                catch (Exception ex) { text = $"Could not be read: {ex.Message}"; }

                fold.Content = new SelectableTextBlock
                {
                    Text = text.Replace("\r", "").Replace("\f", "").TrimStart('\uFEFF'),
                    FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
                    FontSize = 11, LineHeight = 15, TextWrapping = TextWrapping.Wrap, Opacity = 0.86
                };
            };
            yield return fold;
        }
    }

    /// The little of Markdown the notices file uses, and no more: two levels of
    /// heading, bullets whose continuation lines are indented, paragraphs,
    /// **bold** and `code` left as typed. Selectable, so a source address can be
    /// copied out of it.
    private static IEnumerable<Control> Read()
    {
        string text;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://AlbumWall/Assets/THIRD-PARTY-NOTICES.md"));
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            text = $"The notices could not be read: {ex.Message}\n\n"
                 + "They are THIRD-PARTY-NOTICES.md in AlbumWall's source.";
        }

        var paragraph = new List<string>();
        var bullet = false;

        Control Flush()
        {
            var body = string.Join(" ", paragraph).Replace("**", "").Replace("`", "");
            var block = new SelectableTextBlock
            {
                Text = bullet ? "•  " + body[2..] : body,
                FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
                Margin = bullet ? new Thickness(6, 4, 0, 4) : new Thickness(0, 3, 0, 5),
                Opacity = 0.86
            };
            paragraph.Clear();
            return block;
        }

        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = line.StartsWith("## ") ? 2 : line.StartsWith("# ") ? 1 : 0;
            var startsBullet = line.StartsWith("- ");

            // A blank line, a heading or a new bullet ends whatever was running.
            if ((line.Length == 0 || heading > 0 || startsBullet) && paragraph.Count > 0)
                yield return Flush();

            if (line.Length == 0) continue;

            if (heading > 0)
            {
                yield return new TextBlock
                {
                    Text = line[(heading + 1)..],
                    FontSize = heading == 1 ? 22 : 15,
                    FontWeight = heading == 1 ? FontWeight.Bold : FontWeight.SemiBold,
                    Margin = new Thickness(0, heading == 1 ? 0 : 16, 0, 4)
                };
                continue;
            }

            if (startsBullet) bullet = true;
            else if (paragraph.Count == 0) bullet = false;

            // "License:" and "Source:" each get a line of their own inside a
            // bullet; everything else in a paragraph is one run of text.
            var t = line.Trim();
            paragraph.Add(bullet && (t.StartsWith("License:") || t.StartsWith("Source:")) ? "\n" + t : t);
        }

        if (paragraph.Count > 0) yield return Flush();
    }
}
