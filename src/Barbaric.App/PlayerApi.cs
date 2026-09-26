using System.IO;
using System.Windows;
using System.Windows.Threading;
using Barbaric.App.Bridge;
using Barbaric.Core.Audio;
using Barbaric.Core.Playback;
using Microsoft.Win32;

namespace Barbaric.App;

/// <summary>Exposes playback to the UI as <c>player.*</c> bridge methods and events.</summary>
public sealed class PlayerApi : IDisposable
{
    private const string AudioFileFilter =
        "Audio files|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.wma|All files|*.*";

    private readonly AudioEngine _engine;
    private readonly PlaybackController _controller;
    private readonly WebBridge _bridge;
    private readonly Window _owner;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;

    public PlayerApi(AudioEngine engine, PlaybackController controller, WebBridge bridge, Window owner)
    {
        _engine = engine;
        _controller = controller;
        _bridge = bridge;
        _owner = owner;
        _dispatcher = owner.Dispatcher;

        bridge.Query("player.getState", _ => Snapshot());
        bridge.QueryAsync("player.openFile", async _ => await OpenFileAsync());
        bridge.CommandAsync("player.playTrack", p => _controller.PlayTrackAsync(
            p.GetProperty("id").GetInt64(),
            p.TryGetProperty("context", out var context) ? LibraryApi.ParseQuery(context) : null));
        bridge.CommandAsync("player.setBpmLens", async p =>
        {
            await _controller.SetBpmLensAsync(LibraryApi.ParseBpmRange(p));
            EmitState();
        });
        bridge.CommandAsync("player.play", _ => _engine.PlayAsync());
        bridge.Command("player.pause", _ => _engine.Pause());
        bridge.CommandAsync("player.toggle", _ => _engine.TogglePlayPauseAsync());
        bridge.CommandAsync("player.stop", _ => _controller.StopAsync());
        bridge.CommandAsync("player.next", _ => _controller.NextAsync());
        bridge.CommandAsync("player.previous", _ => _controller.PreviousAsync());
        bridge.CommandAsync("player.setShuffle", async p =>
        {
            await _controller.SetShuffleAsync(p.GetProperty("on").GetBoolean());
            EmitState();
        });
        bridge.Command("player.seek", p =>
        {
            _engine.Seek(TimeSpan.FromSeconds(p.GetProperty("seconds").GetDouble()));
            EmitPosition();
        });
        bridge.CommandAsync("player.setTrackGain", async p =>
        {
            await _controller.SetTrackGainAsync(p.GetProperty("db").GetDouble());
            EmitState();
        });
        bridge.Command("player.setVolume", p =>
        {
            _engine.MasterVolume = (float)p.GetProperty("volume").GetDouble();
            EmitState();
        });

        // Engine events can arrive on the audio thread; hop to the UI thread before touching anything.
        _engine.StateChanged += OnEngineStateChanged;
        _engine.PlaybackFailed += OnEnginePlaybackFailed;
        _controller.Error += OnControllerError;
        _controller.ListenRecorded += OnListenRecorded;

        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => EmitPosition(), _dispatcher);
    }

    public Task OpenAsync(string path) => _controller.PlayFileAsync(path);

    public void Dispose()
    {
        _positionTimer.Stop();
        _engine.StateChanged -= OnEngineStateChanged;
        _engine.PlaybackFailed -= OnEnginePlaybackFailed;
        _controller.Error -= OnControllerError;
        _controller.ListenRecorded -= OnListenRecorded;
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
    });

    private void OnEnginePlaybackFailed(object? sender, Exception ex) =>
        _bridge.Emit("player.error", new { message = ex.Message });

    private void OnControllerError(object? sender, string message) =>
        _bridge.Emit("player.error", new { message });

    // Play and skip counts show in the list, and the flag decides what's in the suggestions.
    private void OnListenRecorded(object? sender, ListenRecord e) => _bridge.Emit("library.changed");

    private void EmitState() => _bridge.Emit("player.state", Snapshot());

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
            _engine.MasterVolume,
            _controller.Queue.HasNext,
            _controller.Queue.HasPrevious,
            _controller.Shuffle);
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
        float Volume,
        bool HasNext,
        bool HasPrevious,
        bool Shuffle);
}
