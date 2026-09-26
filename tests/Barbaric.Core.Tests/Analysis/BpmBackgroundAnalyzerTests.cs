using System.Collections.Concurrent;
using Barbaric.Core.Analysis;
using Barbaric.Core.Library;
using Barbaric.Core.Tests.Library;

namespace Barbaric.Core.Tests.Analysis;

public sealed class BpmBackgroundAnalyzerTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private readonly ConcurrentQueue<string> _analyzedPaths = new();
    private readonly ConcurrentQueue<AnalysisStatus> _statuses = new();
    private readonly ConcurrentQueue<BpmInfo> _results = new();
    private Dictionary<string, long> _ids = [];

    // s.wav → 101, ss.wav → 102 and so on, so each result is recognisable.
    private Func<string, BpmResult?> _fake = path => new BpmResult(100 + Path.GetFileNameWithoutExtension(path).Length, 0.9);

    public async Task InitializeAsync()
    {
        for (var i = 1; i <= 5; i++)
        {
            _library.AddSong($"{new string('s', i)}.wav", title: $"Song {i}");
        }

        _library.AddSong("tagged.wav", title: "Tagged", bpm: 123);
        await _library.AddMusicFolderAndScanAsync();
        _ids = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Start_AnalyzesEveryTrackWithoutABpm_AndReportsProgress()
    {
        using var analyzer = Create();

        analyzer.Start();
        await analyzer.Idle;

        Assert.Equal(5, _analyzedPaths.Count);
        Assert.DoesNotContain(_analyzedPaths, p => p.EndsWith("tagged.wav"));
        Assert.Equal(0, await _library.Tracks.CountBpmPendingAsync());
        Assert.Equal(105, (await _library.Tracks.GetAsync(_ids["Song 5"]))!.Bpm);
        Assert.Equal(5, _results.Count);

        Assert.Equal(new AnalysisStatus(false, 5, 5), _statuses.Last());
        Assert.Contains(_statuses, s => s.Running && s.Total == 5 && s.Done < 5);
    }

    [Fact]
    public async Task Cancel_StopsPartway_AndStartResumesWithTheRest()
    {
        using var gate = new SemaphoreSlim(0);
        _fake = _ =>
        {
            gate.Wait();
            return new BpmResult(120, 0.9);
        };
        using var analyzer = Create();

        analyzer.Start();
        gate.Release(2);
        await WaitUntil(() => _analyzedPaths.Count == 3);
        analyzer.Cancel();
        gate.Release();
        await analyzer.Idle;

        // The track in progress when cancelling still finishes: two left of five.
        Assert.False(analyzer.Status.Running);
        Assert.Equal(2, await _library.Tracks.CountBpmPendingAsync());

        gate.Release(10);
        analyzer.Start();
        await analyzer.Idle;

        Assert.Equal(0, await _library.Tracks.CountBpmPendingAsync());
    }

    [Fact]
    public async Task AnalyzeNow_ReplacesTagBpm_ButSkipsManualBpm()
    {
        await _library.Tracks.SetManualBpmAsync([_ids["Song 1"]], 88);
        using var analyzer = Create();

        analyzer.AnalyzeNow([_ids["Tagged"], _ids["Song 1"]]);
        await analyzer.Idle;

        Assert.Single(_analyzedPaths);
        var tagged = (await _library.Tracks.GetAsync(_ids["Tagged"]))!;
        Assert.Equal(("analyzed", 106.0), (tagged.BpmSource, tagged.Bpm));
        Assert.Equal(88, (await _library.Tracks.GetAsync(_ids["Song 1"]))!.Bpm);

        // An explicit request doesn't start on the backlog.
        Assert.Equal(4, await _library.Tracks.CountBpmPendingAsync());
    }

    [Fact]
    public async Task LockedFiles_StayPending_AndUndecodableOnesAreNotRetried()
    {
        _fake = path => Path.GetFileName(path) switch
        {
            "s.wav" => throw new IOException("locked"),
            "ss.wav" => throw new InvalidDataException("not audio"),
            _ => null,
        };
        using var analyzer = Create();

        analyzer.Start();
        await analyzer.Idle;

        var pending = await _library.Tracks.GetBpmPendingAsync(10);
        Assert.Equal([_ids["Song 1"]], pending.Select(p => p.Id));
        Assert.Equal("analyzed", (await _library.Tracks.GetAsync(_ids["Song 2"]))!.BpmSource);
        Assert.Equal(new AnalysisStatus(false, 5, 5), analyzer.Status);
    }

    private BpmBackgroundAnalyzer Create()
    {
        var analyzer = new BpmBackgroundAnalyzer(_library.Tracks, (path, _) =>
        {
            _analyzedPaths.Enqueue(path);
            return _fake(path);
        });
        analyzer.StatusChanged += (_, status) => _statuses.Enqueue(status);
        analyzer.Analyzed += (_, info) => _results.Enqueue(info);
        return analyzer;
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
