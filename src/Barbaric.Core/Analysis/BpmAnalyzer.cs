using Barbaric.Core.Audio;
using NAudio.Dsp;
using NAudio.Wave;

namespace Barbaric.Core.Analysis;

/// <summary>A detected tempo. <see cref="Confidence"/> runs from 0 (no beat) to 1 (metronome-steady).</summary>
public sealed record BpmResult(double Bpm, double Confidence);

/// <summary>
/// Estimates a song's tempo: an onset-strength envelope (spectral flux) of about a minute from the
/// middle of the track, autocorrelated over the 60–200 BPM range.
/// </summary>
public static class BpmAnalyzer
{
    public const double MinBpm = 60;
    public const double MaxBpm = 200;

    /// <summary>Results below this are reported as "no clear beat".</summary>
    public const double MinConfidence = 0.1;

    private const double TargetRate = 11025;
    private const int FrameSize = 512;
    private const int FrameBits = 9;
    private const int Hop = 64;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinLength = TimeSpan.FromSeconds(10);

    /// <summary>Lower edges (Hz) of the frequency bands above the first: lows, mids and highs.</summary>
    private static readonly double[] BandEdges = [200, 2000];

    /// <summary>
    /// How much each band counts in the mix, lows first. Tuned on the synthetic grooves in the tests:
    /// weighting the lows keeps a backbeat (snare on 2 and 4) and offbeat hats from halving or
    /// doubling the tempo.
    /// </summary>
    private static readonly double[] BandWeights = [1, 0.35, 0.2];

    /// <summary>Scales FFT magnitudes before log compression; lower keeps loud and soft onsets further apart.</summary>
    private const double Compression = 20;

    /// <summary>A beat twice as fast wins when its periodicity is at least this share of the slower one's.</summary>
    private const double DoubleTempoRatio = 0.85;

