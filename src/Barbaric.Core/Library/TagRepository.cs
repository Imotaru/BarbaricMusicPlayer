using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Barbaric.Core.Library;

/// <summary>A tag with the number of (non-missing) tracks that carry it.</summary>
public sealed record TagInfo(long Id, string Name, string Color, long Count);

/// <summary>User-defined labels on tracks. A track can have any number of tags.</summary>
public sealed partial class TagRepository(LibraryDatabase database)
{
    public const int MaxNameLength = 60;

    /// <summary>Colours offered for tags. New tags take the least-used one.</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#ef6f6c", "#f5a524", "#d6d24a", "#7cd05a", "#3ecfb2", "#4fa3ff", "#9b7bff", "#e86bd8",
    ];

    private const string SelectInfo = $"""
        SELECT g.id, g.name, g.color,
               (SELECT count(*) FROM track_tags tt JOIN tracks t ON t.id = tt.track_id
                WHERE tt.tag_id = g.id AND {TrackRepository.Visible}) AS count
        FROM tags g
        """;

    public async Task<IReadOnlyList<TagInfo>> GetAllAsync()
    {
        using var connection = database.Open();
        var tags = await connection.QueryAsync<TagInfo>($"{SelectInfo} ORDER BY g.name COLLATE NOCASE");
        return tags.AsList();
    }

    public async Task<TagInfo?> GetAsync(long id)
    {
        using var connection = database.Open();
        return await connection.QuerySingleOrDefaultAsync<TagInfo>($"{SelectInfo} WHERE g.id = @id", new { id });
    }

    /// <summary>How many of the given tracks carry each tag; tags on none of them are left out.</summary>
    public async Task<IReadOnlyDictionary<long, long>> GetUsageAsync(IEnumerable<long> trackIds)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<(long TagId, long Count)>(
            """
            SELECT tag_id, count(*) FROM track_tags
            WHERE track_id IN (SELECT value FROM json_each(@trackIds))
            GROUP BY tag_id
            """,
            new { trackIds = IdList.ToJson(trackIds) });
        return rows.ToDictionary(r => r.TagId, r => r.Count);
    }

    /// <summary>Creates a tag, or returns the existing one when the name is taken (ignoring case).</summary>
    public async Task<TagInfo> CreateAsync(string name, string? color = null)
    {
        name = CleanName(name);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var id = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM tags WHERE name = @name", new { name }, transaction);
        if (id is null)
        {
            color = color is null ? await LeastUsedColorAsync(connection, transaction) : CleanColor(color);
            id = await connection.ExecuteScalarAsync<long>(
                "INSERT INTO tags (name, color) VALUES (@name, @color) RETURNING id", new { name, color }, transaction);
        }

        transaction.Commit();
        return (await GetAsync(id.Value))!;
    }

    public async Task RenameAsync(long id, string name)
    {
        name = CleanName(name);
        using var connection = database.Open();
        var clash = await connection.ExecuteScalarAsync<long?>(
            "SELECT id FROM tags WHERE name = @name AND id <> @id", new { id, name });
        if (clash is not null)
        {
            throw new InvalidOperationException($"There is already a tag called \"{name}\".");
        }

        await connection.ExecuteAsync("UPDATE tags SET name = @name WHERE id = @id", new { id, name });
    }

    public async Task SetColorAsync(long id, string color)
    {
        color = CleanColor(color);
        using var connection = database.Open();
        await connection.ExecuteAsync("UPDATE tags SET color = @color WHERE id = @id", new { id, color });
    }

    /// <summary>Deletes a tag, removes it from every track and from every saved filter that used it.</summary>
    public async Task DeleteAsync(long id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM tags WHERE id = @id", new { id }, transaction);
        await PlaylistRepository.RemoveTagFromFiltersAsync(connection, transaction, id);
        transaction.Commit();
    }

    /// <summary>Tags the tracks. Tracks that already have the tag are left alone.</summary>
    public async Task AddToTracksAsync(long tagId, IEnumerable<long> trackIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            """
            INSERT OR IGNORE INTO track_tags (track_id, tag_id)
            SELECT @trackId, @tagId WHERE EXISTS (SELECT 1 FROM tracks WHERE id = @trackId)
            """,
            trackIds.Distinct().Select(trackId => new { trackId, tagId }),
            transaction);
        transaction.Commit();
    }

    public async Task RemoveFromTracksAsync(long tagId, IEnumerable<long> trackIds)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            "DELETE FROM track_tags WHERE track_id = @trackId AND tag_id = @tagId",
            trackIds.Distinct().Select(trackId => new { trackId, tagId }),
            transaction);
        transaction.Commit();
    }

    private static async Task<string> LeastUsedColorAsync(SqliteConnection connection, IDbTransaction transaction)
    {
        var used = (await connection.QueryAsync<string>("SELECT color FROM tags", transaction: transaction))
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return Palette.MinBy(c => used.GetValueOrDefault(c))!;
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A tag needs a name.");
        }

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    // The colour ends up in the UI's styles, so only plain hex colours are accepted.
    private static string CleanColor(string color) =>
        HexColor().IsMatch(color) ? color.ToLowerInvariant() : throw new ArgumentException($"'{color}' is not a #rrggbb colour.");

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
