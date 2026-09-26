using Barbaric.Core.Library;

namespace Barbaric.Core.Tests.Library;

public sealed class SearchTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();

    public async Task InitializeAsync()
    {
        _library.AddSong("1.wav", title: "Beat It", artist: "Michael Jackson", album: "Thriller");
        _library.AddSong("2.wav", title: "Billie Jean", artist: "Michael Jackson", album: "Thriller");
        _library.AddSong("3.wav", title: "Halo", artist: "Beyoncé", album: "I Am... Sasha Fierce");
        _library.AddSong("4.wav", title: "Back in Black", artist: "AC/DC", album: "Back in Black", seconds: 1);
        _library.AddSong("untagged demo take.wav");
        await _library.AddMusicFolderAndScanAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("beat", new[] { "Beat It" })]
    [InlineData("jack", new[] { "Beat It", "Billie Jean" })]
    [InlineData("jack bil", new[] { "Billie Jean" })]
    [InlineData("beyonce", new[] { "Halo" })] // accents are ignored
    [InlineData("ac/dc", new[] { "Back in Black" })]
    [InlineData("thrill", new[] { "Beat It", "Billie Jean" })] // album
    [InlineData("demo", new[] { "untagged demo take" })] // file name
    [InlineData("nothing-like-this", new string[0])]
    public async Task Search_MatchesWordPrefixes(string text, string[] expected)
    {
        var rows = await _library.AllRowsAsync(new TrackQuery(text));

        Assert.Equal(expected.Order(), rows.Select(r => r.Title).Order());
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("ac\"dc")]
    [InlineData("OR AND NOT")]
    [InlineData("* ( ) :")]
    public async Task Search_TreatsOperatorsAndQuotesAsText(string text)
    {
        var page = await _library.Tracks.QueryAsync(new TrackQuery(text), 0, 10);

        Assert.True(page.Total >= 0); // i.e. no FTS syntax error
    }

    [Fact]
    public async Task EmptySearch_ReturnsEverything_SortedByArtistWithUnknownLast()
    {
        var rows = await _library.AllRowsAsync();

        Assert.Equal(
            ["Back in Black", "Halo", "Beat It", "Billie Jean", "untagged demo take"],
            rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Sort_ByTitleDescending()
    {
        var rows = await _library.AllRowsAsync(new TrackQuery(Sort: TrackSort.Title, Descending: true));

        Assert.Equal(
            ["untagged demo take", "Halo", "Billie Jean", "Beat It", "Back in Black"],
            rows.Select(r => r.Title));
    }

    [Fact]
    public async Task Paging_ReturnsTotalAndRequestedSlice()
    {
        var page = await _library.Tracks.QueryAsync(new TrackQuery(Sort: TrackSort.Title), offset: 1, limit: 2);

        Assert.Equal(5, page.Total);
        Assert.Equal(["Beat It", "Billie Jean"], page.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task QueryIds_FollowTheListOrder()
    {
        var query = new TrackQuery("jack", TrackSort.Title, Descending: true);

        var ids = await _library.Tracks.QueryIdsAsync(query);

        Assert.Equal((await _library.AllRowsAsync(query)).Select(r => r.Id), ids);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("- !", null)]
    [InlineData("beat it", "\"beat\"* AND \"it\"*")]
    [InlineData("say \"hi\"", "\"say\"* AND \"\"\"hi\"\"\"*")]
    public void SearchText_BuildsPrefixTerms(string? text, string? expected)
    {
        Assert.Equal(expected, SearchText.ToMatchExpression(text));
    }
}
