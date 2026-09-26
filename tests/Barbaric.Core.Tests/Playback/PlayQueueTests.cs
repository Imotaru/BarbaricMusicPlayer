using Barbaric.Core.Playback;

namespace Barbaric.Core.Tests.Playback;

public class PlayQueueTests
{
    [Fact]
    public void MovesWithinBounds()
    {
        var queue = new PlayQueue();
        queue.Set([10, 20, 30], startIndex: 1);

        Assert.Equal(20, queue.Current);
        Assert.True(queue.MoveNext());
        Assert.Equal(30, queue.Current);
        Assert.False(queue.MoveNext());
        Assert.Equal(30, queue.Current);

        Assert.True(queue.MovePrevious());
        Assert.True(queue.MovePrevious());
        Assert.False(queue.MovePrevious());
        Assert.Equal(10, queue.Current);
    }

    [Fact]
    public void EmptyQueue_HasNoCurrent()
    {
        var queue = new PlayQueue();
        queue.Set([1, 2], 0);

        queue.Clear();

        Assert.Null(queue.Current);
        Assert.False(queue.HasNext);
        Assert.False(queue.HasPrevious);
    }

    [Fact]
    public void StartIndex_IsClamped()
    {
        var queue = new PlayQueue();

        queue.Set([1, 2, 3], startIndex: 99);

        Assert.Equal(3, queue.Current);
    }

    [Fact]
    public void Remove_KeepsTheCurrentSong_AndItsPlace()
    {
        var queue = new PlayQueue();
        queue.Set([1, 2, 3, 4, 5], startIndex: 2);

        queue.Remove([1, 3, 5]);

        Assert.Equal<long>([2, 3, 4], queue.Ids);
        Assert.Equal(3, queue.Current);
        Assert.Equal(1, queue.Index);
    }

    [Fact]
    public void Remove_EverythingAfterTheCurrentSong_EndsTheQueueThere()
    {
        var queue = new PlayQueue();
        queue.Set([7, 8, 9], startIndex: 0);

        queue.Remove([7, 8, 9]);

        Assert.Equal<long>([7], queue.Ids);
        Assert.False(queue.HasNext);
    }
}
