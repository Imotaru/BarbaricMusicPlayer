using Barbaric.App.Bridge;
using Barbaric.Core.Library;

namespace Barbaric.App;

/// <summary>
/// Exposes tags to the UI as <c>tags.*</c> bridge methods. Every change emits <c>tags.changed</c>
/// (the UI refetches the list and counts) and <c>library.changed</c> (rows redraw their chips).
/// </summary>
public sealed class TagApi
{
    private readonly TagRepository _tags;
    private readonly WebBridge _bridge;

    public TagApi(TagRepository tags, WebBridge bridge)
    {
        _tags = tags;
        _bridge = bridge;

        bridge.QueryAsync("tags.getAll", async _ => await _tags.GetAllAsync());
        bridge.Query("tags.getPalette", _ => TagRepository.Palette);
        bridge.QueryAsync("tags.getUsage", async p =>
        {
            var trackIds = p.GetIds();
            var usage = await Task.Run(() => _tags.GetUsageAsync(trackIds));
            return usage.Select(u => new { tagId = u.Key, count = u.Value });
        });
        bridge.QueryAsync("tags.create", async p =>
        {
            var tag = await _tags.CreateAsync(p.GetText("name"), p.GetOptionalText("color"));
            EmitChanged(libraryChanged: false);
            return tag;
        });
        bridge.CommandAsync("tags.rename", async p =>
        {
            await _tags.RenameAsync(p.GetId(), p.GetText("name"));
            EmitChanged(libraryChanged: false);
        });
        bridge.CommandAsync("tags.setColor", async p =>
        {
            await _tags.SetColorAsync(p.GetId(), p.GetText("color"));
            EmitChanged(libraryChanged: false);
        });
        bridge.CommandAsync("tags.delete", async p =>
        {
            await _tags.DeleteAsync(p.GetId());
            EmitChanged();

            // Saved filters that used the tag were rewritten.
            _bridge.Emit("playlists.changed");
        });
        bridge.CommandAsync("tags.apply", async p =>
        {
            var tagId = p.GetId("tagId");
            var trackIds = p.GetIds();
            var add = p.GetProperty("add").GetBoolean();
            await Task.Run(() => add ? _tags.AddToTracksAsync(tagId, trackIds) : _tags.RemoveFromTracksAsync(tagId, trackIds));
            EmitChanged();
        });
    }

    private void EmitChanged(bool libraryChanged = true)
    {
        _bridge.Emit("tags.changed");
        if (libraryChanged)
        {
            _bridge.Emit("library.changed");
        }
    }
}
