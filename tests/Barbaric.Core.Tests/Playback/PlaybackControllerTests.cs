using Barbaric.Core.Audio;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Barbaric.Core.Tests.Audio;
using Barbaric.Core.Tests.Library;
using NAudio.Wave;

namespace Barbaric.Core.Tests.Playback;

public sealed class PlaybackControllerTests : IAsyncLifetime
{
    private static readonly TrackQuery ByTitle = new(Sort: TrackSort.Title);

    private readonly LibraryFixture _library = new();
    private readonly List<FakeWavePlayer> _outputs = [];
    private readonly AudioEngine _engine;
    private readonly PlaybackController _controller;
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, long> _ids = [];
    private readonly List<ListenRecord> _listens = [];

    public PlaybackControllerTests()
    {
        _engine = new AudioEngine(() =>
        {
            var output = new FakeWavePlayer();
            _outputs.Add(output);
            return Task.FromResult<IWavePlayer>(output);
        });
        _controller = new PlaybackController(
            _engine, _library.Tracks, _library.Stats, clock: _library.Clock, random: new Random(1234));
        _controller.ListenRecorded += (_, record) => _listens.Add(record);
        _controller.Error += (_, message) => _errors.Add(message);
    }

    private FakeWavePlayer CurrentOutput => _outputs[^1];

    public async Task InitializeAsync()
    {
        _library.AddSong("a.wav", title: "A", seconds: 5);
        _library.AddSong("b.wav", title: "B", seconds: 5);
        _library.AddSong("c.wav", title: "C", seconds: 5);
        await _library.AddMusicFolderAndScanAsync();
        foreach (var row in await _library.AllRowsAsync())
        {
            _ids[row.Title] = row.Id;
        }
    }

    public Task DisposeAsync()
    {
        _controller.Dispose();
        _engine.Dispose();
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PlayTrack_QueuesTheList_AndAdvancesWhenASongEnds()
    {
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);
        Assert.Equal("B", _controller.CurrentTrack?.Title);
        Assert.True(_controller.Queue.HasPrevious);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _controller.CurrentTrack?.Title == "C");
        Assert.Equal(PlayerState.Playing, _engine.State);

