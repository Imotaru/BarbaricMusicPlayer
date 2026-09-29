using Barbaric.Core.Analysis;
using Dapper;

namespace Barbaric.Core.Library;

public sealed class TrackRepository(LibraryDatabase database)
{
    /// <summary>Hand-entered and scaled BPMs are kept within this range.</summary>
    public const double MinManualBpm = 20;

    public const double MaxManualBpm = 400;

    /// <summary>The largest year or track number a song can be given.</summary>
    public const int MaxInfoNumber = 9999;

    /// <summary>The songs the library shows, for queries that alias <c>tracks</c> as <c>t</c>.</summary>
    internal const string Visible = "t.missing = 0 AND t.hidden = 0";

    /// <summary>Songs that carry no tags, for queries that alias <c>tracks</c> as <c>t</c>.</summary>
    internal const string Untagged = "NOT EXISTS (SELECT 1 FROM track_tags WHERE track_id = t.id)";

    private const string PendingBpm = "missing = 0 AND hidden = 0 AND bpm IS NULL AND bpm_source IS NULL";

    private const string PendingLoudness = "missing = 0 AND hidden = 0 AND loudness_analyzed = 0";

    private const string RowColumns =
        "t.id, t.title, t.artist, t.album, t.path, t.duration_ms, t.bpm, t.bpm_source, t.bpm_confidence, t.play_count, t.skip_count, " +
        "(SELECT group_concat(tag_id) FROM track_tags WHERE track_id = t.id) AS tag_id_list";

    /// <summary>Returns one page of the list plus the total number of matching tracks.</summary>
    public async Task<QueryPage> QueryAsync(TrackQuery query, int offset, int limit)
    {
        var (source, parameters) = Filter(query);
        parameters.Add("offset", Math.Max(0, offset));
        parameters.Add("limit", Math.Clamp(limit, 1, 1000));
        var columns = query switch
        {
            { Scope: TrackScope.Playing } => RowColumns + ", pool.key AS position",
            { PlaylistId: not null } => RowColumns + ", pt.position",
            _ => RowColumns,
        };

        using var connection = database.Open();
        var total = await connection.ExecuteScalarAsync<long>($"SELECT count(*) {source}", parameters);
        var rows = await connection.QueryAsync<TrackRow>(
            $"SELECT {columns} {source} ORDER BY {OrderBy(query)} LIMIT @limit OFFSET @offset",
            parameters);

        return new QueryPage(total, rows.AsList());
    }

    /// <summary>All matching track ids in list order, used to build the play queue.</summary>
    public async Task<IReadOnlyList<long>> QueryIdsAsync(TrackQuery query)
    {
        var (source, parameters) = Filter(query);
        using var connection = database.Open();
        var ids = await connection.QueryAsync<long>($"SELECT t.id {source} ORDER BY {OrderBy(query)}", parameters);
        return ids.AsList();
    }

    /// <summary>The ids of one slice of the list, for selecting ranges of rows that aren't loaded.</summary>
    public async Task<IReadOnlyList<long>> QueryIdsAsync(TrackQuery query, int offset, int limit)
    {
        var (source, parameters) = Filter(query);
        parameters.Add("offset", Math.Max(0, offset));
        parameters.Add("limit", Math.Max(0, limit));

        using var connection = database.Open();
        var ids = await connection.QueryAsync<long>(
            $"SELECT t.id {source} ORDER BY {OrderBy(query)} LIMIT @limit OFFSET @offset",
            parameters);
        return ids.AsList();
    }

    public async Task<Track?> GetAsync(long id)
    {
        using var connection = database.Open();
        return await connection.QuerySingleOrDefaultAsync<Track>("SELECT * FROM tracks WHERE id = @id", new { id });
    }

    public async Task<Track?> GetByPathAsync(string path)
    {
        using var connection = database.Open();
        return await connection.QuerySingleOrDefaultAsync<Track>(
            "SELECT * FROM tracks WHERE path = @path",
            new { path = Path.GetFullPath(path) });
    }

