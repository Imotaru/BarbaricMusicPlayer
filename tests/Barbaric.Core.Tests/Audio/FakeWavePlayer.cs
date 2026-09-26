using NAudio.Wave;

namespace Barbaric.Core.Tests.Audio;

/// <summary>Output device stand-in that lets tests drain the audio chain synchronously.</summary>
internal sealed class FakeWavePlayer : IWavePlayer
{
    private IWaveProvider? _source;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public float Volume { get; set; } = 1f;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

    public WaveFormat? OutputWaveFormat => _source?.WaveFormat;

    public void Init(IWaveProvider waveProvider) => _source = waveProvider;

    public void Play() => PlaybackState = PlaybackState.Playing;

    public void Pause() => PlaybackState = PlaybackState.Paused;

    public void Stop() => PlaybackState = PlaybackState.Stopped;

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;

    /// <summary>Reads the rest of the stream the way a device would, then reports the end.</summary>
    public byte[] DrainToEnd()
    {
        var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = _source!.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        return output.ToArray();
    }
}
