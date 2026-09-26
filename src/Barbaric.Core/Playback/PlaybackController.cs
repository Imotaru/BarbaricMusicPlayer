using Barbaric.Core.Audio;
using Barbaric.Core.Library;

namespace Barbaric.Core.Playback;

/// <summary>
/// Plays library tracks through the <see cref="AudioEngine"/>: builds the queue from the list the
/// user played from, advances when a song ends, and remembers each song's volume.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    /// <summary>"Previous" restarts the current song when it has played longer than this.</summary>
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    private readonly AudioEngine _engine;
    private readonly TrackRepository _tracks;
    private readonly SynchronizationContext? _context;

    /// <param name="context">
    /// Where end-of-song handling runs. Pass the UI context in the app; <c>null</c> runs it inline.
    /// </param>
    public PlaybackController(AudioEngine engine, TrackRepository tracks, SynchronizationContext? context = null)
    {
        _engine = engine;
        _tracks = tracks;
        _context = context;
        _engine.TrackEnded += OnTrackEnded;
    }

    /// <summary>Reports songs that were skipped or failed, as user-facing messages.</summary>
    public event EventHandler<string>? Error;

    public PlayQueue Queue { get; } = new();

    /// <summary>The library track that is loaded, or null for a file played from outside the library.</summary>
    public Track? CurrentTrack { get; private set; }

    /// <summary>Plays a track and queues the rest of the list it was picked from.</summary>
    public async Task PlayTrackAsync(long id, TrackQuery? context = null)
    {
        List<long> ids = context is null ? [id] : [.. await _tracks.QueryIdsAsync(context)];
        var index = ids.IndexOf(id);
        if (index < 0)
        {
            ids = [id];
            index = 0;
        }

        Queue.Set(ids, index);
        await PlayCurrentAsync(forward: true);
    }

    /// <summary>Plays any file. If it is part of the library, it plays as that track (with its saved volume).</summary>
    public async Task PlayFileAsync(string path)
    {
        var track = await _tracks.GetByPathAsync(path);
        if (track is not null)
        {
            await PlayTrackAsync(track.Id);
            return;
        }

        Queue.Clear();
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

        await PlayCurrentAsync(forward: true);
        return true;
    }

    public async Task PreviousAsync()
    {
        if (_engine.Position > RestartThreshold || !Queue.MovePrevious())
        {
            _engine.Seek(TimeSpan.Zero);
            return;
        }

        await PlayCurrentAsync(forward: false);
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

    public void Dispose() => _engine.TrackEnded -= OnTrackEnded;

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

    private bool TryLoad(Track track)
    {
        if (track.Missing || !File.Exists(track.Path))
        {
            Error?.Invoke(this, $"Skipped \"{track.Title}\": the file is missing.");
            return false;
        }

        // Set before loading: Load raises StateChanged and listeners read CurrentTrack.
        var previous = CurrentTrack;
        CurrentTrack = track;
        try
        {
            _engine.Load(track.Path, track.GainDb);
            return true;
        }
        catch (Exception ex)
        {
            CurrentTrack = previous;
            Error?.Invoke(this, $"Skipped \"{track.Title}\": {ex.Message}");
            return false;
        }
    }

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        if (_context is null)
        {
            _ = AdvanceAfterEndAsync();
        }
        else
        {
            _context.Post(_ => _ = AdvanceAfterEndAsync(), null);
        }
    }

    private async Task AdvanceAfterEndAsync()
    {
        try
        {
            await NextAsync();
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }
}
