using AlbumWall.Domain;

namespace AlbumWall.Tests;

/// LRC as found in the wild (Lyrics.cs).
public sealed class LyricsTests
{
    private static TimeSpan T(int m, double s) => TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);

    [Fact]
    public void Nothing_is_no_lyrics()
    {
        Assert.Null(Lyrics.Parse(null));
        Assert.Null(Lyrics.Parse("  \n \r\n"));
    }

    [Fact]
    public void Plain_lyrics_keep_their_lines_and_gaps()
    {
        var l = Lyrics.Parse("First line\r\nSecond line\n\nAfter a gap")!;
        Assert.False(l.Timed);
        Assert.Equal(["First line", "Second line", "", "After a gap"], l.Lines.Select(x => x.Text));
        Assert.All(l.Lines, x => Assert.Null(x.At));
    }

    [Fact]
    public void Timed_lyrics_are_in_order_with_headers_dropped()
    {
        var l = Lyrics.Parse("[ar:Someone]\n[ti:A song]\n[00:12.50]One\n[00:05.00]Zero\n[01:02.345]Two")!;
        Assert.True(l.Timed);
        Assert.Equal(["Zero", "One", "Two"], l.Lines.Select(x => x.Text));
        Assert.Equal([T(0, 5), T(0, 12.5), T(1, 2.345)], l.Lines.Select(x => x.At!.Value));
    }

    [Fact]
    public void Several_stamps_on_a_line_are_that_line_each_time()
    {
        var l = Lyrics.Parse("[00:10.00][01:30.00]Chorus\n[00:50.00]Verse")!;
        Assert.Equal([("Chorus", T(0, 10)), ("Verse", T(0, 50)), ("Chorus", T(1, 30))], l.Lines.Select(x => (x.Text, x.At!.Value)));
    }

    [Fact]
    public void An_offset_moves_every_line_earlier_when_positive()
    {
        var l = Lyrics.Parse("[offset:+250]\n[00:10.00]A")!;
        Assert.Equal(T(0, 9.75), l.Lines.Single().At);
    }

    [Fact]
    public void Stamps_as_written_in_the_wild_are_read()
    {
        var l = Lyrics.Parse("[00:01]No fraction\n[00:02:50]Colon for a dot\n[120:00.00]Past an hour and a half")!;
        Assert.Equal([T(0, 1), T(0, 2.5), T(120, 0)], l.Lines.Select(x => x.At!.Value));
    }

    [Fact]
    public void A_line_without_a_stamp_inside_timed_lyrics_is_dropped()
    {
        var l = Lyrics.Parse("Lyrics by someone\n[00:01.00]A\n[00:02.00]B")!;
        Assert.True(l.Timed);
        Assert.Equal(["A", "B"], l.Lines.Select(x => x.Text));
    }
}
