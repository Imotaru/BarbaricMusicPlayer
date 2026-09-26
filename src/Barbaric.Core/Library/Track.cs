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

    /// <summary>Hidden from the library by the user; unlike <see cref="Missing"/>, a rescan leaves it alone.</summary>
    public bool Hidden { get; set; }

    public long AddedUtc { get; set; }
}

/// <summary>The slim shape of a track shown in the library list.</summary>
public sealed class TrackRow
{
    public long Id { get; set; }

    public string Title { get; set; } = "";

    public string? Artist { get; set; }

    public string? Album { get; set; }

    /// <summary>Where the file is, or was last seen for a missing song.</summary>
    public string Path { get; set; } = "";

    public long DurationMs { get; set; }

    public double? Bpm { get; set; }

    /// <summary>Where <see cref="Bpm"/> came from: <c>tag</c>, <c>analyzed</c> or <c>manual</c>.</summary>
    public string? BpmSource { get; set; }

    public double? BpmConfidence { get; set; }

    public long PlayCount { get; set; }

    public long SkipCount { get; set; }

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
    Plays,
    Skips,

    /// <summary>Manual playlist order. Only meaningful with <see cref="TrackQuery.PlaylistId"/>.</summary>
    Position,
}

/// <summary>Narrows the list by tags and BPM. Empty lists and nulls mean "no constraint".</summary>
public sealed record TrackFilter
{
    /// <summary>Tracks must have every one of these tags.</summary>
    public IReadOnlyList<long> AllTags { get; init; } = [];

    /// <summary>Tracks must have at least one of these tags.</summary>
    public IReadOnlyList<long> AnyTags { get; init; } = [];

    /// <summary>Tracks must have none of these tags.</summary>
    public IReadOnlyList<long> NoneTags { get; init; } = [];

    /// <summary>Tracks must carry no tags at all.</summary>
    public bool Untagged { get; init; }

    public double? BpmMin { get; init; }

    public double? BpmMax { get; init; }

    /// <summary>With a BPM bound set, also keeps tracks whose BPM is not known.</summary>
    public bool IncludeUnknownBpm { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        AllTags.Count == 0 && AnyTags.Count == 0 && NoneTags.Count == 0 && !Untagged && BpmMin is null && BpmMax is null;
}

/// <summary>A BPM range; a null bound leaves that side open.</summary>
public sealed record BpmRange(double? Min = null, double? Max = null, bool IncludeUnknown = false)
{
    [JsonIgnore]
    public bool IsOpen => Min is null && Max is null;
}

/// <summary>Which part of the library a query looks at.</summary>
public enum TrackScope
{
    /// <summary>Every song that is neither missing nor hidden.</summary>
    Library,

    /// <summary>Visible songs flagged for being skipped a lot.</summary>
    Suggested,

    /// <summary>Songs the user hid from the library.</summary>
    Hidden,

    /// <summary>Songs whose file is gone, kept so their data comes back if the file does.</summary>
    Missing,
}

/// <summary>
/// What the library list is showing: search text, sort order, an optional filter, and optionally a
/// manual playlist to show instead of the whole library. <see cref="Bpm"/> is the BPM range the user
/// narrows every view with; unlike <see cref="Filter"/> it is never saved with a playlist.
/// <see cref="KeepIds"/> are tracks that stay listed although the filter's tags no longer match them,
/// such as songs just tagged in the Untagged view; they are never saved either.
/// </summary>
public sealed record TrackQuery(
    string? Text = null,
    TrackSort Sort = TrackSort.Artist,
    bool Descending = false,
    TrackFilter? Filter = null,
    long? PlaylistId = null,
    BpmRange? Bpm = null,
    TrackScope Scope = TrackScope.Library,
    IReadOnlyList<long>? KeepIds = null);

/// <summary>What the user can override about a song. The file's tags fill these in until they do.</summary>
public enum TrackField
{
    Title,
    Artist,
    Album,
    AlbumArtist,
    Genre,
    Year,
    TrackNumber,
}

public static class TrackFields
{
    public static readonly IReadOnlyList<TrackField> All = Enum.GetValues<TrackField>();

    /// <summary>The <c>tracks</c> column behind a field, which is also its key in <c>overrides</c>.</summary>
    public static string Column(TrackField field) => field switch
    {
        TrackField.Title => "title",
        TrackField.Artist => "artist",
        TrackField.Album => "album",
        TrackField.AlbumArtist => "album_artist",
        TrackField.Genre => "genre",
        TrackField.Year => "year",
        TrackField.TrackNumber => "track_number",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static bool IsNumber(TrackField field) => field is TrackField.Year or TrackField.TrackNumber;

    /// <summary>The field's value in a file's tags.</summary>
    public static object? From(TrackMetadata tags, TrackField field) => field switch
    {
        TrackField.Title => tags.Title,
        TrackField.Artist => tags.Artist,
        TrackField.Album => tags.Album,
        TrackField.AlbumArtist => tags.AlbumArtist,
        TrackField.Genre => tags.Genre,
        TrackField.Year => tags.Year,
        TrackField.TrackNumber => tags.TrackNumber,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}

/// <summary>A song's editable details, and which of them the user has overridden.</summary>
public sealed record TrackInfo(
    long Id,
    string FileName,
    string Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    string? Genre,
    int? Year,
    int? TrackNumber,
    IReadOnlyList<TrackField> Overridden);

/// <summary>A track's tempo and where it came from, as sent to the UI after it changes.</summary>
public sealed record BpmInfo(long Id, double? Bpm, string? BpmSource, double? BpmConfidence);

public sealed record QueryPage(long Total, IReadOnlyList<TrackRow> Rows);
