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

    private static readonly long Minute = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    [Fact]
    public void KeepChance_IsInverselyProportionalToLength()
    {
        Assert.Equal(1, SmartShuffle.KeepChance(new ShuffleStats(1, 0, 0, null, Minute), Minute));
        Assert.Equal(0.1, SmartShuffle.KeepChance(new ShuffleStats(2, 0, 0, null, 10 * Minute), Minute), precision: 9);
        Assert.Equal(1, SmartShuffle.KeepChance(new ShuffleStats(3, 0, 0, null, Minute / 2), Minute));
        Assert.Equal(1, SmartShuffle.KeepChance(new ShuffleStats(4, 0, 0, null, 0), Minute));
    }

    [Fact]
    public void ReferenceLength_IsTheShortestKnownSong_ButNotBelowTheFloor()
    {
        var floor = (long)SmartShuffle.ShortestLength.TotalMilliseconds;

        Assert.Equal(Minute, SmartShuffle.ReferenceLengthMs([new(1, 0, 0, null, 3 * Minute), new(2, 0, 0, null, Minute), new(3, 0, 0, null, 0)]));
        Assert.Equal(floor, SmartShuffle.ReferenceLengthMs([new(1, 0, 0, null, 3 * Minute), new(2, 0, 0, null, 1000)]));
        Assert.Equal(floor, SmartShuffle.ReferenceLengthMs([new(1, 0, 0, null, 0)]));
    }

    [Fact]
    public void ThinByLength_GivesEverySongAboutTheSameListeningTime()
    {
        const long epic = 11;
        var songs = Enumerable.Range(1, 10).Select(id => new ShuffleStats(id, 0, 0, null, Minute)).ToList();
        songs.Add(new ShuffleStats(epic, 0, 0, null, 10 * Minute));
        var reference = SmartShuffle.ReferenceLengthMs(songs);
        var random = new Random(3);

        var plays = new Dictionary<long, int>();
        for (var pass = 0; pass < 5000; pass++)
        {
            foreach (var song in SmartShuffle.ThinByLength(songs, reference, random))
            {
                plays[song.Id] = plays.GetValueOrDefault(song.Id) + 1;
            }
        }

        Assert.All(Enumerable.Range(1, 10), id => Assert.Equal(5000, plays[id]));
        Assert.InRange(plays[epic], 400, 600);
    }
}