        // End of the list: it starts over from the top.
        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _controller.CurrentTrack?.Title == "A");
        Assert.Equal(PlayerState.Playing, _engine.State);
        Assert.Equal(0, _controller.Queue.Index);
    }

    [Fact]
    public async Task NextOnTheLastSong_WrapsToTheFirst()
    {
        await _controller.PlayTrackAsync(_ids["C"], ByTitle);
        Assert.False(_controller.Queue.HasNext);
        Assert.True(_controller.HasNext);

        Assert.True(await _controller.NextAsync());

        Assert.Equal("A", _controller.CurrentTrack?.Title);
        Assert.Equal(3, _controller.Queue.Count);
    }

    [Fact]
    public async Task Shuffle_AtTheEnd_ReshufflesAndPlaysEverySongAgain()
    {
        await _controller.PlayShuffledAsync(ByTitle);
        await _controller.NextAsync();
        await _controller.NextAsync();
        var last = _controller.Queue.Current;
        var version = _controller.Queue.Version;

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _controller.Queue.Version != version);

        Assert.Equal(0, _controller.Queue.Index);
        Assert.NotEqual(last, _controller.Queue.Current);
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
        Assert.Equal(PlayerState.Playing, _engine.State);
    }

    [Fact]
    public async Task ASingleSong_PlayedOnItsOwn_StopsAtTheEnd()
    {
        await _controller.PlayTrackAsync(_ids["B"]);
        Assert.False(_controller.HasNext);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _listens.Count == 1);

        Assert.Equal(PlayerState.Stopped, _engine.State);
        Assert.Equal("B", _controller.CurrentTrack?.Title);
    }

    [Fact]
    public async Task LoopTrack_ReplaysTheSong_AndCountsEachPlay()
    {
        _controller.LoopTrack = true;
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _listens.Count == 1 && _engine.State == PlayerState.Playing);
        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _listens.Count == 2 && _engine.State == PlayerState.Playing);

        Assert.Equal("A", _controller.CurrentTrack?.Title);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
        Assert.Equal([PlayKind.Complete, PlayKind.Complete], _listens.Select(l => l.Kind));
        Assert.Equal(2, (await TrackAsync("A")).PlayCount);
    }

    [Fact]
    public async Task LoopTrack_NextStillMovesOn()
    {
        _controller.LoopTrack = true;
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        await _controller.NextAsync();
        Assert.Equal("B", _controller.CurrentTrack?.Title);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _listens.Count == 2 && _engine.State == PlayerState.Playing);
        Assert.Equal("B", _controller.CurrentTrack?.Title);
    }

    [Fact]
    public async Task Previous_GoesBackEarlyInTheSong_ButRestartsLaterOn()
    {
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);

        _engine.Seek(TimeSpan.FromSeconds(4));
        await _controller.PreviousAsync();
        Assert.Equal("B", _controller.CurrentTrack?.Title);
        Assert.Equal(TimeSpan.Zero, _engine.Position);

        await _controller.PreviousAsync();
        Assert.Equal("A", _controller.CurrentTrack?.Title);
    }

    [Fact]
    public async Task SongVolume_IsSaved_AndRestoredNextTime()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        await _controller.SetTrackGainAsync(-6);

        await _controller.NextAsync();
        Assert.Equal(0, _engine.TrackGainDb);

        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        Assert.Equal(-6, _engine.TrackGainDb);
        Assert.Equal(-6, (await _library.Tracks.GetAsync(_ids["A"]))!.GainDb);
    }

    [Fact]
    public async Task MissingFile_IsSkipped()
    {
        File.Delete(Path.Combine(_library.MusicDir, "b.wav"));

        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        await _controller.NextAsync();

        Assert.Equal("C", _controller.CurrentTrack?.Title);
        Assert.Contains(_errors, e => e.Contains("\"B\""));
    }

    [Fact]
    public async Task PlayingFromAManualPlaylist_QueuesItInPlaylistOrder()
    {
        var playlist = await _library.Playlists.CreateManualAsync("Mix", [_ids["C"], _ids["A"]]);
        var context = new TrackQuery(Sort: TrackSort.Position, PlaylistId: playlist);

        await _controller.PlayTrackAsync(_ids["C"], context);
        Assert.Equal(2, _controller.Queue.Count);

        Assert.True(await _controller.NextAsync());
        Assert.Equal("A", _controller.CurrentTrack?.Title);
        Assert.False(_controller.Queue.HasNext);
    }

    [Fact]
    public async Task PlayingFromATagFilter_QueuesOnlyMatchingSongs()
    {
        var tag = await _library.Tags.CreateAsync("keep");
        await _library.Tags.AddToTracksAsync(tag.Id, [_ids["A"], _ids["C"]]);
        var context = ByTitle with { Filter = new TrackFilter { AllTags = [tag.Id] } };

        await _controller.PlayTrackAsync(_ids["A"], context);
        await _controller.NextAsync();

        Assert.Equal("C", _controller.CurrentTrack?.Title);
        Assert.False(_controller.Queue.HasNext);
    }

    [Fact]
    public async Task PlayFile_UsesTheLibraryTrackWhenThereIsOne()
    {
        await _controller.PlayFileAsync(Path.Combine(_library.MusicDir, "c.wav"));
        Assert.Equal(_ids["C"], _controller.CurrentTrack?.Id);

        var outside = Path.Combine(_library.Root, "outside.wav");
        File.Copy(Path.Combine(_library.MusicDir, "a.wav"), outside);
        await _controller.PlayFileAsync(outside);

        Assert.Null(_controller.CurrentTrack);
        Assert.Equal(outside, _engine.CurrentPath);
        Assert.Equal(0, _controller.Queue.Count);
    }

    [Fact]
    public async Task BpmLens_RefiltersTheQueue_AroundThePlayingSong()
    {
        await _library.Tracks.SetManualBpmAsync([_ids["A"]], 90);
        await _library.Tracks.SetManualBpmAsync([_ids["B"]], 120);
        await _library.Tracks.SetManualBpmAsync([_ids["C"]], 140);
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);

        await _controller.SetBpmLensAsync(new BpmRange(100, 130));
        Assert.Equal((false, false), (_controller.Queue.HasPrevious, _controller.Queue.HasNext));

        await _controller.SetBpmLensAsync(new BpmRange(80, 130));
        Assert.Equal((true, false), (_controller.Queue.HasPrevious, _controller.Queue.HasNext));

        // The playing song stays in the queue even when the range leaves it out.
        await _controller.SetBpmLensAsync(new BpmRange(130, 150));
        Assert.Equal(_ids["B"], _controller.Queue.Current);
        Assert.True(await _controller.NextAsync());
        Assert.Equal("C", _controller.CurrentTrack?.Title);

        await _controller.SetBpmLensAsync(null);
        Assert.Equal((3, _ids["C"]), (_controller.Queue.Count, _controller.Queue.Current));
    }

    [Fact]
    public async Task BpmLens_LeavesASingleSongQueueAlone()
    {
        await _controller.PlayTrackAsync(_ids["A"]);

        await _controller.SetBpmLensAsync(new BpmRange(200, 210));

        Assert.Equal((1, _ids["A"]), (_controller.Queue.Count, _controller.Queue.Current));
    }

    [Fact]
    public async Task Next_EarlyInASong_CountsAsASkip()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        _engine.Seek(TimeSpan.FromSeconds(0.5));

        await _controller.NextAsync();

        Assert.Equal(new ListenRecord(_ids["A"], PlayKind.Skip, false), Assert.Single(_listens));
        Assert.Equal(1, (await TrackAsync("A")).SkipCount);
    }

    [Fact]
    public async Task PickingAnotherSong_EarlyOn_CountsAsASkip_ButLateOn_AsAPlay()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);
        _engine.Seek(TimeSpan.FromSeconds(4.5));
        await _controller.PlayTrackAsync(_ids["C"], ByTitle);

        Assert.Equal([PlayKind.Skip, PlayKind.Complete], _listens.Select(l => l.Kind));
    }

    [Fact]
    public async Task ASongThatEnds_CountsAsAPlay()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _controller.CurrentTrack?.Title == "B");

        var a = await TrackAsync("A");
        Assert.Equal((1, 0), (a.PlayCount, a.SkipCount));
        Assert.Equal(_library.Clock.Now.UtcTicks, a.LastPlayedUtc);
    }

    [Fact]
    public async Task TheLastSongEnding_IsCountedToo()
    {
        await _controller.PlayTrackAsync(_ids["C"], ByTitle);

        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _listens.Count == 1);

        Assert.Equal(PlayKind.Complete, _listens[0].Kind);
    }

    [Fact]
    public async Task Previous_Stop_AndClosing_AreNeverSkips()
    {
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);
        _engine.Seek(TimeSpan.FromSeconds(1));
        await _controller.PreviousAsync();
        Assert.Equal("A", _controller.CurrentTrack?.Title);

        await _controller.StopAsync();

        // Stopping already logged A's listen, so moving on from the stopped song logs nothing.
        await _controller.NextAsync();
        await _controller.CloseAsync();

        Assert.Equal([PlayKind.Partial, PlayKind.Partial, PlayKind.Partial], _listens.Select(l => l.Kind));
        Assert.Equal([_ids["B"], _ids["A"], _ids["B"]], _listens.Select(l => l.TrackId));
        Assert.All(await _library.AllRowsAsync(), row => Assert.Equal(0, row.SkipCount));
    }

    [Fact]
    public async Task Restarting_StartsAFreshListen()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        _engine.Seek(TimeSpan.FromSeconds(4.5));
        await _controller.PreviousAsync();
        _engine.Seek(TimeSpan.FromSeconds(0.5));
        await _controller.NextAsync();

        Assert.Equal([PlayKind.Complete, PlayKind.Skip], _listens.Select(l => l.Kind));
    }

    [Fact]
    public async Task SkippedMissingFiles_AreNotCounted()
    {
        File.Delete(Path.Combine(_library.MusicDir, "b.wav"));

        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        await _controller.NextAsync();

        Assert.Equal("C", _controller.CurrentTrack?.Title);
        Assert.Equal([_ids["A"]], _listens.Select(l => l.TrackId));
        Assert.Null((await TrackAsync("B")).LastPlayedUtc);
    }

    [Fact]
    public async Task FifthSkip_ReportsTheFlagChange()
    {
        for (var i = 0; i < 5; i++)
        {
            await _controller.PlayTrackAsync(_ids["A"], ByTitle);
            await _controller.NextAsync();
        }

        Assert.Equal([false, false, false, false, true], _listens.Where(l => l.TrackId == _ids["A"]).Select(l => l.FlagChanged));
        Assert.True((await TrackAsync("A")).Flagged);
    }

    [Fact]
    public async Task Shuffle_PlaysThePickedSongFirst_ThenEverySongOnce()
    {
        await _controller.SetShuffleAsync(true);
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);

        Assert.Equal(_ids["B"], _controller.Queue.Ids[0]);
        Assert.Equal(0, _controller.Queue.Index);
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
    }

    [Fact]
    public async Task PlayShuffled_TurnsShuffleOn_AndQueuesEverySongOnce_WithoutASkip()
    {
        await _controller.PlayShuffledAsync(ByTitle);

        Assert.True(_controller.Shuffle);
        Assert.Equal(0, _controller.Queue.Index);
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
        Assert.Equal(_controller.Queue.Current, _controller.CurrentTrack?.Id);
        Assert.Empty(_listens);
    }

    [Fact]
    public async Task TurningShuffleOnAndOff_KeepsThePlayingSong()
    {
        await _controller.PlayTrackAsync(_ids["B"], ByTitle);

        await _controller.SetShuffleAsync(true);
        Assert.True(_controller.Shuffle);
        Assert.Equal((0, _ids["B"]), (_controller.Queue.Index, _controller.Queue.Current));
        Assert.Equal(3, _controller.Queue.Count);

        await _controller.NextAsync();
        var playing = _controller.Queue.Current;

        await _controller.SetShuffleAsync(false);
        Assert.Equal([_ids["A"], _ids["B"], _ids["C"]], _controller.Queue.Ids);
        Assert.Equal(playing, _controller.Queue.Current);
    }

    [Fact]
    public async Task Shuffle_PutsOftenSkippedSongsLast()
    {
        for (var i = 0; i < 20; i++)
        {
            await _library.Stats.RecordAsync(_ids["A"], 0, 5000, PlayKind.Skip);
            await _library.Stats.RecordAsync(_ids["C"], 5000, 5000, PlayKind.Complete);
        }

        _library.Clock.Advance(TimeSpan.FromDays(30));
        await _controller.SetShuffleAsync(true);
        var lastA = 0;
        for (var i = 0; i < 50; i++)
        {
            await _controller.PlayTrackAsync(_ids["B"], ByTitle);
            lastA += _controller.Queue.Ids[^1] == _ids["A"] ? 1 : 0;
        }

        Assert.InRange(lastA, 40, 50);
    }

    [Fact]
    public async Task Shuffle_LeavesLongSongsOutOfSomePasses()
    {
        // A, B and C are 5 s, so they count as 30 s; D at 5 minutes makes it into one pass in ten.
        await AddLongSongAsync();

        int withD = 0;
        for (var i = 0; i < 200; i++)
        {
            await _controller.PlayShuffledAsync(ByTitle);
            Assert.Equal(["A", "B", "C"], _ids.Where(e => e.Key != "D" && _controller.Queue.Ids.Contains(e.Value)).Select(e => e.Key).Order());
            withD += _controller.Queue.Ids.Contains(_ids["D"]) ? 1 : 0;
        }

        Assert.InRange(withD, 5, 40);

        _controller.WeighByLength = false;
        await _controller.PlayShuffledAsync(ByTitle);
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
    }

    [Fact]
    public async Task Shuffle_StartsEachPassFromTheWholeList()
    {
        await AddLongSongAsync();
        await PlayShuffledUntilLeftOut(_ids["D"]);

        // With the weighting off, the next pass has nothing to leave out.
        _controller.WeighByLength = false;
        while (_controller.Queue.HasNext)
        {
            await _controller.NextAsync();
        }

        await _controller.NextAsync();
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
    }

    [Fact]
    public async Task Reshuffle_AfterTurningTheWeightingOff_BringsLongSongsBack()
    {
        await AddLongSongAsync();
        await PlayShuffledUntilLeftOut(_ids["D"]);
        var playing = _controller.Queue.Current;

        _controller.WeighByLength = false;
        await _controller.ReshuffleAsync();

        Assert.Equal((0, playing), (_controller.Queue.Index, _controller.Queue.Current));
        Assert.Equal(_ids.Values.Order(), _controller.Queue.Ids.Order());
    }


    [Fact]
    public async Task SongsTaggedWhilePlaying_JoinThePoolOnlyWhenTheQueueIsDrawnAgain()
    {
        var tag = await _library.Tags.CreateAsync("keep");
        await _library.Tags.AddToTracksAsync(tag.Id, [_ids["A"], _ids["C"]]);
        var context = ByTitle with { Filter = new TrackFilter { AllTags = [tag.Id] } };
        await _controller.PlayTrackAsync(_ids["A"], context);

        await _library.Tags.AddToTracksAsync(tag.Id, [_ids["B"]]);
        Assert.Equal([_ids["A"], _ids["C"]], _controller.Pool);
        Assert.Equal(context, _controller.Source);

        await _controller.SetShuffleAsync(true);
        Assert.Equal(_ids.Values.Order(), _controller.Pool.Order());
    }

    [Fact]
    public async Task Pool_KeepsTheSourceOrder_AndTheSongsShuffleLeftOut()
    {
        await AddLongSongAsync();
        await PlayShuffledUntilLeftOut(_ids["D"]);

        Assert.Equal([_ids["A"], _ids["B"], _ids["C"], _ids["D"]], _controller.Pool);
    }

    [Fact]
    public async Task PlayFromPool_InListOrder_MovesToTheSong()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        await _controller.PlayFromPoolAsync(_ids["C"]);

        Assert.Equal("C", _controller.CurrentTrack?.Title);
        Assert.Equal([_ids["A"], _ids["B"], _ids["C"]], _controller.Queue.Ids);
        Assert.Equal(2, _controller.Queue.Index);
    }

    [Fact]
    public async Task PlayFromPool_InShuffle_PlaysTheSongNext_AndKeepsTheRestOfThePass()
    {
        await AddLongSongAsync();
        await PlayShuffledUntilLeftOut(_ids["D"]);
        var before = _controller.Queue.Ids.ToList();

        await _controller.PlayFromPoolAsync(_ids["D"]);

        Assert.Equal("D", _controller.CurrentTrack?.Title);
        Assert.Equal(1, _controller.Queue.Index);
        Assert.Equal(before, _controller.Queue.Ids.Where(id => id != _ids["D"]));
    }

    [Fact]
    public async Task HiddenSongs_LeaveThePool()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        await _controller.RemoveAsync([_ids["B"]], unload: false);

        Assert.Equal([_ids["A"], _ids["C"]], _controller.Pool);
    }
    /// <summary>Adds a 5-minute song D to the library.</summary>
    private async Task AddLongSongAsync()
    {
        _library.AddSong("d.wav", title: "D", seconds: 300);
        await _library.Scanner.ScanAsync();
        _ids["D"] = (await _library.AllRowsAsync()).Single(row => row.Title == "D").Id;
    }

    private async Task PlayShuffledUntilLeftOut(long id)
    {
        for (var i = 0; i < 100; i++)
        {
            await _controller.PlayShuffledAsync(ByTitle);
            if (!_controller.Queue.Ids.Contains(id))
            {
                return;
            }
        }

        Assert.Fail("The long song was never left out.");
    }

    [Fact]
    public async Task BpmLens_InShuffle_KeepsTheSongsAlreadyPlayed()
    {
        await _library.Tracks.SetManualBpmAsync([_ids["A"]], 90);
        await _library.Tracks.SetManualBpmAsync([_ids["B"]], 120);
        await _library.Tracks.SetManualBpmAsync([_ids["C"]], 140);
        await _controller.SetShuffleAsync(true);
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);
        await _controller.NextAsync();
        var playing = _controller.Queue.Current;

        await _controller.SetBpmLensAsync(new BpmRange(80, 150));
        Assert.Equal((1, _ids["A"], playing), (_controller.Queue.Index, _controller.Queue.Ids[0], _controller.Queue.Current));

        // A falls outside this range, so it drops out of the history.
        await _controller.SetBpmLensAsync(new BpmRange(100, 150));
        Assert.Equal((0, playing), (_controller.Queue.Index, _controller.Queue.Current));
        Assert.Equal(2, _controller.Queue.Count);
    }

    [Fact]
    public async Task HiddenSongs_LeaveTheQueue()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        await _controller.RemoveAsync([_ids["B"]], unload: false);
        await _controller.NextAsync();

        Assert.Equal("C", _controller.CurrentTrack?.Title);
    }

    [Fact]
    public async Task Removing_ThePlayingSong_ClosesItsFile()
    {
        await _controller.PlayTrackAsync(_ids["A"], ByTitle);

        await _controller.RemoveAsync([_ids["A"]], unload: true);

        Assert.Null(_controller.CurrentTrack);
        Assert.Null(_engine.CurrentPath);
        Assert.Equal(PlayerState.Stopped, _engine.State);
        File.Delete(Path.Combine(_library.MusicDir, "a.wav"));
        Assert.Equal(PlayKind.Partial, Assert.Single(_listens).Kind);

        // Next carries on from where the removed song was.
        await _controller.NextAsync();
        Assert.Equal("B", _controller.CurrentTrack?.Title);
    }

    private async Task<Track> TrackAsync(string title) => (await _library.Tracks.GetAsync(_ids[title]))!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(10);
        }
    }
}
