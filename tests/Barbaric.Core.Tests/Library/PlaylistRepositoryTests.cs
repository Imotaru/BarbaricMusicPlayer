using Barbaric.Core.Library;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class PlaylistRepositoryTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private Dictionary<string, long> _tracks = [];

    public async Task InitializeAsync()
    {
        foreach (var title in new[] { "A", "B", "C", "D", "E" })
        {
            _library.AddSong($"{title}.wav", title: title, artist: title is "A" or "B" ? "First" : "Second");
        }

        await _library.AddMusicFolderAndScanAsync();
        _tracks = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Manual_KeepsInsertionOrder_AndSkipsTracksAlreadyInIt()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("C", "A"));

        var added = await _library.Playlists.AddTracksAsync(id, Ids("B", "A", "B", "E"));

        Assert.Equal(2, added);
        Assert.Equal(["C", "A", "B", "E"], await TitlesInAsync(id));
        Assert.Equal(4, (await _library.Playlists.GetAsync(id))!.Count);
    }

    [Fact]
    public async Task Manual_ShowsPositions_AndSortsBothWays()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("C", "A", "B"));

        var rows = await _library.AllRowsAsync(new TrackQuery(Sort: TrackSort.Position, PlaylistId: id));
        Assert.Equal([0, 1, 2], rows.Select(r => r.Position ?? -1));

        Assert.Equal(["B", "A", "C"], await TitlesInAsync(id, descending: true));
        Assert.Equal(["A", "B", "C"], await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Title, PlaylistId: id)));
    }

    [Fact]
    public async Task Manual_CanBeSearchedAndFiltered()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("E", "A", "C", "B"));
        var tag = await _library.Tags.CreateAsync("t");
        await _library.Tags.AddToTracksAsync(tag.Id, Ids("E", "B"));

        Assert.Equal(["A", "B"], await _library.TitlesAsync(new TrackQuery("first", TrackSort.Position, PlaylistId: id)));
        Assert.Equal(
            ["E", "B"],
            await _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Position, Filter: new TrackFilter { AllTags = [tag.Id] }, PlaylistId: id)));
    }

    [Fact]
    public async Task Remove_ClosesTheGap()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("A", "B", "C", "D"));

        await _library.Playlists.RemoveTracksAsync(id, Ids("B", "D"));

        var rows = await _library.AllRowsAsync(new TrackQuery(Sort: TrackSort.Position, PlaylistId: id));
        Assert.Equal(["A", "C"], rows.Select(r => r.Title));
        Assert.Equal([0, 1], rows.Select(r => r.Position ?? -1));

        await _library.Playlists.AddTracksAsync(id, Ids("B"));
        Assert.Equal(["A", "C", "B"], await TitlesInAsync(id));
    }

    [Theory]
    [InlineData(new[] { "B", "C" }, 0, new[] { "B", "C", "A", "D", "E" })]
    [InlineData(new[] { "B", "C" }, 2, new[] { "A", "D", "B", "C", "E" })]
    [InlineData(new[] { "B", "C" }, 3, new[] { "A", "D", "E", "B", "C" })]
    [InlineData(new[] { "B", "C" }, 99, new[] { "A", "D", "E", "B", "C" })]
    [InlineData(new[] { "D" }, 1, new[] { "A", "D", "B", "C", "E" })]
    [InlineData(new[] { "E", "A" }, 1, new[] { "B", "A", "E", "C", "D" })] // the block keeps playlist order
    [InlineData(new[] { "A" }, -5, new[] { "A", "B", "C", "D", "E" })]
    public async Task Move_PlacesTheBlockAtTheTargetIndex(string[] moving, int toIndex, string[] expected)
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("A", "B", "C", "D", "E"));

        await _library.Playlists.MoveTracksAsync(id, Ids(moving), toIndex);

        Assert.Equal(expected, await TitlesInAsync(id));
    }

    [Fact]
    public async Task FilterPlaylists_CannotHoldTracks()
    {
        var id = await _library.Playlists.CreateFilterAsync("View", new TrackQuery("first"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.Playlists.AddTracksAsync(id, Ids("A")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.Playlists.MoveTracksAsync(id, Ids("A"), 0));
    }

    [Fact]
    public async Task FilterPlaylist_SavesTheView_AndReplaysTheSameList()
    {
        var tag = await _library.Tags.CreateAsync("t");
        await _library.Tags.AddToTracksAsync(tag.Id, Ids("A", "C", "D"));
        var view = new TrackQuery("second", TrackSort.Title, Descending: true, new TrackFilter { NoneTags = [tag.Id], BpmMax = 200 });

        var id = await _library.Playlists.CreateFilterAsync("View", view with { PlaylistId = 12345 });

        var saved = (await _library.Playlists.GetAsync(id))!;
        Assert.Equal(PlaylistRepository.FilterKind, saved.Kind);
        Assert.Null(saved.Count);
        Assert.Equal("second", saved.Query!.Text);
        Assert.Equal(TrackSort.Title, saved.Query.Sort);
        Assert.True(saved.Query.Descending);
        Assert.Null(saved.Query.PlaylistId);
        Assert.Equal([tag.Id], saved.Query.Filter!.NoneTags);
        Assert.Equal(200, saved.Query.Filter.BpmMax);
        Assert.Equal(await _library.Tracks.QueryIdsAsync(view), await _library.Tracks.QueryIdsAsync(saved.Query));
    }

    [Fact]
    public async Task UpdateFilter_ReplacesTheSavedView()
    {
        var id = await _library.Playlists.CreateFilterAsync("View", new TrackQuery("first"));

        await _library.Playlists.UpdateFilterAsync(id, new TrackQuery("second", TrackSort.Title));

        Assert.Equal(["C", "D", "E"], await _library.TitlesAsync((await _library.Playlists.GetAsync(id))!.Query));
    }

    [Fact]
    public async Task DamagedFilterJson_FallsBackToTheWholeLibrary()
    {
        var id = await _library.Playlists.CreateFilterAsync("View", new TrackQuery("first"));
        using (var connection = _library.Database.Open())
        {
            connection.Execute("UPDATE playlists SET filter_json = '{\"sort\": \"nonsense\"' WHERE id = @id", new { id });
        }

        Assert.Equal(new TrackQuery(), (await _library.Playlists.GetAsync(id))!.Query);
    }

    [Fact]
    public async Task Sidebar_ListsPlaylistsInCreationOrder_AndRenameKeepsThePlace()
    {
        var b = await _library.Playlists.CreateManualAsync("Bee");
        var a = await _library.Playlists.CreateFilterAsync("Ay", new TrackQuery());
        var c = await _library.Playlists.CreateManualAsync("Cee");

        await _library.Playlists.RenameAsync(a, "  Aaa  ");

        var all = await _library.Playlists.GetAllAsync();
        Assert.Equal([b, a, c], all.Select(p => p.Id));
        Assert.Equal("Aaa", all[1].Name);
        await Assert.ThrowsAsync<ArgumentException>(() => _library.Playlists.RenameAsync(a, " "));
    }

    [Fact]
    public async Task Delete_RemovesItsTrackList()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("A", "B"));

        await _library.Playlists.DeleteAsync(id);

        Assert.Empty(await _library.Playlists.GetAllAsync());
        using var connection = _library.Database.Open();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM playlist_tracks"));
    }

    [Fact]
    public async Task MissingTracks_AreHiddenAndNotCounted_ButKeepTheirPlace()
    {
        var id = await _library.Playlists.CreateManualAsync("Mine", Ids("A", "B", "C"));

        File.Delete(Path.Combine(_library.MusicDir, "B.wav"));
        await _library.Scanner.ScanAsync();
        Assert.Equal(["A", "C"], await TitlesInAsync(id));
        Assert.Equal(2, (await _library.Playlists.GetAsync(id))!.Count);

        _library.AddSong("B.wav", title: "B");
        await _library.Scanner.ScanAsync();
        Assert.Equal(["A", "B", "C"], await TitlesInAsync(id));
    }

    private List<long> Ids(params string[] titles) => [.. titles.Select(t => _tracks[t])];

    private Task<IReadOnlyList<string>> TitlesInAsync(long playlistId, bool descending = false) =>
        _library.TitlesAsync(new TrackQuery(Sort: TrackSort.Position, Descending: descending, PlaylistId: playlistId));
}
