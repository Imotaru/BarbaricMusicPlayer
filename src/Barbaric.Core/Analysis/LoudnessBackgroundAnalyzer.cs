using Barbaric.Core.Audio;
using Barbaric.Core.Library;

namespace Barbaric.Core.Analysis;

/// <summary>A track's measured volume, as reported after it changes.</summary>
public sealed record LoudnessInfo(long Id, double? LoudnessLufs, double? PeakDb, double AutoGainDb);

/// <summary>
/// Measures the volume of tracks that haven't been measured yet in the background, most played first,
/// plus any tracks the user asks to have measured again.
/// </summary>
public sealed class LoudnessBackgroundAnalyzer(TrackRepository tracks, Func<string, CancellationToken, LoudnessResult?>? analyze = null)
    : BackgroundAnalyzer<LoudnessResult, LoudnessInfo>
{
    private readonly Func<string, CancellationToken, LoudnessResult?> _analyze = analyze ?? LoudnessAnalyzer.AnalyzeFile;

    protected override IReadOnlyList<(long Id, string Path)> GetPending(int limit) =>
        tracks.GetLoudnessPendingAsync(limit).GetAwaiter().GetResult();

    protected override int CountPending() => tracks.CountLoudnessPendingAsync().GetAwaiter().GetResult();

    protected override string? PathForRequest(long id) =>
        tracks.GetAsync(id).GetAwaiter().GetResult() is { Missing: false } track ? track.Path : null;

    protected override LoudnessResult? Analyze(string path, CancellationToken token) => _analyze(path, token);

    protected override LoudnessInfo? Save(long id, LoudnessResult? result) =>
        tracks.SaveLoudnessAsync(id, result).GetAwaiter().GetResult()
            ? new LoudnessInfo(id, result?.LoudPartLufs, result?.PeakDb, Gain.AutoGainDb(result?.LoudPartLufs, result?.PeakDb))
            : null;
}
