using Barbaric.Core.Playback;
using Dapper;

namespace Barbaric.Core.Library;

/// <summary>How many songs the extra library views hold.</summary>
public sealed record LibraryCounts(long Suggested, long Hidden);

/// <summary>Listening history and what the user decided to do about songs they skip.</summary>
public sealed class PlayStatsRepository(LibraryDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Logs one listen and updates the song's counts and flag. The flag follows the counts both
    /// ways, so a song that wins the user back leaves the suggestions by itself.
    /// </summary>
    /// <returns>Whether the song's flag changed, or null if the track no longer exists.</returns>
    public async Task<bool?> RecordAsync(long trackId, long playedMs, long durationMs, PlayKind kind)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var counts = await connection.QuerySingleOrDefaultAsync<Counts>(
            "SELECT play_count AS plays, skip_count AS skips, flagged FROM tracks WHERE id = @trackId",
            new { trackId },
            transaction);
        if (counts is null)
        {
            return null;
        }

        var plays = counts.Plays + (kind == PlayKind.Complete ? 1 : 0);
        var skips = counts.Skips + (kind == PlayKind.Skip ? 1 : 0);
        var flagged = Listening.ShouldFlag(plays, skips);
        var now = _clock.GetUtcNow().UtcTicks;

        await connection.ExecuteAsync(
            """
            INSERT INTO play_events (track_id, at_utc, played_ms, duration_ms, kind)
            VALUES (@trackId, @now, @playedMs, @durationMs, @kind);

            UPDATE tracks
            SET play_count = @plays, skip_count = @skips, flagged = @flagged, last_played_utc = @now
            WHERE id = @trackId;
            """,
            new
            {
                trackId,
                now,
                playedMs = Math.Max(0, playedMs),
                durationMs = Math.Max(0, durationMs),
                kind = kind.ToString().ToLowerInvariant(),
                plays,
                skips,
                flagged,
            },
            transaction);

        transaction.Commit();
        return flagged != (counts.Flagged != 0);
    }

    /// <summary>Takes songs off the suggestions by starting their counts over. The event log stays.</summary>
    public async Task KeepAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            "UPDATE tracks SET play_count = 0, skip_count = 0, flagged = 0 WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids) });
    }

    public async Task SetHiddenAsync(IEnumerable<long> ids, bool hidden)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            "UPDATE tracks SET hidden = @hidden WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids), hidden });
    }

    /// <summary>
    /// Marks deleted files as missing rather than dropping the rows, so a file restored from the
    /// Recycle Bin comes back on the next scan with its history.
    /// </summary>
    public async Task MarkMissingAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        await connection.ExecuteAsync(
            "UPDATE tracks SET missing = 1 WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids) });
    }

    public async Task<IReadOnlyList<ShuffleStats>> GetShuffleStatsAsync(IEnumerable<long> ids)
    {
        using var connection = database.Open();
        var rows = await connection.QueryAsync<(long Id, long Plays, long Skips, long? LastPlayed)>(
            "SELECT id, play_count, skip_count, last_played_utc FROM tracks WHERE id IN (SELECT value FROM json_each(@ids))",
            new { ids = IdList.ToJson(ids) });
        return rows.Select(r => new ShuffleStats(r.Id, r.Plays, r.Skips, r.LastPlayed)).ToList();
    }

    public async Task<LibraryCounts> GetCountsAsync()
    {
        using var connection = database.Open();
        return await connection.QuerySingleAsync<LibraryCounts>(
            $"""
            SELECT
                (SELECT count(*) FROM tracks t WHERE {TrackRepository.Visible} AND t.flagged = 1) AS suggested,
                (SELECT count(*) FROM tracks t WHERE t.missing = 0 AND t.hidden = 1) AS hidden
            """);
    }

    private sealed record Counts(long Plays, long Skips, long Flagged);
}
