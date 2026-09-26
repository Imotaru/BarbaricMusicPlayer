using Barbaric.Core.Library;

namespace Barbaric.Core.Analysis;

public sealed record BpmStatus(bool Running, int Done, int Total);

/// <summary>
/// Works through tracks without a BPM on one low-priority thread, plus any tracks the user asks to
/// have analyzed again (those go first). Results never replace a BPM that was set by hand.
/// </summary>
public sealed class BpmBackgroundAnalyzer : IDisposable
{
    private const int BatchSize = 100;

    private readonly TrackRepository _tracks;
    private readonly Func<string, CancellationToken, BpmResult?> _analyze;
    private readonly object _sync = new();
    private readonly Queue<long> _requested = new();
    private readonly Queue<(long Id, string Path)> _batch = new();

    // Tracks that couldn't be opened (locked, or gone before the next scan) stay pending in the
    // database; remembering them keeps this session from retrying them in a loop.
    private readonly HashSet<long> _skipped = [];
    private bool _background;
    private CancellationTokenSource? _cancel;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private int _done;
    private int _total;

    public BpmBackgroundAnalyzer(TrackRepository tracks, Func<string, CancellationToken, BpmResult?>? analyze = null)
    {
        _tracks = tracks;
        _analyze = analyze ?? BpmAnalyzer.AnalyzeFile;
    }

    /// <summary>Raised on the worker thread whenever progress changes or the worker starts or stops.</summary>
    public event EventHandler<BpmStatus>? StatusChanged;

    /// <summary>Raised on the worker thread for each track whose stored BPM changed.</summary>
    public event EventHandler<BpmInfo>? Analyzed;

    /// <summary>Raised when the worker stops on an unexpected error (e.g. the database is unavailable).</summary>
    public event EventHandler<Exception>? Failed;

    public BpmStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new BpmStatus(_running, _done, _total);
            }
        }
    }

    /// <summary>Completes when the worker has nothing left to do (or was cancelled).</summary>
    internal Task Idle
    {
        get
        {
            lock (_sync)
            {
                return _worker;
            }
        }
    }

    /// <summary>Starts (or keeps) working through every track that has no BPM yet.</summary>
    public void Start()
    {
        lock (_sync)
        {
            _background = true;
            EnsureWorker();
        }
    }

    /// <summary>Analyzes these tracks next, replacing a BPM that came from a tag. Manual BPMs are skipped.</summary>
    public void AnalyzeNow(IEnumerable<long> ids)
    {
        lock (_sync)
        {
            foreach (var id in ids.Distinct().Where(id => !_requested.Contains(id)))
            {
                _requested.Enqueue(id);
                _total++;
            }

            EnsureWorker();
        }

        RaiseStatus();
    }

    /// <summary>Stops after the current track and forgets queued requests. <see cref="Start"/> resumes.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _background = false;
            _requested.Clear();
            _batch.Clear();
            _cancel?.Cancel();
        }
    }

    public void Dispose()
    {
        Cancel();
        try
        {
            Idle.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelled before it started.
        }
    }

    private void EnsureWorker()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _done = 0;
        _total = _requested.Count;
        _skipped.Clear();
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _worker = Task.Factory.StartNew(() => Run(cancel), cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationTokenSource cancel)
    {
        var token = cancel.Token;
        // LongRunning gives this task its own thread, so lowering its priority affects nothing else.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            RaiseStatus();
            while (!token.IsCancellationRequested && TakeNext() is { } next)
            {
                Process(next.Id, next.Path, next.Forced, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
        finally
        {
            lock (_sync)
            {
                // A newer worker may already have started once TakeNext gave up; leave its state alone.
                if (_cancel == cancel)
                {
                    _running = false;
                    _cancel = null;
                }

                cancel.Dispose();
            }

            RaiseStatus();
        }
    }

    /// <summary>The next track to analyze: explicit requests first, then the pending backlog.</summary>
    private (long Id, string? Path, bool Forced)? TakeNext()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_requested.TryDequeue(out var id))
                {
                    return (id, null, true);
                }

                if (_batch.TryDequeue(out var pending))
                {
                    return (pending.Id, pending.Path, false);
                }

                if (!_background)
                {
                    // Deciding to stop under the lock means a Start/AnalyzeNow either lands before
                    // this (and is picked up) or after it (and starts a new worker).
                    _running = false;
                    return null;
                }
            }

            if (!RefillBatch())
            {
                lock (_sync)
                {
                    _background = false;
                }
            }
        }
    }

    private bool RefillBatch()
    {
        HashSet<long> skipped;
        lock (_sync)
        {
            skipped = [.. _skipped];
        }

        var rows = _tracks.GetBpmPendingAsync(BatchSize + skipped.Count).GetAwaiter().GetResult()
            .Where(r => !skipped.Contains(r.Id))
            .Take(BatchSize)
            .ToList();
        var pending = _tracks.CountBpmPendingAsync().GetAwaiter().GetResult() - skipped.Count;

        lock (_sync)
        {
            foreach (var row in rows)
            {
                _batch.Enqueue(row);
            }

            // Songs added by a scan meanwhile grow the total rather than restarting the count.
            _total = _done + _requested.Count + Math.Max(pending, rows.Count);
        }

        RaiseStatus();
        return rows.Count > 0;
    }

    private void Process(long id, string? path, bool forced, CancellationToken token)
    {
        if (forced)
        {
            var track = _tracks.GetAsync(id).GetAwaiter().GetResult();
            path = track is { Missing: false, BpmSource: not "manual" } ? track.Path : null;
        }

        if (path is not null)
        {
            BpmResult? result = null;
            var save = true;
            try
            {
                result = _analyze(path, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                save = false;
                lock (_sync)
                {
                    _skipped.Add(id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Undecodable: remember it as analyzed without a beat rather than retrying every start.
            }

            if (save && _tracks.SaveAnalyzedBpmAsync(id, result).GetAwaiter().GetResult())
            {
                Analyzed?.Invoke(this, new BpmInfo(id, result?.Bpm, "analyzed", result?.Confidence));
            }
        }

        lock (_sync)
        {
            _done++;
        }

        RaiseStatus();
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, Status);
}
