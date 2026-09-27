namespace Barbaric.Core.Playback;

/// <summary>
/// An ordered list of track ids with a cursor on the one that is playing, and the pool of songs it
/// was drawn from.
/// </summary>
public sealed class PlayQueue
{
    private List<long> _ids = [];
    private List<long> _pool = [];
    private int _index = -1;

    public int Count => _ids.Count;

    public IReadOnlyList<long> Ids => _ids;

    /// <summary>
    /// Every song the queue was drawn from, which can hold more than <see cref="Ids"/>: shuffle may
    /// leave long songs out of a pass. Songs join it only when the queue is built again.
    /// </summary>
    public IReadOnlyList<long> Pool => _pool;

    /// <summary>Where <see cref="Current"/> sits in <see cref="Ids"/>, or -1 when the queue is empty.</summary>
    public int Index => _index;

    public long? Current => _index >= 0 && _index < _ids.Count ? _ids[_index] : null;

    public bool HasNext => _index + 1 < _ids.Count;

    public bool HasPrevious => _index > 0 && _ids.Count > 0;

    /// <summary>
    /// Goes up whenever the list of ids or the pool changes (not when the cursor moves), so they can
    /// be saved only then.
    /// </summary>
    public int Version { get; private set; }

    /// <param name="pool">The songs <paramref name="ids"/> were drawn from; the ids themselves when null.</param>
    public void Set(IEnumerable<long> ids, int startIndex, IEnumerable<long>? pool = null)
    {
        Version++;
        _ids = [.. ids];
        _pool = [.. (pool ?? _ids).Union(_ids)];
        _index = _ids.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _ids.Count - 1);
    }

    public void Clear() => Set([], 0);

    /// <summary>Takes songs out of the queue. The current song stays, so playback carries on from it.</summary>
    public void Remove(IEnumerable<long> ids)
    {
        var removed = ids.ToHashSet();
        if (Current is { } current)
        {
            removed.Remove(current);
        }

        Version++;
        var before = _ids.Take(Math.Max(_index, 0)).Count(removed.Contains);
        _ids.RemoveAll(removed.Contains);
        _pool.RemoveAll(removed.Contains);
        _index = _ids.Count == 0 ? -1 : Math.Clamp(_index - before, 0, _ids.Count - 1);
    }

    /// <summary>
    /// Puts a song right after the current one, moving it there if it is already queued, so Next
    /// plays it and the rest of the queue keeps its order.
    /// </summary>
    public void PlayNext(long id)
    {
        if (Current == id)
        {
            return;
        }

        Version++;
        var at = _ids.IndexOf(id);
        if (at >= 0)
        {
            _ids.RemoveAt(at);
            if (at < _index)
            {
                _index--;
            }
        }

        _ids.Insert(_index + 1, id);
        if (!_pool.Contains(id))
        {
            _pool.Add(id);
        }
    }

    /// <summary>Where a song sits in <see cref="Ids"/>, or -1.</summary>
    public int IndexOf(long id) => _ids.IndexOf(id);

    /// <summary>Puts the cursor on the song at <paramref name="index"/>.</summary>
    public bool MoveTo(int index)
    {
        if (index < 0 || index >= _ids.Count)
        {
            return false;
        }

        _index = index;
        return true;
    }

    public bool MoveNext()
    {
        if (!HasNext)
        {
            return false;
        }

        _index++;
        return true;
    }

    /// <summary>Puts the cursor back on the first song, e.g. to start the list over.</summary>
    public void MoveFirst()
    {
        if (_ids.Count > 0)
        {
            _index = 0;
        }
    }

    public bool MovePrevious()
    {
        if (!HasPrevious)
        {
            return false;
        }

        _index--;
        return true;
    }
}
