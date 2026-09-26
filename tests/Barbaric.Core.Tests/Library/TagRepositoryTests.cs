using Barbaric.Core.Library;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class TagRepositoryTests : IAsyncLifetime
{
    private readonly LibraryFixture _library = new();
    private Dictionary<string, long> _tracks = [];

    public async Task InitializeAsync()
    {
        _library.AddSong("a.wav", title: "A");
        _library.AddSong("b.wav", title: "B");
        _library.AddSong("c.wav", title: "C");
        await _library.AddMusicFolderAndScanAsync();
        _tracks = await _library.IdsByTitleAsync();
    }

    public Task DisposeAsync()
    {
        _library.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_IgnoresCaseAndSurroundingSpace_AndReturnsTheExistingTag()
    {
        var first = await _library.Tags.CreateAsync("Workout");
        var again = await _library.Tags.CreateAsync("  workout ");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Workout", again.Name);
        Assert.Single(await _library.Tags.GetAllAsync());
    }

    [Fact]
    public async Task Create_SpreadsColoursOverThePalette_UnlessOneIsGiven()
    {
        var first = await _library.Tags.CreateAsync("one");
        var second = await _library.Tags.CreateAsync("two");
        var picked = await _library.Tags.CreateAsync("three", "#ABCDEF");

        Assert.Equal(TagRepository.Palette[0], first.Color);
        Assert.Equal(TagRepository.Palette[1], second.Color);
        Assert.Equal("#abcdef", picked.Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsEmptyNames(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _library.Tags.CreateAsync(name));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#12345g")]
    [InlineData("#123456; background: url(x)")]
    public async Task Colours_MustBePlainHex(string color)
    {
        var tag = await _library.Tags.CreateAsync("x");

        await Assert.ThrowsAsync<ArgumentException>(() => _library.Tags.SetColorAsync(tag.Id, color));
    }

    [Fact]
    public async Task Rename_RefusesAnotherTagsName_ButAllowsChangingCase()
    {
        var chill = await _library.Tags.CreateAsync("chill");
        await _library.Tags.CreateAsync("party");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _library.Tags.RenameAsync(chill.Id, "PARTY"));
        Assert.Contains("already a tag", error.Message);

        await _library.Tags.RenameAsync(chill.Id, "Chill");
        Assert.Equal("Chill", (await _library.Tags.GetAsync(chill.Id))!.Name);
    }

    [Fact]
    public async Task AddAndRemove_AreIdempotent_AndCountsFollow()
    {
        var tag = await _library.Tags.CreateAsync("fav");

        await _library.Tags.AddToTracksAsync(tag.Id, [_tracks["A"], _tracks["B"]]);
        await _library.Tags.AddToTracksAsync(tag.Id, [_tracks["B"], _tracks["C"], _tracks["C"]]);
        Assert.Equal(3, (await _library.Tags.GetAsync(tag.Id))!.Count);

        await _library.Tags.RemoveFromTracksAsync(tag.Id, [_tracks["A"], _tracks["A"]]);
        await _library.Tags.RemoveFromTracksAsync(tag.Id, [_tracks["A"]]);
        Assert.Equal(2, (await _library.Tags.GetAsync(tag.Id))!.Count);
    }

    [Fact]
    public async Task Usage_CountsTagsOverTheGivenTracks_EvenForHugeSelections()
    {
        var a = await _library.Tags.CreateAsync("a");
        var b = await _library.Tags.CreateAsync("b");
        await _library.Tags.CreateAsync("unused");
        await _library.Tags.AddToTracksAsync(a.Id, [_tracks["A"], _tracks["B"]]);
        await _library.Tags.AddToTracksAsync(b.Id, [_tracks["C"]]);

        // Far more ids than SQLite allows as separate parameters.
        var selection = Enumerable.Range(100_000, 50_000).Select(i => (long)i).Concat([_tracks["A"], _tracks["C"]]);
        var usage = await _library.Tags.GetUsageAsync(selection);

        Assert.Equal(new Dictionary<long, long> { [a.Id] = 1, [b.Id] = 1 }, usage);
    }

    [Fact]
    public async Task Add_IgnoresTrackIdsThatDoNotExist()
    {
        var tag = await _library.Tags.CreateAsync("fav");

        await _library.Tags.AddToTracksAsync(tag.Id, [_tracks["A"], 987654]);

        Assert.Equal(1, (await _library.Tags.GetAsync(tag.Id))!.Count);
    }

    [Fact]
    public async Task Counts_LeaveOutMissingTracks_ButKeepTheirTags()
    {
        var tag = await _library.Tags.CreateAsync("fav");
        await _library.Tags.AddToTracksAsync(tag.Id, _tracks.Values);

        File.Delete(Path.Combine(_library.MusicDir, "b.wav"));
        await _library.Scanner.ScanAsync();
        Assert.Equal(2, (await _library.Tags.GetAsync(tag.Id))!.Count);

        _library.AddSong("b.wav", title: "B");
        await _library.Scanner.ScanAsync();
        Assert.Equal(3, (await _library.Tags.GetAsync(tag.Id))!.Count);
    }

    [Fact]
    public async Task Delete_UntagsTracks_AndDropsTheTagFromSavedFilters()
    {
        var keep = await _library.Tags.CreateAsync("keep");
        var gone = await _library.Tags.CreateAsync("gone");
        await _library.Tags.AddToTracksAsync(keep.Id, [_tracks["A"], _tracks["B"]]);
        await _library.Tags.AddToTracksAsync(gone.Id, [_tracks["B"]]);
        var playlist = await _library.Playlists.CreateFilterAsync(
            "Mix",
            new TrackQuery(Filter: new TrackFilter { AllTags = [keep.Id, gone.Id], NoneTags = [gone.Id] }));

        await _library.Tags.DeleteAsync(gone.Id);

        Assert.Equal(["keep"], (await _library.Tags.GetAllAsync()).Select(t => t.Name));
        using (var connection = _library.Database.Open())
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM track_tags WHERE tag_id = @Id", gone));
        }

        var saved = (await _library.Playlists.GetAsync(playlist))!.Query!;
        Assert.Equal([keep.Id], saved.Filter!.AllTags);
        Assert.Empty(saved.Filter.NoneTags);
        Assert.Equal(["A", "B"], await _library.TitlesAsync(saved with { Sort = TrackSort.Title }));
    }
}
