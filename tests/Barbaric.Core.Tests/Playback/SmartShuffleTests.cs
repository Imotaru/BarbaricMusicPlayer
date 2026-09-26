using Barbaric.Core.Playback;

namespace Barbaric.Core.Tests.Playback;

public class SmartShuffleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Order_IsAPermutation()
    {
        var songs = Enumerable.Range(1, 50).Select(id => new ShuffleStats(id, id % 3, id % 5, null)).ToList();

        var order = SmartShuffle.Order(songs, Now, new Random(1));

        Assert.Equal(songs.Select(s => s.Id).Order(), order.Order());
    }

    [Fact]
    public void Order_IsRepeatableWithTheSameSeed()
    {
        var songs = Enumerable.Range(1, 20).Select(id => new ShuffleStats(id, 0, 0, null)).ToList();

        Assert.Equal(SmartShuffle.Order(songs, Now, new Random(7)), SmartShuffle.Order(songs, Now, new Random(7)));
        Assert.NotEqual(SmartShuffle.Order(songs, Now, new Random(7)), SmartShuffle.Order(songs, Now, new Random(8)));
    }

    [Fact]
    public void Weight_FallsWithSkips_ButNeverBelowTheFloor()
    {
        var loved = SmartShuffle.Weight(new ShuffleStats(1, 20, 0, null), Now);
        var fresh = SmartShuffle.Weight(new ShuffleStats(2, 0, 0, null), Now);
        var hated = SmartShuffle.Weight(new ShuffleStats(3, 0, 200, null), Now);

        Assert.True(loved > fresh);
        Assert.Equal(0.5, fresh);
        Assert.Equal(SmartShuffle.MinWeight, hated);
    }

    [Fact]
    public void Weight_IsLowerForSongsPlayedRecently_AndRecovers()
    {
        long HoursAgo(double hours) => (Now - TimeSpan.FromHours(hours)).UtcTicks;

        var never = SmartShuffle.Weight(new ShuffleStats(1, 0, 0, null), Now);
        var justNow = SmartShuffle.Weight(new ShuffleStats(1, 0, 0, HoursAgo(0)), Now);
        var yesterday = SmartShuffle.Weight(new ShuffleStats(1, 0, 0, HoursAgo(24)), Now);
        var lastWeek = SmartShuffle.Weight(new ShuffleStats(1, 0, 0, HoursAgo(24 * 7)), Now);

        Assert.Equal(0.05, justNow, precision: 6);
        Assert.True(justNow < yesterday && yesterday < lastWeek && lastWeek < never);
        Assert.True(lastWeek > never * 0.99);
    }

    [Fact]
    public void SkippedSongs_ComeUpLater_ButStillComeUp()
    {
        const long hated = 1, clean = 2;
        var songs = new List<ShuffleStats> { new(hated, 0, 20, null), new(clean, 20, 0, null) };
        songs.AddRange(Enumerable.Range(3, 8).Select(id => new ShuffleStats(id, 20, 0, null)));
        var random = new Random(42);

        int hatedEarly = 0, cleanEarly = 0;
        for (var i = 0; i < 2000; i++)
        {
            var firstHalf = SmartShuffle.Order(songs, Now, random).Take(songs.Count / 2).ToHashSet();
            hatedEarly += firstHalf.Contains(hated) ? 1 : 0;
            cleanEarly += firstHalf.Contains(clean) ? 1 : 0;
        }

        Assert.InRange(cleanEarly, 1000, 2000);
        Assert.InRange(hatedEarly, 1, cleanEarly / 5);
    }
}
