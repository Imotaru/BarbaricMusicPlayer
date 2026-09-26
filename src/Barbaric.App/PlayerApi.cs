using System.IO;
using System.Windows;
using System.Windows.Threading;
using Barbaric.App.Bridge;
using Barbaric.Core.Audio;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Microsoft.Win32;

namespace Barbaric.App;

/// <summary>What is loaded and how it's doing, for the OS media controls and the taskbar buttons.</summary>
public sealed record NowPlaying(
    string? Path,
    string? Title,
    string? Artist,
    string? Album,
    bool Playing,
    bool Paused,
    TimeSpan Position,
    TimeSpan Duration,
    bool HasNext,
    bool HasPrevious);

/// <summary>
/// Exposes playback to the UI as <c>player.*</c> bridge methods and events. Its public methods are
/// the one set of player commands the bridge, the OS media controls, the taskbar buttons and the
/// global hotkeys all go through.
/// </summary>
public sealed class PlayerApi : IDisposable
{
    private const string AudioFileFilter =
        "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.wma|All files|*.*";

    private const string QueueKey = "queue";
    private const string QueuePositionKey = "queuePos";
    internal const string VolumeKey = "volume";
    internal const string LoopTrackKey = "loopTrack";
    internal const string NormalizeKey = "normalize";

    private readonly AudioEngine _engine;
    private readonly PlaybackController _controller;
    private readonly WebBridge _bridge;
    private readonly SettingsStore _settings;
    private readonly Window _owner;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;
    private int _savedQueueVersion = -1;

