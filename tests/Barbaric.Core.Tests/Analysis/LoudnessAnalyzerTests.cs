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

    [Fact]
    public void SilenceAroundTheSong_IsFound_WithSomeRoomLeft()
    {
        float[] song = [.. new float[44100 * 2], .. Tone(44100, seconds: 3, 997, dbfs: -20), .. new float[44100 * 2]];

        var result = LoudnessAnalyzer.Analyze(song, channels: 1, 44100);

        Assert.Equal(Silence.Thresholds, result!.Edges!.Select(e => e.Db));
        Assert.All(result.Edges!, edge =>
        {
            Assert.Equal(2000 - Silence.LeadMs, edge.StartMs);
            Assert.Equal(5000 + Silence.TailMs, edge.EndMs);
        });
    }

    [Fact]
    public void QuietNoise_CountsAsSilenceOnlyBelowTheThreshold()
    {
        // A -52 dBFS peak sine has an RMS level of -55 dB.
        float[] song = [.. Tone(44100, seconds: 2, 3000, dbfs: -52), .. Tone(44100, seconds: 3, 997, dbfs: -20)];

        var edges = LoudnessAnalyzer.Analyze(song, channels: 1, 44100)!.Edges;

        Assert.Equal(0, Silence.At(edges, -60)!.StartMs);
        Assert.Equal(2000 - Silence.LeadMs, Silence.At(edges, -50)!.StartMs);
    }

    [Fact]
    public void SongWithoutSilence_IsKeptWhole()
    {
        var edges = LoudnessAnalyzer.Analyze(Tone(44100, seconds: 4, 997, dbfs: -20), channels: 1, 44100)!.Edges;

        Assert.All(edges!, edge => Assert.Equal((0L, 4000L), (edge.StartMs, edge.EndMs)));
    }

    [Fact]
    public void SongQuieterThanTheThreshold_IsKeptWhole_NotSkipped()
    {
        var edges = LoudnessAnalyzer.Analyze(
            [.. new float[44100], .. Tone(44100, seconds: 4, 997, dbfs: -40)], channels: 1, 44100)!.Edges;

        Assert.Equal((0L, 5000L), (Silence.At(edges, -30)!.StartMs, Silence.At(edges, -30)!.EndMs));
        Assert.Equal(1000 - Silence.LeadMs, Silence.At(edges, -50)!.StartMs);
    }

    [Theory]
    [InlineData(-52, -50)]
    [InlineData(-90, -70)]
    [InlineData(0, -30)]
    public void Thresholds_SnapToTheMeasuredSteps(int db, int snapped) => Assert.Equal(snapped, Silence.Snap(db));

    [Fact]
    public void Edges_RoundTripThroughTheirStoredForm()
    {
        SilenceEdge[] edges = [new(-70, 0, 1000), new(-65, 20, 990)];

        Assert.Equal(edges, Silence.FromJson(Silence.ToJson(edges))!);
        Assert.Null(Silence.FromJson("not json"));
    }

    [Fact]
    public void Waveform_HasOneLevelPerBlock_OnADbScale()
    {
        float[] song = [.. new float[4410], .. Tone(44100, seconds: 0.1, 997, dbfs: 0), .. Tone(44100, seconds: 0.1, 997, dbfs: -30)];

        var waveform = Waveform.FromSamples(song, channels: 1, 44100);

        Assert.Equal(300, waveform.DurationMs);
        Assert.Equal(30, waveform.Peaks.Length);
        Assert.Equal(0, waveform.Peaks[0]);
        Assert.Equal(255, waveform.Peaks[15]);
        Assert.Equal(128, waveform.Peaks[25], tolerance: 2);
        Assert.True(waveform.Rms[15] < waveform.Peaks[15]);
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
