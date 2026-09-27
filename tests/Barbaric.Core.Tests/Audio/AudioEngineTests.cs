using Barbaric.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Barbaric.Core.Tests.Audio;

public sealed class AudioEngineTests : IDisposable
{
    private static readonly TimeSpan ToneLength = TimeSpan.FromSeconds(2);

    private readonly string _dir = Directory.CreateTempSubdirectory("barbaric-tests-").FullName;
    private readonly FakeWavePlayer _output = new();
    private readonly AudioEngine _engine;
    private readonly string _tonePath;

    public AudioEngineTests()
    {
        _engine = new AudioEngine(() => Task.FromResult<IWavePlayer>(_output));
        _tonePath = Path.Combine(_dir, "tone.wav");
        var tone = new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.5 }.Take(ToneLength);
        WaveFileWriter.CreateWaveFile16(_tonePath, tone);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_ReadsDurationAndStartsStopped()
    {
        _engine.Load(_tonePath);

        Assert.Equal(_tonePath, _engine.CurrentPath);
        Assert.Equal(PlayerState.Stopped, _engine.State);
        Assert.Equal(ToneLength.TotalSeconds, _engine.Duration.TotalSeconds, precision: 1);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
    }

    [Fact]
    public void Load_MissingFile_ThrowsAndKeepsCurrentTrack()
    {
        _engine.Load(_tonePath);

        Assert.Throws<FileNotFoundException>(() => _engine.Load(Path.Combine(_dir, "missing.mp3")));
        Assert.Equal(_tonePath, _engine.CurrentPath);
    }

    [Fact]
    public async Task Play_WithoutTrack_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.PlayAsync());
    }

    [Fact]
    public async Task PlayPauseToggle_UpdateStateAndRaiseEvents()
    {
        _engine.Load(_tonePath);
        var changes = 0;
        _engine.StateChanged += (_, _) => changes++;

        await _engine.PlayAsync();
        Assert.Equal(PlayerState.Playing, _engine.State);
        Assert.Equal(PlaybackState.Playing, _output.PlaybackState);

        await _engine.TogglePlayPauseAsync();
        Assert.Equal(PlayerState.Paused, _engine.State);

        await _engine.TogglePlayPauseAsync();
        Assert.Equal(PlayerState.Playing, _engine.State);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Seek_ClampsToTrackBounds()
    {
        _engine.Load(_tonePath);

        _engine.Seek(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _engine.Position.TotalSeconds, precision: 2);

        _engine.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, _engine.Position);

        _engine.Seek(TimeSpan.FromMinutes(5));
        Assert.Equal(_engine.Duration, _engine.Position);
    }

    [Fact]
    public async Task PlayingToEnd_RaisesTrackEnded_AndReplayRestartsFromTop()
    {
        _engine.Load(_tonePath);
        var ended = false;
        _engine.TrackEnded += (_, _) => ended = true;

        await _engine.PlayAsync();
        _output.DrainToEnd();

        Assert.True(ended);
        Assert.Equal(PlayerState.Stopped, _engine.State);

        await _engine.PlayAsync();
        Assert.True(_engine.Position < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task TrackGain_IsAppliedToOutput()
    {
        _engine.Load(_tonePath);
        await _engine.PlayAsync();
        var full = Peak(_output.DrainToEnd());

        _engine.Load(_tonePath, trackGainDb: -6);
        await _engine.PlayAsync();
        var reduced = Peak(_output.DrainToEnd());

        Assert.Equal(0.5, full, precision: 2);
        Assert.Equal(0.5 * 0.501, reduced, precision: 2);
    }

    [Fact]
    public async Task AutoGain_AddsToTrackGain_UnlessNormalizeIsOff()
    {
        _engine.Load(_tonePath, trackGainDb: -3, autoGainDb: -3);
        await _engine.PlayAsync();
        var normalized = Peak(_output.DrainToEnd());

        _engine.Normalize = false;
        _engine.Load(_tonePath, trackGainDb: -3, autoGainDb: -3);
        await _engine.PlayAsync();
        var plain = Peak(_output.DrainToEnd());

        Assert.Equal(0.5 * 0.501, normalized, precision: 2);
        Assert.Equal(0.5 * 0.708, plain, precision: 2);
    }

    [Fact]
    public void MasterVolume_CannotExceedTheVolumeLimit()
    {
        _engine.VolumeLimit = 0.3f;
        _engine.MasterVolume = 0.8f;

        Assert.Equal(0.3f, _engine.MasterVolume);
    }

    [Fact]
    public void LoweringTheVolumeLimit_TurnsTheVolumeDown_RaisingItLeavesTheVolume()
    {
        _engine.MasterVolume = 0.5f;

        _engine.VolumeLimit = 0.1f;
        Assert.Equal(0.1f, _engine.MasterVolume);

        _engine.VolumeLimit = 1f;
        Assert.Equal(0.1f, _engine.MasterVolume);

        _engine.VolumeLimit = 0f;
        Assert.Equal(AudioEngine.MinVolumeLimit, _engine.VolumeLimit);
    }

    [Fact]
    public async Task LoadingWhileDeviceStarts_DiscardsTheStaleDevice()
    {
        var slowDevice = new TaskCompletionSource<IWavePlayer>();
        var staleOutput = new FakeWavePlayer();
        using var engine = new AudioEngine(() => slowDevice.Task);
        engine.Load(_tonePath);

        var play = engine.PlayAsync();
        engine.Load(_tonePath);
        slowDevice.SetResult(staleOutput);
        await play;

        Assert.Equal(PlayerState.Stopped, engine.State);
        Assert.NotEqual(PlaybackState.Playing, staleOutput.PlaybackState);
        Assert.True(staleOutput.Disposed);
    }

    [Fact]
    public async Task DeviceFailure_IsReported_AndNextPlayRetries()
    {
        var attempts = 0;
        using var engine = new AudioEngine(() => ++attempts == 1
            ? Task.FromException<IWavePlayer>(new InvalidOperationException("no device"))
            : Task.FromResult<IWavePlayer>(_output));
        engine.Load(_tonePath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());
        await engine.PlayAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(PlayerState.Playing, engine.State);
    }

    private static float Peak(byte[] ieeeFloatBytes)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(ieeeFloatBytes);
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }
}
