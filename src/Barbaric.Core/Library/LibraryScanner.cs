using System.Collections.Frozen;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Barbaric.Core.Library;

public sealed record ScanProgress(int Processed, int Total);

public sealed record ScanResult(int Added, int Updated, int Moved, int Missing, int Unchanged, int Failed);

/// <summary>
/// Brings the <c>tracks</c> table in line with the files in the library folders.
/// Tracks keep their id (and with it gain, stats and later tags) when a file is moved or renamed,
/// and files that disappear are hidden rather than deleted.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase database, TimeProvider? clock = null)
{
    public static readonly FrozenSet<string> AudioExtensions =
        FrozenSet.ToFrozenSet([".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma"], StringComparer.OrdinalIgnoreCase);

    private const int BatchSize = 200;

    private const string InsertSql = """
        INSERT INTO tracks (path, file_name, fingerprint, file_size, modified_utc, title, artist, album, album_artist,
                            genre, year, track_number, duration_ms, bpm, bpm_source, added_utc)
        VALUES (@Path, @FileName, @Fingerprint, @FileSize, @ModifiedUtc, @Title, @Artist, @Album, @AlbumArtist,
                @Genre, @Year, @TrackNumber, @DurationMs, @Bpm, CASE WHEN @Bpm IS NULL THEN NULL ELSE 'tag' END, @AddedUtc)
        """;

    // A BPM that was measured or set by hand wins over whatever the file's tag says. An analysis that
    // found no beat (analyzed, bpm NULL) gives way to a tag that turns up later.
    private const string TagWins =
        "(bpm_source IS NULL OR bpm_source = 'tag' OR (bpm_source = 'analyzed' AND bpm IS NULL AND @Bpm IS NOT NULL))";

    private const string UpdateSql = $"""
        UPDATE tracks SET
            path = @Path, file_name = @FileName, fingerprint = @Fingerprint, file_size = @FileSize,
            modified_utc = @ModifiedUtc, title = @Title, artist = @Artist, album = @Album,
            album_artist = @AlbumArtist, genre = @Genre, year = @Year, track_number = @TrackNumber,
            duration_ms = @DurationMs, missing = 0,
            bpm_confidence = CASE WHEN {TagWins} THEN NULL ELSE bpm_confidence END,
            bpm_source = CASE WHEN {TagWins} THEN CASE WHEN @Bpm IS NULL THEN NULL ELSE 'tag' END ELSE bpm_source END,
            bpm = CASE WHEN {TagWins} THEN @Bpm ELSE bpm END
        WHERE id = @Id
        """;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(progress, cancellationToken), cancellationToken);

    private ScanResult Scan(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var connection = database.Open();
        var roots = connection.Query<string>("SELECT path FROM library_folders").AsList();
        var existing = connection
            .Query<KnownFile>("SELECT id, path, fingerprint, file_size, modified_utc, missing FROM tracks")
            .ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var files = FindAudioFiles(roots);

        int added = 0, updated = 0, moved = 0, unchanged = 0, failed = 0, processed = 0;
        var seen = new HashSet<long>();
        var newFiles = new List<string>();
        using var writer = new BatchWriter(connection);

        void Report() => progress?.Report(new ScanProgress(++processed, files.Count));

        // Pass 1: files at known paths. Unchanged ones cost a single stat call.
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!existing.TryGetValue(path, out var known))
            {
                newFiles.Add(path);
                continue;
            }

            // A file that is merely locked right now must not be treated as gone.
            seen.Add(known.Id);
            try
            {
                var info = new FileInfo(path);
                if (!known.Missing && known.FileSize == info.Length && known.ModifiedUtc == info.LastWriteTimeUtc.Ticks)
                {
                    unchanged++;
                }
                else
                {
                    writer.Execute(UpdateSql, Parameters(ReadFile(info), known.Id));
                    updated++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }

            Report();
        }

        // Tracks whose file is no longer where we left it, keyed by content: candidates for a move.
        var vanished = existing.Values
            .Where(t => !seen.Contains(t.Id))
            .GroupBy(t => (t.Fingerprint, t.FileSize))
            .ToDictionary(g => g.Key, g => new Queue<KnownFile>(g));

        // Pass 2: new paths are either moved/renamed tracks or brand-new songs.
        foreach (var path in newFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = ReadFile(new FileInfo(path));
                if (vanished.TryGetValue((file.Fingerprint, file.FileSize), out var candidates) && candidates.TryDequeue(out var original))
                {
                    writer.Execute(UpdateSql, Parameters(file, original.Id));
                    moved++;
                }
                else
                {
                    writer.Execute(InsertSql, Parameters(file));
                    added++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }

            Report();
        }

        var missing = 0;
        foreach (var gone in vanished.Values.SelectMany(q => q).Where(t => !t.Missing))
        {
            writer.Execute("UPDATE tracks SET missing = 1 WHERE id = @Id", new { gone.Id });
            missing++;
        }

        writer.Commit();
        return new ScanResult(added, updated, moved, missing, unchanged, failed);
    }

    private static List<string> FindAudioFiles(IEnumerable<string> roots)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        // Overlapping folders (one inside another) must not produce duplicates.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (AudioExtensions.Contains(Path.GetExtension(file)))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
        }

        return [.. files];
    }

    private static ScannedFile ReadFile(FileInfo info)
    {
        var metadata = TrackMetadataReader.Read(info.FullName);
        return new ScannedFile(
            info.FullName,
            info.Name,
            FileFingerprint.Compute(info.FullName),
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            metadata);
    }

    private DynamicParameters Parameters(ScannedFile file, long? id = null)
    {
        var m = file.Metadata;
        var parameters = new DynamicParameters(new
        {
            file.Path,
            file.FileName,
            file.Fingerprint,
            file.FileSize,
            file.ModifiedUtc,
            m.Title,
            m.Artist,
            m.Album,
            m.AlbumArtist,
            m.Genre,
            m.Year,
            m.TrackNumber,
            m.DurationMs,
            m.Bpm,
            AddedUtc = _clock.GetUtcNow().UtcTicks,
        });

        if (id is not null)
        {
            parameters.Add("Id", id);
        }

        return parameters;
    }

    private sealed record ScannedFile(string Path, string FileName, string Fingerprint, long FileSize, long ModifiedUtc, TrackMetadata Metadata);

    private sealed class KnownFile
    {
        public long Id { get; set; }

        public string Path { get; set; } = "";

        public string Fingerprint { get; set; } = "";

        public long FileSize { get; set; }

        public long ModifiedUtc { get; set; }

        public bool Missing { get; set; }
    }

    /// <summary>Groups writes into transactions so a large first scan doesn't fsync once per file.</summary>
    private sealed class BatchWriter(SqliteConnection connection) : IDisposable
    {
        private SqliteTransaction? _transaction;
        private int _pending;

        public void Execute(string sql, object parameters)
        {
            _transaction ??= connection.BeginTransaction();
            connection.Execute(sql, parameters, _transaction);
            if (++_pending >= BatchSize)
            {
                Commit();
            }
        }

        public void Commit()
        {
            _transaction?.Commit();
            _transaction?.Dispose();
            _transaction = null;
            _pending = 0;
        }

        // Anything not yet committed (e.g. after cancellation) is rolled back by disposing the transaction.
        public void Dispose() => _transaction?.Dispose();
    }
}
