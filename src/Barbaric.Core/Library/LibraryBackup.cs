using System.Data;
using System.Text.Json;
using Barbaric.Core.Analysis;
using Barbaric.Core.Playback;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Barbaric.Core.Library;

/// <summary>What an import did. <see cref="TrackIds"/> holds every song the backup touched or added.</summary>
public sealed record ImportResult(
    int Matched,
    int Added,
    IReadOnlyList<long> TrackIds,
    int TagsCreated,
    int PlaylistsCreated,
    int PlaylistsReplaced);

/// <summary>A song whose file isn't where the library last saw it, with enough to go and find it.</summary>
public sealed record MissingSong(long Id, string FileName, string Path, string Title, string? Artist, string? Album);

/// <summary>
/// Writes the library to a <see cref="BackupFile"/> and merges one back in. On import the backup wins
/// for the songs, tags and playlists it holds; everything else is left alone. A song whose file isn't
/// in the library is kept as a missing track, which a later scan brings back by path or content.
/// </summary>
public sealed class LibraryBackup(LibraryDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<BackupFile> ExportAsync(IReadOnlyDictionary<string, JsonElement>? settings = null)
    {
        using var connection = database.Open();

        // Deferred: a read-only snapshot that doesn't hold up writers.
        using var transaction = connection.BeginTransaction(deferred: true);

        var tags = (await connection.QueryAsync<(long Id, string Name, string Color)>(
            "SELECT id, name, color FROM tags ORDER BY name COLLATE NOCASE", transaction: transaction)).AsList();
        var tagNames = tags.ToDictionary(t => t.Id, t => t.Name);

        var songTags = (await connection.QueryAsync<(long TrackId, long TagId)>(
                "SELECT track_id, tag_id FROM track_tags", transaction: transaction))
            .ToLookup(r => r.TrackId, r => tagNames[r.TagId]);

        var history = (await connection.QueryAsync<EventRow>(
                "SELECT track_id, at_utc, played_ms, duration_ms, kind FROM play_events ORDER BY track_id, at_utc, id",
                transaction: transaction))
            .ToLookup(e => e.TrackId, e => new BackupPlayEvent(
                Time(e.AtUtc), e.PlayedMs, e.DurationMs, Enum.Parse<PlayKind>(e.Kind, ignoreCase: true)));

        var tracks = await connection.QueryAsync<TrackRecord>(
            """
            SELECT id, path, file_name, fingerprint, file_size, duration_ms, title, artist, album, album_artist, genre,
                   year, track_number, overrides, bpm, bpm_source, bpm_confidence, gain_db, loudness_lufs, peak_db,
                   silence_edges, loudness_analyzed, trim_start_ms, trim_end_ms, play_count, skip_count, last_played_utc, added_utc, flagged, hidden
            FROM tracks ORDER BY id
            """,
            transaction: transaction);

        var songs = tracks.Select(t => new BackupSong
        {
            Key = t.Id,
            FileName = t.FileName,
            Path = t.Path,
            Fingerprint = t.Fingerprint,
            FileSize = t.FileSize,
            DurationMs = t.DurationMs,
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            AlbumArtist = t.AlbumArtist,
            Genre = t.Genre,
            Year = t.Year,
            TrackNumber = t.TrackNumber,
            Edited = TrackRepository.Overridden(t.Overrides),
            Bpm = t.Bpm,
            BpmSource = t.BpmSource,
            BpmConfidence = t.BpmConfidence,
            GainDb = t.GainDb,
            Loudness = t.LoudnessAnalyzed ? new BackupLoudness(t.LoudnessLufs, t.PeakDb, Silence.FromJson(t.SilenceEdges)) : null,
            TrimStartMs = t.TrimStartMs,
            TrimEndMs = t.TrimEndMs,
            Plays = t.PlayCount,
            Skips = t.SkipCount,
            LastPlayed = t.LastPlayedUtc is { } played ? Time(played) : null,
            Added = Time(t.AddedUtc),
            Flagged = t.Flagged,
            Hidden = t.Hidden,
            Tags = [.. songTags[t.Id].Order(StringComparer.OrdinalIgnoreCase)],
            History = [.. history[t.Id]],
        }).ToList();

        var entries = (await connection.QueryAsync<(long PlaylistId, long TrackId)>(
                "SELECT playlist_id, track_id FROM playlist_tracks ORDER BY playlist_id, position", transaction: transaction))
            .ToLookup(r => r.PlaylistId, r => r.TrackId);

        var playlists = (await connection.QueryAsync<(long Id, string Name, string Kind, string? FilterJson)>(
                "SELECT id, name, kind, filter_json FROM playlists ORDER BY position, id", transaction: transaction))
            .Select(p => p.Kind == PlaylistRepository.FilterKind
                ? new BackupPlaylist { Name = p.Name, Kind = p.Kind, View = ToView(PlaylistRepository.DeserializeQuery(p.FilterJson), tagNames) }
                : new BackupPlaylist { Name = p.Name, Kind = p.Kind, Songs = [.. entries[p.Id]] })
            .ToList();

        return new BackupFile
        {
            Exported = _clock.GetUtcNow(),
            Tags = [.. tags.Select(t => new BackupTag(t.Name, t.Color))],
            Songs = songs,
            Playlists = playlists,
            Settings = settings ?? new Dictionary<string, JsonElement>(),
        };
    }

    public async Task<ImportResult> ImportAsync(BackupFile backup)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var tags = new TagImporter(connection, transaction);
        await tags.LoadAsync();
        foreach (var tag in backup.Tags)
        {
            await tags.EnsureAsync(tag.Name, tag.Color);
        }

        var (keys, matched, added) = await ImportSongsAsync(connection, transaction, backup.Songs, tags);
        var (created, replaced) = await ImportPlaylistsAsync(connection, transaction, backup.Playlists, keys, tags);

        transaction.Commit();
        return new ImportResult(matched, added, [.. keys.Values.Distinct()], tags.Created, created, replaced);
    }

    /// <summary>Those of the given songs whose file is still missing, by file name.</summary>
    public async Task<IReadOnlyList<MissingSong>> GetMissingAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<MissingSong>(
            """
            SELECT id, file_name AS FileName, path, title, artist, album FROM tracks
            WHERE id IN (SELECT value FROM json_each(@ids)) AND missing = 1
            ORDER BY file_name COLLATE NOCASE, path COLLATE NOCASE
            """,
            new { ids = IdList.ToJson(ids) });
        return rows.AsList();
    }

    private async Task<(Dictionary<long, long> Keys, int Matched, int Added)> ImportSongsAsync(
        SqliteConnection connection, IDbTransaction transaction, IReadOnlyList<BackupSong> songs, TagImporter tags)
    {
        var existing = (await connection.QueryAsync<ExistingTrack>(
            "SELECT id, path, fingerprint, file_size, missing, overrides FROM tracks", transaction: transaction)).AsList();
        var byPath = existing.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);

        // Present files first, so a backed-up song lands on a copy that plays rather than a missing one.
        var byContent = existing
            .OrderBy(t => t.Missing)
            .GroupBy(t => (t.Fingerprint, t.FileSize))
            .ToDictionary(g => g.Key, g => g.ToList());

        var claimed = new HashSet<long>();
        var keys = new Dictionary<long, long>();
        int matched = 0, added = 0;

        foreach (var song in songs)
        {
            if (string.IsNullOrWhiteSpace(song.Path) || keys.ContainsKey(song.Key))
            {
                continue;
            }

            var match = Match(song, byPath, byContent, claimed);
            long id;
            if (match is not null)
            {
                id = match.Id;
                await UpdateAsync(connection, transaction, id, song, match.Overrides);
                matched++;
            }
            else if (byPath.ContainsKey(song.Path))
            {
                // Another song in the backup already took this path; two rows can't share it.
                continue;
            }
            else
            {
                id = await InsertMissingAsync(connection, transaction, song);
                byPath[song.Path] = new ExistingTrack { Id = id, Path = song.Path, Fingerprint = song.Fingerprint, FileSize = song.FileSize, Missing = true };
                added++;
            }

            claimed.Add(id);
            keys[song.Key] = id;

            await connection.ExecuteAsync("DELETE FROM track_tags WHERE track_id = @id", new { id }, transaction);
            foreach (var name in song.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await tags.EnsureAsync(name) is { } tagId)
                {
                    await connection.ExecuteAsync(
                        "INSERT OR IGNORE INTO track_tags (track_id, tag_id) VALUES (@id, @tagId)", new { id, tagId }, transaction);
                }
            }

            await connection.ExecuteAsync(
                """
                INSERT INTO play_events (track_id, at_utc, played_ms, duration_ms, kind)
                SELECT @id, @at, @playedMs, @durationMs, @kind
                WHERE NOT EXISTS (SELECT 1 FROM play_events WHERE track_id = @id AND at_utc = @at)
                """,
                song.History.Select(e => new
                {
                    id,
                    at = e.At.UtcTicks,
                    playedMs = Math.Max(0, e.PlayedMs),
                    durationMs = Math.Max(0, e.DurationMs),
                    kind = e.Kind.ToString().ToLowerInvariant(),
                }).ToList(),
                transaction);
        }

        return (keys, matched, added);
    }

    /// <summary>
    /// The library's row for a backed-up song: the same file at the same place, then the same file
    /// anywhere (it was moved), then whatever is at its path now (its tags were rewritten, which
    /// changes the fingerprint).
    /// </summary>
    private static ExistingTrack? Match(
        BackupSong song,
        Dictionary<string, ExistingTrack> byPath,
        Dictionary<(string, long), List<ExistingTrack>> byContent,
        HashSet<long> claimed)
    {
        var atPath = byPath.GetValueOrDefault(song.Path) is { } p && !claimed.Contains(p.Id) ? p : null;
        if (atPath is not null && atPath.Fingerprint == song.Fingerprint && atPath.FileSize == song.FileSize)
        {
            return atPath;
        }

        if (byContent.TryGetValue((song.Fingerprint, song.FileSize), out var copies)
            && copies.FirstOrDefault(c => !claimed.Contains(c.Id)) is { } copy)
        {
            return copy;
        }

        return atPath;
    }

    private static async Task UpdateAsync(
        SqliteConnection connection, IDbTransaction transaction, long id, BackupSong song, string? currentOverrides)
    {
        var edited = EditedValues(song);
        var parameters = new DynamicParameters(new
        {
            id,
            overrides = OverridesJson(edited),
            gainDb = song.GainDb,
            trimStart = song.TrimStartMs,
            trimEnd = song.TrimEndMs,
            plays = Math.Max(0, song.Plays),
            skips = Math.Max(0, song.Skips),
            lastPlayed = song.LastPlayed?.UtcTicks,
            added = song.Added.UtcTicks,
            flagged = song.Flagged,
            hidden = song.Hidden,
        });

        var set = new List<string>
        {
            "overrides = @overrides", "gain_db = @gainDb", "trim_start_ms = @trimStart", "trim_end_ms = @trimEnd", "play_count = @plays", "skip_count = @skips",
            "last_played_utc = @lastPlayed", "added_utc = min(added_utc, @added)", "flagged = @flagged", "hidden = @hidden",
        };

        foreach (var (field, value) in edited)
        {
            set.Add($"{TrackFields.Column(field)} = @{field}");
            parameters.Add(field.ToString(), value);
        }

        // A field the user had edited here but not in the backup goes back to the file's tag on the next scan.
        if (TrackRepository.Overridden(currentOverrides).Except(edited.Keys).Any())
        {
            set.Add("modified_utc = 0");
        }

        // A BPM that came from the file's tag is best read from the file itself.
        if (song.BpmSource is "manual" or "analyzed")
        {
            set.Add("bpm = @bpm, bpm_source = @bpmSource, bpm_confidence = @bpmConfidence");
            parameters.Add("bpm", song.Bpm);
            parameters.Add("bpmSource", song.BpmSource);
            parameters.Add("bpmConfidence", song.BpmConfidence);
        }

        // A measurement only describes the same content; a song matched by path whose file changed is measured anew.
        if (song.Loudness is { } loudness)
        {
            set.Add("""
                loudness_lufs = CASE WHEN fingerprint = @fingerprint THEN @loudness ELSE loudness_lufs END,
                peak_db = CASE WHEN fingerprint = @fingerprint THEN @peak ELSE peak_db END,
                silence_edges = CASE WHEN fingerprint = @fingerprint THEN @silence ELSE silence_edges END,
                loudness_analyzed = CASE WHEN fingerprint = @fingerprint THEN @analyzed ELSE loudness_analyzed END
                """);
            parameters.Add("fingerprint", song.Fingerprint);
            parameters.Add("loudness", loudness.LoudPartLufs);
            parameters.Add("peak", loudness.PeakDb);
            parameters.Add("silence", Silence.ToJson(loudness.Silence));
            parameters.Add("analyzed", Measured(loudness));
        }

        await connection.ExecuteAsync($"UPDATE tracks SET {string.Join(", ", set)} WHERE id = @id", parameters, transaction);
    }

    /// <summary>Adds a song whose file isn't in the library. A scan that finds it (by path or content) fills in the rest.</summary>
    private static async Task<long> InsertMissingAsync(SqliteConnection connection, IDbTransaction transaction, BackupSong song)
    {
        var bpmSource = song.BpmSource is "tag" or "manual" or "analyzed" ? song.BpmSource : null;
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO tracks (path, file_name, fingerprint, file_size, modified_utc, title, artist, album, album_artist,
                                genre, year, track_number, duration_ms, bpm, bpm_confidence, bpm_source, gain_db,
                                loudness_lufs, peak_db, silence_edges, loudness_analyzed, trim_start_ms, trim_end_ms,
                                play_count, skip_count, last_played_utc, flagged, missing, hidden, added_utc, overrides)
            VALUES (@path, @fileName, @fingerprint, @fileSize, 0, @title, @artist, @album, @albumArtist,
                    @genre, @year, @trackNumber, @durationMs, @bpm, @bpmConfidence, @bpmSource, @gainDb,
                    @loudness, @peak, @silence, @loudnessAnalyzed, @trimStart, @trimEnd, @plays, @skips, @lastPlayed, @flagged, 1, @hidden, @added, @overrides)
            RETURNING id
            """,
            new
            {
                path = song.Path,
                fileName = string.IsNullOrEmpty(song.FileName) ? Path.GetFileName(song.Path) : song.FileName,
                fingerprint = song.Fingerprint ?? "",
                fileSize = song.FileSize,
                title = string.IsNullOrWhiteSpace(song.Title) ? Path.GetFileNameWithoutExtension(song.Path) : song.Title,
                artist = song.Artist,
                album = song.Album,
                albumArtist = song.AlbumArtist,
                genre = song.Genre,
                year = song.Year,
                trackNumber = song.TrackNumber,
                durationMs = Math.Max(0, song.DurationMs),
                bpm = bpmSource is null ? null : song.Bpm,
                bpmConfidence = bpmSource is null ? null : song.BpmConfidence,
                bpmSource,
                gainDb = song.GainDb,
                loudness = song.Loudness?.LoudPartLufs,
                peak = song.Loudness?.PeakDb,
                silence = Silence.ToJson(song.Loudness?.Silence),
                loudnessAnalyzed = song.Loudness is { } loudness && Measured(loudness),
                trimStart = song.TrimStartMs,
                trimEnd = song.TrimEndMs,
                plays = Math.Max(0, song.Plays),
                skips = Math.Max(0, song.Skips),
                lastPlayed = song.LastPlayed?.UtcTicks,
                flagged = song.Flagged,
                hidden = song.Hidden,
                added = song.Added.UtcTicks,
                overrides = OverridesJson(EditedValues(song)),
            },
            transaction);
    }

    private static async Task<(int Created, int Replaced)> ImportPlaylistsAsync(
        SqliteConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<BackupPlaylist> playlists,
        Dictionary<long, long> keys,
        TagImporter tags)
    {
        // Names aren't unique; the first playlist with a name is the one a backup playlist replaces.
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in await connection.QueryAsync<(long, string)>(
            "SELECT id, name FROM playlists ORDER BY position, id", transaction: transaction))
        {
            byName.TryAdd(name, id);
        }

        int created = 0, replaced = 0;
        foreach (var playlist in playlists)
        {
            if (string.IsNullOrWhiteSpace(playlist.Name) || playlist.Kind is not (PlaylistRepository.ManualKind or PlaylistRepository.FilterKind))
            {
                continue;
            }

            var name = PlaylistRepository.CleanName(playlist.Name);
            var json = playlist.Kind == PlaylistRepository.FilterKind
                ? PlaylistRepository.SerializeQuery(await ToQueryAsync(playlist.View ?? new BackupView(), tags))
                : null;

            if (byName.Remove(name, out var id))
            {
                await connection.ExecuteAsync(
                    """
                    UPDATE playlists SET name = @name, kind = @kind, filter_json = @json WHERE id = @id;
                    DELETE FROM playlist_tracks WHERE playlist_id = @id;
                    """,
                    new { id, name, kind = playlist.Kind, json },
                    transaction);
                replaced++;
            }
            else
            {
                id = await connection.ExecuteScalarAsync<long>(
                    """
                    INSERT INTO playlists (name, kind, filter_json, position)
                    VALUES (@name, @kind, @json, (SELECT coalesce(max(position) + 1, 0) FROM playlists))
                    RETURNING id
                    """,
                    new { name, kind = playlist.Kind, json },
                    transaction);
                created++;
            }

            if (playlist.Kind == PlaylistRepository.ManualKind)
            {
                var trackIds = (playlist.Songs ?? []).Select(k => keys.GetValueOrDefault(k)).Where(t => t != 0).Distinct();
                await connection.ExecuteAsync(
                    "INSERT INTO playlist_tracks (playlist_id, track_id, position) VALUES (@id, @trackId, @position)",
                    trackIds.Select((trackId, position) => new { id, trackId, position }).ToList(),
                    transaction);
            }
        }

        return (created, replaced);
    }

    private static BackupView ToView(TrackQuery? query, Dictionary<long, string> tagNames)
    {
        query ??= new TrackQuery();
        List<string> Names(IReadOnlyList<long> ids) => [.. ids.Select(tagNames.GetValueOrDefault).OfType<string>()];

        var filter = query.Filter is { } f
            ? new BackupFilter
            {
                Artist = f.Artist,
                AllTags = Names(f.AllTags),
                AnyTags = Names(f.AnyTags),
                NoneTags = Names(f.NoneTags),
                Untagged = f.Untagged,
                BpmMin = f.BpmMin,
                BpmMax = f.BpmMax,
                IncludeUnknownBpm = f.IncludeUnknownBpm,
            }
            : null;
        return new BackupView(query.Text, query.Sort, query.Descending, filter);
    }

    private static async Task<TrackQuery> ToQueryAsync(BackupView view, TagImporter tags)
    {
        async Task<List<long>> Ids(IReadOnlyList<string>? names)
        {
            var ids = new List<long>();
            foreach (var name in names ?? [])
            {
                if (await tags.EnsureAsync(name) is { } id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        var filter = view.Filter is { } f
            ? new TrackFilter
            {
                Artist = string.IsNullOrWhiteSpace(f.Artist) ? null : f.Artist.Trim(),
                AllTags = await Ids(f.AllTags),
                AnyTags = await Ids(f.AnyTags),
                NoneTags = await Ids(f.NoneTags),
                Untagged = f.Untagged,
                BpmMin = f.BpmMin,
                BpmMax = f.BpmMax,
                IncludeUnknownBpm = f.IncludeUnknownBpm,
            }
            : null;
        return new TrackQuery(view.Text, view.Sort, view.Descending, filter);
    }

    /// <summary>The backup's hand-set values, cleaned the way the info editor would clean them.</summary>
    private static Dictionary<TrackField, object?> EditedValues(BackupSong song)
    {
        var values = new Dictionary<TrackField, object?>();
        foreach (var field in song.Edited.Distinct())
        {
            object? value = field switch
            {
                TrackField.Title => song.Title,
                TrackField.Artist => song.Artist,
                TrackField.Album => song.Album,
                TrackField.AlbumArtist => song.AlbumArtist,
                TrackField.Genre => song.Genre,
                TrackField.Year => song.Year,
                TrackField.TrackNumber => song.TrackNumber,
                _ => null,
            };

            if (value is string text)
            {
                value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            else if (value is int number && number is < 1 or > TrackRepository.MaxInfoNumber)
            {
                value = null;
            }

            // Every song has a title; a blank one isn't an edit worth keeping.
            if (field == TrackField.Title && value is null)
            {
                continue;
            }

            values[field] = value;
        }

        return values;
    }

    /// <summary>
    /// Whether a measurement is complete: a backup from before silence was measured has none for a
    /// song with sound, which is then measured again.
    /// </summary>
    private static bool Measured(BackupLoudness loudness) => loudness.Silence is not null || loudness.LoudPartLufs is null;

    private static string? OverridesJson(Dictionary<TrackField, object?> edited) =>
        edited.Count == 0 ? null : JsonSerializer.Serialize(edited.ToDictionary(e => TrackFields.Column(e.Key), e => e.Value));

    private static DateTimeOffset Time(long utcTicks) => new(utcTicks, TimeSpan.Zero);

    /// <summary>Finds tags by name (ignoring case) and creates the ones that don't exist yet.</summary>
    private sealed class TagImporter(SqliteConnection connection, IDbTransaction transaction)
    {
        private readonly Dictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _colorUse = new(StringComparer.OrdinalIgnoreCase);

        public int Created { get; private set; }

        public async Task LoadAsync()
        {
            foreach (var (id, name, color) in await connection.QueryAsync<(long, string, string)>(
                "SELECT id, name, color FROM tags", transaction: transaction))
            {
                _ids[name] = id;
                _colorUse[color] = _colorUse.GetValueOrDefault(color) + 1;
            }
        }

        /// <summary>The tag's id, or null for a blank name. A colour given here replaces the tag's current one.</summary>
        public async Task<long?> EnsureAsync(string? name, string? color = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            name = TagRepository.CleanName(name);
            color = TagRepository.IsColor(color) ? color!.ToLowerInvariant() : null;

            if (_ids.TryGetValue(name, out var id))
            {
                if (color is not null)
                {
                    await connection.ExecuteAsync("UPDATE tags SET color = @color WHERE id = @id", new { id, color }, transaction);
                }

                return id;
            }

            color ??= TagRepository.Palette.MinBy(c => _colorUse.GetValueOrDefault(c))!;
            _colorUse[color] = _colorUse.GetValueOrDefault(color) + 1;
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO tags (name, color) VALUES (@name, @color) RETURNING id", new { name, color }, transaction);
            _ids[name] = id;
            Created++;
            return id;
        }
    }

    private sealed class TrackRecord
    {
        public long Id { get; set; }

        public string Path { get; set; } = "";

        public string FileName { get; set; } = "";

        public string Fingerprint { get; set; } = "";

        public long FileSize { get; set; }

        public long DurationMs { get; set; }

        public string Title { get; set; } = "";

        public string? Artist { get; set; }

        public string? Album { get; set; }

        public string? AlbumArtist { get; set; }

        public string? Genre { get; set; }

        public int? Year { get; set; }

        public int? TrackNumber { get; set; }

        public string? Overrides { get; set; }

        public double? Bpm { get; set; }

        public string? BpmSource { get; set; }

        public double? BpmConfidence { get; set; }

        public double GainDb { get; set; }

        public double? LoudnessLufs { get; set; }

        public double? PeakDb { get; set; }

        public string? SilenceEdges { get; set; }

        public bool LoudnessAnalyzed { get; set; }

        public long? TrimStartMs { get; set; }

        public long? TrimEndMs { get; set; }

        public long PlayCount { get; set; }

        public long SkipCount { get; set; }

        public long? LastPlayedUtc { get; set; }

        public long AddedUtc { get; set; }

        public bool Flagged { get; set; }

        public bool Hidden { get; set; }
    }

    private sealed class ExistingTrack
    {
        public long Id { get; set; }

        public string Path { get; set; } = "";

        public string Fingerprint { get; set; } = "";

        public long FileSize { get; set; }

        public bool Missing { get; set; }

        public string? Overrides { get; set; }
    }

    private sealed class EventRow
    {
        public long TrackId { get; set; }

        public long AtUtc { get; set; }

        public long PlayedMs { get; set; }

        public long DurationMs { get; set; }

        public string Kind { get; set; } = "";
    }
}
