using NAudio.Wave;

namespace Barbaric.Core.Audio;

/// <summary>
/// Applies a linear gain to the source and serializes reads with seeks via a shared lock,
/// so the playback thread never reads from the decoder while it is being repositioned.
/// </summary>
internal sealed class GainSampleProvider(ISampleProvider source, object sync) : ISampleProvider
{
    private volatile float _gain = 1f;

    public float Gain
    {
        get => _gain;
        set => _gain = value;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int read;
        lock (sync)
        {
            read = source.Read(buffer);
        }

        var gain = _gain;
        if (gain != 1f)
        {
            var samples = buffer[..read];
            for (var i = 0; i < samples.Length; i++)
            {
                // Hard-limit boosted samples so positive gain can't overdrive the output.
                samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
            }
        }

        return read;
    }
}
