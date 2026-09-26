using Dapper;

namespace Barbaric.Core.Library;

public sealed class TrackRepository(LibraryDatabase database)
{
    private const string RowColumns =
        "t.id, t.title, t.artist, t.album, t.duration_ms, t.bpm, " +
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

    /// <summary>Builds the <c>FROM … WHERE …</c> part of the list query. Every user value goes in as a parameter.</summary>
    private static (string Source, DynamicParameters Parameters) Filter(TrackQuery query)
    {
        var parameters = new DynamicParameters();
        var from = "FROM tracks t";
        var where = new List<string> { "t.missing = 0" };

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

            if (filter.BpmMin is { } bpmMin)
            {
                where.Add("t.bpm >= @bpmMin");
                parameters.Add("bpmMin", bpmMin);
            }

            if (filter.BpmMax is { } bpmMax)
            {
                where.Add("t.bpm <= @bpmMax");
                parameters.Add("bpmMax", bpmMax);
            }
        }

        return ($"{from} WHERE {string.Join(" AND ", where)}", parameters);
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
            TrackSort.Position when query.PlaylistId is not null => $"pt.position {dir}",
            _ => $"t.artist IS NULL, t.artist COLLATE NOCASE {dir}, t.album COLLATE NOCASE {dir}, t.track_number, t.title COLLATE NOCASE",
        };

        return order + $", t.id {dir}";
    }
}
