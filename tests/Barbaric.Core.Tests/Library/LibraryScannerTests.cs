namespace Barbaric.Core.Tests.Library;

public sealed class LibraryScannerTests : IDisposable
{
    private readonly LibraryFixture _library = new();

    public void Dispose() => _library.Dispose();

    [Fact]
    public async Task Scan_AddsTracksWithTheirTags()
    {
        _library.AddSong("a.wav", title: "Beat It", artist: "Michael Jackson", album: "Thriller", bpm: 139);
        _library.AddSong("sub/untagged song.wav");

        var result = await _library.AddMusicFolderAndScanAsync();

        Assert.Equal(2, result.Added);
        var rows = await _library.AllRowsAsync();
        var beatIt = Assert.Single(rows, r => r.Title == "Beat It");
        Assert.Equal("Michael Jackson", beatIt.Artist);
        Assert.Equal("Thriller", beatIt.Album);
        Assert.Equal(139, beatIt.Bpm);
        Assert.InRange(beatIt.DurationMs, 400, 600);

        var track = await _library.Tracks.GetAsync(beatIt.Id);
        Assert.Equal("tag", track!.BpmSource);

        // Untagged files are listed under their file name.
        Assert.Contains(rows, r => r.Title == "untagged song" && r.Artist is null);
    }

    [Fact]
    public async Task Rescan_LeavesUnchangedFilesAlone()
    {
        _library.AddSong("a.wav", title: "A");
        _library.AddSong("b.wav", title: "B");
        await _library.AddMusicFolderAndScanAsync();

        var result = await _library.Scanner.ScanAsync();

        Assert.Equal(new(Added: 0, Updated: 0, Moved: 0, Missing: 0, Unchanged: 2, Failed: 0), result);
    }

    [Fact]
    public async Task EditedTags_AreReRead()
    {
        var path = _library.AddSong("a.wav", title: "Old Title");
        await _library.AddMusicFolderAndScanAsync();

        LibraryFixture.SetTags(path, title: "New Title");
        var result = await _library.Scanner.ScanAsync();

        Assert.Equal(1, result.Updated);
        Assert.Equal("New Title", Assert.Single(await _library.AllRowsAsync()).Title);
    }

    [Fact]
    public async Task MovedAndRenamedFile_KeepsItsIdAndVolume()
    {
        var path = _library.AddSong("a.wav", title: "Keeper");
        await _library.AddMusicFolderAndScanAsync();
        var original = (await _library.AllRowsAsync()).Single();
        await _library.Tracks.SetGainAsync(original.Id, -4.5);

        var newPath = Path.Combine(_library.MusicDir, "moved", "renamed.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(path, newPath);
        var result = await _library.Scanner.ScanAsync();

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Added);
        var track = await _library.Tracks.GetByPathAsync(newPath);
        Assert.NotNull(track);
        Assert.Equal(original.Id, track.Id);
        Assert.Equal(-4.5, track.GainDb);
        Assert.Equal("renamed.wav", track.FileName);
    }

    [Fact]
    public async Task DeletedFile_IsHidden_AndComesBackWithItsId()
    {
        var path = _library.AddSong("a.wav", title: "Gone");
        _library.AddSong("b.wav", title: "Stays");
        await _library.AddMusicFolderAndScanAsync();
        var id = (await _library.AllRowsAsync()).Single(r => r.Title == "Gone").Id;

        var parked = Path.Combine(_library.Root, "parked.wav");
        File.Move(path, parked);
        var result = await _library.Scanner.ScanAsync();

        Assert.Equal(1, result.Missing);
        Assert.DoesNotContain(await _library.AllRowsAsync(), r => r.Title == "Gone");

        File.Move(parked, path);
        await _library.Scanner.ScanAsync();

        Assert.Contains(await _library.AllRowsAsync(), r => r.Id == id);
    }

    [Fact]
    public async Task RemovingAFolder_HidesItsTracks()
    {
        _library.AddSong("a.wav", title: "A");
        await _library.AddMusicFolderAndScanAsync();

        await _library.Folders.RemoveAsync(_library.MusicDir);
        await _library.Scanner.ScanAsync();

        Assert.Empty(await _library.AllRowsAsync());
    }

    [Fact]
    public async Task OverlappingFolders_DoNotDuplicateTracks()
    {
        _library.AddSong("sub/a.wav", title: "A");
        await _library.Folders.AddAsync(Path.Combine(_library.MusicDir, "sub"));

        var result = await _library.AddMusicFolderAndScanAsync();

        Assert.Equal(1, result.Added);
    }

    [Fact]
    public async Task Scan_ReportsProgress()
    {
        _library.AddSong("a.wav");
        _library.AddSong("b.wav");
        await _library.Folders.AddAsync(_library.MusicDir);
        var reports = new List<Barbaric.Core.Library.ScanProgress>();

        await _library.Scanner.ScanAsync(new SyncProgress(reports.Add));

        Assert.Equal(2, reports.Count);
        Assert.Equal(new(2, 2), reports[^1]);
    }

    private sealed class SyncProgress(Action<Barbaric.Core.Library.ScanProgress> report) : IProgress<Barbaric.Core.Library.ScanProgress>
    {
        public void Report(Barbaric.Core.Library.ScanProgress value) => report(value);
    }
}
