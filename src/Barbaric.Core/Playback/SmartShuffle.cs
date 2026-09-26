namespace Barbaric.Core.Playback;

/// <summary>A song's history, as far as shuffle cares.</summary>
public readonly record struct ShuffleStats(long Id, long PlayCount, long SkipCount, long? LastPlayedUtc);

/// <summary>
/// Shuffles so that songs the user tends to skip, and songs heard recently, come up later. Nothing
/// is ever ruled out: every weight is at least <see cref="MinWeight"/>.
/// </summary>
public static class SmartShuffle
{
    public const double MinWeight = 0.05;

    /// <summary>How much of a song's weight is taken away right after it played.</summary>
    public const double RecentPenalty = 0.9;

    /// <summary>The recency penalty shrinks by a factor of e over this many hours.</summary>
    public const double RecentHours = 24;

    public static double Weight(ShuffleStats song, DateTimeOffset now)
    {
        var liked = 1 - Listening.SmoothedSkipRatio(song.PlayCount, song.SkipCount);
        var recency = 1.0;
        if (song.LastPlayedUtc is { } ticks)
        {
            var hours = Math.Max(0, (now.UtcTicks - ticks) / (double)TimeSpan.TicksPerHour);
            recency = 1 - RecentPenalty * Math.Exp(-hours / RecentHours);
        }

        return Math.Max(MinWeight, liked * recency);
    }

    /// <summary>
    /// Weighted random order without replacement (Efraimidis–Spirakis): each song draws the key
    /// u^(1/w), and sorting by key descending picks heavier songs earlier.
    /// </summary>
    public static List<long> Order(IEnumerable<ShuffleStats> songs, DateTimeOffset now, Random random) =>
        songs
            .Select(song => (song.Id, Key: Math.Log(1 - random.NextDouble()) / Weight(song, now)))
            .OrderByDescending(entry => entry.Key)
            .Select(entry => entry.Id)
            .ToList();
}
