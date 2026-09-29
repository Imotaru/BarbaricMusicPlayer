using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Barbaric.Core.Audio;

/// <summary>
/// Plays one track at a time. Owns the decoder and the output device, and applies the track's
/// automatic gain (which evens out volume between songs) and the user's own gain, both in dB,
/// combined with a master volume. Plays only the track's range (see <see cref="SetRange"/>), so
/// silence or parts the user cut off are skipped; positions stay in the file's own time.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Func<Task<IWavePlayer>> _outputFactory;
    private readonly object _sync = new();
    private WaveStream? _reader;
    private RangeSampleProvider? _range;
    private GainSampleProvider? _chain;
    private TimeSpan _rangeStart;
    private TimeSpan? _rangeEnd;
    private IWavePlayer? _output;
    private Task<IWavePlayer>? _pendingOutput;
    private int _generation;
    private double _trackGainDb;
    private double _autoGainDb;
    private bool _normalize = true;
    private float _masterVolume = 1f;
    private float _volumeLimit = 1f;

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

    /// <summary>Where the loaded track starts playing.</summary>
    public TimeSpan RangeStart => Clamp(_rangeStart, Duration);

    /// <summary>Where the loaded track stops playing.</summary>
    public TimeSpan RangeEnd => _rangeEnd is { } end ? Clamp(end, Duration) : Duration;

    /// <summary>True from loading or rewinding the track until it starts playing or is moved.</summary>
    public bool AtStart
    {
        get
        {
            lock (_sync)
            {
                return _range?.AtStart ?? true;
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

    /// <summary>The gain that makes the loaded track as loud as the others; applied when <see cref="Normalize"/> is on.</summary>
    public double AutoGainDb
    {
        get => _autoGainDb;
        set
        {
            _autoGainDb = Gain.ClampDb(value);
            ApplyGain();
        }
    }

    /// <summary>Whether <see cref="AutoGainDb"/> applies. The user's <see cref="TrackGainDb"/> always does.</summary>
    public bool Normalize
    {
        get => _normalize;
        set
        {
            _normalize = value;
            ApplyGain();
        }
    }

    /// <summary>Master volume, 0..<see cref="VolumeLimit"/>, applied on top of the track gain.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, _volumeLimit);
            ApplyGain();
        }
    }

    /// <summary>The highest <see cref="MasterVolume"/> can go, 0.01..1. Lowering it below the volume turns the volume down to it.</summary>
    public float VolumeLimit
    {
        get => _volumeLimit;
        set
        {
            _volumeLimit = Math.Clamp(value, MinVolumeLimit, 1f);
            if (_masterVolume > _volumeLimit)
            {
                MasterVolume = _volumeLimit;
            }
        }
    }

    public const float MinVolumeLimit = 0.01f;

    /// <summary>Opens a track, ready to play from <paramref name="start"/> to <paramref name="end"/> (null: the file's end).</summary>
    public void Load(string path, double trackGainDb = 0, double autoGainDb = 0, TimeSpan start = default, TimeSpan? end = null)
    {
        var reader = AudioDecoder.Open(path);
        CloseCurrent();

        lock (_sync)
        {
            _reader = reader;
            _range = new RangeSampleProvider(reader, reader.ToSampleProvider());
        }

        _chain = new GainSampleProvider(_range, _sync);
        _trackGainDb = Gain.ClampDb(trackGainDb);
        _autoGainDb = Gain.ClampDb(autoGainDb);
        ApplyGain();
        CurrentPath = path;
        SetRange(start, end);
        SetState(PlayerState.Stopped, force: true);
    }

    /// <summary>
    /// Sets the part of the track that plays; null <paramref name="end"/> plays to the file's end. Takes
    /// effect while playing; it moves the position only when it falls outside the new range, and to the
    /// start only when the track hasn't started playing.
    /// </summary>
    public void SetRange(TimeSpan start, TimeSpan? end)
    {
        _rangeStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        _rangeEnd = end is { } e && e > _rangeStart ? e : null;
        lock (_sync)
        {
            if (_reader is null || _range is null)
            {
                return;
            }

            _range.SetEnd(_rangeEnd is null ? null : RangeEnd);
            if (_range.AtStart || _reader.CurrentTime < RangeStart)
            {
                Rewind();
            }
        }
    }

    public async Task PlayAsync()
    {
        if (_chain is null || _reader is null)
        {
            throw new InvalidOperationException("No track loaded.");
        }

        if (AtEnd)
        {
            Rewind();
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

    /// <summary>Pauses output and rewinds to the start of the range. Does not raise <see cref="TrackEnded"/>.</summary>
    public void Stop()
    {
        _output?.Pause();
        Rewind();
        SetState(PlayerState.Stopped);
    }

    /// <summary>Goes back to the start of the range, as if the track was just loaded.</summary>
    public void Rewind()
    {
        lock (_sync)
        {
            if (_reader is null || _range is null)
            {
                return;
            }

            _reader.CurrentTime = RangeStart;
            _range.Sync(toStart: true);
        }
    }

    /// <summary>Stops and closes the current file so it can be moved or deleted.</summary>
    public void Unload()
    {
        CloseCurrent();
        SetState(PlayerState.Stopped, force: true);
    }

    /// <summary>Moves within the range; a position outside it goes to its nearest end.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_sync)
        {
            if (_reader is null || _range is null)
            {
                return;
            }

            var start = RangeStart;
            var end = RangeEnd;
            _reader.CurrentTime = position < start ? start : position > end ? end : position;
            _range.Sync(toStart: false);
        }
    }

    public void Dispose() => CloseCurrent();

    /// <summary>Whether the track has played to the end of its range.</summary>
    private bool AtEnd
    {
        get
        {
            lock (_sync)
            {
                return _reader is null || _range is null || _range.Finished || _reader.CurrentTime >= _reader.TotalTime;
            }
        }
    }

    private static TimeSpan Clamp(TimeSpan time, TimeSpan duration) => time > duration ? duration : time;

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
            var auto = _normalize ? Gain.DbToLinear(_autoGainDb) : 1f;
            _chain.Gain = auto * Gain.DbToLinear(_trackGainDb) * _masterVolume;
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
            _range = null;
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