    private static readonly float[] Hann = Enumerable.Range(0, FrameSize)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1))))
        .ToArray();

    /// <summary>Decodes up to a minute from the middle of the file and analyzes it.</summary>
    /// <returns>The tempo, or null when the song is too short, silent, or has no clear beat.</returns>
    public static BpmResult? AnalyzeFile(string path, CancellationToken cancellationToken = default)
    {
        float[] samples;
        double rate;
        using (var reader = AudioDecoder.Open(path))
        {
            (samples, rate) = ReadMiddle(reader, cancellationToken);
        }

        return Analyze(samples, rate, cancellationToken);
    }

    /// <summary>Analyzes mono samples at any rate; rates well above ~11 kHz are decimated first.</summary>
    public static BpmResult? Analyze(ReadOnlySpan<float> samples, double sampleRate, CancellationToken cancellationToken = default)
    {
        var factor = Math.Max(1, (int)Math.Round(sampleRate / TargetRate));
        if (factor > 1)
        {
            samples = Decimate(samples, factor);
            sampleRate /= factor;
        }

        if (samples.Length < MinLength.TotalSeconds * sampleRate || IsSilent(samples))
        {
            return null;
        }

        var envelope = OnsetEnvelope(samples, sampleRate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FindTempo(envelope, sampleRate / Hop);
    }

    private static (float[] Samples, double Rate) ReadMiddle(WaveStream reader, CancellationToken cancellationToken)
    {
        if (reader.TotalTime > Window)
        {
            reader.CurrentTime = (reader.TotalTime - Window) / 2;
        }

        var source = reader.ToSampleProvider();
        var channels = source.WaveFormat.Channels;
        var rate = source.WaveFormat.SampleRate;
        var factor = Math.Max(1, (int)Math.Round(rate / TargetRate));
        var wanted = (long)(Window.TotalSeconds * rate);

        // Downmix and decimate while reading, so a minute of 48 kHz stereo never sits in memory.
        var output = new List<float>((int)(wanted / factor) + 1);
        var buffer = new float[channels * factor * 4096];
        double sum = 0;
        int inBlock = 0;
        long frames = 0;
        while (frames < wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i + channels <= read && frames < wanted; i += channels, frames++)
            {
                for (var c = 0; c < channels; c++)
                {
                    sum += buffer[i + c];
                }

                if (++inBlock == factor)
                {
                    output.Add((float)(sum / (factor * channels)));
                    sum = 0;
                    inBlock = 0;
                }
            }
        }

        return ([.. output], (double)rate / factor);
    }

    // Averaging blocks is a crude low-pass, but aliasing doesn't matter for finding onsets.
    private static float[] Decimate(ReadOnlySpan<float> samples, int factor)
    {
        var output = new float[samples.Length / factor];
        for (var i = 0; i < output.Length; i++)
        {
            var sum = 0f;
            for (var j = 0; j < factor; j++)
            {
                sum += samples[i * factor + j];
            }

            output[i] = sum / factor;
        }

        return output;
    }

    private static bool IsSilent(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (var s in samples)
        {
            energy += s * s;
        }

        return Math.Sqrt(energy / samples.Length) < 1e-4;
    }

    /// <summary>
    /// Spectral flux: how much louder each frame got than the one before, per frequency band, with
    /// the slow-moving part removed so only onsets remain. Each band is scaled to the same strength
    /// before they are mixed, so a kick (a few low bins) isn't drowned out by a snare or hats (many
    /// high bins); the mix then leans towards the low end, where the beat usually lives.
    /// The result has zero mean.
    /// </summary>
    private static double[] OnsetEnvelope(ReadOnlySpan<float> samples, double sampleRate, CancellationToken cancellationToken)
    {
        var frames = (samples.Length - FrameSize) / Hop + 1;
        var bins = FrameSize / 2;
        var band = new int[bins];
        for (var b = 0; b < bins; b++)
        {
            var hz = b * sampleRate / FrameSize;
            band[b] = BandEdges.Count(edge => hz >= edge);
        }

        var flux = new double[BandWeights.Length][];
        for (var k = 0; k < flux.Length; k++)
        {
            flux[k] = new double[frames];
        }

        var fft = new Complex[FrameSize];
        var previous = new double[bins];
        var current = new double[bins];
        for (var f = 0; f < frames; f++)
        {
            if ((f & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offset = f * Hop;
            for (var i = 0; i < FrameSize; i++)
            {
                fft[i].X = samples[offset + i] * Hann[i];
                fft[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FrameBits, fft);

            for (var b = 1; b < bins; b++)
            {
                var magnitude = Math.Sqrt(fft[b].X * fft[b].X + fft[b].Y * fft[b].Y);
                current[b] = Math.Log(1 + Compression * magnitude);
                if (f > 0)
                {
                    flux[band[b]][f] += Math.Max(0, current[b] - previous[b]);
                }
            }

            (previous, current) = (current, previous);
        }

        var onsets = flux.Select(x => Detrend(x, (int)(sampleRate / Hop / 2))).ToArray();
        var levels = onsets.Select(x => Math.Sqrt(x.Sum(v => v * v) / frames)).ToArray();

        // Near-empty bands are scaled against the loudest one rather than blown up to full strength.
        var floor = Math.Max(levels.Max() * 0.1, 1e-12);
        var envelope = new double[frames];
        for (var k = 0; k < onsets.Length; k++)
        {
            var scale = BandWeights[k] / Math.Max(levels[k], floor);
            for (var i = 0; i < frames; i++)
            {
                envelope[i] += onsets[k][i] * scale;
            }
        }

        var mean = envelope.Average();
        for (var i = 0; i < frames; i++)
        {
            envelope[i] -= mean;
        }

        return envelope;
    }

    /// <summary>Subtracts a moving average (radius in frames) and keeps only what pokes above it.</summary>
    private static double[] Detrend(double[] x, int radius)
    {
        var prefix = new double[x.Length + 1];
        for (var i = 0; i < x.Length; i++)
        {
            prefix[i + 1] = prefix[i] + x[i];
        }

        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            int from = Math.Max(0, i - radius), to = Math.Min(x.Length, i + radius + 1);
            result[i] = Math.Max(0, x[i] - (prefix[to] - prefix[from]) / (to - from));
        }

        return result;
    }

    private static BpmResult? FindTempo(double[] envelope, double frameRate)
    {
        var minLag = (int)Math.Floor(frameRate * 60 / MaxBpm);
        var maxLag = (int)Math.Ceiling(frameRate * 60 / MinBpm);

        // Room for the refinement to look at the fourth multiple of the slowest tempo.
        var acf = Autocorrelate(envelope, Math.Min(envelope.Length / 2, 4 * maxLag + 8));
        if (acf[0] <= 0 || acf.Length <= maxLag + 1)
        {
            return null;
        }

        // Best lag, nudged towards moderate tempos.
        var best = -1;
        var bestScore = 0.0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            var score = acf[lag] * TempoPrior(60 * frameRate / lag);
            if (score > bestScore)
            {
                best = lag;
                bestScore = score;
            }
        }

        if (best < 0)
        {
            return null;
        }

        // Octave errors: a steady pulse at half the period means the beat is really twice as fast.
        while (60 * frameRate / (best / 2.0) <= MaxBpm * 1.02)
        {
            var half = PeakNear(acf, best / 2.0, 1);
            if (acf[half] < DoubleTempoRatio * acf[best])
            {
                break;
            }

            best = half;
        }

        var baseline = 0.0;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            baseline += acf[lag];
        }

        baseline /= maxLag - minLag + 1;
        var confidence = Math.Clamp((acf[best] - baseline) / (acf[0] - baseline), 0, 1);
        if (confidence < MinConfidence)
        {
            return null;
        }

        var period = RefinePeriod(acf, best);
        return new BpmResult(Math.Round(60 * frameRate / period, 2), Math.Round(confidence, 3));
    }

    /// <summary>Unbiased autocorrelation, so a steady pulse scores the same at every multiple of its period.</summary>
    private static double[] Autocorrelate(double[] x, int maxLag)
    {
        var acf = new double[maxLag + 1];
        for (var lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < x.Length; i++)
            {
                sum += x[i] * x[i + lag];
            }

            acf[lag] = sum / (x.Length - lag);
        }

        return acf;
    }

    /// <summary>
    /// A log-normal preference for tempos around 130 BPM, one octave wide. It mainly settles
    /// "beat or half-bar?" when the two are close.
    /// </summary>
    private static double TempoPrior(double bpm)
    {
        var octaves = Math.Log2(bpm / 130);
        return Math.Exp(-0.5 * octaves * octaves);
    }

    private static int PeakNear(double[] acf, double center, int radius)
    {
        var from = Math.Max(1, (int)Math.Round(center) - radius);
        var to = Math.Min(acf.Length - 1, (int)Math.Round(center) + radius);
        var peak = from;
        for (var i = from + 1; i <= to; i++)
        {
            if (acf[i] > acf[peak])
            {
                peak = i;
            }
        }

        return peak;
    }

    /// <summary>
    /// Sub-frame period: the peaks near 1×, 2×, 3× and 4× the lag, each placed by parabolic
    /// interpolation, fitted with a line through the origin. Later multiples pin the period down finer.
    /// </summary>
    private static double RefinePeriod(double[] acf, int lag)
    {
        double estimate = lag, weighted = 0, squares = 0;
        for (var k = 1; k <= 4; k++)
        {
            var center = k * estimate;
            if (center + 3 >= acf.Length)
            {
                break;
            }

            var peak = PeakNear(acf, center, 2);
            if (peak <= 0 || peak >= acf.Length - 1)
            {
                break;
            }

            double left = acf[peak - 1], middle = acf[peak], right = acf[peak + 1];
            var curvature = left - 2 * middle + right;
            var offset = curvature < 0 ? Math.Clamp(0.5 * (left - right) / curvature, -0.5, 0.5) : 0;

            weighted += k * (peak + offset);
            squares += k * k;
            estimate = weighted / squares;
        }

        return estimate;
    }
}
