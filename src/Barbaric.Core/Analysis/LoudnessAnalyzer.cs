using Barbaric.Core.Audio;
using NAudio.Wave;

namespace Barbaric.Core.Analysis;

/// <summary>
/// How loud a song gets: <see cref="LoudPartLufs"/> is the loudness of its loud parts (see
/// <see cref="LoudnessAnalyzer"/>) and <see cref="PeakDb"/> its loudest sample, in dB below full scale.
/// <see cref="Edges"/> are where its sound starts and ends at each <see cref="Silence"/> threshold.
/// </summary>
public sealed record LoudnessResult(double LoudPartLufs, double PeakDb, IReadOnlyList<SilenceEdge>? Edges = null);

/// <summary>
/// Measures how loud the loud parts of a song are, so songs can be played equally loud. The whole
/// song is K-weighted (ITU-R BS.1770) and its 3-second short-term loudness taken every 100 ms; the
/// 95th percentile of the non-silent windows is the result. Unlike an average over the whole song, a
/// long quiet intro doesn't make a song that then gets loud count as quiet, and unlike the sample
/// peak, a single drum hit doesn't make a quiet song count as loud.
/// The same pass finds the silence before and after the song, from the unweighted level of 10 ms blocks.
/// </summary>
public static class LoudnessAnalyzer
{
    /// <summary>Which share of the song's 3-second windows is at most as loud as the result.</summary>
    public const double Percentile = 0.95;

    /// <summary>Windows below this count as silence and are left out.</summary>
    private const double SilenceLufs = -70;

    private const int BlocksPerSecond = 10;
    private const int BlocksPerWindow = 30;

    private const int EdgeBlocksPerSecond = 100;

    /// <summary>Songs whose sound would last less than this at a threshold aren't trimmed at it.</summary>
    private const long MinSoundMs = 1000;

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
        private readonly int _sampleRate;
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

        // Silence edges: the first and last 10 ms block at or above each threshold's power.
        private readonly int _edgeBlockFrames;
        private readonly double[] _thresholds = [.. Silence.Thresholds.Select(db => Math.Pow(10, db / 10.0))];
        private readonly long[] _firstSound;
        private readonly long[] _lastSound;
        private double _edgePower;
        private int _framesInEdgeBlock;
        private long _edgeBlocks;
        private long _frames;

        public Meter(int channels, int sampleRate)
        {
            _channels = channels;
            _sampleRate = sampleRate;
            _edgeBlockFrames = Math.Max(1, sampleRate / EdgeBlocksPerSecond);
            _firstSound = [.. _thresholds.Select(_ => -1L)];
            _lastSound = new long[_thresholds.Length];
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
                    _edgePower += sample * sample;
                    var weighted = _filters[c].Process(sample);
                    _blockEnergy[c] += weighted * weighted;
                }

                _frames++;
                if (++_framesInBlock == _blockFrames)
                {
                    EndBlock();
                }

                if (++_framesInEdgeBlock == _edgeBlockFrames)
                {
                    EndEdgeBlock();
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

            return new LoudnessResult(Math.Round(loudness, 2), Math.Round(20 * Math.Log10(_peak), 2), Edges());
        }

        private IReadOnlyList<SilenceEdge> Edges()
        {
            if (_framesInEdgeBlock > 0)
            {
                EndEdgeBlock();
            }

            var totalMs = Ms(_frames);
            var edges = new List<SilenceEdge>(_thresholds.Length);
            for (var i = 0; i < _thresholds.Length; i++)
            {
                var db = Silence.Thresholds[i];
                var start = Math.Max(0, Ms(_firstSound[i] * _edgeBlockFrames) - Silence.LeadMs);
                var end = Math.Min(totalMs, Ms(Math.Min(_frames, (_lastSound[i] + 1) * _edgeBlockFrames)) + Silence.TailMs);

                // Nothing, or next to nothing, that loud: the whole song is kept rather than skipped.
                edges.Add(_firstSound[i] < 0 || end - start < MinSoundMs
                    ? new SilenceEdge(db, 0, totalMs)
                    : new SilenceEdge(db, start, end));
            }

            return edges;
        }

        private long Ms(long frames) => (long)Math.Round(frames * 1000.0 / _sampleRate);

        private void EndEdgeBlock()
        {
            var power = _edgePower / (_framesInEdgeBlock * (double)_channels);
            for (var i = 0; i < _thresholds.Length; i++)
            {
                if (power >= _thresholds[i])
                {
                    if (_firstSound[i] < 0)
                    {
                        _firstSound[i] = _edgeBlocks;
                    }

                    _lastSound[i] = _edgeBlocks;
                }
            }

            _edgePower = 0;
            _framesInEdgeBlock = 0;
            _edgeBlocks++;
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
