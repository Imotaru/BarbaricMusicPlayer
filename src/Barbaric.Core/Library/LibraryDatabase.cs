using Dapper;
using Microsoft.Data.Sqlite;

namespace Barbaric.Core.Library;

/// <summary>
/// The SQLite file that holds the music library. Opening it applies any pending migrations;
/// later phases add entries to <see cref="Migrations"/> instead of editing existing ones.
/// </summary>
public sealed class LibraryDatabase
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE library_folders (
            path TEXT PRIMARY KEY COLLATE NOCASE
        );

        CREATE TABLE tracks (
            id              INTEGER PRIMARY KEY,
            path            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            file_name       TEXT    NOT NULL,
            fingerprint     TEXT    NOT NULL,
            file_size       INTEGER NOT NULL,
            modified_utc    INTEGER NOT NULL,
            title           TEXT    NOT NULL,
            artist          TEXT,
            album           TEXT,
            album_artist    TEXT,
            genre           TEXT,
            year            INTEGER,
            track_number    INTEGER,
            duration_ms     INTEGER NOT NULL DEFAULT 0,
            bpm             REAL,
            bpm_confidence  REAL,
            bpm_source      TEXT,
            gain_db         REAL    NOT NULL DEFAULT 0,
            play_count      INTEGER NOT NULL DEFAULT 0,
            skip_count      INTEGER NOT NULL DEFAULT 0,
            last_played_utc INTEGER,
            flagged         INTEGER NOT NULL DEFAULT 0,
            missing         INTEGER NOT NULL DEFAULT 0,
            added_utc       INTEGER NOT NULL
        );

        CREATE INDEX ix_tracks_fingerprint ON tracks (fingerprint);

        CREATE VIRTUAL TABLE tracks_fts USING fts5 (
            title, artist, album, album_artist, genre, file_name,
            content = 'tracks',
            content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER tracks_fts_insert AFTER INSERT ON tracks BEGIN
            INSERT INTO tracks_fts (rowid, title, artist, album, album_artist, genre, file_name)
            VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.genre, new.file_name);
        END;

        CREATE TRIGGER tracks_fts_delete AFTER DELETE ON tracks BEGIN
            INSERT INTO tracks_fts (tracks_fts, rowid, title, artist, album, album_artist, genre, file_name)
            VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.genre, old.file_name);
        END;

        CREATE TRIGGER tracks_fts_update AFTER UPDATE OF title, artist, album, album_artist, genre, file_name ON tracks BEGIN
            INSERT INTO tracks_fts (tracks_fts, rowid, title, artist, album, album_artist, genre, file_name)
            VALUES ('delete', old.id, old.title, old.artist, old.album, old.album_artist, old.genre, old.file_name);
            INSERT INTO tracks_fts (rowid, title, artist, album, album_artist, genre, file_name)
            VALUES (new.id, new.title, new.artist, new.album, new.album_artist, new.genre, new.file_name);
        END;
        """,
    ];

    private readonly string _connectionString;

    static LibraryDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public LibraryDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        Migrate();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BarbaricMusicPlayer",
        "library.db");

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();

        // WAL lets the UI read while a scan is writing.
        connection.Execute("PRAGMA journal_mode = WAL;");

        var version = connection.ExecuteScalar<long>("PRAGMA user_version;");
        for (var i = (int)version; i < Migrations.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(Migrations[i], transaction: transaction);
            connection.Execute($"PRAGMA user_version = {i + 1};", transaction: transaction);
            transaction.Commit();
        }
    }
}
