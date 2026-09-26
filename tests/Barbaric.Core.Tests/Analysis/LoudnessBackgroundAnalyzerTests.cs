using System.Collections.Concurrent;
using Barbaric.Core.Analysis;
using Barbaric.Core.Tests.Library;

namespace Barbaric.Core.Tests.Analysis;

public sealed class LoudnessBackgroundAnalyzerTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private readonly ConcurrentQueue<string> _analyzedPaths = new();
    private readonly ConcurrentQueue<LoudnessInfo> _results = new();
    private Dictionary<string, long> _ids = [];

    // a.wav → -11, aa.wav → -12 and so on, so each result is recognisable.
    private Func<string, LoudnessResult?> _fake = path => new LoudnessResult(-10 - Path.GetFileNameWithoutExtension(path).Length, -1);

    public async Task InitializeAsync()
    {
        for (var i = 1; i <= 3; i++)
        {
            _library.AddSong($"{new string('a', i)}.wav", title: $"Song {i}");
        }

        await _library.AddMusicFolderAndScanAsync();
        _ids = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Start_MeasuresEverySong()
    {
        using var analyzer = Create();

        analyzer.Start();
        await analyzer.Idle;

        Assert.Equal(3, _analyzedPaths.Count);
        Assert.Equal(0, await _library.Tracks.CountLoudnessPendingAsync());
        var track = (await _library.Tracks.GetAsync(_ids["Song 3"]))!;
        Assert.True(track.LoudnessAnalyzed);
        Assert.Equal((-13.0, -1.0), (track.LoudnessLufs!.Value, track.PeakDb!.Value));
        Assert.Equal(-1, track.AutoGainDb);
        Assert.Contains(_results, r => r.Id == _ids["Song 3"] && r.AutoGainDb == -1);
    }

    [Fact]
    public async Task SilentOrUndecodable_IsRememberedAndPlayedAsIs()
    {
        _fake = path => path.EndsWith("aa.wav") ? throw new InvalidDataException() : null;
        using var analyzer = Create();

        analyzer.Start();
        await analyzer.Idle;

        Assert.Equal(0, await _library.Tracks.CountLoudnessPendingAsync());
        var track = (await _library.Tracks.GetAsync(_ids["Song 2"]))!;
        Assert.True(track.LoudnessAnalyzed);
        Assert.Null(track.LoudnessLufs);
        Assert.Equal(0, track.AutoGainDb);
    }

    [Fact]
    public async Task AnalyzeNow_MeasuresAgain()
    {
        using var analyzer = Create();
        analyzer.Start();
        await analyzer.Idle;
        _analyzedPaths.Clear();
        _fake = _ => new LoudnessResult(-6, -0.1);

        analyzer.AnalyzeNow([_ids["Song 1"]]);
        await analyzer.Idle;

        Assert.Single(_analyzedPaths);
        Assert.Equal(-8, (await _library.Tracks.GetAsync(_ids["Song 1"]))!.AutoGainDb);
    }

    [Fact]
    public async Task ChangedFile_IsMeasuredAgainAfterARescan()
    {
        using var analyzer = Create();
        analyzer.Start();
        await analyzer.Idle;

        var path = _library.AddSong("a.wav", title: "Song 1", seconds: 1);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await _library.Scanner.ScanAsync();

        Assert.Equal(1, await _library.Tracks.CountLoudnessPendingAsync());
        Assert.False((await _library.Tracks.GetAsync(_ids["Song 1"]))!.LoudnessAnalyzed);
    }

    [Fact]
    public async Task UnchangedFile_KeepsItsMeasurementThroughARescan()
    {
        using var analyzer = Create();
        analyzer.Start();
        await analyzer.Idle;

        // Touched but not changed (e.g. copied back from a backup drive): the measurement still fits.
        var path = Path.Combine(_library.MusicDir, "a.wav");
        var before = File.ReadAllBytes(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await _library.Scanner.ScanAsync();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0, await _library.Tracks.CountLoudnessPendingAsync());
    }

    private LoudnessBackgroundAnalyzer Create()
    {
        var analyzer = new LoudnessBackgroundAnalyzer(_library.Tracks, (path, _) =>
        {
            _analyzedPaths.Enqueue(path);
            return _fake(path);
        });
        analyzer.Analyzed += (_, info) => _results.Enqueue(info);
        return analyzer;
    }
}
