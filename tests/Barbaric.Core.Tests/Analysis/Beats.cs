using NAudio.Wave;

namespace Barbaric.Core.Tests.Analysis;

/// <summary>Synthetic rhythm tracks with a known tempo.</summary>
internal static class Beats
{
    public const int SampleRate = 44100;

    /// <summary>A metronome: short decaying 1.5 kHz clicks over a faint noise floor.</summary>
    public static float[] Clicks(double bpm, double seconds = 90, int seed = 1)
    {
        var samples = Noise(seconds, 0.005, seed);
        for (var beat = 0; beat * 60 / bpm < seconds; beat++)
        {
            AddBurst(samples, beat * 60 / bpm, 1500, 0.5, 0.01);
        }

        return samples;
    }

    /// <summary>A low kick on every beat and a quieter high hat halfway between beats.</summary>
    public static float[] KickAndOffbeatHats(double bpm, double hatGainDb, double seconds = 90, int seed = 2)
    {
        var samples = Noise(seconds, 0.005, seed);
        var random = new Random(seed + 100);
        var hat = 0.5 * Math.Pow(10, hatGainDb / 20);
        for (var beat = 0; beat * 60 / bpm < seconds; beat++)
        {
            var at = beat * 60 / bpm;
            AddBurst(samples, at, 70, 0.8, 0.08);
            AddNoiseBurst(samples, at + 30 / bpm, hat, 0.02, random);
        }

        return samples;
    }

    /// <summary>Two different drums of equal weight alternating, each landing on every other beat.</summary>
    public static float[] AlternatingDrums(double bpm, double seconds = 90, int seed = 4)
    {
        var samples = Noise(seconds, 0.005, seed);
        var random = new Random(seed + 100);
        for (var beat = 0; beat * 60 / bpm < seconds; beat++)
        {
            if (beat % 2 == 0)
            {
                AddNoiseBurst(samples, beat * 60 / bpm, 0.5, 0.03, random);
            }
            else
            {
                AddNoiseBurst(samples, beat * 60 / bpm, 0.5, 0.05, random);
            }
        }

        return samples;
    }

    /// <summary>A backbeat: kick on every beat, snare on 2 and 4, quiet hats on the offbeats.</summary>
    public static float[] Groove(double bpm, double seconds = 90, int seed = 5)
    {
        var samples = Noise(seconds, 0.005, seed);
        var random = new Random(seed + 100);
        var beat = 60 / bpm;
        for (var n = 0; n * beat < seconds; n++)
        {
            AddBurst(samples, n * beat, 55, 0.7, 0.07);
            AddNoiseBurst(samples, n * beat + beat / 2, 0.08, 0.015, random);
            if (n % 2 == 1)
            {
                AddNoiseBurst(samples, n * beat, 0.3, 0.04, random);
            }
        }

        return samples;
    }

    public static float[] Noise(double seconds, double level, int seed = 3)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2 - 1) * level);
        }

        return samples;
    }

    public static void WriteWav(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static void AddBurst(float[] samples, double atSeconds, double frequency, double amplitude, double decaySeconds)
    {
        var start = (int)Math.Round(atSeconds * SampleRate);
        var length = (int)(decaySeconds * 5 * SampleRate);
        for (var i = 0; i < length && start + i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            samples[start + i] += (float)(amplitude * Math.Exp(-t / decaySeconds) * Math.Sin(2 * Math.PI * frequency * t));
        }
    }

    private static void AddNoiseBurst(float[] samples, double atSeconds, double amplitude, double decaySeconds, Random random)
    {
        var start = (int)Math.Round(atSeconds * SampleRate);
        var length = (int)(decaySeconds * 5 * SampleRate);
        for (var i = 0; i < length && start + i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            samples[start + i] += (float)(amplitude * Math.Exp(-t / decaySeconds) * (random.NextDouble() * 2 - 1));
        }
    }
}
