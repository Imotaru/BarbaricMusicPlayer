using Barbaric.App.Bridge;
using Barbaric.Core.Library;

namespace Barbaric.App;

/// <summary>
/// Exposes playlists to the UI as <c>playlists.*</c> bridge methods. Changes emit <c>playlists.changed</c>,
/// plus <c>library.changed</c> when the songs in a manual playlist change.
/// </summary>
public sealed class PlaylistApi
{
    private readonly PlaylistRepository _playlists;
    private readonly WebBridge _bridge;

    public PlaylistApi(PlaylistRepository playlists, WebBridge bridge)
    {
        _playlists = playlists;
        _bridge = bridge;

        bridge.QueryAsync("playlists.getAll", async _ => await _playlists.GetAllAsync());
        bridge.QueryAsync("playlists.createFilter", async p =>
        {
            var id = await _playlists.CreateFilterAsync(p.GetText("name"), LibraryApi.ParseQuery(p.GetProperty("query")));
            EmitChanged(libraryChanged: false);
            return id;
        });
        bridge.QueryAsync("playlists.createManual", async p =>
        {
            var trackIds = p.GetIds();
            var id = await Task.Run(() => _playlists.CreateManualAsync(p.GetText("name"), trackIds));
            EmitChanged(libraryChanged: false);
            return id;
        });
        bridge.CommandAsync("playlists.rename", async p =>
        {
            await _playlists.RenameAsync(p.GetId(), p.GetText("name"));
            EmitChanged(libraryChanged: false);
        });
        bridge.CommandAsync("playlists.updateFilter", async p =>
        {
            await _playlists.UpdateFilterAsync(p.GetId(), LibraryApi.ParseQuery(p.GetProperty("query")));
            EmitChanged(libraryChanged: false);
        });
        bridge.CommandAsync("playlists.delete", async p =>
        {
            await _playlists.DeleteAsync(p.GetId());
            EmitChanged(libraryChanged: false);
        });
        bridge.QueryAsync("playlists.addTracks", async p =>
        {
            var (id, trackIds) = (p.GetId(), p.GetIds());
            var added = await Task.Run(() => _playlists.AddTracksAsync(id, trackIds));
            EmitChanged();
            return added;
        });
        bridge.CommandAsync("playlists.removeTracks", async p =>
        {
            var (id, trackIds) = (p.GetId(), p.GetIds());
            await Task.Run(() => _playlists.RemoveTracksAsync(id, trackIds));
            EmitChanged();
        });
        bridge.CommandAsync("playlists.moveTracks", async p =>
        {
            var (id, trackIds, toIndex) = (p.GetId(), p.GetIds(), p.GetProperty("toIndex").GetInt32());
            await Task.Run(() => _playlists.MoveTracksAsync(id, trackIds, toIndex));
            EmitChanged();
        });
    }

    private void EmitChanged(bool libraryChanged = true)
    {
        _bridge.Emit("playlists.changed");
        if (libraryChanged)
        {
            _bridge.Emit("library.changed");
        }
    }
}
