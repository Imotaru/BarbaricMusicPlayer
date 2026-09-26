using Barbaric.Core.Audio;
using NAudio.Wave;

namespace Barbaric.Core.Tests.Audio;

public class GainTests
{
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-6, 0.501)]
    [InlineData(6, 1.995)]
    [InlineData(-100, 0.063)] // clamped to MinDb (-24)
    [InlineData(100, 3.981)] // clamped to MaxDb (+12)
    public void DbToLinear_ConvertsAndClamps(double db, double expected)
    {
        Assert.Equal(expected, Gain.DbToLinear(db), precision: 3);
    }

    [Fact]
    public void GainSampleProvider_ScalesAndLimitsSamples()
    {
        var source = new ConstantSampleProvider([0.25f, -0.25f, 0.8f, -0.8f]);
        var provider = new GainSampleProvider(source, new object()) { Gain = 2f };

        var buffer = new float[4];
        var read = provider.Read(buffer);

        Assert.Equal(4, read);
        Assert.Equal([0.5f, -0.5f, 1f, -1f], buffer);
    }

    private sealed class ConstantSampleProvider(float[] samples) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length);
            samples.AsSpan(0, count).CopyTo(buffer);
            return count;
        }
    }
}
