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

    public PlaybackControllerTests()
    {
        _engine = new AudioEngine(() =>
        {
            var output = new FakeWavePlayer();
            _outputs.Add(output);
            return Task.FromResult<IWavePlayer>(output);
        });
        _controller = new PlaybackController(_engine, _library.Tracks);
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
