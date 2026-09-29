using System.Text.Json;
using Barbaric.Core.Playback;

namespace Barbaric.Core.Library;

/// <summary>
/// A backup of everything the user made: tags, playlists, and each song's info, BPM, volume and
/// listening history, plus the app's settings. Tags are referred to by name and songs by a
/// <see cref="BackupSong.Key"/> local to the file, since database ids differ between libraries.
/// </summary>
public sealed record BackupFile
{
    public const string FormatName = "barbaric-backup";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new(CoreJson.Options) { WriteIndented = true };

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = CurrentVersion;

    public DateTimeOffset Exported { get; init; }

    public IReadOnlyList<BackupTag> Tags { get; init; } = [];

    public IReadOnlyList<BackupSong> Songs { get; init; } = [];

    public IReadOnlyList<BackupPlaylist> Playlists { get; init; } = [];

    /// <summary>Raw setting values by key. Core doesn't look inside them; the app picks which keys to keep.</summary>
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init; } = new Dictionary<string, JsonElement>();

    /// <exception cref="InvalidDataException">The file isn't a backup, or is from a newer version.</exception>
    public static BackupFile Read(Stream stream)
    {
        BackupFile? backup;
        try
        {
            backup = JsonSerializer.Deserialize<BackupFile>(stream, CoreJson.Options);
        }
        catch (JsonException)
        {
            backup = null;
        }

        if (backup is null || backup.Format != FormatName)
        {
            throw new InvalidDataException("This file isn't a Barbaric Music Player backup.");
        }

        if (backup.Version > CurrentVersion)
        {
            throw new InvalidDataException("This backup was made by a newer version of Barbaric Music Player.");
        }

        // An explicit null in the file would otherwise get past the defaults.
        return backup with
        {
            Tags = [.. (backup.Tags ?? []).OfType<BackupTag>()],
            Songs =
            [
                .. (backup.Songs ?? []).OfType<BackupSong>().Select(s => s with
                {
                    Edited = s.Edited ?? [],
                    Tags = [.. (s.Tags ?? []).OfType<string>()],
                    History = [.. (s.History ?? []).OfType<BackupPlayEvent>()],
                }),
            ],
            Playlists = [.. (backup.Playlists ?? []).OfType<BackupPlaylist>()],
            Settings = backup.Settings ?? new Dictionary<string, JsonElement>(),
        };
    }

    public void Write(Stream stream) => JsonSerializer.Serialize(stream, this, WriteOptions);
}

public sealed record BackupTag(string Name, string? Color = null);

public sealed record BackupSong
{
    /// <summary>The song's id in the library it came from; playlists in the same file refer to it.</summary>
    public long Key { get; init; }

    public string FileName { get; init; } = "";

    public string Path { get; init; } = "";

    public string Fingerprint { get; init; } = "";

    public long FileSize { get; init; }

    public long DurationMs { get; init; }

    public string Title { get; init; } = "";

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Genre { get; init; }

    public int? Year { get; init; }

    public int? TrackNumber { get; init; }

    /// <summary>Fields the user set by hand, which a rescan leaves alone.</summary>
    public IReadOnlyList<TrackField> Edited { get; init; } = [];

    public double? Bpm { get; init; }

    public string? BpmSource { get; init; }

    public double? BpmConfidence { get; init; }

    /// <summary>The user's volume adjustment, on top of the automatic one.</summary>
    public double GainDb { get; init; }

    /// <summary>The song's measured volume; absent when it wasn't measured yet.</summary>
    public BackupLoudness? Loudness { get; init; }

    public long Plays { get; init; }

    public long Skips { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }

    public DateTimeOffset Added { get; init; }

    public bool Flagged { get; init; }

    public bool Hidden { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<BackupPlayEvent> History { get; init; } = [];
}

/// <summary>A song's volume measurement; null values mean it was measured as silent or couldn't be decoded.</summary>
public sealed record BackupLoudness(double? LoudPartLufs, double? PeakDb);

public sealed record BackupPlayEvent(DateTimeOffset At, long PlayedMs, long DurationMs, PlayKind Kind);

public sealed record BackupPlaylist
{
    public string Name { get; init; } = "";

    /// <summary><see cref="PlaylistRepository.ManualKind"/> or <see cref="PlaylistRepository.FilterKind"/>.</summary>
    public string Kind { get; init; } = PlaylistRepository.ManualKind;

    /// <summary>A manual playlist's songs in order, as <see cref="BackupSong.Key"/>s.</summary>
    public IReadOnlyList<long>? Songs { get; init; }

    /// <summary>A filter playlist's saved view.</summary>
    public BackupView? View { get; init; }
}

public sealed record BackupView(string? Text = null, TrackSort Sort = TrackSort.Artist, bool Descending = false, BackupFilter? Filter = null);

/// <summary>A <see cref="TrackFilter"/> with tag names in place of ids.</summary>
public sealed record BackupFilter
{
    public string? Artist { get; init; }

    public IReadOnlyList<string> AllTags { get; init; } = [];

    public IReadOnlyList<string> AnyTags { get; init; } = [];

    public IReadOnlyList<string> NoneTags { get; init; } = [];

    public bool Untagged { get; init; }

    public double? BpmMin { get; init; }

    public double? BpmMax { get; init; }

    public bool IncludeUnknownBpm { get; init; }
}
