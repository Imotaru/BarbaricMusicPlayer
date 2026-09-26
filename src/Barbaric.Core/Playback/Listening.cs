namespace Barbaric.Core.Playback;

/// <summary>Why playback left a song.</summary>
public enum LeaveReason
{
    /// <summary>The user pressed Next.</summary>
    Next,

    /// <summary>The user picked another song or file.</summary>
    Switched,

    /// <summary>The song played to its end.</summary>
    Ended,

    /// <summary>The user pressed Previous, either going back a song or restarting this one.</summary>
    Back,

    Stopped,

    /// <summary>The app closed.</summary>
    Closed,

    /// <summary>The song was deleted while loaded.</summary>
    Removed,
}

public enum PlayKind
{
    Complete,
    Skip,
    Partial,
}

/// <summary>Turns how a song was left into play and skip counts, and decides when to flag a song.</summary>
public static class Listening
{
    /// <summary>Leaving by moving on before this share of the song has played counts as a skip.</summary>
    public const double SkipBefore = 0.3;

    /// <summary>Playing at least this share of the song counts as a full play.</summary>
    public const double CompleteAfter = 0.8;

    public const long FlagMinSkips = 5;

    public const double FlagMinRatio = 0.7;

    /// <remarks>
    /// Only moving forward rejects a song: going back, stopping or closing the app before
    /// <see cref="SkipBefore"/> says nothing about whether the user likes it.
    /// </remarks>
    public static PlayKind Classify(long playedMs, long durationMs, LeaveReason reason)
    {
        if (reason == LeaveReason.Ended)
        {
            return PlayKind.Complete;
        }

        var share = durationMs > 0 ? (double)playedMs / durationMs : 0;
        if (share >= CompleteAfter)
        {
            return PlayKind.Complete;
        }

        return share < SkipBefore && reason is LeaveReason.Next or LeaveReason.Switched
            ? PlayKind.Skip
            : PlayKind.Partial;
    }

    /// <summary>
    /// The skip ratio pulled towards ½ by one imaginary play and one imaginary skip, so a couple of
    /// early skips don't condemn a song.
    /// </summary>
    public static double SmoothedSkipRatio(long plays, long skips) => (skips + 1.0) / (plays + skips + 2.0);

    public static bool ShouldFlag(long plays, long skips) =>
        skips >= FlagMinSkips && SmoothedSkipRatio(plays, skips) > FlagMinRatio;
}
