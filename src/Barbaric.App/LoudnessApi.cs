using System.Windows.Threading;
using Barbaric.App.Bridge;
using Barbaric.Core.Analysis;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;

namespace Barbaric.App;

/// <summary>
/// Measures song volumes in the background so every song plays equally loud, and exposes that to the
/// UI as <c>loudness.*</c> bridge methods. The loaded and upcoming songs are measured first.
/// </summary>
public sealed class LoudnessApi : IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly LoudnessBackgroundAnalyzer _analyzer;
    private readonly TrackRepository _tracks;
    private readonly PlaybackController _controller;
    private readonly PlayerApi _player;
    private readonly WebBridge _bridge;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private bool _flushScheduled;

    public LoudnessApi(
        LoudnessBackgroundAnalyzer analyzer,
        TrackRepository tracks,
        PlaybackController controller,
        PlayerApi player,
        WebBridge bridge,
        Dispatcher dispatcher)
    {
        _analyzer = analyzer;
        _tracks = tracks;
        _controller = controller;
        _player = player;
        _bridge = bridge;
        _dispatcher = dispatcher;

        bridge.QueryAsync("loudness.getStatus", async _ => await StatusAsync());
        bridge.Command("loudness.start", _ => _analyzer.Start());
        bridge.Command("loudness.cancel", _ => _analyzer.Cancel());
        bridge.Query("loudness.analyze", p =>
        {
            var ids = p.GetIds();
            _analyzer.AnalyzeNow(ids);
            return new { queued = ids.Count };
        });

        _analyzer.StatusChanged += OnStatusChanged;
        _analyzer.Analyzed += OnAnalyzed;
        _analyzer.Failed += OnFailed;
        _controller.TrackLoaded += OnTrackLoaded;
    }

    /// <summary>Works through every song not measured yet. Called after each library scan.</summary>
    public void StartBackground() => _analyzer.Start();

    public void Dispose()
    {
        _controller.TrackLoaded -= OnTrackLoaded;
        _analyzer.StatusChanged -= OnStatusChanged;
        _analyzer.Analyzed -= OnAnalyzed;
        _analyzer.Failed -= OnFailed;
        _analyzer.Dispose();
    }

    private async Task<BpmApi.StatusSnapshot> StatusAsync()
    {
        var status = _analyzer.Status;

        // Songs still waiting after a cancel, so the UI can offer to resume.
        var pending = status.Running ? 0 : await _tracks.CountLoudnessPendingAsync();
        return new BpmApi.StatusSnapshot(status.Running, status.Done, status.Total, pending);
    }

    /// <summary>
    /// Measures the loaded song (it takes the result if it hasn't started yet) and the one after it,
    /// so the next song is even with the rest by the time it plays.
    /// </summary>
    private async void OnTrackLoaded(object? sender, Track track)
    {
        try
        {
            var ids = new List<long>();
            if (!track.LoudnessAnalyzed)
            {
                ids.Add(track.Id);
            }

            if (_controller.UpcomingId is { } next && await _tracks.GetAsync(next) is { LoudnessAnalyzed: false, Missing: false })
            {
                ids.Add(next);
            }

            if (ids.Count > 0)
            {
                _analyzer.AnalyzeNow(ids);
            }
        }
        catch (Exception)
        {
            // The database went away (shutting down); the background pass measures these later anyway.
        }
    }

    private void OnAnalyzed(object? sender, LoudnessInfo info) => _dispatcher.BeginInvoke(() =>
    {
        if (_controller.UpdateLoudness(info.Id, info.LoudnessLufs, info.PeakDb, info.SilenceEdges))
        {
            _player.RefreshState();
        }
    });

    private void OnFailed(object? sender, Exception ex) =>
        _bridge.Emit("library.error", new { message = $"Volume measuring stopped: {ex.Message}" });

    // The worker reports from its own thread after every song; batch those into a few events a second.
    private void OnStatusChanged(object? sender, AnalysisStatus status)
    {
        lock (_sync)
        {
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        _ = Task.Delay(FlushDelay).ContinueWith(_ => FlushAsync(), TaskScheduler.Default).Unwrap();
    }

    private async Task FlushAsync()
    {
        lock (_sync)
        {
            _flushScheduled = false;
        }

        try
        {
            _bridge.Emit("loudness.status", await StatusAsync());
        }
        catch (Exception)
        {
            // The database went away (shutting down); nothing left to report to.
        }
    }
}
