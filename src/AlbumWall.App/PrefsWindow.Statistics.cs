// AlbumWall — the numbers on the Library tab of Preferences.
//
// The five totals of the selected library, under the list, and the rest
// below them (LibraryNumbers). The numbers come from Domain.LibraryStats,
// worked out from a library's last scan: live for the library on the wall,
// and from what that scan stored for any other (Domain/LibraryStatsStore.cs).
// Nothing here counts anything, it only lays the counts out.
//
// A tab of its own until 2026-10-10, then the lower half of Library, with the
// file types, bitrates, lossless formats and cover art in a window of their
// own while the page could not scroll. Since it scrolls they are back here,
// under the totals (LibraryNumbers, 2026-10-30).

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AlbumWall.Domain;

namespace AlbumWall.App;

public partial class PrefsWindow
{
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
        StatsHeader.Text = $"{library.Name}  —  {asOf}";
        var any = s is { Tracks: > 0 };

        StatsEmpty.IsVisible = !any;
        StatsTotals.IsVisible = any;
        StatsDetail.IsVisible = any;
        if (!any) return;

        // ---- the five figures across the top
        StatsTotals.Children.Clear();
        (string Figure, string Label)[] totals =
        [
            ($"{s!.Albums:N0}", "albums"),
            ($"{s.Tracks:N0}", "tracks"),
            ($"{s.Artists:N0}", "artists"),
            (LibraryNumbers.Spoken(s.PlayingTime), "of music"),
            (LibraryNumbers.Bytes(s.Bytes), "on disk")
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

        LibraryNumbers.Fill(StatsDetail, library, s, _host.SearchFor);
    }
}
