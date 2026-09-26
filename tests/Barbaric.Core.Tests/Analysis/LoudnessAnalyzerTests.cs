using Barbaric.Core.Analysis;

namespace Barbaric.Core.Tests.Analysis;

public class LoudnessAnalyzerTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void SineInBothChannels_MeasuresItsLevel(int rate)
    {
        // BS.1770: a 1 kHz sine at -20 dBFS in both channels is -20 LUFS.
        var tone = Tone(rate, seconds: 10, 997, dbfs: -20);

        var result = LoudnessAnalyzer.Analyze(Interleave(tone, tone), channels: 2, rate);

        Assert.NotNull(result);
        Assert.Equal(-20, result.LoudPartLufs, 0.1);
        Assert.Equal(-20, result.PeakDb, 0.05);
    }

    [Fact]
    public void Mono_CountsAsTheSameSoundFromBothSpeakers()
    {
        var tone = Tone(44100, seconds: 10, 997, dbfs: -20);

        var mono = LoudnessAnalyzer.Analyze(tone, channels: 1, 44100);
        var stereo = LoudnessAnalyzer.Analyze(Interleave(tone, tone), channels: 2, 44100);

        Assert.Equal(stereo!.LoudPartLufs, mono!.LoudPartLufs, 0.01);
    }

    [Fact]
    public void QuietIntroThenLoud_MeasuresTheLoudPart()
    {
        float[] song = [.. Tone(44100, seconds: 60, 997, dbfs: -40), .. Tone(44100, seconds: 20, 997, dbfs: -10)];

        var result = LoudnessAnalyzer.Analyze(song, channels: 1, 44100);

        // Averaged over the whole song it would come out around -16.
        Assert.Equal(-10, result!.LoudPartLufs, 0.5);
    }

    [Fact]
    public void SingleLoudHit_DoesNotMakeAQuietSongLoud()
    {
        var song = Tone(44100, seconds: 90, 997, dbfs: -30);
        for (var i = 0; i < 44100 / 5; i++)
        {
            song[44100 * 45 + i] = i % 2 == 0 ? 1f : -1f;
        }

        var result = LoudnessAnalyzer.Analyze(song, channels: 1, 44100);

        Assert.Equal(-30, result!.LoudPartLufs, 0.5);
        Assert.Equal(0, result.PeakDb, 0.01);
    }

    [Fact]
    public void ShorterThanAWindow_MeasuresTheWholeSong()
    {
        var result = LoudnessAnalyzer.Analyze(Tone(44100, seconds: 1, 997, dbfs: -20), channels: 1, 44100);

        Assert.Equal(-20, result!.LoudPartLufs, 0.2);
    }

    [Fact]
    public void Silence_HasNoLoudness()
    {
        Assert.Null(LoudnessAnalyzer.Analyze(new float[44100 * 10], channels: 1, 44100));
        Assert.Null(LoudnessAnalyzer.Analyze([], channels: 2, 44100));
    }

    [Fact]
    public void Bass_CountsLessThanMids()
    {
        // K-weighting rolls off the lows, so a 40 Hz tone measures quieter than 1 kHz at the same level.
        var bass = LoudnessAnalyzer.Analyze(Tone(44100, seconds: 10, 40, dbfs: -20), channels: 1, 44100);
        var mid = LoudnessAnalyzer.Analyze(Tone(44100, seconds: 10, 997, dbfs: -20), channels: 1, 44100);

        Assert.True(bass!.LoudPartLufs < mid!.LoudPartLufs - 1);
    }

    private static float[] Tone(int rate, double seconds, double frequency, double dbfs)
    {
        var amplitude = Math.Pow(10, dbfs / 20);
        return [.. Enumerable.Range(0, (int)(rate * seconds))
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)))];
    }

    private static float[] Interleave(float[] left, float[] right)
    {
        var result = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            result[2 * i] = left[i];
            result[2 * i + 1] = right[i];
        }

        return result;
    }
}
