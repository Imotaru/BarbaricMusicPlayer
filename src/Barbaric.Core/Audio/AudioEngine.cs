using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Barbaric.Core.Audio;

/// <summary>
/// Plays one track at a time. Owns the decoder and the output device, and applies
/// per-track gain (dB) combined with a master volume.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Func<Task<IWavePlayer>> _outputFactory;
    private readonly object _sync = new();
    private WaveStream? _reader;
    private GainSampleProvider? _chain;
    private IWavePlayer? _output;
    private Task<IWavePlayer>? _pendingOutput;
    private int _generation;
    private double _trackGainDb;
    private float _masterVolume = 1f;

    /// <summary>
    /// Plays through the default Windows device and follows it when it changes
    /// (e.g. headphones plugged in or unplugged).
    /// </summary>
    public AudioEngine()
        : this(async () => await new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(100)
            .WithCategory(AudioStreamCategory.Media)
            .WithDefaultDeviceStreamRouting()
            .BuildAsync())
    {
    }

    public AudioEngine(Func<Task<IWavePlayer>> outputFactory)
    {
        _outputFactory = outputFactory;
    }

    /// <summary>Raised whenever <see cref="State"/> changes or a new track is loaded.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the current track plays to its end.</summary>
    public event EventHandler? TrackEnded;

    /// <summary>Raised when the output device fails during playback.</summary>
    public event EventHandler<Exception>? PlaybackFailed;

    public string? CurrentPath { get; private set; }

    public PlayerState State { get; private set; } = PlayerState.Stopped;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                return _reader?.CurrentTime ?? TimeSpan.Zero;
            }
        }
    }

    public double TrackGainDb
    {
        get => _trackGainDb;
        set
        {
            _trackGainDb = Gain.ClampDb(value);
            ApplyGain();
        }
    }

    /// <summary>Master volume, 0..1, applied on top of the track gain.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, 1f);
            ApplyGain();
        }
    }

    public void Load(string path, double trackGainDb = 0)
    {
        var reader = AudioDecoder.Open(path);
        CloseCurrent();

        _reader = reader;
        _chain = new GainSampleProvider(reader.ToSampleProvider(), _sync);
        _trackGainDb = Gain.ClampDb(trackGainDb);
        ApplyGain();
        CurrentPath = path;
        SetState(PlayerState.Stopped, force: true);
    }

    public async Task PlayAsync()
    {
        if (_chain is null || _reader is null)
        {
            throw new InvalidOperationException("No track loaded.");
        }

        if (Position >= Duration)
        {
            Seek(TimeSpan.Zero);
        }

        if (_output is null)
        {
            var generation = _generation;
            _pendingOutput ??= CreateOutputAsync(_chain);

            IWavePlayer output;
            try
            {
                output = await _pendingOutput;
            }
            catch when (generation != _generation)
            {
                return; // Failure belongs to a track that has since been replaced.
            }
            catch
            {
                _pendingOutput = null; // Allow the next Play to retry the device.
                throw;
            }

            // Another track was loaded while the device was starting; CloseCurrent disposes this one.
            if (generation != _generation)
            {
                return;
            }

            _output = output;
            _pendingOutput = null;
        }

        _output.Play();
        SetState(PlayerState.Playing);
    }

    public void Pause()
    {
        if (State != PlayerState.Playing)
        {
            return;
        }

        _output?.Pause();
        SetState(PlayerState.Paused);
    }

    public Task TogglePlayPauseAsync()
    {
        if (State == PlayerState.Playing)
        {
            Pause();
            return Task.CompletedTask;
        }

        return PlayAsync();
    }

    /// <summary>Pauses output and rewinds to the start. Does not raise <see cref="TrackEnded"/>.</summary>
    public void Stop()
    {
        _output?.Pause();
        Seek(TimeSpan.Zero);
        SetState(PlayerState.Stopped);
    }

    /// <summary>Stops and closes the current file so it can be moved or deleted.</summary>
    public void Unload()
    {
        CloseCurrent();
        SetState(PlayerState.Stopped, force: true);
    }

    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_reader is null)
            {
                return;
            }

            var clamped = position < TimeSpan.Zero ? TimeSpan.Zero
                : position > _reader.TotalTime ? _reader.TotalTime
                : position;
            _reader.CurrentTime = clamped;
        }
    }

    public void Dispose() => CloseCurrent();

    private async Task<IWavePlayer> CreateOutputAsync(ISampleProvider chain)
    {
        var output = await _outputFactory();
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(chain.ToWaveProvider());
        return output;
    }

    private void DisposeOutput(IWavePlayer output)
    {
        output.PlaybackStopped -= OnPlaybackStopped;
        output.Stop();
        output.Dispose();
    }

    private void ApplyGain()
    {
        if (_chain is not null)
        {
            _chain.Gain = Gain.DbToLinear(_trackGainDb) * _masterVolume;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Only fires when the stream runs dry or the device fails; user stops go through Pause.
        SetState(PlayerState.Stopped);
        if (e.Exception is not null)
        {
            PlaybackFailed?.Invoke(this, e.Exception);
        }
        else
        {
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CloseCurrent()
    {
        _generation++;

        if (_output is not null)
        {
            DisposeOutput(_output);
            _output = null;
        }

        if (_pendingOutput is not null)
        {
            _pendingOutput.ContinueWith(
                t => DisposeOutput(t.Result),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _pendingOutput = null;
        }

        lock (_sync)
        {
            _reader?.Dispose();
            _reader = null;
        }

        _chain = null;
        CurrentPath = null;
    }

    private void SetState(PlayerState state, bool force = false)
    {
        if (State == state && !force)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
