using Barbaric.Core.Library;

namespace Barbaric.Core.Tests.Library;

public sealed class FilterTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private Dictionary<string, long> _tracks = [];
    private readonly Dictionary<string, long> _tags = [];

    public async Task InitializeAsync()
    {
        _library.AddSong("a.wav", title: "Alpha", artist: "Band One", bpm: 90);
        _library.AddSong("b.wav", title: "Bravo", artist: "Band One", bpm: 120);
        _library.AddSong("c.wav", title: "Charlie", artist: "Band Two", bpm: 140);
        _library.AddSong("d.wav", title: "Delta", artist: "Band Two");
        _library.AddSong("e.wav", title: "Echo", artist: "Band Two");
        await _library.AddMusicFolderAndScanAsync();
        _tracks = await _library.IdsByTitleAsync();

        await TagAsync("rock", "Alpha", "Bravo", "Charlie");
        await TagAsync("chill", "Bravo", "Delta");
        await TagAsync("live", "Charlie");
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(new[] { "rock" }, new string[0], new string[0], null, new[] { "Alpha", "Bravo", "Charlie" })]
    [InlineData(new[] { "rock", "chill" }, new string[0], new string[0], null, new[] { "Bravo" })]
    [InlineData(new string[0], new[] { "chill", "live" }, new string[0], null, new[] { "Bravo", "Charlie", "Delta" })]
    [InlineData(new string[0], new string[0], new[] { "rock" }, null, new[] { "Delta", "Echo" })]
    [InlineData(new[] { "rock" }, new string[0], new[] { "live" }, null, new[] { "Alpha", "Bravo" })]
    [InlineData(new[] { "rock" }, new[] { "chill", "live" }, new string[0], null, new[] { "Bravo", "Charlie" })]
    [InlineData(new[] { "rock" }, new string[0], new string[0], "one", new[] { "Alpha", "Bravo" })]
    [InlineData(new string[0], new string[0], new[] { "chill" }, "two", new[] { "Charlie", "Echo" })]
    [InlineData(new[] { "chill" }, new[] { "live" }, new string[0], null, new string[0])]
    public async Task TagFilters_CombineWithEachOtherAndWithSearch(
        string[] all, string[] any, string[] none, string? text, string[] expected)
    {
        var filter = new TrackFilter { AllTags = Ids(all), AnyTags = Ids(any), NoneTags = Ids(none) };

        var titles = await _library.TitlesAsync(new TrackQuery(text, TrackSort.Title, Filter: filter));

        Assert.Equal(expected, titles);
    }

    [Fact]
    public async Task Filter_KeepsTheRequestedSortOrder()
    {
        var filter = new TrackFilter { AllTags = Ids(["rock"]) };

        var titles = await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Title, Descending: true, Filter: filter));

        Assert.Equal(["Charlie", "Bravo", "Alpha"], titles);
    }

    [Fact]
    public async Task UnknownTagIds_MatchNothingWhenRequired_AndExcludeNothing()
    {
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Filter: new TrackFilter { AllTags = [987654] })));
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Filter: new TrackFilter { AnyTags = [987654] })));
        Assert.Equal(5, (await _library.TitlesAsync(new TrackQuery(Filter: new TrackFilter { NoneTags = [987654] }))).Count);
    }

    [Fact]
    public async Task RepeatedTagIds_DoNotBreakTheAllMatch()
    {
        var rock = _tags["rock"];

        var titles = await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Title, Filter: new TrackFilter { AllTags = [rock, rock] }));

        Assert.Equal(["Alpha", "Bravo", "Charlie"], titles);
    }

    [Theory]
    [InlineData(100.0, null, new[] { "Bravo", "Charlie" })]
    [InlineData(null, 130.0, new[] { "Alpha", "Bravo" })]
    [InlineData(100.0, 130.0, new[] { "Bravo" })]
    [InlineData(120.0, 120.0, new[] { "Bravo" })]
    public async Task BpmRange_LeavesOutUnknownBpm(double? min, double? max, string[] expected)
    {
        var filter = new TrackFilter { BpmMin = min, BpmMax = max };

        Assert.Equal(expected, await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Title, Filter: filter)));
    }

    [Fact]
    public async Task Rows_CarryTheirTagIds()
    {
        var rows = await _library.AllRowsAsync(new TrackQuery(Sort: TrackSort.Title));

        Assert.Equal(new[] { _tags["rock"], _tags["chill"] }.Order(), rows.Single(r => r.Title == "Bravo").TagIds);
        Assert.Empty(rows.Single(r => r.Title == "Echo").TagIds);
        Assert.All(rows, r => Assert.Null(r.Position));
    }

    [Fact]
    public async Task QueryIds_ReturnsTheRequestedSliceInListOrder()
    {
        var query = new TrackQuery(Sort: TrackSort.Title, Descending: true);
        var all = await _library.Tracks.QueryIdsAsync(query);

        var slice = await _library.Tracks.QueryIdsAsync(query, offset: 1, limit: 3);

        Assert.Equal(all.Skip(1).Take(3), slice);
    }

    [Fact]
    public async Task PositionSort_OutsideAPlaylist_FallsBackToArtistOrder()
    {
        Assert.Equal(await _library.TitlesAsync(), await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Position)));
    }

    private async Task TagAsync(string tag, params string[] titles)
    {
        var info = await _library.Tags.CreateAsync(tag);
        _tags[tag] = info.Id;
        await _library.Tags.AddToTracksAsync(info.Id, titles.Select(t => _tracks[t]));
    }

    private List<long> Ids(string[] tags) => [.. tags.Select(t => _tags[t])];
}
