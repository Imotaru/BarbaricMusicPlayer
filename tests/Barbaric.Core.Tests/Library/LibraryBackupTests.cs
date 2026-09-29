using System.Text;
using Barbaric.Core.Analysis;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class LibraryBackupTests : IDisposable
{
    private readonly LibraryFixture _source = new();
    private readonly LibraryFixture _target = new();

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    [Fact]
    public async Task Import_OntoTheSameFilesElsewhere_BringsBackEverything()
    {
        _source.AddSong("One.wav", title: "One", artist: "A");
        _source.AddSong("Two.wav", title: "Two", artist: "B");
        _source.AddSong("Three.wav", title: "Three", artist: "C");
        await _source.AddMusicFolderAndScanAsync();
        var ids = await _source.IdsByTitleAsync();

        var chill = await _source.Tags.CreateAsync("Chill", "#4fa3ff");
        var gym = await _source.Tags.CreateAsync("Gym");
        await _source.Tags.AddToTracksAsync(chill.Id, [ids["One"], ids["Two"]]);
        await _source.Tags.AddToTracksAsync(gym.Id, [ids["Three"]]);
        await _source.Playlists.CreateManualAsync("Mix", [ids["Three"], ids["One"]]);
        await _source.Playlists.CreateFilterAsync(
            "Chilled", new TrackQuery(Sort: TrackSort.Bpm, Filter: new TrackFilter { Artist = "A", AllTags = [chill.Id], BpmMin = 100 }));
        await _source.Tracks.SetInfoAsync([ids["One"]], new Dictionary<TrackField, object?> { [TrackField.Artist] = "Edited" });
        await _source.Tracks.SetManualBpmAsync([ids["Two"]], 128);
        await _source.Tracks.SetGainAsync(ids["Three"], -3);
        await _source.Tracks.SaveLoudnessAsync(ids["Two"], new LoudnessResult(-9.5, -0.3));
        await _source.Stats.RecordAsync(ids["One"], 500, 500, PlayKind.Complete);
        await _source.Stats.RecordAsync(ids["Two"], 10, 500, PlayKind.Skip);
        await _source.Stats.SetHiddenAsync([ids["Three"]], true);
        var backup = await ExportAsync(_source);

        // The same files, under other names, already scanned into another library.
        foreach (var title in ids.Keys)
        {
            File.Copy(Path.Combine(_source.MusicDir, $"{title}.wav"), Path.Combine(_target.MusicDir, $"copy of {title}.wav"));
        }

        await _target.AddMusicFolderAndScanAsync();
        var result = await new LibraryBackup(_target.Database).ImportAsync(backup);

        Assert.Equal((3, 0, 2, 2, 0), (result.Matched, result.Added, result.TagsCreated, result.PlaylistsCreated, result.PlaylistsReplaced));
        using var connection = _target.Database.Open();
        Assert.Equal(3, connection.ExecuteScalar<long>("SELECT count(*) FROM tracks"));

        var tags = await _target.Tags.GetAllAsync();
        Assert.Equal(["Chill", "Gym"], tags.Select(t => t.Name));
        Assert.Equal("#4fa3ff", tags[0].Color);

        var target = TitleIds(connection);
        Assert.Equal(["Chill"], TagNames(connection, target["One"]));
        Assert.Equal(["Gym"], TagNames(connection, target["Three"]));

        var info = (await _target.Tracks.GetInfoAsync([target["One"]])).Single();
        Assert.Equal("Edited", info.Artist);
        Assert.Equal([TrackField.Artist], info.Overridden);

        var bpm = (await _target.Tracks.GetBpmInfoAsync([target["Two"]])).Single();
        Assert.Equal((128.0, "manual"), (bpm.Bpm!.Value, bpm.BpmSource));

        var three = (await _target.Tracks.GetAsync(target["Three"]))!;
        Assert.Equal(-3, three.GainDb);
        Assert.False(three.LoudnessAnalyzed);

        var measured = (await _target.Tracks.GetAsync(target["Two"]))!;
        Assert.True(measured.LoudnessAnalyzed);
        Assert.Equal((-9.5, -0.3), (measured.LoudnessLufs!.Value, measured.PeakDb!.Value));
        Assert.True(three.Hidden);

        var one = (await _target.Tracks.GetAsync(target["One"]))!;
        var two = (await _target.Tracks.GetAsync(target["Two"]))!;
        Assert.Equal((1L, 0L), (one.PlayCount, one.SkipCount));
        Assert.Equal((0L, 1L), (two.PlayCount, two.SkipCount));
        Assert.Equal(_source.Clock.Now.UtcTicks, one.LastPlayedUtc);
        Assert.Equal(2, connection.ExecuteScalar<long>("SELECT count(*) FROM play_events"));

        var playlists = await _target.Playlists.GetAllAsync();
        Assert.Equal(["Mix", "Chilled"], playlists.Select(p => p.Name));
        Assert.Equal(["Three", "One"], PlaylistTitles(connection, playlists[0].Id));

        var filter = playlists[1].Query!;
        Assert.Equal(TrackSort.Bpm, filter.Sort);
        Assert.Equal([tags[0].Id], filter.Filter!.AllTags);
        Assert.Equal(100.0, filter.Filter.BpmMin);
        Assert.Equal("A", filter.Filter.Artist);
    }

    [Fact]
    public async Task MissingFiles_AreKept_AndComeBackWhenAScanFindsThem()
    {
        _source.AddSong("One.wav", title: "One");
        _source.AddSong("Two.wav", title: "Two");
        await _source.AddMusicFolderAndScanAsync();
        var ids = await _source.IdsByTitleAsync();
        var tag = await _source.Tags.CreateAsync("Chill");
        await _source.Tags.AddToTracksAsync(tag.Id, [ids["One"]]);
        await _source.Playlists.CreateManualAsync("Mix", [ids["Two"], ids["One"]]);
        var backup = await ExportAsync(_source);

        var library = new LibraryBackup(_target.Database);
        var result = await library.ImportAsync(backup);

        Assert.Equal((0, 2), (result.Matched, result.Added));
        var missing = await library.GetMissingAsync(result.TrackIds);
        Assert.Equal(["One.wav", "Two.wav"], missing.Select(m => m.FileName));
        Assert.Equal(Path.Combine(_source.MusicDir, "One.wav"), missing[0].Path);
        Assert.Equal("One", missing[0].Title);
        Assert.Equal(2, (await _target.Stats.GetCountsAsync()).Missing);
        Assert.Equal(["One", "Two"], await _target.TitlesAsync(new TrackQuery(Sort: TrackSort.Title, Scope: TrackScope.Missing)));
        Assert.Empty(await _target.TitlesAsync());

        // The file turns up somewhere else, under another name.
        File.Copy(Path.Combine(_source.MusicDir, "One.wav"), Path.Combine(_target.MusicDir, "Found it.wav"));
        var scan = await _target.AddMusicFolderAndScanAsync();

        Assert.Equal(1, scan.Moved);
        Assert.Equal(["Two.wav"], (await library.GetMissingAsync(result.TrackIds)).Select(m => m.FileName));
        var row = Assert.Single(await _target.AllRowsAsync());
        Assert.Equal("One", row.Title);
        Assert.Equal(Path.Combine(_target.MusicDir, "Found it.wav"), row.Path);
        Assert.Equal(1, (await _target.Tags.GetAllAsync()).Single().Count);

        var playlist = (await _target.Playlists.GetAllAsync()).Single();
        Assert.Equal(1, playlist.Count);
        using var connection = _target.Database.Open();
        Assert.Equal(["Two", "One"], PlaylistTitles(connection, playlist.Id));
    }

    [Fact]
    public async Task Import_Merges_AndTheBackupWinsForWhatItHolds()
    {
        _source.AddSong("One.wav", title: "One");
        _source.AddSong("Two.wav", title: "Two");
        await _source.AddMusicFolderAndScanAsync();
        var ids = await _source.IdsByTitleAsync();
        var chill = await _source.Tags.CreateAsync("Chill", "#ef6f6c");
        await _source.Tags.AddToTracksAsync(chill.Id, [ids["One"]]);
        await _source.Playlists.CreateManualAsync("Mix", [ids["One"]]);
        var backup = await ExportAsync(_source);

        File.Copy(Path.Combine(_source.MusicDir, "One.wav"), Path.Combine(_target.MusicDir, "One.wav"));
        File.Copy(Path.Combine(_source.MusicDir, "Two.wav"), Path.Combine(_target.MusicDir, "Two.wav"));
        _target.AddSong("Local.wav", title: "Local", seconds: 0.7);
        await _target.AddMusicFolderAndScanAsync();
        var target = await _target.IdsByTitleAsync();
        var localChill = await _target.Tags.CreateAsync("chill", "#9b7bff");
        var mine = await _target.Tags.CreateAsync("Mine");
        await _target.Tags.AddToTracksAsync(localChill.Id, [target["Two"]]);
        await _target.Tags.AddToTracksAsync(mine.Id, [target["One"], target["Local"]]);
        var mix = await _target.Playlists.CreateManualAsync("mix", [target["Local"]]);
        var other = await _target.Playlists.CreateManualAsync("Other", [target["Local"]]);

        var result = await new LibraryBackup(_target.Database).ImportAsync(backup);

        Assert.Equal((0, 0, 1), (result.TagsCreated, result.PlaylistsCreated, result.PlaylistsReplaced));
        var tags = await _target.Tags.GetAllAsync();
        Assert.Equal([("chill", "#ef6f6c"), ("Mine", mine.Color)], tags.Select(t => (t.Name, t.Color)));

        using var connection = _target.Database.Open();
        Assert.Equal(["chill"], TagNames(connection, target["One"]));
        Assert.Empty(TagNames(connection, target["Two"]));
        Assert.Equal(["Mine"], TagNames(connection, target["Local"]));
        Assert.Equal(["One"], PlaylistTitles(connection, mix));
        Assert.Equal(["Local"], PlaylistTitles(connection, other));
        Assert.Equal(2, (await _target.Playlists.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Import_DropsLocalEditsTheBackupDoesNotHave_AtTheNextScan()
    {
        _source.AddSong("One.wav", title: "One");
        await _source.AddMusicFolderAndScanAsync();
        var backup = await ExportAsync(_source);
        var id = (await _source.IdsByTitleAsync())["One"];
        await _source.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?> { [TrackField.Title] = "Renamed here" });

        await new LibraryBackup(_source.Database).ImportAsync(backup);
        await _source.Scanner.ScanAsync();

        var info = (await _source.Tracks.GetInfoAsync([id])).Single();
        Assert.Equal("One", info.Title);
        Assert.Empty(info.Overridden);
    }

    [Fact]
    public async Task ImportingTheSameBackupTwice_DoesNotDuplicateAnything()
    {
        _source.AddSong("One.wav", title: "One");
        await _source.AddMusicFolderAndScanAsync();
        var id = (await _source.IdsByTitleAsync())["One"];
        await _source.Stats.RecordAsync(id, 500, 500, PlayKind.Complete);
        await _source.Playlists.CreateManualAsync("Mix", [id]);
        var backup = await ExportAsync(_source);

        var library = new LibraryBackup(_source.Database);
        await library.ImportAsync(backup);
        var result = await library.ImportAsync(backup);

        Assert.Equal((1, 0, 0, 1), (result.Matched, result.Added, result.PlaylistsCreated, result.PlaylistsReplaced));
        using var connection = _source.Database.Open();
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM tracks"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM play_events"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM playlists"));
        Assert.Equal(1, (await _source.Tracks.GetAsync(id))!.PlayCount);
    }

    [Fact]
    public async Task ForgetAsync_DropsOnlyMissingSongs()
    {
        _source.AddSong("One.wav", title: "One");
        var two = _source.AddSong("Two.wav", title: "Two");
        await _source.AddMusicFolderAndScanAsync();
        var ids = await _source.IdsByTitleAsync();
        File.Delete(two);
        await _source.Scanner.ScanAsync();

        var forgotten = await _source.Tracks.ForgetAsync(ids.Values);

        Assert.Equal(1, forgotten);
        Assert.NotNull(await _source.Tracks.GetAsync(ids["One"]));
        Assert.Null(await _source.Tracks.GetAsync(ids["Two"]));
    }

    [Theory]
    [InlineData("not json at all", "isn't a Barbaric")]
    [InlineData("[1, 2]", "isn't a Barbaric")]
    [InlineData("""{ "format": "something-else", "version": 1 }""", "isn't a Barbaric")]
    [InlineData("""{ "format": "barbaric-backup", "version": 99 }""", "newer version")]
    public void Read_RejectsFilesThatAreNotUsableBackups(string json, string message)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var error = Assert.Throws<InvalidDataException>(() => BackupFile.Read(stream));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Read_TreatsNullListsAsEmpty()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """{ "format": "barbaric-backup", "version": 1, "tags": null, "songs": [{ "key": 1, "path": "C:\\a.mp3", "tags": null }] }"""));

        var backup = BackupFile.Read(stream);

        Assert.Empty(backup.Tags);
        Assert.Empty(backup.Songs.Single().Tags);
        Assert.Empty(backup.Playlists);
    }

    /// <summary>Exports and reads the file back, so every test goes through the JSON.</summary>
    private static async Task<BackupFile> ExportAsync(LibraryFixture library)
    {
        var backup = await new LibraryBackup(library.Database, library.Clock).ExportAsync();
        using var stream = new MemoryStream();
        backup.Write(stream);
        stream.Position = 0;
        return BackupFile.Read(stream);
    }

    private static Dictionary<string, long> TitleIds(System.Data.IDbConnection connection) =>
        connection.Query<(long Id, string Title)>("SELECT id, title FROM tracks").ToDictionary(r => r.Title, r => r.Id);

    private static List<string> TagNames(System.Data.IDbConnection connection, long trackId) =>
        connection.Query<string>(
            "SELECT g.name FROM track_tags tt JOIN tags g ON g.id = tt.tag_id WHERE tt.track_id = @trackId ORDER BY g.name",
            new { trackId }).AsList();

    private static List<string> PlaylistTitles(System.Data.IDbConnection connection, long playlistId) =>
        connection.Query<string>(
            "SELECT t.title FROM playlist_tracks pt JOIN tracks t ON t.id = pt.track_id WHERE pt.playlist_id = @playlistId ORDER BY pt.position",
            new { playlistId }).AsList();
}
