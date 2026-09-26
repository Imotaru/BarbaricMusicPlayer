using Barbaric.App.Bridge;
using Barbaric.Core.Analysis;
using Barbaric.Core.Library;

namespace Barbaric.App;

/// <summary>
/// Exposes BPM analysis and editing to the UI as <c>bpm.*</c> bridge methods. Every change to a
/// track's BPM, measured or typed, reaches the UI as a <c>bpm.updated</c> event.
/// </summary>
public sealed class BpmApi : IDisposable
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(250);

    private readonly BpmBackgroundAnalyzer _analyzer;
    private readonly TrackRepository _tracks;
    private readonly WebBridge _bridge;
    private readonly object _sync = new();
    private readonly List<BpmInfo> _pendingUpdates = [];
    private bool _statusDirty;
    private bool _flushScheduled;

    public BpmApi(BpmBackgroundAnalyzer analyzer, TrackRepository tracks, WebBridge bridge)
    {
        _analyzer = analyzer;
        _tracks = tracks;
        _bridge = bridge;

        bridge.QueryAsync("bpm.getStatus", async _ => await StatusAsync());
        bridge.QueryAsync("bpm.get", async p => await _tracks.GetBpmInfoAsync(p.GetIds()));
        bridge.Command("bpm.start", _ => _analyzer.Start());
        bridge.Command("bpm.cancel", _ => _analyzer.Cancel());
        bridge.QueryAsync("bpm.analyze", async p =>
        {
            var ids = p.GetIds();
            var manual = (await _tracks.GetBpmInfoAsync(ids)).Count(t => t.BpmSource == "manual");
            _analyzer.AnalyzeNow(ids);
            return new { queued = ids.Count - manual, skippedManual = manual };
        });
        bridge.CommandAsync("bpm.set", async p =>
        {
            var ids = p.GetIds();
            await _tracks.SetManualBpmAsync(ids, p.GetProperty("bpm").GetDouble());
            await EmitUpdatedAsync(ids);
        });
        bridge.QueryAsync("bpm.scale", async p =>
        {
            var ids = p.GetIds();
            var changed = await _tracks.ScaleBpmAsync(ids, p.GetProperty("factor").GetDouble());
            await EmitUpdatedAsync(ids);
            return new { changed };
        });
        bridge.CommandAsync("bpm.reset", async p =>
        {
            var ids = p.GetIds();
            await Task.Run(() => _tracks.ResetBpmAsync(ids));
            await EmitUpdatedAsync(ids);

            // Tracks without a tag are pending again.
            _analyzer.Start();
        });

        _analyzer.StatusChanged += OnStatusChanged;
        _analyzer.Analyzed += OnAnalyzed;
        _analyzer.Failed += OnFailed;
    }

    /// <summary>Works through every track without a BPM. Called after each library scan.</summary>
    public void StartBackground() => _analyzer.Start();

    public void Dispose()
    {
        _analyzer.StatusChanged -= OnStatusChanged;
        _analyzer.Analyzed -= OnAnalyzed;
        _analyzer.Failed -= OnFailed;
        _analyzer.Dispose();
    }

    private async Task<StatusSnapshot> StatusAsync()
    {
        var status = _analyzer.Status;

        // Tracks still waiting after a cancel, so the UI can offer to resume.
        var pending = status.Running ? 0 : await _tracks.CountBpmPendingAsync();
        return new StatusSnapshot(status.Running, status.Done, status.Total, pending);
    }

    private async Task EmitUpdatedAsync(IEnumerable<long> ids)
    {
        var tracks = await _tracks.GetBpmInfoAsync(ids);
        _bridge.Emit("bpm.updated", new { tracks });
    }

    // The worker reports from its own thread after every track; batch those into a few events a second.
    private void OnStatusChanged(object? sender, BpmStatus status)
    {
        lock (_sync)
        {
            _statusDirty = true;
            ScheduleFlush();
        }
    }

    private void OnAnalyzed(object? sender, BpmInfo info)
    {
        lock (_sync)
        {
            _pendingUpdates.Add(info);
            ScheduleFlush();
        }
    }

    private void OnFailed(object? sender, Exception ex) =>
        _bridge.Emit("library.error", new { message = $"BPM analysis stopped: {ex.Message}" });

    private void ScheduleFlush()
    {
        if (_flushScheduled)
        {
            return;
        }

        _flushScheduled = true;
        _ = Task.Delay(FlushDelay).ContinueWith(_ => FlushAsync(), TaskScheduler.Default).Unwrap();
    }

    private async Task FlushAsync()
    {
        List<BpmInfo> updates;
        bool statusDirty;
        lock (_sync)
        {
            updates = [.. _pendingUpdates];
            _pendingUpdates.Clear();
            statusDirty = _statusDirty;
            _statusDirty = false;
            _flushScheduled = false;
        }

        if (updates.Count > 0)
        {
            _bridge.Emit("bpm.updated", new { tracks = updates });
        }

        if (statusDirty)
        {
            try
            {
                _bridge.Emit("bpm.status", await StatusAsync());
            }
            catch (Exception)
            {
                // The database went away (shutting down); nothing left to report to.
            }
        }
    }

    public sealed record StatusSnapshot(bool Running, int Done, int Total, int Pending);
}
