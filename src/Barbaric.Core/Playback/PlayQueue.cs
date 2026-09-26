namespace Barbaric.Core.Playback;

/// <summary>An ordered list of track ids with a cursor on the one that is playing.</summary>
public sealed class PlayQueue
{
    private List<long> _ids = [];
    private int _index = -1;

    public int Count => _ids.Count;

    public IReadOnlyList<long> Ids => _ids;

    /// <summary>Where <see cref="Current"/> sits in <see cref="Ids"/>, or -1 when the queue is empty.</summary>
    public int Index => _index;

    public long? Current => _index >= 0 && _index < _ids.Count ? _ids[_index] : null;

    public bool HasNext => _index + 1 < _ids.Count;

    public bool HasPrevious => _index > 0 && _ids.Count > 0;

    public void Set(IEnumerable<long> ids, int startIndex)
    {
        _ids = [.. ids];
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

        var before = _ids.Take(Math.Max(_index, 0)).Count(removed.Contains);
        _ids.RemoveAll(removed.Contains);
        _index = _ids.Count == 0 ? -1 : Math.Clamp(_index - before, 0, _ids.Count - 1);
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
