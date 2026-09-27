using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class PlayStatsTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private Dictionary<string, long> _ids = [];

    public async Task InitializeAsync()
    {
        _library.AddSong("a.wav", title: "A");
        _library.AddSong("b.wav", title: "B");
        _library.AddSong("c.wav", title: "C");
        await _library.AddMusicFolderAndScanAsync();
        _ids = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Record_CountsPlaysAndSkips_AndStampsLastPlayed()
    {
        await _library.Stats.RecordAsync(_ids["A"], 9_000, 10_000, PlayKind.Complete);
        await _library.Stats.RecordAsync(_ids["A"], 1_000, 10_000, PlayKind.Skip);
        _library.Clock.Advance(TimeSpan.FromMinutes(5));
        await _library.Stats.RecordAsync(_ids["A"], 5_000, 10_000, PlayKind.Partial);

        var track = (await _library.Tracks.GetAsync(_ids["A"]))!;
        Assert.Equal((1, 1), (track.PlayCount, track.SkipCount));
        Assert.Equal(_library.Clock.Now.UtcTicks, track.LastPlayedUtc);

        using var connection = _library.Database.Open();
        var kinds = await connection.QueryAsync<string>("SELECT kind FROM play_events WHERE track_id = @id ORDER BY id", new { id = _ids["A"] });
        Assert.Equal(["complete", "skip", "partial"], kinds);
    }

    [Fact]
    public async Task FifthSkip_FlagsTheSong_AndKeepClearsIt()
    {
        for (var i = 0; i < 4; i++)
        {
            Assert.False(await _library.Stats.RecordAsync(_ids["B"], 500, 10_000, PlayKind.Skip));
        }

        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));
        Assert.True(await _library.Stats.RecordAsync(_ids["B"], 500, 10_000, PlayKind.Skip));
        Assert.Equal(["B"], await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));
        Assert.Equal(new LibraryCounts(1, 0, 3, 0), await _library.Stats.GetCountsAsync());

        await _library.Stats.KeepAsync([_ids["B"]]);

        var track = (await _library.Tracks.GetAsync(_ids["B"]))!;
        Assert.Equal((0, 0, false), (track.PlayCount, track.SkipCount, track.Flagged));
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));
    }

    [Fact]
    public async Task Flag_ClearsByItself_WhenTheSongWinsTheUserBack()
    {
        for (var i = 0; i < 5; i++)
        {
            await _library.Stats.RecordAsync(_ids["B"], 500, 10_000, PlayKind.Skip);
        }

        Assert.False(await _library.Stats.RecordAsync(_ids["B"], 10_000, 10_000, PlayKind.Complete)); // 6/8 = 0.75
        Assert.True(await _library.Stats.RecordAsync(_ids["B"], 10_000, 10_000, PlayKind.Complete));  // 6/9 ≈ 0.67

        Assert.False((await _library.Tracks.GetAsync(_ids["B"]))!.Flagged);
    }

    [Fact]
    public async Task SetSkips_ChangesOnlySkips_AndTheFlagFollows()
    {
        await _library.Stats.RecordAsync(_ids["B"], 10_000, 10_000, PlayKind.Complete);
        await _library.Stats.RecordAsync(_ids["B"], 10_000, 10_000, PlayKind.Complete);

        await _library.Stats.SetSkipsAsync([_ids["B"]], 9);

        var track = (await _library.Tracks.GetAsync(_ids["B"]))!;
        Assert.Equal((2, 9, true), (track.PlayCount, track.SkipCount, track.Flagged));
        Assert.Equal(["B"], await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));

        await _library.Stats.SetSkipsAsync([_ids["B"]], 0);

        track = (await _library.Tracks.GetAsync(_ids["B"]))!;
        Assert.Equal((2, 0, false), (track.PlayCount, track.SkipCount, track.Flagged));
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));

        using var connection = _library.Database.Open();
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM play_events WHERE track_id = @id", new { id = _ids["B"] }));
    }

    [Fact]
    public async Task SetSkips_ClampsNegativeCountsToZero()
    {
        await _library.Stats.RecordAsync(_ids["A"], 500, 10_000, PlayKind.Skip);

        await _library.Stats.SetSkipsAsync([_ids["A"]], -3);

        Assert.Equal(0, (await _library.Tracks.GetAsync(_ids["A"]))!.SkipCount);
    }

    [Fact]
    public async Task Record_ForAVanishedTrack_ReturnsNull()
    {
        Assert.Null(await _library.Stats.RecordAsync(999_999, 500, 10_000, PlayKind.Skip));
    }

    [Fact]
    public async Task HiddenSongs_LeaveTheLibraryAndItsCounts_AndStayHiddenAfterARescan()
    {
        var tag = await _library.Tags.CreateAsync("mine");
        await _library.Tags.AddToTracksAsync(tag.Id, [_ids["A"], _ids["B"]]);
        var playlist = await _library.Playlists.CreateManualAsync("Mix", [_ids["A"], _ids["B"]]);

        await _library.Stats.SetHiddenAsync([_ids["A"]], hidden: true);
        await _library.Scanner.ScanAsync();

        Assert.Equal(["B", "C"], await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Title)));
        Assert.Equal(["A"], await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Hidden)));
        Assert.Equal(1, (await _library.Tags.GetAsync(tag.Id))!.Count);
        Assert.Equal(1, (await _library.Playlists.GetAsync(playlist))!.Count);
        Assert.Equal(new LibraryCounts(0, 1, 1, 0), await _library.Stats.GetCountsAsync());

        await _library.Stats.SetHiddenAsync([_ids["A"]], hidden: false);
        Assert.Equal(3, (await _library.AllRowsAsync()).Count);
    }

    [Fact]
    public async Task UntaggedCount_DropsAsSongsGetTagged()
    {
        Assert.Equal(3, (await _library.Stats.GetCountsAsync()).Untagged);

        var tag = await _library.Tags.CreateAsync("mine");
        await _library.Tags.AddToTracksAsync(tag.Id, [_ids["A"]]);
        Assert.Equal(2, (await _library.Stats.GetCountsAsync()).Untagged);

        await _library.Tags.RemoveFromTracksAsync(tag.Id, [_ids["A"]]);
        Assert.Equal(3, (await _library.Stats.GetCountsAsync()).Untagged);
    }

    [Fact]
    public async Task HiddenSongs_AreNotSuggested()
    {
        for (var i = 0; i < 5; i++)
        {
            await _library.Stats.RecordAsync(_ids["C"], 500, 10_000, PlayKind.Skip);
        }

        await _library.Stats.SetHiddenAsync([_ids["C"]], hidden: true);

        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Suggested)));
    }

    [Fact]
    public async Task MarkMissing_TakesSongsOutOfEveryView()
    {
        await _library.Stats.SetHiddenAsync([_ids["B"]], hidden: true);

        await _library.Stats.MarkMissingAsync([_ids["A"], _ids["B"]]);

        Assert.Equal(["C"], await _library.TitlesAsync());
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Scope: TrackScope.Hidden)));
    }

    [Fact]
    public async Task List_SortsByPlaysAndSkips_AndCarriesTheCounts()
    {
        await _library.Stats.RecordAsync(_ids["B"], 500, 10_000, PlayKind.Skip);
        await _library.Stats.RecordAsync(_ids["B"], 500, 10_000, PlayKind.Skip);
        await _library.Stats.RecordAsync(_ids["C"], 500, 10_000, PlayKind.Skip);
        await _library.Stats.RecordAsync(_ids["A"], 10_000, 10_000, PlayKind.Complete);

        Assert.Equal(["B", "C", "A"], await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Skips, Descending: true)));
        Assert.Equal(["A", "B", "C"], await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Plays, Descending: true)));

        var b = (await _library.AllRowsAsync()).Single(r => r.Title == "B");
        Assert.Equal((0, 2), (b.PlayCount, b.SkipCount));
    }

    [Fact]
    public async Task ShuffleStats_ComeBackForTheGivenIds()
    {
        await _library.Stats.RecordAsync(_ids["A"], 500, 10_000, PlayKind.Skip);

        var stats = await _library.Stats.GetShuffleStatsAsync([_ids["A"], _ids["B"]]);

        Assert.Equal(2, stats.Count);
        var a = stats.Single(s => s.Id == _ids["A"]);
        Assert.Equal(new ShuffleStats(_ids["A"], 0, 1, _library.Clock.Now.UtcTicks, a.DurationMs), a);
        Assert.True(a.DurationMs > 0);
    }

    [Fact]
    public void SavedPlaylists_NeverKeepTheScope()
    {
        var json = PlaylistRepository.SerializeQuery(new TrackQuery(Scope: TrackScope.Suggested));

        Assert.Equal(TrackScope.Library, PlaylistRepository.DeserializeQuery(json)!.Scope);
    }
}
