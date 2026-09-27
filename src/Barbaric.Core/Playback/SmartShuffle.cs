namespace Barbaric.Core.Playback;

/// <summary>A song's history, as far as shuffle cares.</summary>
public readonly record struct ShuffleStats(long Id, long PlayCount, long SkipCount, long? LastPlayedUtc, long DurationMs = 0);

/// <summary>
/// Shuffles so that songs the user tends to skip, and songs heard recently, come up later. Nothing
/// is ever ruled out: every weight is at least <see cref="MinWeight"/>. Optionally, long songs are
/// left out of some passes through a list (<see cref="ThinByLength"/>) so they come up less often.
/// </summary>
public static class SmartShuffle
{
    public const double MinWeight = 0.05;

    /// <summary>How much of a song's weight is taken away right after it played.</summary>
    public const double RecentPenalty = 0.9;

    /// <summary>The recency penalty shrinks by a factor of e over this many hours.</summary>
    public const double RecentHours = 24;

    /// <summary>Songs shorter than this count as this long, so a short interlude doesn't crowd out everything else.</summary>
    public static readonly TimeSpan ShortestLength = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// The length a song must exceed before it can be left out of a pass: the shortest song in the
    /// list, but no shorter than <see cref="ShortestLength"/>.
    /// </summary>
    public static long ReferenceLengthMs(IEnumerable<ShuffleStats> songs)
    {
        var shortest = songs.Where(song => song.DurationMs > 0).Select(song => song.DurationMs).DefaultIfEmpty(0).Min();
        return Math.Max((long)ShortestLength.TotalMilliseconds, shortest);
    }

    /// <summary>
    /// The chance a song makes it into a pass: inversely proportional to its length, so a 1-minute
    /// song comes up ten times as often as a 10-minute one and every song gets about the same
    /// listening time. Songs of unknown length always make it in.
    /// </summary>
    public static double KeepChance(ShuffleStats song, long referenceMs) =>
        song.DurationMs <= 0 ? 1 : Math.Min(1, referenceMs / (double)song.DurationMs);

    /// <summary>
    /// Picks the songs for one pass through a list, each with its <see cref="KeepChance"/>. Take
    /// <paramref name="referenceMs"/> from the whole list, so the shortest song in it is always kept.
    /// </summary>
    public static List<ShuffleStats> ThinByLength(IEnumerable<ShuffleStats> songs, long referenceMs, Random random) =>
        songs
            .Where(song => KeepChance(song, referenceMs) is var chance && (chance >= 1 || random.NextDouble() < chance))
            .ToList();
}
