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
}

public enum TrackSort
{
    Artist,
    Title,
    Album,
    Duration,
    Bpm,
    Added,
}

/// <summary>What the library list is showing: search text plus sort order.</summary>
public sealed record TrackQuery(string? Text = null, TrackSort Sort = TrackSort.Artist, bool Descending = false);

public sealed record QueryPage(long Total, IReadOnlyList<TrackRow> Rows);
