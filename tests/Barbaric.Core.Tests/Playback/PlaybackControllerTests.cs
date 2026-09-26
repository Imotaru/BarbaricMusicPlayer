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

        // End of the list: playback stops on the last song.
        CurrentOutput.DrainToEnd();
        await WaitUntil(() => _engine.State == PlayerState.Stopped);
        Assert.Equal("C", _controller.CurrentTrack?.Title);
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
