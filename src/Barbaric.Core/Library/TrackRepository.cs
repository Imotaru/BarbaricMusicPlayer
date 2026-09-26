using Barbaric.Core.Analysis;
using Dapper;

namespace Barbaric.Core.Library;

public sealed class TrackRepository(LibraryDatabase database)
{
    /// <summary>Hand-entered and scaled BPMs are kept within this range.</summary>
    public const double MinManualBpm = 20;

    public const double MaxManualBpm = 400;

    /// <summary>The songs the library shows, for queries that alias <c>tracks</c> as <c>t</c>.</summary>
    internal const string Visible = "t.missing = 0 AND t.hidden = 0";

    private const string PendingBpm = "missing = 0 AND hidden = 0 AND bpm IS NULL AND bpm_source IS NULL";

    private const string RowColumns =
        "t.id, t.title, t.artist, t.album, t.duration_ms, t.bpm, t.bpm_source, t.bpm_confidence, t.play_count, t.skip_count, " +
        "(SELECT group_concat(tag_id) FROM track_tags WHERE track_id = t.id) AS tag_id_list";

    /// <summary>Returns one page of the list plus the total number of matching tracks.</summary>
    public async Task<QueryPage> QueryAsync(TrackQuery query, int offset, int limit)
    {
        var (source, parameters) = Filter(query);
        parameters.Add("offset", Math.Max(0, offset));
        parameters.Add("limit", Math.Clamp(limit, 1, 1000));
        var columns = query.PlaylistId is null ? RowColumns : RowColumns + ", pt.position";

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
                _ => Visible,
            },
        };

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
            var all = filter.AllTags.Distinct().ToList();
            if (all.Count > 0)
            {
                where.Add("(SELECT count(*) FROM track_tags WHERE track_id = t.id AND tag_id IN @allTags) = @allCount");
                parameters.Add("allTags", all);
                parameters.Add("allCount", all.Count);
            }

            if (filter.AnyTags.Count > 0)
            {
                where.Add("t.id IN (SELECT track_id FROM track_tags WHERE tag_id IN @anyTags)");
                parameters.Add("anyTags", filter.AnyTags.Distinct().ToList());
            }

            if (filter.NoneTags.Count > 0)
            {
                where.Add("t.id NOT IN (SELECT track_id FROM track_tags WHERE tag_id IN @noneTags)");
                parameters.Add("noneTags", filter.NoneTags.Distinct().ToList());
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
            TrackSort.Position when query.PlaylistId is not null => $"pt.position {dir}",
            _ => $"t.artist IS NULL, t.artist COLLATE NOCASE {dir}, t.album COLLATE NOCASE {dir}, t.track_number, t.title COLLATE NOCASE",
        };

        return order + $", t.id {dir}";
    }
}
