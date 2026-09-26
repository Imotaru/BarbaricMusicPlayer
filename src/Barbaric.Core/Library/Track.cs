using System.Text.Json.Serialization;

namespace Barbaric.Core.Library;

/// <summary>A song in the library, as stored in the <c>tracks</c> table.</summary>
public sealed class Track
{
    public long Id { get; set; }

    public string Path { get; set; } = "";

    public string FileName { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    public long FileSize { get; set; }

    public long ModifiedUtc { get; set; }

    public string Title { get; set; } = "";

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    public string? Genre { get; set; }

    public int? Year { get; set; }

    public int? TrackNumber { get; set; }

    public long DurationMs { get; set; }

    public double? Bpm { get; set; }

    public double? BpmConfidence { get; set; }

    public string? BpmSource { get; set; }

    public double GainDb { get; set; }

    public long PlayCount { get; set; }

    public long SkipCount { get; set; }

    public long? LastPlayedUtc { get; set; }

    public bool Flagged { get; set; }

    public bool Missing { get; set; }

    public long AddedUtc { get; set; }
}

/// <summary>The slim shape of a track shown in the library list.</summary>
public sealed class TrackRow
{
    public long Id { get; set; }

    public string Title { get; set; } = "";

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public long DurationMs { get; set; }

    public double? Bpm { get; set; }

    /// <summary>Zero-based place in the manual playlist being shown; null in other views.</summary>
    public int? Position { get; set; }

    /// <summary>Ids of the track's tags, in ascending order.</summary>
    public IReadOnlyList<long> TagIds { get; private set; } = [];

    /// <summary>The comma-separated <c>group_concat</c> the list query returns; fills <see cref="TagIds"/>.</summary>
    [JsonIgnore]
    public string? TagIdList
    {
        get => TagIds.Count == 0 ? null : string.Join(',', TagIds);
        set => TagIds = string.IsNullOrEmpty(value) ? [] : [.. value.Split(',').Select(long.Parse).Order()];
    }
}

public enum TrackSort
{
    Artist,
    Title,
    Album,
    Duration,
    Bpm,
    Added,

    /// <summary>Manual playlist order. Only meaningful with <see cref="TrackQuery.PlaylistId"/>.</summary>
    Position,
}

/// <summary>Narrows the list by tags and (from phase 4) by BPM. Empty lists and nulls mean "no constraint".</summary>
public sealed record TrackFilter
{
    /// <summary>Tracks must have every one of these tags.</summary>
    public IReadOnlyList<long> AllTags { get; init; } = [];

    /// <summary>Tracks must have at least one of these tags.</summary>
    public IReadOnlyList<long> AnyTags { get; init; } = [];

    /// <summary>Tracks must have none of these tags.</summary>
    public IReadOnlyList<long> NoneTags { get; init; } = [];

    public double? BpmMin { get; init; }

    public double? BpmMax { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        AllTags.Count == 0 && AnyTags.Count == 0 && NoneTags.Count == 0 && BpmMin is null && BpmMax is null;
}

/// <summary>
/// What the library list is showing: search text, sort order, an optional filter, and optionally a
/// manual playlist to show instead of the whole library.
/// </summary>
public sealed record TrackQuery(
    string? Text = null,
    TrackSort Sort = TrackSort.Artist,
    bool Descending = false,
    TrackFilter? Filter = null,
    long? PlaylistId = null);

public sealed record QueryPage(long Total, IReadOnlyList<TrackRow> Rows);
