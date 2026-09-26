using Barbaric.Core.Analysis;
using Barbaric.Core.Library;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class BpmStorageTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private Dictionary<string, long> _ids = [];

    public async Task InitializeAsync()
    {
        _library.AddSong("tagged.wav", title: "Tagged", bpm: 100);
        _library.AddSong("plain.wav", title: "Plain");
        _library.AddSong("other.wav", title: "Other");
        _library.AddSong("gone.wav", title: "Gone");
        await _library.AddMusicFolderAndScanAsync();
        _ids = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Pending_IsTracksWithNoBpmThatWereNeverAnalyzed()
    {
        File.Delete(Path.Combine(_library.MusicDir, "gone.wav"));
        await _library.Scanner.ScanAsync();
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Other"], null);

        var pending = await _library.Tracks.GetBpmPendingAsync(10);

        Assert.Equal([_ids["Plain"]], pending.Select(p => p.Id));
        Assert.EndsWith("plain.wav", pending[0].Path);
        Assert.Equal(1, await _library.Tracks.CountBpmPendingAsync());
    }

    [Fact]
    public async Task AnalyzedBpm_ReplacesATagValue()
    {
        Assert.True(await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Tagged"], new BpmResult(128.4, 0.8)));

        var track = await _library.Tracks.GetAsync(_ids["Tagged"]);
        Assert.Equal(128.4, track!.Bpm);
        Assert.Equal(0.8, track.BpmConfidence);
        Assert.Equal("analyzed", track.BpmSource);
    }

    [Fact]
    public async Task AnalyzedBpm_NeverReplacesAManualValue()
    {
        await _library.Tracks.SetManualBpmAsync([_ids["Plain"]], 90);

        Assert.False(await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Plain"], new BpmResult(180, 0.9)));

        var track = await _library.Tracks.GetAsync(_ids["Plain"]);
        Assert.Equal(90, track!.Bpm);
        Assert.Equal("manual", track.BpmSource);
    }

    [Fact]
    public async Task ManualBpm_IsClamped_AndResetGoesBackToTheTagOrToTheAnalyzer()
    {
        await _library.Tracks.SetManualBpmAsync([_ids["Plain"], _ids["Tagged"]], 1000);
        Assert.All(await _library.Tracks.GetBpmInfoAsync([_ids["Plain"], _ids["Tagged"]]), info =>
        {
            Assert.Equal(TrackRepository.MaxManualBpm, info.Bpm);
            Assert.Equal("manual", info.BpmSource);
        });

        await _library.Tracks.ResetBpmAsync([_ids["Plain"], _ids["Tagged"]]);

        var info = (await _library.Tracks.GetBpmInfoAsync([_ids["Plain"], _ids["Tagged"]])).ToDictionary(i => i.Id);
        Assert.Equal((100.0, "tag"), (info[_ids["Tagged"]].Bpm, info[_ids["Tagged"]].BpmSource));
        Assert.Equal((null, null), (info[_ids["Plain"]].Bpm, info[_ids["Plain"]].BpmSource));
        Assert.Contains(_ids["Plain"], (await _library.Tracks.GetBpmPendingAsync(10)).Select(p => p.Id));
    }

    [Fact]
    public async Task Scale_DoublesOrHalvesKnownBpms_WithinBounds()
    {
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Other"], new BpmResult(250, 0.7));

        var changed = await _library.Tracks.ScaleBpmAsync([_ids["Tagged"], _ids["Other"], _ids["Plain"]], 2);

        // Tagged goes 100 → 200. Other would reach 500 and Plain has no BPM, so both stay as they were.
        Assert.Equal(1, changed);
        var info = (await _library.Tracks.GetBpmInfoAsync(_ids.Values)).ToDictionary(i => i.Id);
        Assert.Equal(200, info[_ids["Tagged"]].Bpm);
        Assert.Equal("manual", info[_ids["Tagged"]].BpmSource);
        Assert.Equal(250, info[_ids["Other"]].Bpm);
        Assert.Equal("analyzed", info[_ids["Other"]].BpmSource);
        Assert.Null(info[_ids["Plain"]].Bpm);

        await _library.Tracks.ScaleBpmAsync([_ids["Tagged"]], 0.5);
        Assert.Equal(100, (await _library.Tracks.GetAsync(_ids["Tagged"]))!.Bpm);
    }

    [Fact]
    public async Task Rescan_KeepsAnalyzedAndManualBpm_ButATagFillsInANoBeatResult()
    {
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Tagged"], new BpmResult(131, 0.6));
        await _library.Tracks.SetManualBpmAsync([_ids["Other"]], 77);
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Plain"], null);

        foreach (var name in new[] { "Tagged", "Other", "Plain" })
        {
            var path = Path.Combine(_library.MusicDir, $"{name.ToLowerInvariant()}.wav");
            LibraryFixture.SetTags(path, title: name, bpm: 111);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        }

        await _library.Scanner.ScanAsync();

        var info = (await _library.Tracks.GetBpmInfoAsync(_ids.Values)).ToDictionary(i => i.Id);
        Assert.Equal((131.0, "analyzed"), (info[_ids["Tagged"]].Bpm, info[_ids["Tagged"]].BpmSource));
        Assert.Equal((77.0, "manual"), (info[_ids["Other"]].Bpm, info[_ids["Other"]].BpmSource));
        Assert.Equal((111.0, "tag"), (info[_ids["Plain"]].Bpm, info[_ids["Plain"]].BpmSource));
    }

    [Fact]
    public async Task RescanWithoutATag_KeepsANoBeatResult()
    {
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Plain"], null);
        File.SetLastWriteTimeUtc(Path.Combine(_library.MusicDir, "plain.wav"), DateTime.UtcNow.AddMinutes(1));

        await _library.Scanner.ScanAsync();

        using var connection = _library.Database.Open();
        Assert.Equal("analyzed", connection.ExecuteScalar<string>("SELECT bpm_source FROM tracks WHERE id = @id", new { id = _ids["Plain"] }));
    }

    [Fact]
    public async Task Rows_CarryTheBpmSourceAndConfidence()
    {
        await _library.Tracks.SaveAnalyzedBpmAsync(_ids["Plain"], new BpmResult(95.5, 0.42));

        var rows = (await _library.AllRowsAsync()).ToDictionary(r => r.Title);

        Assert.Equal((95.5, "analyzed", 0.42), (rows["Plain"].Bpm, rows["Plain"].BpmSource, rows["Plain"].BpmConfidence));
        Assert.Equal("tag", rows["Tagged"].BpmSource);
    }
}
