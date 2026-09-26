using Barbaric.Core.Audio;
using Barbaric.Core.Library;

namespace Barbaric.Core.Playback;

/// <summary>A listen that was logged, and whether it moved the song on or off the suggestions.</summary>
public sealed record ListenRecord(long TrackId, PlayKind Kind, bool FlagChanged);

/// <summary>
/// Plays library tracks through the <see cref="AudioEngine"/>: builds the queue from the list the
/// user played from (in list order or smart-shuffled), advances when a song ends, remembers each
/// song's volume, and logs how every song was left.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    /// <summary>"Previous" restarts the current song when it has played longer than this.</summary>
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    private readonly AudioEngine _engine;
    private readonly TrackRepository _tracks;
    private readonly PlayStatsRepository _stats;
    private readonly SynchronizationContext? _context;
    private readonly TimeProvider _clock;
    private readonly Random _random;

    /// <summary>The list the queue was built from, so a new BPM lens or shuffle can rebuild it.</summary>
    private TrackQuery? _queueSource;

    /// <summary>The song being listened to, from the moment it starts playing until it is left.</summary>
    private Track? _listening;

    /// <param name="context">
    /// Where end-of-song handling runs. Pass the UI context in the app; <c>null</c> runs it inline.
    /// </param>
    /// <param name="random">Drives shuffle; tests pass a seeded one.</param>
    public PlaybackController(
        AudioEngine engine,
        TrackRepository tracks,
        PlayStatsRepository stats,
        SynchronizationContext? context = null,
        TimeProvider? clock = null,
        Random? random = null)
    {
        _engine = engine;
        _tracks = tracks;
        _stats = stats;
        _context = context;
        _clock = clock ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _engine.TrackEnded += OnTrackEnded;
        _engine.StateChanged += OnEngineStateChanged;
    }

    /// <summary>Reports songs that were skipped or failed, as user-facing messages.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Raised after a song was left and its play or skip was saved.</summary>
    public event EventHandler<ListenRecord>? ListenRecorded;

    /// <summary>
    /// Raised when the loaded song's details changed: playing it taught us something (e.g. a length
    /// its tags didn't have), or the user edited it.
    /// </summary>
    public event EventHandler<Track>? TrackUpdated;

    public PlayQueue Queue { get; } = new();

    /// <summary>The library track that is loaded, or null for a file played from outside the library.</summary>
    public Track? CurrentTrack { get; private set; }

    public bool Shuffle { get; private set; }

    /// <summary>Plays a track and queues the rest of the list it was picked from.</summary>
    public async Task PlayTrackAsync(long id, TrackQuery? context = null)
    {
        await FinishListeningAsync(LeaveReason.Switched);
        _queueSource = context;
        List<long> ids = context is null ? [id] : [.. await _tracks.QueryIdsAsync(context)];
        if (!ids.Contains(id))
        {
            ids = [id];
        }

        var (order, index) = await ArrangeAsync(ids, id, played: []);
        Queue.Set(order, index);
        await PlayCurrentAsync(forward: true);
    }

    /// <summary>
    /// Plays the list shuffled, starting from a song shuffle picks, and turns shuffle on. Nothing is
    /// skipped to get there, so no song is counted against.
    /// </summary>
    public async Task PlayShuffledAsync(TrackQuery context)
    {
        await FinishListeningAsync(LeaveReason.Switched);
        var ids = await _tracks.QueryIdsAsync(context);
        if (ids.Count == 0)
        {
            return;
        }

        Shuffle = true;
        _queueSource = context;
        var stats = await _stats.GetShuffleStatsAsync(ids);
        Queue.Set(SmartShuffle.Order(stats, _clock.GetUtcNow(), _random), 0);
        await PlayCurrentAsync(forward: true);
    }

    /// <summary>Plays any file. If it is part of the library, it plays as that track (with its saved volume).</summary>
    public async Task PlayFileAsync(string path)
    {
        await FinishListeningAsync(LeaveReason.Switched);
        var track = await _tracks.GetByPathAsync(path);
        if (track is not null)
        {
            await PlayTrackAsync(track.Id);
            return;
        }

        Queue.Clear();
        _queueSource = null;
        CurrentTrack = null;
        _engine.Load(path);
        await _engine.PlayAsync();
    }

    public async Task<bool> NextAsync()
    {
        if (!Queue.MoveNext())
        {
            return false;
        }

        await FinishListeningAsync(LeaveReason.Next);
        await PlayCurrentAsync(forward: true);
        return true;
    }

    public async Task PreviousAsync()
    {
        if (_engine.Position > RestartThreshold || !Queue.MovePrevious())
        {
            await FinishListeningAsync(LeaveReason.Back);
            _engine.Seek(TimeSpan.Zero);
            BeginListening();
            return;
        }

        await FinishListeningAsync(LeaveReason.Back);
        await PlayCurrentAsync(forward: false);
    }

    public async Task StopAsync()
    {
        await FinishListeningAsync(LeaveReason.Stopped);
        _engine.Stop();
    }

    /// <summary>Saves the listen in progress; call when the app closes.</summary>
    public Task CloseAsync() => FinishListeningAsync(LeaveReason.Closed);

    /// <summary>
    /// Re-filters the queue by a new BPM range. The playing song stays put even when it falls
    /// outside the range, so Next and Previous carry on from where it sits in the list.
    /// </summary>
    public async Task SetBpmLensAsync(BpmRange? lens)
    {
        if (_queueSource is null || Queue.Current is null)
        {
            return;
        }

        _queueSource = _queueSource with { Bpm = lens };
        await RebuildQueueAsync(played: Shuffle ? [.. Queue.Ids.Take(Queue.Index)] : []);
    }

    /// <summary>
    /// Turns shuffle on or off. The playing song carries on either way: on, the rest of the list is
    /// shuffled after it; off, the queue goes back to list order around it.
    /// </summary>
    public async Task SetShuffleAsync(bool shuffle)
    {
        if (Shuffle == shuffle)
        {
            return;
        }

        Shuffle = shuffle;
        await RebuildQueueAsync(played: []);
    }

    /// <summary>
    /// Takes songs out of the queue, e.g. after they were hidden. With <paramref name="unload"/>, a
    /// loaded song among them is also closed so its file can be deleted.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyCollection<long> ids, bool unload)
    {
        Queue.Remove(ids);
        if (unload && CurrentTrack is { } track && ids.Contains(track.Id))
        {
            await FinishListeningAsync(LeaveReason.Removed);

            // Cleared first: Unload raises StateChanged and listeners read CurrentTrack.
            CurrentTrack = null;
            _engine.Unload();
        }
    }

    /// <summary>The queue to save, or null when nothing from the library is loaded.</summary>
    public QueueSnapshot? Snapshot() =>
        CurrentTrack is null || Queue.Count == 0 ? null : new QueueSnapshot([.. Queue.Ids], _queueSource, Shuffle);

    /// <summary>Where playback is in the queue, or null when nothing from the library is loaded.</summary>
    public QueuePosition? Position() =>
        CurrentTrack is { } track && Queue.Current == track.Id
            ? new QueuePosition(track.Id, Queue.Index, _engine.Position.TotalSeconds)
            : null;

    /// <summary>
    /// Brings back a saved queue, loaded and paused where it was left. Songs that have since gone
    /// missing or been hidden are dropped; if the current one is among them, the next one that is
    /// left takes its place, from the start. Nothing is reported for them: the user didn't ask to
    /// play anything yet.
    /// </summary>
    /// <returns>False when nothing in the saved queue can be played any more.</returns>
    public async Task<bool> RestoreAsync(QueueSnapshot snapshot, QueuePosition position)
    {
        // The UI starts every session with an open BPM lens, so the queue must not keep a narrower one.
        var source = snapshot.Source is null ? null : snapshot.Source with { Bpm = null };
        var ids = (await _tracks.KeepPlayableAsync(snapshot.Ids, source?.Scope ?? TrackScope.Library)).ToList();
        if (ids.Count == 0)
        {
            return false;
        }

        var index = ids.IndexOf(position.CurrentId);
        var seek = TimeSpan.FromSeconds(Math.Max(0, position.PositionSeconds));
        if (index < 0)
        {
            var survivors = ids.ToHashSet();
            index = Math.Min(snapshot.Ids.Take(Math.Max(0, position.Index)).Count(survivors.Contains), ids.Count - 1);
            seek = TimeSpan.Zero;
        }

        _queueSource = source;
        Shuffle = snapshot.Shuffle;
        Queue.Set(ids, index);

        for (var attempt = 0; attempt < Queue.Count; attempt++)
        {
            if (Queue.Current is not { } id)
            {
                break;
            }

            if (await _tracks.GetAsync(id) is { } track && TryLoad(track, reportErrors: false))
            {
                if (track.Id == position.CurrentId && seek > TimeSpan.Zero && seek < _engine.Duration)
                {
                    _engine.Seek(seek);
                }

                // The saved ids were narrowed by that lens; without it, the rest of the list belongs back in.
                if (snapshot.Source?.Bpm is { IsOpen: false })
                {
                    await RebuildQueueAsync(played: Shuffle ? [.. Queue.Ids.Take(Queue.Index)] : []);
                }

                return true;
            }

            if (!Queue.MoveNext())
            {
                break;
            }
        }

        Queue.Clear();
        _queueSource = null;
        return false;
    }

    public async Task SetTrackGainAsync(double gainDb)
    {
        _engine.TrackGainDb = gainDb;
        if (CurrentTrack is { } track)
        {
            track.GainDb = _engine.TrackGainDb;
            await _tracks.SetGainAsync(track.Id, track.GainDb);
        }
    }

    /// <summary>Picks up edited details (title, artist, …) of the loaded song when it is among <paramref name="ids"/>.</summary>
    public async Task RefreshCurrentTrackAsync(IReadOnlyCollection<long> ids)
    {
        if (CurrentTrack is not { } track || !ids.Contains(track.Id) || await _tracks.GetAsync(track.Id) is not { } fresh)
        {
            return;
        }

        // Updated in place: the listen in progress and end-of-song handling hold on to this object.
        track.Title = fresh.Title;
        track.Artist = fresh.Artist;
        track.Album = fresh.Album;
        track.AlbumArtist = fresh.AlbumArtist;
        track.Genre = fresh.Genre;
        track.Year = fresh.Year;
        track.TrackNumber = fresh.TrackNumber;
        TrackUpdated?.Invoke(this, track);
    }

    public void Dispose()
    {
        _engine.TrackEnded -= OnTrackEnded;
        _engine.StateChanged -= OnEngineStateChanged;
    }

    /// <summary>
    /// Orders the queue around <paramref name="current"/>. In shuffle, the songs already played
    /// keep their place before it and only the rest are shuffled, so nothing repeats.
    /// </summary>
    private async Task<(List<long> Order, int Index)> ArrangeAsync(
        IReadOnlyList<long> ids,
        long current,
        IReadOnlyList<long> played)
    {
        if (!Shuffle)
        {
            List<long> order = [.. ids];
            return (order, order.IndexOf(current));
        }

        var inList = ids.ToHashSet();
        var history = played.Where(id => id != current && inList.Contains(id)).Distinct().ToList();
        var placed = history.Append(current).ToHashSet();
        var rest = await _stats.GetShuffleStatsAsync(ids.Where(id => !placed.Contains(id)));
        var shuffled = SmartShuffle.Order(rest, _clock.GetUtcNow(), _random);
        return ([.. history, current, .. shuffled], history.Count);
    }

    private async Task RebuildQueueAsync(IReadOnlyList<long> played)
    {
        if (_queueSource is not { } source || Queue.Current is not { } current)
        {
            return;
        }

        // Query without the lens and filter here, so a playing song outside the range keeps its place.
        var all = await _tracks.QueryIdsAsync(source with { Bpm = null });
        var kept = source.Bpm is null or { IsOpen: true } ? null : (await _tracks.QueryIdsAsync(source)).ToHashSet();
        var ids = all.Where(id => id == current || kept is null || kept.Contains(id)).ToList();
        var (order, index) = await ArrangeAsync(ids, current, played);

        // The queue may have moved on while the ids were loading.
        if (Queue.Current == current && index >= 0)
        {
            Queue.Set(order, index);
        }
    }

    /// <summary>
    /// Loads the queue's current track, skipping over songs whose file is gone or can't be decoded
    /// in the direction of travel.
    /// </summary>
    private async Task PlayCurrentAsync(bool forward)
    {
        for (var attempt = 0; attempt < Queue.Count; attempt++)
        {
            if (Queue.Current is not { } id)
            {
                return;
            }

            var track = await _tracks.GetAsync(id);
            if (track is not null && TryLoad(track))
            {
                // Device errors surface to the caller: skipping tracks would not fix them.
                await _engine.PlayAsync();
                return;
            }

            if (!(forward ? Queue.MoveNext() : Queue.MovePrevious()))
            {
                return;
            }
        }
    }

    private bool TryLoad(Track track, bool reportErrors = true)
    {
        if (track.Missing || !File.Exists(track.Path))
        {
            if (reportErrors)
            {
                Error?.Invoke(this, $"Skipped \"{track.Title}\": the file is missing.");
            }

            return false;
        }

        // Set before loading: Load raises StateChanged and listeners read CurrentTrack.
        var previous = CurrentTrack;
        CurrentTrack = track;
        try
        {
            _engine.Load(track.Path, track.GainDb);
        }
        catch (Exception ex)
        {
            CurrentTrack = previous;
            if (reportErrors)
            {
                Error?.Invoke(this, $"Skipped \"{track.Title}\": {ex.Message}");
            }

            return false;
        }

        if (track.DurationMs <= 0 && _engine.Duration > TimeSpan.Zero)
        {
            track.DurationMs = (long)_engine.Duration.TotalMilliseconds;
            _ = SaveDurationAsync(track);
        }

        return true;
    }

    /// <summary>Stores a length the decoder found for a file whose tags had none.</summary>
    private async Task SaveDurationAsync(Track track)
    {
        try
        {
            await _tracks.SetDurationAsync(track.Id, track.DurationMs);
            TrackUpdated?.Invoke(this, track);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Couldn't save the length of \"{track.Title}\": {ex.Message}");
        }
    }

    /// <summary>Starts a listen when a library song starts playing, including after Stop or a restart.</summary>
    private void BeginListening()
    {
        if (_engine.State == PlayerState.Playing && CurrentTrack is { } track)
        {
            _listening ??= track;
        }
    }

    /// <summary>Logs how the song being listened to was left. The position is read before anything is awaited.</summary>
    private async Task FinishListeningAsync(LeaveReason reason)
    {
        if (_listening is not { } track)
        {
            return;
        }

        _listening = null;
        var playedMs = (long)_engine.Position.TotalMilliseconds;
        var durationMs = (long)_engine.Duration.TotalMilliseconds;
        if (durationMs <= 0)
        {
            durationMs = track.DurationMs;
        }

        if (durationMs <= 0)
        {
            return;
        }

        var kind = Listening.Classify(playedMs, durationMs, reason);
        try
        {
            if (await _stats.RecordAsync(track.Id, playedMs, durationMs, kind) is { } flagChanged)
            {
                ListenRecorded?.Invoke(this, new ListenRecord(track.Id, kind, flagChanged));
            }
        }
        catch (Exception ex)
        {
            // Losing one entry of play history must not stop the music.
            Error?.Invoke(this, $"Couldn't save play history: {ex.Message}");
        }
    }

    private void OnEngineStateChanged(object? sender, EventArgs e) => BeginListening();

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        // Read now: by the time the posted handler runs, the user may have moved on.
        var ended = CurrentTrack;
        if (_context is null)
        {
            _ = AdvanceAfterEndAsync(ended);
        }
        else
        {
            _context.Post(_ => _ = AdvanceAfterEndAsync(ended), null);
        }
    }

    private async Task AdvanceAfterEndAsync(Track? ended)
    {
        if (CurrentTrack != ended)
        {
            return;
        }

        try
        {
            await FinishListeningAsync(LeaveReason.Ended);
            if (Queue.MoveNext())
            {
                await PlayCurrentAsync(forward: true);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }
}
