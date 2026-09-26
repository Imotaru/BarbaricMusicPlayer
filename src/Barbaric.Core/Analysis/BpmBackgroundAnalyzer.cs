using Barbaric.Core.Library;

namespace Barbaric.Core.Analysis;

/// <summary>
/// Measures the tempo of tracks without a BPM in the background, plus any tracks the user asks to
/// have analyzed again. Results never replace a BPM that was set by hand.
/// </summary>
public sealed class BpmBackgroundAnalyzer(TrackRepository tracks, Func<string, CancellationToken, BpmResult?>? analyze = null)
    : BackgroundAnalyzer<BpmResult, BpmInfo>
{
    private readonly Func<string, CancellationToken, BpmResult?> _analyze = analyze ?? BpmAnalyzer.AnalyzeFile;

    protected override IReadOnlyList<(long Id, string Path)> GetPending(int limit) =>
        tracks.GetBpmPendingAsync(limit).GetAwaiter().GetResult();

    protected override int CountPending() => tracks.CountBpmPendingAsync().GetAwaiter().GetResult();

    /// <summary>A tag's BPM gets measured again; a manual one is left alone.</summary>
    protected override string? PathForRequest(long id)
    {
        var track = tracks.GetAsync(id).GetAwaiter().GetResult();
        return track is { Missing: false, BpmSource: not "manual" } ? track.Path : null;
    }

    protected override BpmResult? Analyze(string path, CancellationToken token) => _analyze(path, token);

    protected override BpmInfo? Save(long id, BpmResult? result) =>
        tracks.SaveAnalyzedBpmAsync(id, result).GetAwaiter().GetResult()
            ? new BpmInfo(id, result?.Bpm, "analyzed", result?.Confidence)
            : null;
}
