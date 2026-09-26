namespace Barbaric.Core.Library;

public sealed record TrackMetadata(
    string Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    string? Genre,
    int? Year,
    int? TrackNumber,
    long DurationMs,
    double? Bpm);

/// <summary>Reads tags and duration with TagLib, falling back to the file name for untagged or unreadable files.</summary>
public static class TrackMetadataReader
{
    /// <exception cref="IOException">The file could not be opened (e.g. locked).</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not accessible.</exception>
    public static TrackMetadata Read(string path)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            return new TrackMetadata(
                Clean(tag.Title) ?? fallbackTitle,
                Clean(tag.JoinedPerformers),
                Clean(tag.Album),
                Clean(tag.FirstAlbumArtist),
                Clean(tag.FirstGenre),
                tag.Year > 0 ? (int)tag.Year : null,
                tag.Track > 0 ? (int)tag.Track : null,
                (long)(file.Properties?.Duration.TotalMilliseconds ?? 0),
                tag.BeatsPerMinute > 0 ? tag.BeatsPerMinute : null);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
        {
            // Corrupt or unsupported tags: still list the song, named after its file.
            return new TrackMetadata(fallbackTitle, null, null, null, null, null, null, 0, null);
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