    public async Task SetGainAsync(long id, double gainDb)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync("UPDATE tracks SET gain_db = @gainDb WHERE id = @id", new { id, gainDb });
    }

    /// <summary>Fills in a length the file's tags didn't give, e.g. from the decoder. A known length is kept.</summary>
    public async Task SetDurationAsync(long id, long durationMs)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            "UPDATE tracks SET duration_ms = @durationMs WHERE id = @id AND duration_ms = 0",
            new { id, durationMs });
    }

    /// <summary>
    /// The given ids that can still be played from a list in <paramref name="scope"/> (not missing, and
    /// not hidden unless the list is the hidden songs), in the order given.
    /// </summary>
    public async Task<IReadOnlyList<long>> KeepPlayableAsync(IReadOnlyList<long> ids, TrackScope scope)
    {
        using var connection = database.Open();
        var kept = (await connection.QueryAsync<long>(
            """
            SELECT id FROM tracks
            WHERE id IN (SELECT value FROM json_each(@ids)) AND missing = 0 AND (hidden = 0 OR @hidden)
            """,
            new { ids = IdList.ToJson(ids), hidden = scope == TrackScope.Hidden })).ToHashSet();
        return ids.Where(kept.Contains).ToList();
    }

    /// <summary>
    /// Drops songs whose file is missing, with their tags, playlist entries and history. Songs whose
    /// file is present are left alone.
    /// </summary>
    /// <returns>How many songs were dropped.</returns>
    public async Task<int> ForgetAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        return await connection.ExecuteAsync(
            "DELETE FROM tracks WHERE id IN (SELECT value FROM json_each(@ids)) AND missing = 1",
            new { ids = IdList.ToJson(ids) });
    }

    /// <summary>Tracks the background analyzer still has to look at, newest first.</summary>
    public async Task<IReadOnlyList<(long Id, string Path)>> GetBpmPendingAsync(int limit)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<(long, string)>(
            $"SELECT id, path FROM tracks WHERE {PendingBpm} ORDER BY added_utc DESC, id DESC LIMIT @limit",
            new { limit });
        return rows.AsList();
    }

    public async Task<int> CountBpmPendingAsync()
    {
        using var connection = database.Open();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM tracks WHERE {PendingBpm}");
    }

    /// <summary>
    /// Stores an analysis result, replacing a BPM from a tag but never one set by hand. A null result
    /// records that the track was analyzed without finding a beat, so it isn't tried again.
    /// </summary>
    /// <returns>False when the track has a manual BPM (or no longer exists) and was left alone.</returns>
    public async Task<bool> SaveAnalyzedBpmAsync(long id, BpmResult? result)
    {
        using var connection = database.Open();
        var changed = await connection.ExecuteAsync(
            """
            UPDATE tracks SET bpm = @bpm, bpm_confidence = @confidence, bpm_source = 'analyzed'
            WHERE id = @id AND bpm_source IS NOT 'manual'
            """,
            new { id, bpm = result?.Bpm, confidence = result?.Confidence });
        return changed > 0;
    }

    /// <summary>Tracks whose volume hasn't been measured yet, most played first.</summary>
    public async Task<IReadOnlyList<(long Id, string Path)>> GetLoudnessPendingAsync(int limit)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<(long, string)>(
            $"SELECT id, path FROM tracks WHERE {PendingLoudness} ORDER BY play_count DESC, added_utc DESC, id DESC LIMIT @limit",
            new { limit });
        return rows.AsList();
    }

    public async Task<int> CountLoudnessPendingAsync()
    {
        using var connection = database.Open();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM tracks WHERE {PendingLoudness}");
    }

    /// <summary>Stores a volume measurement. A null result records a silent or undecodable song, played as is.</summary>
    /// <returns>False when the track no longer exists.</returns>
    public async Task<bool> SaveLoudnessAsync(long id, LoudnessResult? result)
    {
        using var connection = database.Open();
        var changed = await connection.ExecuteAsync(
            "UPDATE tracks SET loudness_lufs = @loudness, peak_db = @peak, loudness_analyzed = 1 WHERE id = @id",
            new { id, loudness = result?.LoudPartLufs, peak = result?.PeakDb });
        return changed > 0;
    }

    /// <summary>Sets the BPM by hand, within <see cref="MinManualBpm"/>–<see cref="MaxManualBpm"/>.</summary>
    public async Task SetManualBpmAsync(IEnumerable<long> ids, double bpm)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            """
            UPDATE tracks SET bpm = @bpm, bpm_confidence = NULL, bpm_source = 'manual'
            WHERE id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = IdList.ToJson(ids), bpm = Math.Round(Math.Clamp(bpm, MinManualBpm, MaxManualBpm), 2) });
    }

    /// <summary>
    /// Undoes manual and analyzed BPMs: tracks go back to the BPM in their file's tag, or to no BPM
    /// at all, which hands them back to the analyzer.
    /// </summary>
    public async Task ResetBpmAsync(IEnumerable<long> ids)
    {
        var tracks = await GetBpmInfoAsync(ids);
        using var connection = database.Open();
        foreach (var track in tracks)
        {
            var path = await connection.ExecuteScalarAsync<string>("SELECT path FROM tracks WHERE id = @Id", track);
            double? tagBpm = null;
            try
            {
                tagBpm = path is null ? null : TrackMetadataReader.Read(path).Bpm;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable right now: fall back to "unknown" and let the analyzer try later.
            }

            await connection.ExecuteAsync(
                """
                UPDATE tracks
                SET bpm = @tagBpm, bpm_confidence = NULL, bpm_source = CASE WHEN @tagBpm IS NULL THEN NULL ELSE 'tag' END
                WHERE id = @id
                """,
                new { id = track.Id, tagBpm });
        }
    }

    /// <summary>
    /// Multiplies known BPMs (e.g. ×2 or ×½ to fix an octave error) and marks them as set by hand.
    /// Tracks whose result would leave the allowed range are left alone.
    /// </summary>
    /// <returns>How many tracks changed.</returns>
    public async Task<int> ScaleBpmAsync(IEnumerable<long> ids, double factor)
    {
        using var connection = database.Open();
        return await connection.ExecuteAsync(
            """
            UPDATE tracks
            SET bpm = round(bpm * @factor, 2), bpm_confidence = NULL, bpm_source = 'manual'
            WHERE id IN (SELECT value FROM json_each(@ids))
              AND bpm IS NOT NULL AND bpm * @factor BETWEEN @min AND @max
            """,
            new { ids = IdList.ToJson(ids), factor, min = MinManualBpm, max = MaxManualBpm });
    }

    public async Task<IReadOnlyList<BpmInfo>> GetBpmInfoAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<BpmInfo>(
            "SELECT id, bpm, bpm_source AS BpmSource, bpm_confidence AS BpmConfidence FROM tracks WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids) });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<TrackInfo>> GetInfoAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<InfoRow>(
            """
            SELECT id, file_name, title, artist, album, album_artist, genre, year, track_number, overrides
            FROM tracks WHERE id IN (SELECT value FROM json_each(@ids))
            """,
            new { ids = IdList.ToJson(ids) });

        return rows.Select(r => new TrackInfo(
            r.Id, r.FileName, r.Title, r.Artist, r.Album, r.AlbumArtist, r.Genre, r.Year, r.TrackNumber, Overridden(r.Overrides)))
            .ToList();
    }

    /// <summary>
    /// Overrides fields of songs with the user's values, which then stay through rescans until reset.
    /// Text is trimmed and blank text clears the field, except for the title, which is required.
    /// </summary>
    /// <param name="values">Strings for text fields, whole numbers (or null) for the year and track number.</param>
    /// <exception cref="ArgumentException">A value is missing where required, of the wrong type or out of range.</exception>
    public async Task SetInfoAsync(IEnumerable<long> ids, IReadOnlyDictionary<TrackField, object?> values)
    {
        var normalized = values.Select(v => (Field: v.Key, Value: Normalize(v.Key, v.Value))).ToList();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var (field, value) in normalized)
        {
            var column = TrackFields.Column(field);
            await connection.ExecuteAsync(
                $$"""
                UPDATE tracks SET {{column}} = @value, overrides = json_set(coalesce(overrides, '{}'), '$.{{column}}', @value)
                WHERE id IN (SELECT value FROM json_each(@ids))
                """,
                new { ids = IdList.ToJson(ids), value },
                transaction);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Drops overrides, putting the file's tags back. A file that can't be read right now keeps what
    /// it shows until the next scan, which then fills in its tags.
    /// </summary>
    public async Task ResetInfoAsync(IEnumerable<long> ids, IReadOnlyCollection<TrackField> fields)
    {
        if (fields.Count == 0)
        {
            return;
        }

        var paths = await GetPathsAsync(ids);
        var keys = string.Join(", ", fields.Select(f => $"'$.{TrackFields.Column(f)}'"));
        using var connection = database.Open();
        foreach (var (id, path) in paths)
        {
            TrackMetadata? tags = null;
            try
            {
                tags = TrackMetadataReader.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreachable: the next scan reads the tags instead (see below).
            }

            var parameters = new DynamicParameters(new { id });
            var set = new List<string> { $"overrides = nullif(json_remove(overrides, {keys}), '{{}}')" };
            if (tags is null)
            {
                set.Add("modified_utc = 0");
            }
            else
            {
                foreach (var field in fields)
                {
                    set.Add($"{TrackFields.Column(field)} = @{field}");
                    parameters.Add(field.ToString(), TrackFields.From(tags, field));
                }
            }

            await connection.ExecuteAsync($"UPDATE tracks SET {string.Join(", ", set)} WHERE id = @id", parameters);
        }
    }

    private async Task<IReadOnlyList<(long Id, string Path)>> GetPathsAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<(long, string)>(
            "SELECT id, path FROM tracks WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids) });
        return rows.AsList();
    }

    private static object? Normalize(TrackField field, object? value)
    {
        if (TrackFields.IsNumber(field))
        {
            if (value is null)
            {
                return null;
            }

            var number = value switch
            {
                int i => i,
                long l => l,
                _ => throw new ArgumentException($"The {field} must be a whole number."),
            };
            return number is >= 1 and <= MaxInfoNumber
                ? (int)number
                : throw new ArgumentException($"The {field} must be between 1 and {MaxInfoNumber}.");
        }

        if (value is not (null or string))
        {
            throw new ArgumentException($"The {field} must be text.");
        }

        var text = string.IsNullOrWhiteSpace((string?)value) ? null : ((string)value).Trim();
        return text is null && field == TrackField.Title ? throw new ArgumentException("A song needs a title.") : text;
    }

    internal static IReadOnlyList<TrackField> Overridden(string? overrides)
    {
        if (string.IsNullOrEmpty(overrides))
        {
            return [];
        }

        using var json = System.Text.Json.JsonDocument.Parse(overrides);
        var keys = json.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        return TrackFields.All.Where(f => keys.Contains(TrackFields.Column(f))).ToList();
    }

    private sealed class InfoRow
    {
        public long Id { get; set; }

        public string FileName { get; set; } = "";

        public string Title { get; set; } = "";

        public string? Artist { get; set; }

        public string? Album { get; set; }

        public string? AlbumArtist { get; set; }

        public string? Genre { get; set; }

        public int? Year { get; set; }

        public int? TrackNumber { get; set; }

        public string? Overrides { get; set; }
    }

    /// <summary>Builds the <c>FROM … WHERE …</c> part of the list query. Every user value goes in as a parameter.</summary>
    private static (string Source, DynamicParameters Parameters) Filter(TrackQuery query)
    {
        var parameters = new DynamicParameters();
        var from = "FROM tracks t";
        var where = new List<string>
        {
            query.Scope switch
            {
                TrackScope.Suggested => $"{Visible} AND t.flagged = 1",
                TrackScope.Hidden => "t.missing = 0 AND t.hidden = 1",
                TrackScope.Missing => "t.missing = 1",
                TrackScope.Playing => "t.missing = 0",
                _ => Visible,
            },
        };

        // The pool's order is the order of the list it was drawn from, so it sorts by position.
        if (query.Scope == TrackScope.Playing)
        {
            from += " JOIN json_each(@ids) pool ON pool.value = t.id";
            parameters.Add("ids", IdList.ToJson(query.Ids ?? []));
        }

        if (query.PlaylistId is { } playlistId)
        {
            from += " JOIN playlist_tracks pt ON pt.track_id = t.id AND pt.playlist_id = @playlistId";
            parameters.Add("playlistId", playlistId);
        }

        if (SearchText.ToMatchExpression(query.Text) is { } match)
        {
            where.Add("t.id IN (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH @match)");
            parameters.Add("match", match);
        }

        if (query.Filter is { } filter)
        {
            if (filter.Artist is { } artist)
            {
                where.Add("t.artist = @artist COLLATE NOCASE");
                parameters.Add("artist", artist);
            }

            var tagWhere = new List<string>();
            var all = filter.AllTags.Distinct().ToList();
            if (all.Count > 0)
            {
                tagWhere.Add("(SELECT count(*) FROM track_tags WHERE track_id = t.id AND tag_id IN @allTags) = @allCount");
                parameters.Add("allTags", all);
                parameters.Add("allCount", all.Count);
            }

            if (filter.AnyTags.Count > 0)
            {
                tagWhere.Add("t.id IN (SELECT track_id FROM track_tags WHERE tag_id IN @anyTags)");
                parameters.Add("anyTags", filter.AnyTags.Distinct().ToList());
            }

            if (filter.NoneTags.Count > 0)
            {
                tagWhere.Add("t.id NOT IN (SELECT track_id FROM track_tags WHERE tag_id IN @noneTags)");
                parameters.Add("noneTags", filter.NoneTags.Distinct().ToList());
            }

            if (filter.Untagged)
            {
                tagWhere.Add(Untagged);
            }

            // Kept tracks bypass only the tag clauses; scope, search and BPM still apply to them.
            if (tagWhere.Count > 0 && query.KeepIds is { Count: > 0 } keep)
            {
                where.Add($"(({string.Join(" AND ", tagWhere)}) OR t.id IN @keepIds)");
                parameters.Add("keepIds", keep.Distinct().ToList());
            }
            else
            {
                where.AddRange(tagWhere);
            }

            AddBpmRange(where, parameters, "filterBpm", new BpmRange(filter.BpmMin, filter.BpmMax, filter.IncludeUnknownBpm));
        }

        // The lens and a playlist's own range are separate clauses, so together they intersect.
        if (query.Bpm is { } lens)
        {
            AddBpmRange(where, parameters, "lensBpm", lens);
        }

        return ($"{from} WHERE {string.Join(" AND ", where)}", parameters);
    }

    private static void AddBpmRange(List<string> where, DynamicParameters parameters, string prefix, BpmRange range)
    {
        if (range.IsOpen)
        {
            return;
        }

        var bounds = new List<string>();
        if (range.Min is { } min)
        {
            bounds.Add($"t.bpm >= @{prefix}Min");
            parameters.Add($"{prefix}Min", min);
        }

        if (range.Max is { } max)
        {
            bounds.Add($"t.bpm <= @{prefix}Max");
            parameters.Add($"{prefix}Max", max);
        }

        var clause = string.Join(" AND ", bounds);
        where.Add(range.IncludeUnknown ? $"(({clause}) OR t.bpm IS NULL)" : $"({clause})");
    }

    // Only fixed SQL fragments end up here, never user input. Unknown values sort last.
    private static string OrderBy(TrackQuery query)
    {
        var dir = query.Descending ? "DESC" : "ASC";
        var order = query.Sort switch
        {
            TrackSort.Title => $"t.title COLLATE NOCASE {dir}, t.artist COLLATE NOCASE",
            TrackSort.Album => $"t.album IS NULL, t.album COLLATE NOCASE {dir}, t.track_number, t.title COLLATE NOCASE",
            TrackSort.Duration => $"t.duration_ms {dir}, t.title COLLATE NOCASE",
            TrackSort.Bpm => $"t.bpm IS NULL, t.bpm {dir}, t.title COLLATE NOCASE",
            TrackSort.Added => $"t.added_utc {dir}",
            TrackSort.Plays => $"t.play_count {dir}, t.title COLLATE NOCASE",
            TrackSort.Skips => $"t.skip_count {dir}, t.title COLLATE NOCASE",
            TrackSort.Position when query.Scope == TrackScope.Playing => $"pool.key {dir}",
            TrackSort.Position when query.PlaylistId is not null => $"pt.position {dir}",
            _ => $"t.artist IS NULL, t.artist COLLATE NOCASE {dir}, t.album COLLATE NOCASE {dir}, t.track_number, t.title COLLATE NOCASE",
        };

        return order + $", t.id {dir}";
    }
}