    public PlayerApi(AudioEngine engine, PlaybackController controller, WebBridge bridge, SettingsStore settings, Window owner)
    {
        _engine = engine;
        _controller = controller;
        _bridge = bridge;
        _settings = settings;
        _owner = owner;
        _dispatcher = owner.Dispatcher;

        if (settings.Get<float?>(VolumeKey) is { } volume)
        {
            _engine.MasterVolume = volume;
        }

        _controller.LoopTrack = settings.Get<bool?>(LoopTrackKey) ?? false;
        _engine.Normalize = settings.Get<bool?>(NormalizeKey) ?? true;

        bridge.Query("player.getState", _ => Snapshot());
        bridge.QueryAsync("player.openFile", async _ => await OpenFileAsync());
        bridge.CommandAsync("player.playTrack", p => _controller.PlayTrackAsync(
            p.GetProperty("id").GetInt64(),
            p.TryGetProperty("context", out var context) ? LibraryApi.ParseQuery(context) : null));
        bridge.CommandAsync("player.playShuffled", async p =>
        {
            await _controller.PlayShuffledAsync(LibraryApi.ParseQuery(p.GetProperty("context")));
            EmitState();
        });
        bridge.CommandAsync("player.setBpmLens", async p =>
        {
            await _controller.SetBpmLensAsync(LibraryApi.ParseBpmRange(p));
            EmitState();
        });
        bridge.CommandAsync("player.play", _ => _engine.PlayAsync());
        bridge.Command("player.pause", _ => _engine.Pause());
        bridge.CommandAsync("player.toggle", _ => TogglePlayPauseAsync());
        bridge.CommandAsync("player.stop", _ => _controller.StopAsync());
        bridge.CommandAsync("player.next", _ => NextAsync());
        bridge.CommandAsync("player.previous", _ => PreviousAsync());
        bridge.CommandAsync("player.setShuffle", p => SetShuffleAsync(p.GetProperty("on").GetBoolean()));
        bridge.Command("player.setLoop", p => SetLoop(p.GetProperty("on").GetBoolean()));
        bridge.Command("player.setNormalize", p => SetNormalize(p.GetProperty("on").GetBoolean()));
        bridge.Command("player.seek", p => Seek(TimeSpan.FromSeconds(p.GetProperty("seconds").GetDouble())));
        bridge.CommandAsync("player.setTrackGain", async p =>
        {
            await _controller.SetTrackGainAsync(p.GetProperty("db").GetDouble());
            EmitState();
        });
        bridge.Command("player.setVolume", p => SetVolume(p.GetProperty("volume").GetDouble()));

        // Engine events can arrive on the audio thread; hop to the UI thread before touching anything.
        _engine.StateChanged += OnEngineStateChanged;
        _engine.PlaybackFailed += OnEnginePlaybackFailed;
        _controller.Error += OnControllerError;
        _controller.ListenRecorded += OnListenRecorded;
        _controller.TrackUpdated += OnTrackUpdated;

        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => EmitPosition(), _dispatcher);
    }

    /// <summary>Raised on the UI thread whenever the player state sent to the UI changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when the position jumps (a seek), as opposed to moving on by playing.</summary>
    public event EventHandler? Seeked;

    public NowPlaying NowPlaying
    {
        get
        {
            var snapshot = Snapshot();
            var loaded = snapshot.Path is not null;
            return new NowPlaying(
                snapshot.Path,
                snapshot.Title,
                snapshot.Artist,
                snapshot.Album,
                snapshot.State == PlayerState.Playing,

                // A restored song waits, stopped, where it was left: to the OS that's paused.
                loaded && (snapshot.State == PlayerState.Paused || (snapshot.State == PlayerState.Stopped && snapshot.Position > 0)),
                TimeSpan.FromSeconds(snapshot.Position),
                TimeSpan.FromSeconds(snapshot.Duration),
                snapshot.HasNext,
                snapshot.HasPrevious);
        }
    }

    public Task OpenAsync(string path) => _controller.PlayFileAsync(path);

    /// <summary>Plays or pauses what's loaded. With nothing loaded there's nothing to do.</summary>
    public Task TogglePlayPauseAsync() =>
        _engine.CurrentPath is null ? Task.CompletedTask : _engine.TogglePlayPauseAsync();

    public async Task PlayAsync()
    {
        if (_engine.CurrentPath is not null && _engine.State != PlayerState.Playing)
        {
            await _engine.PlayAsync();
        }
    }

    public void Pause() => _engine.Pause();

    public Task NextAsync() => _controller.NextAsync();

    public Task PreviousAsync() => _controller.PreviousAsync();

    public async Task SetShuffleAsync(bool on)
    {
        await _controller.SetShuffleAsync(on);
        EmitState();
    }

    public Task ToggleShuffleAsync() => SetShuffleAsync(!_controller.Shuffle);

    /// <summary>Repeats the current song when it ends, instead of moving on. Remembered across sessions.</summary>
    public void SetLoop(bool on)
    {
        _controller.LoopTrack = on;
        _settings.Save(LoopTrackKey, on);
        EmitState();
    }

    public void ToggleLoop() => SetLoop(!_controller.LoopTrack);

    /// <summary>Evens out the volume between songs. On unless turned off; remembered across sessions.</summary>
    public void SetNormalize(bool on)
    {
        _engine.Normalize = on;
        _settings.Save(NormalizeKey, on);
        EmitState();
    }

    /// <summary>Sends the player state again, e.g. after the loaded song's volume was measured.</summary>
    public void RefreshState() => EmitState();

    public void Seek(TimeSpan position)
    {
        _engine.Seek(position);
        EmitPosition();
        Seeked?.Invoke(this, EventArgs.Empty);
    }

    public void SeekBy(double seconds) => Seek(_engine.Position + TimeSpan.FromSeconds(seconds));

    public void SetVolume(double volume)
    {
        _engine.MasterVolume = (float)volume;
        _settings.Save(VolumeKey, _engine.MasterVolume);
        EmitState();
    }

    public void ChangeVolume(double delta) => SetVolume(_engine.MasterVolume + delta);

    /// <summary>
    /// Runs a command that didn't come from the UI (media keys, taskbar, hotkeys), reporting a
    /// failure the way the UI would see it.
    /// </summary>
    public void Run(Func<Task> command) => _dispatcher.InvokeAsync(async () =>
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            _bridge.Emit("player.error", new { message = ex.Message });
        }
    });

    /// <summary>Loads the queue saved last time, paused where it was left.</summary>
    public async Task RestoreQueueAsync()
    {
        if (_settings.Get<QueueSnapshot>(QueueKey) is not { } queue
            || _settings.Get<QueuePosition>(QueuePositionKey) is not { } position)
        {
            return;
        }

        try
        {
            await _controller.RestoreAsync(queue, position);
        }
        catch (Exception ex)
        {
            // Not worth bothering the user about: they just get an empty player, as on a first start.
            System.Diagnostics.Debug.WriteLine($"Couldn't restore the queue: {ex}");
        }

        _savedQueueVersion = _controller.Queue.Version;
        EmitState();
    }

    /// <summary>
    /// Saves where playback is, and the queue itself when it changed. A file played from outside the
    /// library leaves the last library queue saved.
    /// </summary>
    public void SaveQueue()
    {
        if (_controller.Position() is not { } position)
        {
            return;
        }

        if (_controller.Queue.Version != _savedQueueVersion && _controller.Snapshot() is { } snapshot)
        {
            _settings.Save(QueueKey, snapshot);
            _savedQueueVersion = _controller.Queue.Version;
        }

        _settings.Save(QueuePositionKey, position);
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PlaybackFailed -= OnEnginePlaybackFailed;
        _controller.Error -= OnControllerError;
        _controller.ListenRecorded -= OnListenRecorded;
        _controller.TrackUpdated -= OnTrackUpdated;
    }

    private async Task<PlayerSnapshot?> OpenFileAsync()
    {
        var dialog = new OpenFileDialog { Filter = AudioFileFilter, Title = "Open a song" };
        if (dialog.ShowDialog(_owner) != true)
        {
            return null;
        }

        await OpenAsync(dialog.FileName);
        return Snapshot();
    }

    private void OnEngineStateChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        _positionTimer.IsEnabled = _engine.State == PlayerState.Playing;
        EmitState();
        SaveQueue();
    });

    private void OnEnginePlaybackFailed(object? sender, Exception ex) =>
        _bridge.Emit("player.error", new { message = ex.Message });

    private void OnControllerError(object? sender, string message) =>
        _bridge.Emit("player.error", new { message });

    // Play and skip counts show in the list, and the flag decides what's in the suggestions.
    private void OnListenRecorded(object? sender, ListenRecord e) => _bridge.Emit("library.changed");

    // A length found by the decoder shows in the list; an edited title or artist in the player and the OS too.
    private void OnTrackUpdated(object? sender, Track e) => _dispatcher.BeginInvoke(() =>
    {
        _bridge.Emit("library.changed");
        EmitState();
    });

    private void EmitState()
    {
        _bridge.Emit("player.state", Snapshot());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EmitPosition() => _bridge.Emit("player.position", new { position = _engine.Position.TotalSeconds });

    private PlayerSnapshot Snapshot()
    {
        var track = _controller.CurrentTrack;
        var path = _engine.CurrentPath;
        return new PlayerSnapshot(
            _engine.State,
            track?.Id,
            path,
            track?.Title ?? (path is null ? null : Path.GetFileNameWithoutExtension(path)),
            track?.Artist,
            track?.Album,
            _engine.Duration.TotalSeconds,
            _engine.Position.TotalSeconds,
            _engine.TrackGainDb,
            _controller.CurrentTrack is { LoudnessAnalyzed: true } ? _engine.AutoGainDb : null,
            _engine.Normalize,
            _engine.MasterVolume,
            _controller.HasNext,
            _controller.Queue.HasPrevious,
            _controller.Shuffle,
            _controller.LoopTrack);
    }

    private sealed record PlayerSnapshot(
        PlayerState State,
        long? TrackId,
        string? Path,
        string? Title,
        string? Artist,
        string? Album,
        double Duration,
        double Position,
        double TrackGainDb,
        double? AutoGainDb,
        bool Normalize,
        float Volume,
        bool HasNext,
        bool HasPrevious,
        bool Shuffle,
        bool Loop);
}
