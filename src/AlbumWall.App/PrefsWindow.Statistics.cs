// AlbumWall — the numbers on the Library tab of Preferences.
//
// The five totals of the selected library, under the list, and a button to
// the rest (StatisticsWindow). The numbers come from Domain.LibraryStats,
// worked out from a library's last scan: live for the library on the wall,
// and from what that scan stored for any other (Domain/LibraryStatsStore.cs).
// Nothing here counts anything, it only lays the counts out.
//
// A tab of its own until 2026-10-10, then the lower half of Library with
// everything on it, which made the tab 973 px tall - more than a 1080p laptop
// has. "I love the full data load, I don't want to lose that but maybe most
// people don't care": so the totals stay, and the file types, bitrates,
// lossless formats and cover art open in a window of their own.

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumWall.Domain;

namespace AlbumWall.App;

public partial class PrefsWindow
{
    /// What the totals are about just now, for the window behind the button.
    private (Library Library, LibraryStats? Stats, string AsOf)? _statsShown;
    private StatisticsWindow? _statsWindow;

    /// Called from Fill(), so it follows every scan as the rest of the window
    /// does, and from Select(), so it follows the line he clicks.
    private void FillStatistics()
    {
        if (_host is null || _host.Libraries.Count == 0) return;
        var library = Selected;
        var onWall = library.Id == _host.CurrentLibrary.Id;
        LibraryStats? s;
        string asOf;
        if (onWall && _host.LibraryStatistics is { } live)
        {
            s = live;
            asOf = "as it is now";
        }
        else if (_host.StoredStatistics(library) is { } stored)
        {
            s = stored.Stats;
            asOf = $"as of {stored.AsOf.ToLocalTime():d MMM, HH:mm}";
        }
        else
        {
            s = null;
            asOf = "not read yet";
        }
        _statsShown = (library, s, asOf);
        StatsHeader.Text = $"{library.Name}  —  {asOf}";
        var any = s is { Tracks: > 0 };

        StatsEmpty.IsVisible = !any;
        StatsTotals.IsVisible = any;
        StatsMore.IsEnabled = any;
        _statsWindow?.Refill(library, s, asOf);
        if (!any) return;

        // ---- the five figures across the top
        StatsTotals.Children.Clear();
        (string Figure, string Label)[] totals =
        [
            ($"{s!.Albums:N0}", "albums"),
            ($"{s.Tracks:N0}", "tracks"),
            ($"{s.Artists:N0}", "artists"),
            (StatisticsWindow.Spoken(s.PlayingTime), "of music"),
            (StatisticsWindow.Bytes(s.Bytes), "on disk")
        ];
        for (var i = 0; i < totals.Length; i++)
        {
            var cell = new StackPanel { Spacing = 1 };
            cell.Children.Add(new TextBlock { Text = totals[i].Figure, FontSize = 20, FontWeight = FontWeight.SemiBold });
            var label = new TextBlock { Text = totals[i].Label, FontSize = 12 };
            label.Classes.Add("dim");
            cell.Children.Add(label);
            Grid.SetColumn(cell, i);
            StatsTotals.Children.Add(cell);
        }
    }

    /// ONE window, refilled as the selection or a scan changes what it is
    /// about. Owned by this window, so it goes when this one does.
    private void OpenStatistics()
    {
        if (_host is null || _statsShown is not { Stats: not null } shown) return;
        if (_statsWindow is null)
        {
            _statsWindow = new StatisticsWindow(_host);
            _statsWindow.Closed += (_, _) => _statsWindow = null;
        }
        _statsWindow.Refill(shown.Library, shown.Stats, shown.AsOf);
        if (!_statsWindow.IsVisible) _statsWindow.Show(this);
        else _statsWindow.Activate();
    }
}
