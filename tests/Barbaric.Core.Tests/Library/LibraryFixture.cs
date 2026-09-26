using Barbaric.Core.Library;
using Microsoft.Data.Sqlite;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Barbaric.Core.Tests.Library;

/// <summary>A throwaway library: temp database plus a music folder of small generated, tagged WAV files.</summary>
public sealed class LibraryFixture : IDisposable
{
    private double _nextFrequency = 300;

    public LibraryFixture()
    {
        Root = Directory.CreateTempSubdirectory("barbaric-lib-").FullName;
        MusicDir = Directory.CreateDirectory(Path.Combine(Root, "music")).FullName;
        Database = new LibraryDatabase(Path.Combine(Root, "library.db"));
        Tracks = new TrackRepository(Database);
        Folders = new FolderRepository(Database);
        Tags = new TagRepository(Database);
        Playlists = new PlaylistRepository(Database);
        Scanner = new LibraryScanner(Database);
    }

    public string Root { get; }

    public string MusicDir { get; }

    public LibraryDatabase Database { get; }

    public TrackRepository Tracks { get; }

    public FolderRepository Folders { get; }

    public LibraryScanner Scanner { get; }

    public TagRepository Tags { get; }

    public PlaylistRepository Playlists { get; }

    /// <summary>Writes a short tone (unique per call, so fingerprints differ) and tags it.</summary>
    public string AddSong(
        string relativePath,
        string? title = null,
        string? artist = null,
        string? album = null,
        uint bpm = 0,
        double seconds = 0.5)
    {
        var path = Path.Combine(MusicDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tone = new SignalGenerator(8000, 1) { Frequency = _nextFrequency, Gain = 0.2 }.Take(TimeSpan.FromSeconds(seconds));
        _nextFrequency += 37;
        WaveFileWriter.CreateWaveFile16(path, tone);

        if (title is not null || artist is not null || album is not null || bpm > 0)
        {
            SetTags(path, title, artist, album, bpm);
        }

        return path;
    }

    public static void SetTags(string path, string? title = null, string? artist = null, string? album = null, uint bpm = 0)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.GetTag(TagLib.TagTypes.Id3v2, create: true);
        tag.Title = title;
        tag.Performers = artist is null ? [] : [artist];
        tag.Album = album;
        tag.BeatsPerMinute = bpm;
        file.Save();
    }

    public async Task<ScanResult> AddMusicFolderAndScanAsync()
    {
        await Folders.AddAsync(MusicDir);
        return await Scanner.ScanAsync();
    }

    public async Task<IReadOnlyList<TrackRow>> AllRowsAsync(TrackQuery? query = null) =>
        (await Tracks.QueryAsync(query ?? new TrackQuery(), 0, 1000)).Rows;

    public async Task<IReadOnlyList<string>> TitlesAsync(TrackQuery? query = null) =>
        (await AllRowsAsync(query)).Select(r => r.Title).ToList();

    /// <summary>Track ids keyed by title, for tests that give every song a unique title.</summary>
    public async Task<Dictionary<string, long>> IdsByTitleAsync() =>
        (await AllRowsAsync()).ToDictionary(r => r.Title, r => r.Id);

    public void Dispose()
    {
        // Pooled connections keep the database file open.
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
