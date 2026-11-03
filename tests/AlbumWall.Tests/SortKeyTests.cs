using AlbumWall.Domain;

namespace AlbumWall.Tests;

public sealed class SortKeyTests
{
    private static string Key(string artist, int year, string title) =>
        new Album { AlbumArtist = artist, Title = title, Year = year }.SortKey;

    [Theory]
    [InlineData("The Doobie Brothers", "Doobie Brothers")]
    [InlineData("A Flock of Seagulls", "Flock of Seagulls")]
    [InlineData("An Emotional Fish", "Emotional Fish")]
    [InlineData("THE BEATLES", "BEATLES")]
    [InlineData("Theatre of Tragedy", "Theatre of Tragedy")]     // only a whole word
    [InlineData("Them", "Them")]
    public void A_leading_article_is_left_out(string artist, string sortsAs) =>
        Assert.StartsWith(sortsAs + " ", Key(artist, 1980, "X"));

    [Fact]
    public void An_artists_albums_go_by_year_then_title() =>
        Assert.Equal(["Tales", "I Robot", "Eve"],
            new[] { ("I Robot", 1977), ("Eve", 1979), ("Tales", 1976) }
                .OrderBy(a => Key("The Alan Parsons Project", a.Item2, a.Item1), StringComparer.OrdinalIgnoreCase)
                .Select(a => a.Item1));
}
