using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Barbaric.Core.Library;

/// <summary>
/// A playlist for the sidebar. Filter playlists carry their saved <see cref="Query"/>; manual ones
/// carry the number of (non-missing) tracks in them.
/// </summary>
public sealed record PlaylistInfo(long Id, string Name, string Kind, long? Count, TrackQuery? Query);

/// <summary>
/// Playlists come in two kinds: <see cref="FilterKind"/> is a saved view (search, filter and sort)
/// that is re-run every time, and <see cref="ManualKind"/> is a hand-picked, ordered list of tracks
/// in which each track appears at most once.
/// </summary>
public sealed class PlaylistRepository(LibraryDatabase database)
{
    public const string FilterKind = "filter";
    public const string ManualKind = "manual";
    public const int MaxNameLength = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<PlaylistInfo>> GetAllAsync()
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<PlaylistRecord>(
            $"""
            SELECT p.id, p.name, p.kind, p.filter_json,
                   CASE WHEN p.kind = 'manual' THEN
                       (SELECT count(*) FROM playlist_tracks pt JOIN tracks t ON t.id = pt.track_id
                        WHERE pt.playlist_id = p.id AND {TrackRepository.Visible})
                   END AS count
            FROM playlists p
            ORDER BY p.position, p.id
            """);
        return rows.Select(r => r.ToInfo()).ToList();
    }

    public async Task<PlaylistInfo?> GetAsync(long id) => (await GetAllAsync()).FirstOrDefault(p => p.Id == id);

    /// <summary>Saves a view as a filter playlist. Only its search, filter and sort are kept.</summary>
    public Task<long> CreateFilterAsync(string name, TrackQuery query) =>
        InsertAsync(name, FilterKind, SerializeQuery(query), []);

    public Task<long> CreateManualAsync(string name, IEnumerable<long>? trackIds = null) =>
        InsertAsync(name, ManualKind, null, trackIds ?? []);

    public async Task RenameAsync(long id, string name)
    {
        name = CleanName(name);
        using var connection = database.Open();
        await connection.ExecuteAsync("UPDATE playlists SET name = @name WHERE id = @id", new { id, name });
    }

    public async Task UpdateFilterAsync(long id, TrackQuery query)
    {
        using var connection = database.Open();
        var changed = await connection.ExecuteAsync(
            "UPDATE playlists SET filter_json = @json WHERE id = @id AND kind = 'filter'",
            new { id, json = SerializeQuery(query) });
        if (changed == 0)
        {
            throw new InvalidOperationException("Only filter playlists have a saved view.");
        }
    }

    public async Task DeleteAsync(long id)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync("DELETE FROM playlists WHERE id = @id", new { id });
    }

    /// <summary>Appends tracks in the given order, skipping ones already in the playlist. Returns how many were added.</summary>
    public async Task<int> AddTracksAsync(long id, IEnumerable<long> trackIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await EnsureManualAsync(connection, transaction, id);
        var added = await AppendAsync(connection, transaction, id, trackIds);
        transaction.Commit();
        return added;
    }

    public async Task RemoveTracksAsync(long id, IEnumerable<long> trackIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await EnsureManualAsync(connection, transaction, id);
        await connection.ExecuteAsync(
            "DELETE FROM playlist_tracks WHERE playlist_id = @id AND track_id IN (SELECT value FROM json_each(@trackIds))",
            new { id, trackIds = IdList.ToJson(trackIds) },
            transaction);
        var order = await GetOrderAsync(connection, transaction, id);
        await WriteOrderAsync(connection, transaction, id, order);
        transaction.Commit();
    }

    /// <summary>
    /// Moves the given tracks, as one block in their current relative order, so the block starts at
    /// <paramref name="toIndex"/> in the resulting playlist.
    /// </summary>
    public async Task MoveTracksAsync(long id, IEnumerable<long> trackIds, int toIndex)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await EnsureManualAsync(connection, transaction, id);

        var moving = trackIds.ToHashSet();
        var order = await GetOrderAsync(connection, transaction, id);
        var block = order.Where(moving.Contains).ToList();
        var rest = order.Where(t => !moving.Contains(t)).ToList();
        rest.InsertRange(Math.Clamp(toIndex, 0, rest.Count), block);

        await WriteOrderAsync(connection, transaction, id, rest);
        transaction.Commit();
    }

    /// <summary>Drops a deleted tag from every saved filter so those playlists keep working.</summary>
    internal static async Task RemoveTagFromFiltersAsync(SqliteConnection connection, IDbTransaction transaction, long tagId)
    {
        var playlists = await connection.QueryAsync<(long Id, string Json)>(
            "SELECT id, filter_json FROM playlists WHERE kind = 'filter' AND filter_json IS NOT NULL",
            transaction: transaction);

        foreach (var (id, json) in playlists)
        {
            if (DeserializeQuery(json) is not { Filter: { } filter } query)
            {
                continue;
            }

            var cleaned = filter with
            {
                AllTags = [.. filter.AllTags.Where(t => t != tagId)],
                AnyTags = [.. filter.AnyTags.Where(t => t != tagId)],
                NoneTags = [.. filter.NoneTags.Where(t => t != tagId)],
            };
            if (cleaned.AllTags.Count + cleaned.AnyTags.Count + cleaned.NoneTags.Count
                != filter.AllTags.Count + filter.AnyTags.Count + filter.NoneTags.Count)
            {
                await connection.ExecuteAsync(
                    "UPDATE playlists SET filter_json = @json WHERE id = @id",
                    new { id, json = SerializeQuery(query with { Filter = cleaned }) },
                    transaction);
            }
        }
    }

    internal static string SerializeQuery(TrackQuery query) =>
        JsonSerializer.Serialize(
            query with { PlaylistId = null, Bpm = null, Scope = TrackScope.Library, Filter = query.Filter is { IsEmpty: false } ? query.Filter : null },
            JsonOptions);

    /// <summary>Reads a saved view. A damaged one comes back as the plain library view rather than failing.</summary>
    internal static TrackQuery? DeserializeQuery(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrackQuery>(json, JsonOptions) ?? new TrackQuery();
        }
        catch (JsonException)
        {
            return new TrackQuery();
        }
    }

    private async Task<long> InsertAsync(string name, string kind, string? json, IEnumerable<long> trackIds)
    {
        name = CleanName(name);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO playlists (name, kind, filter_json, position)
            VALUES (@name, @kind, @json, (SELECT coalesce(max(position) + 1, 0) FROM playlists))
            RETURNING id
            """,
            new { name, kind, json },
            transaction);
        await AppendAsync(connection, transaction, id, trackIds);
        transaction.Commit();
        return id;
    }

    private static async Task<int> AppendAsync(SqliteConnection connection, IDbTransaction transaction, long id, IEnumerable<long> trackIds)
    {
        var present = (await GetOrderAsync(connection, transaction, id)).ToHashSet();
        var next = present.Count == 0
            ? 0
            : await connection.ExecuteScalarAsync<int>(
                "SELECT max(position) + 1 FROM playlist_tracks WHERE playlist_id = @id", new { id }, transaction);

        var added = 0;
        foreach (var trackId in trackIds)
        {
            if (!present.Add(trackId))
            {
                continue;
            }

            added += await connection.ExecuteAsync(
                """
                INSERT INTO playlist_tracks (playlist_id, track_id, position)
                SELECT @id, @trackId, @position WHERE EXISTS (SELECT 1 FROM tracks WHERE id = @trackId)
                """,
                new { id, trackId, position = next + added },
                transaction);
        }

        return added;
    }

    private static async Task<List<long>> GetOrderAsync(SqliteConnection connection, IDbTransaction transaction, long id) =>
        (await connection.QueryAsync<long>(
            "SELECT track_id FROM playlist_tracks WHERE playlist_id = @id ORDER BY position",
            new { id },
            transaction)).AsList();

    private static Task WriteOrderAsync(SqliteConnection connection, IDbTransaction transaction, long id, IReadOnlyList<long> order) =>
        connection.ExecuteAsync(
            "UPDATE playlist_tracks SET position = @position WHERE playlist_id = @id AND track_id = @trackId",
            order.Select((trackId, position) => new { id, trackId, position }),
            transaction);

    private static async Task EnsureManualAsync(SqliteConnection connection, IDbTransaction transaction, long id)
    {
        var kind = await connection.ExecuteScalarAsync<string?>(
            "SELECT kind FROM playlists WHERE id = @id", new { id }, transaction);
        if (kind != ManualKind)
        {
            throw new InvalidOperationException(kind is null
                ? "That playlist no longer exists."
                : "Songs can only be added to or arranged in a manual playlist.");
        }
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A playlist needs a name.");
        }

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    private sealed class PlaylistRecord
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string Kind { get; set; } = "";

        public string? FilterJson { get; set; }

        public long? Count { get; set; }

        public PlaylistInfo ToInfo() => new(Id, Name, Kind, Count, DeserializeQuery(FilterJson));
    }
}
