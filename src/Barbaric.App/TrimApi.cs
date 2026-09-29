using System.IO;
using System.Windows.Threading;
using Barbaric.App.Bridge;
using Barbaric.Core.Analysis;
using Barbaric.Core.Audio;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;

namespace Barbaric.App;

/// <summary>
/// Lets the user set where a song starts and ends playing, as <c>trim.*</c> bridge methods: its
/// waveform to place the times on, and previews through a player of their own that pauses the
/// song that's playing until the editor closes. Previews never count as listens.
/// </summary>
public sealed class TrimApi : IDisposable
{
    private readonly TrackRepository _tracks;
    private readonly PlaybackController _controller;
    private readonly AudioEngine _engine;
    private readonly PlayerApi _player;
    private readonly WebBridge _bridge;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _previewTimer;

    // The last waveform drawn, so opening the editor again for the same song is instant.
    private (string Path, long Modified, Waveform Waveform)? _cached;

    private AudioEngine? _preview;
    private string? _previewPath;

    /// <summary>Whether a preview paused the song that was playing, to play it again when the editor closes.</summary>
    private bool _resumeMain;

    public TrimApi(TrackRepository tracks, PlaybackController controller, AudioEngine engine, PlayerApi player, WebBridge bridge, Dispatcher dispatcher)
    {
        _tracks = tracks;
        _controller = controller;
        _engine = engine;
        _player = player;
        _bridge = bridge;
        _dispatcher = dispatcher;

        bridge.QueryAsync("trim.get", async p => await GetAsync(p.GetId()));
        bridge.CommandAsync("trim.set", async p =>
        {
            if (!await _controller.SetTrimAsync(p.GetId(), p.GetOptionalNumber("startMs"), p.GetOptionalNumber("endMs")))
            {
                throw new InvalidOperationException("The song is no longer in the library.");
            }

            _player.RefreshState();
        });
        bridge.CommandAsync("trim.preview", p => PreviewAsync(
            p.GetId(), p.GetOptionalNumber("fromMs") ?? 0, p.GetOptionalNumber("toMs")));
        bridge.Command("trim.stopPreview", _ => StopPreview());
        bridge.CommandAsync("trim.close", _ => CloseAsync());

        _player.Changed += OnPlayerChanged;
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => EmitPreview(), dispatcher);
    }

    public void Dispose()
    {
        _player.Changed -= OnPlayerChanged;
        _previewTimer.Stop();
        ClosePreview();
    }

    private async Task<TrimInfo> GetAsync(long id)
    {
        var track = await _tracks.GetAsync(id) ?? throw new InvalidOperationException("The song is no longer in the library.");
        if (track.Missing || !File.Exists(track.Path))
        {
            throw new InvalidOperationException($"\"{track.Title}\" can't be found on disk.");
        }

        var modified = File.GetLastWriteTimeUtc(track.Path).Ticks;
        var waveformTask = _cached is { } cached && cached.Path == track.Path && cached.Modified == modified
            ? Task.FromResult(cached.Waveform)
            : Task.Run(() => Waveform.FromFile(track.Path));

        // A song not measured yet (e.g. just added) is measured alongside, so its silence shows.
        if (!track.LoudnessAnalyzed)
        {
            var result = await Task.Run(() => LoudnessAnalyzer.AnalyzeFile(track.Path));
            if (await _tracks.SaveLoudnessAsync(id, result))
            {
                track.SilenceEdges = Silence.ToJson(result?.Edges);
                if (_controller.UpdateLoudness(id, result?.LoudPartLufs, result?.PeakDb, track.SilenceEdges))
                {
                    _player.RefreshState();
                }
            }
        }

        var waveform = await waveformTask;
        _cached = (track.Path, modified, waveform);
        return new TrimInfo(
            track.Id,
            track.Title,
            track.Artist,
            waveform.DurationMs,
            waveform.BlockMs,
            Waveform.FloorDb,
            waveform.Peaks,
            waveform.Rms,
            track.Edges ?? [],
            track.TrimStartMs,
            track.TrimEndMs,
            _controller.SkipSilence,
            _controller.SilenceThresholdDb,
            PlaybackController.MinTrimmedMs);
    }

    /// <summary>Plays part of a song on the preview player, pausing the main one the first time.</summary>
    private async Task PreviewAsync(long id, long fromMs, long? toMs)
    {
        var track = await _tracks.GetAsync(id) ?? throw new InvalidOperationException("The song is no longer in the library.");
        if (_preview is null || _previewPath != track.Path)
        {
            ClosePreview();
            _preview = new AudioEngine();
            _preview.TrackEnded += OnPreviewEnded;
            _preview.PlaybackFailed += OnPreviewFailed;
            _preview.Load(track.Path, track.GainDb, track.AutoGainDb);
            _previewPath = track.Path;
        }

        // Played as loud as the song would be in the player.
        _preview.Normalize = _engine.Normalize;
        _preview.VolumeLimit = _engine.VolumeLimit;
        _preview.MasterVolume = _engine.MasterVolume;

        if (_engine.State == PlayerState.Playing)
        {
            _resumeMain = true;
            _player.Pause();
        }

        _preview.Pause();
        _preview.SetRange(TimeSpan.FromMilliseconds(fromMs), toMs is { } to ? TimeSpan.FromMilliseconds(to) : null);
        _preview.Rewind();
        await _preview.PlayAsync();
        _previewTimer.Start();
        EmitPreview();
    }

    private void StopPreview()
    {
        _preview?.Pause();
        _previewTimer.Stop();
        EmitPreview();
    }

    /// <summary>The editor closed: lets go of the file and plays the paused song again.</summary>
    private async Task CloseAsync()
    {
        ClosePreview();
        EmitPreview();
        if (_resumeMain)
        {
            _resumeMain = false;
            await _player.PlayAsync();
        }
    }

    private void ClosePreview()
    {
        _previewTimer.Stop();
        if (_preview is not null)
        {
            _preview.TrackEnded -= OnPreviewEnded;
            _preview.PlaybackFailed -= OnPreviewFailed;
            _preview.Dispose();
            _preview = null;
            _previewPath = null;
        }
    }

    // The song was played from elsewhere (media keys, a hotkey, the mini player): it wins over the preview.
    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        if (_engine.State == PlayerState.Playing && _preview?.State == PlayerState.Playing)
        {
            _resumeMain = false;
            StopPreview();
        }
    }

    private void OnPreviewEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        _previewTimer.Stop();
        EmitPreview();
    });

    private void OnPreviewFailed(object? sender, Exception ex) => _dispatcher.BeginInvoke(() =>
    {
        _previewTimer.Stop();
        EmitPreview();
        _bridge.Emit("player.error", new { message = $"Preview failed: {ex.Message}" });
    });

    private void EmitPreview() => _bridge.Emit("trim.preview", new
    {
        playing = _preview?.State == PlayerState.Playing,
        positionMs = (long)(_preview?.Position.TotalMilliseconds ?? 0),
    });

    /// <summary>
    /// A song's waveform, silence edges and the user's times. <see cref="Peaks"/> and <see cref="Rms"/>
    /// go to the UI as base64 bytes, one per <see cref="BlockMs"/>, on a dB scale from <see cref="FloorDb"/> up.
    /// </summary>
    private sealed record TrimInfo(
        long Id,
        string Title,
        string? Artist,
        long DurationMs,
        int BlockMs,
        double FloorDb,
        byte[] Peaks,
        byte[] Rms,
        IReadOnlyList<SilenceEdge> Edges,
        long? TrimStartMs,
        long? TrimEndMs,
        bool SkipSilence,
        int SilenceThresholdDb,
        long MinTrimmedMs);
}
