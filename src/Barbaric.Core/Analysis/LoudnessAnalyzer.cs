using Barbaric.Core.Audio;
using NAudio.Wave;

namespace Barbaric.Core.Analysis;

/// <summary>
/// How loud a song gets: <see cref="LoudPartLufs"/> is the loudness of its loud parts (see
/// <see cref="LoudnessAnalyzer"/>) and <see cref="PeakDb"/> its loudest sample, in dB below full scale.
/// </summary>
public sealed record LoudnessResult(double LoudPartLufs, double PeakDb);

/// <summary>
/// Measures how loud the loud parts of a song are, so songs can be played equally loud. The whole
/// song is K-weighted (ITU-R BS.1770) and its 3-second short-term loudness taken every 100 ms; the
/// 95th percentile of the non-silent windows is the result. Unlike an average over the whole song, a
/// long quiet intro doesn't make a song that then gets loud count as quiet, and unlike the sample
/// peak, a single drum hit doesn't make a quiet song count as loud.
/// </summary>
public static class LoudnessAnalyzer
{
    /// <summary>Which share of the song's 3-second windows is at most as loud as the result.</summary>
    public const double Percentile = 0.95;

    /// <summary>Windows below this count as silence and are left out.</summary>
    private const double SilenceLufs = -70;

    private const int BlocksPerSecond = 10;
    private const int BlocksPerWindow = 30;

    /// <summary>Decodes the whole file and measures it.</summary>
    /// <returns>The loudness, or null when the song is silent.</returns>
    public static LoudnessResult? AnalyzeFile(string path, CancellationToken cancellationToken = default)
    {
        using var reader = AudioDecoder.Open(path);
        var source = reader.ToSampleProvider();
        var format = source.WaveFormat;
        var meter = new Meter(format.Channels, format.SampleRate);

        var buffer = new float[format.Channels * 8192];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            meter.Add(buffer.AsSpan(0, read));
        }

        return meter.Result();
    }

    /// <summary>Measures interleaved samples.</summary>
    /// <returns>The loudness, or null when the samples are silent.</returns>
    public static LoudnessResult? Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        var meter = new Meter(channels, sampleRate);
        meter.Add(interleaved);
        return meter.Result();
    }

    /// <summary>Takes samples in any number of pieces, as they are decoded.</summary>
    private sealed class Meter
    {
        private readonly int _channels;
        private readonly int _blockFrames;
        private readonly double[] _weights;
        private readonly KWeighting[] _filters;
        private readonly double[] _blockEnergy;

        // The last few blocks' energies, for the 3-second window that ends at the newest one.
        private readonly double[] _recent = new double[BlocksPerWindow];
        private readonly List<double> _windows = [];
        private int _blocks;
        private int _framesInBlock;
        private double _allEnergy;
        private float _peak;

        public Meter(int channels, int sampleRate)
        {
            _channels = channels;
            _blockFrames = Math.Max(1, sampleRate / BlocksPerSecond);
            _weights = ChannelWeights(channels);
            _filters = [.. Enumerable.Range(0, channels).Select(_ => new KWeighting(sampleRate))];
            _blockEnergy = new double[channels];
        }

        public void Add(ReadOnlySpan<float> interleaved)
        {
            for (var i = 0; i + _channels <= interleaved.Length; i += _channels)
            {
                for (var c = 0; c < _channels; c++)
                {
                    var sample = interleaved[i + c];
                    _peak = Math.Max(_peak, Math.Abs(sample));
                    var weighted = _filters[c].Process(sample);
                    _blockEnergy[c] += weighted * weighted;
                }

                if (++_framesInBlock == _blockFrames)
                {
                    EndBlock();
                }
            }
        }

        public LoudnessResult? Result()
        {
            if (_peak == 0)
            {
                return null;
            }

            double loudness;
            if (_blocks >= BlocksPerWindow)
            {
                if (_windows.Count == 0)
                {
                    return null;
                }

                _windows.Sort();
                loudness = _windows[(int)Math.Ceiling(Percentile * _windows.Count) - 1];
            }
            else
            {
                // Shorter than one window: the song as a whole.
                if (_blocks == 0)
                {
                    return null;
                }

                loudness = Lufs(_allEnergy / _blocks);
                if (loudness < SilenceLufs)
                {
                    return null;
                }
            }

            return new LoudnessResult(Math.Round(loudness, 2), Math.Round(20 * Math.Log10(_peak), 2));
        }

        private void EndBlock()
        {
            double energy = 0;
            for (var c = 0; c < _channels; c++)
            {
                energy += _weights[c] * _blockEnergy[c] / _blockFrames;
                _blockEnergy[c] = 0;
            }

            _framesInBlock = 0;
            _allEnergy += energy;
            _recent[_blocks % BlocksPerWindow] = energy;
            _blocks++;

            if (_blocks >= BlocksPerWindow)
            {
                var window = Lufs(_recent.Sum() / BlocksPerWindow);
                if (window >= SilenceLufs)
                {
                    _windows.Add(window);
                }
            }
        }

        private static double Lufs(double energy) => energy <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(energy);

        /// <summary>
        /// BS.1770 channel weights. A mono song plays from both speakers, so it counts twice, like the
        /// same sound in stereo. In 5.1 the LFE channel is left out and the surrounds count 1.41 times.
        /// </summary>
        private static double[] ChannelWeights(int channels) => channels switch
        {
            1 => [2],
            6 => [1, 1, 1, 0, 1.41, 1.41],
            _ => [.. Enumerable.Repeat(1.0, channels)],
        };
    }

    /// <summary>The BS.1770 K-weighting: a high shelf for the head's effect, then a low cut.</summary>
    private sealed class KWeighting
    {
        private readonly Biquad _shelf;
        private readonly Biquad _highPass;

        public KWeighting(double sampleRate)
        {
            // Coefficients derived for any sample rate, as in libebur128; at 48 kHz they match the standard's.
            var k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
            var q = 0.7071752369554196;
            var vh = Math.Pow(10, 3.999843853973347 / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            _shelf = new Biquad(
                (vh + vb * k / q + k * k) / a0,
                2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0,
                (1 - k / q + k * k) / a0);

            k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
            q = 0.5003270373238773;
            a0 = 1 + k / q + k * k;
            _highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        public double Process(double sample) => _highPass.Process(_shelf.Process(sample));
    }

    /// <summary>A second-order filter (transposed direct form II).</summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public double Process(double x)
        {
            var y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
