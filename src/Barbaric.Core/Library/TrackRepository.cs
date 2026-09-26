using Dapper;

namespace Barbaric.Core.Library;

public sealed class TrackRepository(LibraryDatabase database)
{
    private const string RowColumns = "t.id, t.title, t.artist, t.album, t.duration_ms, t.bpm";

    /// <summary>Returns one page of the list plus the total number of matching tracks.</summary>
    public async Task<QueryPage> QueryAsync(TrackQuery query, int offset, int limit)
    {
        var (where, parameters) = Filter(query);
        parameters.Add("offset", Math.Max(0, offset));
        parameters.Add("limit", Math.Clamp(limit, 1, 1000));

        using var connection = database.Open();
        var total = await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM tracks t {where}", parameters);
        var rows = await connection.QueryAsync<TrackRow>(
            $"SELECT {RowColumns} FROM tracks t {where} ORDER BY {OrderBy(query)} LIMIT @limit OFFSET @offset",
            parameters);

        return new QueryPage(total, rows.AsList());
    }

    /// <summary>All matching track ids in list order, used to build the play queue.</summary>
    public async Task<IReadOnlyList<long>> QueryIdsAsync(TrackQuery query)
    {
        var (where, parameters) = Filter(query);
        using var connection = database.Open();
        var ids = await connection.QueryAsync<long>($"SELECT t.id FROM tracks t {where} ORDER BY {OrderBy(query)}", parameters);
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

    private static (string Where, DynamicParameters Parameters) Filter(TrackQuery query)
    {
        var parameters = new DynamicParameters();
        var match = SearchText.ToMatchExpression(query.Text);
        if (match is null)
        {
            return ("WHERE t.missing = 0", parameters);
        }

        parameters.Add("match", match);
        return ("WHERE t.missing = 0 AND t.id IN (SELECT rowid FROM tracks_fts WHERE tracks_fts MATCH @match)", parameters);
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
            _ => $"t.artist IS NULL, t.artist COLLATE NOCASE {dir}, t.album COLLATE NOCASE {dir}, t.track_number, t.title COLLATE NOCASE",
        };

        return order + $", t.id {dir}";
    }
}
