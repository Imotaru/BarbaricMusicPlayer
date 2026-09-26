using Barbaric.Core.Analysis;

namespace Barbaric.Core.Tests.Analysis;

public sealed class BpmAnalyzerTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(140)]
    [InlineData(174)]
    public void ClickTrack_IsDetectedWithinOneBpm(double bpm)
    {
        var result = BpmAnalyzer.Analyze(Beats.Clicks(bpm), Beats.SampleRate);

        Assert.NotNull(result);
        Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
        Assert.True(result.Confidence > 0.5, $"confidence {result.Confidence}");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(140)]
    [InlineData(174)]
    public void ClickTrackFile_IsDetectedFromTheMiddleOfTheSong(double bpm)
    {
        var path = Path.Combine(Path.GetTempPath(), $"barbaric-bpm-{Guid.NewGuid():N}.wav");
        try
        {
            // Two minutes, so the analyzer has to seek to the middle minute.
            Beats.WriteWav(path, Beats.Clicks(bpm, seconds: 120));

            var result = BpmAnalyzer.AnalyzeFile(path);

            Assert.NotNull(result);
            Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    public void TempoAtTheEdgesOfTheRange_IsKept(double bpm)
    {
        var result = BpmAnalyzer.Analyze(Beats.Clicks(bpm), Beats.SampleRate);

        Assert.NotNull(result);
        Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
    }

    [Theory]
    [InlineData(90, -12)]
    [InlineData(100, -12)]
    [InlineData(85, -9)]
    [InlineData(95, -12)]
    public void QuieterOffbeats_DoNotDoubleTheTempo(double bpm, double hatGainDb)
    {
        var result = BpmAnalyzer.Analyze(Beats.KickAndOffbeatHats(bpm, hatGainDb), Beats.SampleRate);

        Assert.NotNull(result);
        Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
    }

    [Theory]
    [InlineData(170)]
    [InlineData(174)]
    [InlineData(140)]
    public void AlternatingDrumsOfEqualWeight_CountEveryHit(double bpm)
    {
        var result = BpmAnalyzer.Analyze(Beats.AlternatingDrums(bpm), Beats.SampleRate);

        Assert.NotNull(result);
        Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(110)]
    [InlineData(122)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(150)]
    [InlineData(170)]
    public void Backbeat_IsCountedInBeats_NotBars(double bpm)
    {
        var result = BpmAnalyzer.Analyze(Beats.Groove(bpm), Beats.SampleRate);

        Assert.NotNull(result);
        Assert.InRange(result.Bpm, bpm - 1, bpm + 1);
    }

    [Fact]
    public void Noise_HasNoClearBeat()
    {
        Assert.Null(BpmAnalyzer.Analyze(Beats.Noise(60, 0.3), Beats.SampleRate));
    }

    [Fact]
    public void Silence_AndShortClips_HaveNoResult()
    {
        Assert.Null(BpmAnalyzer.Analyze(new float[Beats.SampleRate * 30], Beats.SampleRate));
        Assert.Null(BpmAnalyzer.Analyze(Beats.Clicks(120, seconds: 5), Beats.SampleRate));
    }
}
