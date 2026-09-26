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
}
