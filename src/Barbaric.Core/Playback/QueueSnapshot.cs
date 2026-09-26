using Barbaric.Core.Library;

namespace Barbaric.Core.Playback;

/// <summary>The queue's songs and the list they came from, saved so the next start can pick up the same queue.</summary>
public sealed record QueueSnapshot(IReadOnlyList<long> Ids, TrackQuery? Source, bool Shuffle);

/// <summary>Where playback was in a saved queue. Saved more often than the (possibly long) list itself.</summary>
public sealed record QueuePosition(long CurrentId, int Index, double PositionSeconds);
