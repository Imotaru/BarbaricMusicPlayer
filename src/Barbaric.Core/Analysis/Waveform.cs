using Barbaric.Core.Audio;
using NAudio.Wave;

namespace Barbaric.Core.Analysis;

/// <summary>
/// A song's level over time, for drawing: the peak and RMS level of every <see cref="BlockMs"/> ms,
/// each a byte from 0 (<see cref="FloorDb"/> or quieter) to 255 (full scale) on a dB scale, so quiet
/// fades and silence stay visible.
/// </summary>
public sealed record Waveform(long DurationMs, int BlockMs, byte[] Peaks, byte[] Rms)
{
    public const double FloorDb = -60;

    public const int DefaultBlockMs = 10;

    /// <summary>Decodes the whole file.</summary>
    public static Waveform FromFile(string path, CancellationToken cancellationToken = default)
    {
        using var reader = AudioDecoder.Open(path);
        var source = reader.ToSampleProvider();
        var format = source.WaveFormat;
        var builder = new Builder(format.Channels, format.SampleRate, DefaultBlockMs);

        var buffer = new float[format.Channels * 8192];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(buffer.AsSpan(0, read));
        }

        return builder.Build();
    }

    /// <summary>Measures interleaved samples.</summary>
    public static Waveform FromSamples(ReadOnlySpan<float> interleaved, int channels, int sampleRate, int blockMs = DefaultBlockMs)
    {
        var builder = new Builder(channels, sampleRate, blockMs);
        builder.Add(interleaved);
        return builder.Build();
    }

    private static byte ToByte(double level)
    {
        if (level <= 0)
        {
            return 0;
        }

        var db = 20 * Math.Log10(level);
        return (byte)Math.Clamp(Math.Round((db - FloorDb) / -FloorDb * 255), 0, 255);
    }

    private sealed class Builder(int channels, int sampleRate, int blockMs)
    {
        private readonly int _blockFrames = Math.Max(1, sampleRate * blockMs / 1000);
        private readonly List<byte> _peaks = [];
        private readonly List<byte> _rms = [];
        private float _peak;
        private double _power;
        private int _framesInBlock;
        private long _frames;

        public void Add(ReadOnlySpan<float> interleaved)
        {
            for (var i = 0; i + channels <= interleaved.Length; i += channels)
            {
                for (var c = 0; c < channels; c++)
                {
                    var sample = interleaved[i + c];
                    _peak = Math.Max(_peak, Math.Abs(sample));
                    _power += sample * sample;
                }

                _frames++;
                if (++_framesInBlock == _blockFrames)
                {
                    EndBlock();
                }
            }
        }

        public Waveform Build()
        {
            if (_framesInBlock > 0)
            {
                EndBlock();
            }

            return new Waveform((long)Math.Round(_frames * 1000.0 / sampleRate), blockMs, [.. _peaks], [.. _rms]);
        }

        private void EndBlock()
        {
            _peaks.Add(ToByte(_peak));
            _rms.Add(ToByte(Math.Sqrt(_power / (_framesInBlock * (double)channels))));
            _peak = 0;
            _power = 0;
            _framesInBlock = 0;
        }
    }
}
