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

    [Theory]
    [InlineData(null, null, 0.0)] // not measured: played as is
    [InlineData(-8.0, -0.1, -6.0)] // loud song turned down
    [InlineData(-16.0, -10.0, 2.0)] // quiet song turned up, with room to spare
    [InlineData(-16.0, -2.0, 1.5)] // turned up only until its peak is 0.5 dB below full scale
    [InlineData(-16.0, 0.0, 0.0)] // no room to turn up, but never turned down for it
    [InlineData(-60.0, -40.0, 12.0)] // clamped to MaxDb
    [InlineData(20.0, 0.0, -24.0)] // clamped to MinDb
    public void AutoGainDb_BringsLoudPartsToTheTarget(double? loudness, double? peak, double expected)
    {
        Assert.Equal(expected, Gain.AutoGainDb(loudness, peak), precision: 2);
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
