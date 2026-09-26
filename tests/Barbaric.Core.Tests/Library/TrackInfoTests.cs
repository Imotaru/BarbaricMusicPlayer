using Barbaric.Core.Library;

namespace Barbaric.Core.Tests.Library;

public sealed class TrackInfoTests : IDisposable
{
    private readonly LibraryFixture _library = new();

    public void Dispose() => _library.Dispose();

    [Fact]
    public async Task EditedInfo_IsShownSortedAndSearched()
    {
        _library.AddSong("a.wav", title: "Track 01", artist: "Zed");
        _library.AddSong("b.wav", title: "Other", artist: "Mid");
        await _library.AddMusicFolderAndScanAsync();
        var id = (await _library.IdsByTitleAsync())["Track 01"];

        await _library.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?>
        {
            [TrackField.Title] = "  Real Name ",
            [TrackField.Artist] = "Abba",
            [TrackField.Year] = 1976L,
        });

        Assert.Equal(["Real Name", "Other"], await _library.TitlesAsync());
        Assert.Equal(["Real Name"], await _library.TitlesAsync(new TrackQuery(Text: "abba")));
        Assert.Empty(await _library.TitlesAsync(new TrackQuery(Text: "zed")));

        var info = Assert.Single(await _library.Tracks.GetInfoAsync([id]));
        Assert.Equal(("Real Name", "Abba", 1976), (info.Title, info.Artist, info.Year));
        Assert.Equal("a.wav", info.FileName);
        Assert.Equal([TrackField.Title, TrackField.Artist, TrackField.Year], info.Overridden);
    }

    [Fact]
    public async Task Overrides_SurviveARescan_WhileOtherFieldsFollowTheFile()
    {
        var path = _library.AddSong("a.wav", title: "Old", artist: "Old Artist", album: "Old Album");
        await _library.AddMusicFolderAndScanAsync();
        var id = Assert.Single(await _library.AllRowsAsync()).Id;

        await _library.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?>
        {
            [TrackField.Title] = "Mine",
            [TrackField.Artist] = null,
        });
        LibraryFixture.SetTags(path, title: "New", artist: "New Artist", album: "New Album");
        var result = await _library.Scanner.ScanAsync();

        Assert.Equal(1, result.Updated);
        var row = Assert.Single(await _library.AllRowsAsync());
        Assert.Equal(("Mine", null, "New Album"), (row.Title, row.Artist, row.Album));
    }

    [Fact]
    public async Task Reset_PutsTheFileTagBack_AndKeepsOtherOverrides()
    {
        var path = _library.AddSong("a.wav", title: "Old", artist: "Old Artist");
        await _library.AddMusicFolderAndScanAsync();
        var id = Assert.Single(await _library.AllRowsAsync()).Id;
        await _library.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?>
        {
            [TrackField.Title] = "Mine",
            [TrackField.Artist] = "My Artist",
        });

        // The tag changed since the last scan: the reset reads what the file says now.
        LibraryFixture.SetTags(path, title: "Newer", artist: "Newer Artist");
        await _library.Tracks.ResetInfoAsync([id], [TrackField.Artist]);

        var info = Assert.Single(await _library.Tracks.GetInfoAsync([id]));
        Assert.Equal(("Mine", "Newer Artist"), (info.Title, info.Artist));
        Assert.Equal([TrackField.Title], info.Overridden);

        await _library.Tracks.ResetInfoAsync([id], TrackFields.All.ToList());
        info = Assert.Single(await _library.Tracks.GetInfoAsync([id]));
        Assert.Equal("Newer", info.Title);
        Assert.Empty(info.Overridden);
    }

    [Fact]
    public async Task Reset_OfAnUnreadableFile_LetsTheNextScanFillItIn()
    {
        var path = _library.AddSong("a.wav", title: "Tag Title");
        await _library.AddMusicFolderAndScanAsync();
        var id = Assert.Single(await _library.AllRowsAsync()).Id;
        await _library.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?> { [TrackField.Title] = "Mine" });

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await _library.Tracks.ResetInfoAsync([id], [TrackField.Title]);
        }

        var info = Assert.Single(await _library.Tracks.GetInfoAsync([id]));
        Assert.Equal("Mine", info.Title);
        Assert.Empty(info.Overridden);

        await _library.Scanner.ScanAsync();
        Assert.Equal("Tag Title", Assert.Single(await _library.AllRowsAsync()).Title);
    }

    [Fact]
    public async Task EditingSeveralSongs_OnlyTouchesTheGivenFields()
    {
        _library.AddSong("a.wav", title: "A", album: "One");
        _library.AddSong("b.wav", title: "B", album: "Two");
        await _library.AddMusicFolderAndScanAsync();
        var ids = await _library.IdsByTitleAsync();

        await _library.Tracks.SetInfoAsync(ids.Values, new Dictionary<TrackField, object?> { [TrackField.Album] = "Best Of" });

        var infos = await _library.Tracks.GetInfoAsync(ids.Values);
        Assert.Equal(["A", "B"], infos.Select(i => i.Title).Order());
        Assert.All(infos, i => Assert.Equal("Best Of", i.Album));
        Assert.All(infos, i => Assert.Equal([TrackField.Album], i.Overridden));
    }

    [Theory]
    [InlineData(TrackField.Title, "  ")]
    [InlineData(TrackField.Title, null)]
    [InlineData(TrackField.Year, 0L)]
    [InlineData(TrackField.TrackNumber, 10000L)]
    [InlineData(TrackField.Year, "1999")]
    [InlineData(TrackField.Artist, 5L)]
    public async Task InvalidValues_AreRejected(TrackField field, object? value)
    {
        _library.AddSong("a.wav", title: "A");
        await _library.AddMusicFolderAndScanAsync();
        var id = Assert.Single(await _library.AllRowsAsync()).Id;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _library.Tracks.SetInfoAsync([id], new Dictionary<TrackField, object?> { [field] = value }));
        Assert.Empty(Assert.Single(await _library.Tracks.GetInfoAsync([id])).Overridden);
    }
}
