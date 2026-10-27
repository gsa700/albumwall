// AlbumWall - a kiosk decodes its whole wall up front (Program.Kiosk).
//
// Roadmap, "Kiosk mode: a bigger, look-ahead cover cache" (2026-09-26). The first
// 4K scroll on the Pi 5 stuttered with CPU bursts of 100-190% while scrolling and
// 5-20% between: covers being decoded as they came into view. Once decoded, an
// album keeps its cover (AlbumVm.Cover) for as long as the library is on the
// wall, so scrolling BACK was never the cost; the first pass was. So a kiosk does
// the first pass itself, in the background, as soon as the wall is up: nearest
// to the screen first, two covers at a time, so the ones a scroll is about to
// show are ready and the ones on screen are never kept waiting. A desktop does
// not: it shares its memory and its processor with everything else running.

namespace AlbumWall.App;

public partial class MainWindow
{
    private CancellationTokenSource? _warm;
    private const int WarmAtOnce = 2;

    private void WarmCovers()
    {
        _warm?.Cancel();
        var stop = (_warm = new CancellationTokenSource()).Token;

        // Wall order, from the first album on screen outward: down first, the way
        // a wall is read, then up.
        var first = TopAlbumInView() is { } top ? Math.Max(0, _visible.IndexOf(top)) : 0;
        // Then whatever a search is hiding, for when it is cleared.
        var order = _visible.Skip(first).Concat(_visible.Take(first).Reverse())
                            .Concat(_all.Except(_visible)).ToList();
        var px = CoverPixels;

        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var gate = new SemaphoreSlim(WarmAtOnce);
            var jobs = new List<Task>();
            foreach (var album in order)
            {
                try { await gate.WaitAsync(stop); } catch (OperationCanceledException) { return; }
                jobs.Add(Task.Run(async () =>
                {
                    try { await album.LoadCoverAsync(px); } finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(jobs);
            if (!stop.IsCancellationRequested)
                Console.WriteLine($"[art] kiosk: {order.Count} covers ready in {sw.Elapsed.TotalSeconds:0.0} s, "
                                + $"{ArtCache.HeldBytes >> 20} MB held");
        });
    }
}
