using Barbaric.Core.Playback;

namespace Barbaric.Core.Tests.Playback;

public class ListeningTests
{
    [Theory]
    [InlineData(2_990, LeaveReason.Next, PlayKind.Skip)]
    [InlineData(2_990, LeaveReason.Switched, PlayKind.Skip)]
    [InlineData(3_000, LeaveReason.Next, PlayKind.Partial)]
    [InlineData(7_990, LeaveReason.Next, PlayKind.Partial)]
    [InlineData(8_000, LeaveReason.Next, PlayKind.Complete)]
    [InlineData(8_000, LeaveReason.Stopped, PlayKind.Complete)]
    [InlineData(0, LeaveReason.Ended, PlayKind.Complete)]
    public void Classify_UsesTheShareOfTheSongThatPlayed(long playedMs, LeaveReason reason, PlayKind expected) =>
        Assert.Equal(expected, Listening.Classify(playedMs, 10_000, reason));

    [Theory]
    [InlineData(LeaveReason.Back)]
    [InlineData(LeaveReason.Stopped)]
    [InlineData(LeaveReason.Closed)]
    [InlineData(LeaveReason.Removed)]
    public void LeavingWithoutMovingOn_IsNeverASkip(LeaveReason reason) =>
        Assert.Equal(PlayKind.Partial, Listening.Classify(500, 10_000, reason));

    [Fact]
    public void SmoothedSkipRatio_StartsAtOneHalf()
    {
        Assert.Equal(0.5, Listening.SmoothedSkipRatio(0, 0));
        Assert.Equal(6.0 / 7, Listening.SmoothedSkipRatio(0, 5));
    }

    [Theory]
    [InlineData(0, 5, true)]   // 6/7 ≈ 0.86
    [InlineData(0, 4, false)]  // too few skips, however lopsided
    [InlineData(1, 5, true)]   // 6/8 = 0.75
    [InlineData(2, 5, false)]  // 6/9 ≈ 0.67
    [InlineData(10, 30, true)] // 31/42 ≈ 0.74
    public void ShouldFlag_NeedsFiveSkipsAndAHighRatio(long plays, long skips, bool expected) =>
        Assert.Equal(expected, Listening.ShouldFlag(plays, skips));
}
