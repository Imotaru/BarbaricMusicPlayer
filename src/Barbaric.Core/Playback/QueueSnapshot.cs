using Barbaric.Core.Library;

namespace Barbaric.Core.Playback;

/// <summary>
/// The queue's songs, the pool they were drawn from and the list that came from, saved so the next
/// start can pick up the same queue. Queues saved before the pool was kept have none; their ids are the pool.
/// </summary>
public sealed record QueueSnapshot(IReadOnlyList<long> Ids, TrackQuery? Source, bool Shuffle, IReadOnlyList<long>? Pool = null);

/// <summary>Where playback was in a saved queue. Saved more often than the (possibly long) list itself.</summary>
public sealed record QueuePosition(long CurrentId, int Index, double PositionSeconds);
