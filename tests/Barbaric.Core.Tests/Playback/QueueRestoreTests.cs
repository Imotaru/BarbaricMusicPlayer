using Barbaric.Core.Audio;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Barbaric.Core.Tests.Audio;
using Barbaric.Core.Tests.Library;
using Dapper;
using NAudio.Wave;

namespace Barbaric.Core.Tests.Playback;

/// <summary>Saving the queue in one session and bringing it back in a fresh engine and controller.</summary>
public sealed class QueueRestoreTests : IAsyncLifetime
{
    private static readonly TrackQuery ByTitle = new(Sort: TrackSort.Title);

    private readonly LibraryFixture _library = new();
    private readonly List<(AudioEngine Engine, PlaybackController Controller)> _sessions = [];
    private readonly List<string> _errors = [];
    private readonly List<ListenRecord> _listens = [];
    private Dictionary<string, long> _ids = [];

    public async Task InitializeAsync()
    {
        foreach (var title in new[] { "A", "B", "C", "D" })
        {
            _library.AddSong($"{title.ToLowerInvariant()}.wav", title: title, seconds: 5);
        }

        await _library.AddMusicFolderAndScanAsync();
        _ids = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var (engine, controller) in _sessions)
        {
            controller.Dispose();
            engine.Dispose();
        }

        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Restore_BringsBackTheQueue_PausedWhereItWasLeft()
    {
        var (engine, first) = NewSession();
        await first.PlayTrackAsync(_ids["B"], ByTitle);
        await first.SetShuffleAsync(true);
        engine.Seek(TimeSpan.FromSeconds(2));
        var snapshot = RoundTrip(first.Snapshot()!);
        var position = RoundTrip(first.Position()!);

        var (engine2, second) = NewSession();
        Assert.True(await second.RestoreAsync(snapshot, position));

        Assert.Equal(first.Queue.Ids, second.Queue.Ids);
        Assert.Equal(first.Queue.Index, second.Queue.Index);
        Assert.True(second.Shuffle);
        Assert.Equal("B", second.CurrentTrack?.Title);
        Assert.Equal(PlayerState.Stopped, engine2.State);
        Assert.Equal(2, engine2.Position.TotalSeconds, precision: 1);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task Restore_RecordsNoListen_UntilPlayIsPressed()
    {
        var (_, first) = NewSession();
        await first.PlayTrackAsync(_ids["A"], ByTitle);
        var snapshot = first.Snapshot()!;
        var position = first.Position()!;
        await first.StopAsync();
        _listens.Clear();

        var (_, second) = NewSession();
        await second.RestoreAsync(snapshot, position);
        await second.NextAsync();
        await second.CloseAsync();

        // Only the song that Next actually played counts; the restored one was never started.
        Assert.Equal([_ids["B"]], _listens.Select(l => l.TrackId));
    }

    [Fact]
    public async Task HiddenCurrentSong_GivesWayToTheNextOne_FromTheStart()
    {
        var (engine, first) = NewSession();
        await first.PlayTrackAsync(_ids["B"], ByTitle);
        engine.Seek(TimeSpan.FromSeconds(3));
        var snapshot = first.Snapshot()!;
        var position = first.Position()!;
        await _library.Stats.SetHiddenAsync([_ids["B"]], hidden: true);

        var (engine2, second) = NewSession();
        Assert.True(await second.RestoreAsync(snapshot, position));

        Assert.Equal(["A", "C", "D"], await TitlesAsync(second.Queue.Ids));
        Assert.Equal("C", second.CurrentTrack?.Title);
        Assert.Equal(TimeSpan.Zero, engine2.Position);
    }

    [Fact]
    public async Task HiddenScopeQueue_KeepsItsHiddenSongs()
    {
        await _library.Stats.SetHiddenAsync([_ids["A"], _ids["C"]], hidden: true);
        var (_, first) = NewSession();
        await first.PlayTrackAsync(_ids["C"], ByTitle with { Scope = TrackScope.Hidden });
        var snapshot = RoundTrip(first.Snapshot()!);

        var (_, second) = NewSession();
        Assert.True(await second.RestoreAsync(snapshot, first.Position()!));

        Assert.Equal(["A", "C"], await TitlesAsync(second.Queue.Ids));
        Assert.Equal("C", second.CurrentTrack?.Title);
    }

    [Fact]
    public async Task DeletedFile_IsSkippedQuietly()
    {
        var (engine, first) = NewSession();
        await first.PlayTrackAsync(_ids["B"], ByTitle);
        var snapshot = first.Snapshot()!;
        var position = first.Position()!;
        engine.Unload();
        File.Delete(Path.Combine(_library.MusicDir, "b.wav"));

        var (_, second) = NewSession();
        Assert.True(await second.RestoreAsync(snapshot, position));

        Assert.Equal("C", second.CurrentTrack?.Title);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task NothingPlayable_ReturnsFalse_AndLeavesTheQueueEmpty()
    {
        var (_, first) = NewSession();
        await first.PlayTrackAsync(_ids["A"]);
        var snapshot = first.Snapshot()!;
        var position = first.Position()!;
        await _library.Stats.SetHiddenAsync([_ids["A"]], hidden: true);

        var (_, second) = NewSession();
        Assert.False(await second.RestoreAsync(snapshot, position));
        Assert.Null(second.CurrentTrack);
        Assert.Equal(0, second.Queue.Count);
    }

    [Fact]
    public async Task RestoredQueue_StillFollowsTheBpmLens()
    {
        var (_, first) = NewSession();
        await first.PlayTrackAsync(_ids["A"], ByTitle with { Bpm = new BpmRange(100, 200) });
        var snapshot = RoundTrip(first.Snapshot()!);
        var position = first.Position()!;

        var (_, second) = NewSession();
        await second.RestoreAsync(snapshot, position);

        // The saved lens is dropped (the UI starts with an open one), so the whole list comes back.
        Assert.Equal(["A", "B", "C", "D"], await TitlesAsync(second.Queue.Ids));

        // No song has a BPM, so a closed lens leaves only the playing one; opening it brings the list back.
        await second.SetBpmLensAsync(new BpmRange(100, 200));
        Assert.Equal(["A"], await TitlesAsync(second.Queue.Ids));
        await second.SetBpmLensAsync(null);
        Assert.Equal(["A", "B", "C", "D"], await TitlesAsync(second.Queue.Ids));
    }

    [Fact]
    public void SnapshotSource_KeepsPlaylistScopeAndLens()
    {
        var query = new TrackQuery(
            Sort: TrackSort.Position, PlaylistId: 7, Bpm: new BpmRange(90, 120, true), Scope: TrackScope.Hidden);
        var restored = RoundTrip(new QueueSnapshot([1, 2], query, Shuffle: false));

        Assert.Equal(query, restored.Source);
        Assert.Equal([1L, 2L], restored.Ids);
    }

    [Fact]
    public async Task UnknownLength_IsFilledInFromTheDecoder()
    {
        using (var connection = _library.Database.Open())
        {
            connection.Execute("UPDATE tracks SET duration_ms = 0");
        }

        var (_, controller) = NewSession();
        var updated = new List<long>();
        controller.TrackUpdated += (_, track) => updated.Add(track.Id);
        await controller.PlayTrackAsync(_ids["A"], ByTitle);

        Assert.Equal([_ids["A"]], updated);
        Assert.InRange((await _library.Tracks.GetAsync(_ids["A"]))!.DurationMs, 4980, 5020);
        Assert.Equal(0, (await _library.Tracks.GetAsync(_ids["B"]))!.DurationMs);
    }

    private (AudioEngine Engine, PlaybackController Controller) NewSession()
    {
        var engine = new AudioEngine(() => Task.FromResult<IWavePlayer>(new FakeWavePlayer()));
        var controller = new PlaybackController(
            engine, _library.Tracks, _library.Stats, clock: _library.Clock, random: new Random(1234));
        controller.ListenRecorded += (_, record) => _listens.Add(record);
        controller.Error += (_, message) => _errors.Add(message);
        _sessions.Add((engine, controller));
        return (engine, controller);
    }

    private static T RoundTrip<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(SettingsRepository.Serialize(value), CoreJson.Options)!;

    private async Task<List<string>> TitlesAsync(IEnumerable<long> ids)
    {
        var titles = new List<string>();
        foreach (var id in ids)
        {
            titles.Add((await _library.Tracks.GetAsync(id))!.Title);
        }

        return titles;
    }
}
