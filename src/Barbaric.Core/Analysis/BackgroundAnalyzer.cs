namespace Barbaric.Core.Analysis;

public sealed record AnalysisStatus(bool Running, int Done, int Total);

/// <summary>
/// Works through the tracks a subclass reports as pending on one low-priority thread, plus any tracks
/// the user asks to have analyzed again (those go first).
/// </summary>
/// <typeparam name="TResult">What analyzing one file gives; null when there was nothing to find.</typeparam>
/// <typeparam name="TInfo">What <see cref="Analyzed"/> reports about a track whose stored result changed.</typeparam>
public abstract class BackgroundAnalyzer<TResult, TInfo> : IDisposable
    where TResult : class
    where TInfo : class
{
    private const int BatchSize = 100;

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

    /// <summary>Raised on the worker thread whenever progress changes or the worker starts or stops.</summary>
    public event EventHandler<AnalysisStatus>? StatusChanged;

    /// <summary>Raised on the worker thread for each track whose stored result changed.</summary>
    public event EventHandler<TInfo>? Analyzed;

    /// <summary>Raised when the worker stops on an unexpected error (e.g. the database is unavailable).</summary>
    public event EventHandler<Exception>? Failed;

    public AnalysisStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new AnalysisStatus(_running, _done, _total);
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

    /// <summary>Starts (or keeps) working through every pending track.</summary>
    public void Start()
    {
        lock (_sync)
        {
            _background = true;
            EnsureWorker();
        }
    }

    /// <summary>Analyzes these tracks next, whether or not they were analyzed before.</summary>
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

        GC.SuppressFinalize(this);
    }

    /// <summary>Up to <paramref name="limit"/> tracks still waiting for analysis, in the order to analyze them.</summary>
    protected abstract IReadOnlyList<(long Id, string Path)> GetPending(int limit);

    protected abstract int CountPending();

    /// <summary>The file of a track the user asked to have analyzed, or null to leave the track alone.</summary>
    protected abstract string? PathForRequest(long id);

    /// <exception cref="IOException">The file can't be read right now; the track stays pending.</exception>
    protected abstract TResult? Analyze(string path, CancellationToken token);

    /// <summary>Stores a result (null for a file that couldn't be decoded).</summary>
    /// <returns>What to report through <see cref="Analyzed"/>, or null when nothing changed.</returns>
    protected abstract TInfo? Save(long id, TResult? result);

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

        var rows = GetPending(BatchSize + skipped.Count)
            .Where(r => !skipped.Contains(r.Id))
            .Take(BatchSize)
            .ToList();
        var pending = CountPending() - skipped.Count;

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
            path = PathForRequest(id);
        }

        if (path is not null)
        {
            TResult? result = null;
            var save = true;
            try
            {
                result = Analyze(path, token);
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
                // Undecodable: remember it as analyzed without a result rather than retrying every start.
            }

            if (save && Save(id, result) is { } info)
            {
                Analyzed?.Invoke(this, info);
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
