using System.Text.Json;
using System.Windows;
using Barbaric.App.Bridge;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Microsoft.Win32;

namespace Barbaric.App;

/// <summary>Exposes the music library to the UI as <c>library.*</c> bridge methods and events.</summary>
public sealed class LibraryApi : IDisposable
{
    private readonly FolderRepository _folders;
    private readonly TrackRepository _tracks;
    private readonly PlayStatsRepository _stats;
    private readonly PlaybackController _controller;
    private readonly LibraryScanner _scanner;
    private readonly WebBridge _bridge;
    private readonly Window _owner;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _rescanRequested;
    private ScanStatus _status = new(false, 0, 0, null);

    public LibraryApi(
        FolderRepository folders,
        TrackRepository tracks,
        PlayStatsRepository stats,
        PlaybackController controller,
        LibraryScanner scanner,
        WebBridge bridge,
        Window owner)
    {
        _folders = folders;
        _tracks = tracks;
        _stats = stats;
        _controller = controller;
        _scanner = scanner;
        _bridge = bridge;
        _owner = owner;

        bridge.QueryAsync("library.getFolders", async _ => await _folders.GetAllAsync());
        bridge.QueryAsync("library.addFolder", async _ => await AddFoldersAsync());
        bridge.QueryAsync("library.removeFolder", async p =>
        {
            await _folders.RemoveAsync(p.GetProperty("path").GetString()!);
            StartScan();
            return await _folders.GetAllAsync();
        });
        bridge.Command("library.rescan", _ => StartScan());
        bridge.Query("library.getScanStatus", _ => _status);
        bridge.QueryAsync("library.query", async p =>
        {
            var query = WithPool(ParseQuery(p));
            var offset = p.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = p.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
            return await Task.Run(() => _tracks.QueryAsync(query, offset, limit));
        });
        bridge.QueryAsync("library.queryIds", async p =>
        {
            var query = WithPool(ParseQuery(p));
            var offset = p.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = p.TryGetProperty("limit", out var l) ? l.GetInt32() : int.MaxValue;
            return await Task.Run(() => _tracks.QueryIdsAsync(query, offset, limit));
        });
        bridge.QueryAsync("library.getCounts", async _ => await _stats.GetCountsAsync());
        bridge.CommandAsync("library.keep", async p =>
        {
            await _stats.KeepAsync(p.GetIds());
            _bridge.Emit("library.changed");
        });
        bridge.QueryAsync("library.getSkips", async p =>
            (await _stats.GetShuffleStatsAsync(p.GetIds())).Select(s => new { s.Id, s.PlayCount, s.SkipCount }));
        bridge.CommandAsync("library.setSkips", async p =>
        {
            await _stats.SetSkipsAsync(p.GetIds(), p.GetProperty("skips").GetInt64());
            _bridge.Emit("library.changed");
        });
        bridge.QueryAsync("library.getInfo", async p => await _tracks.GetInfoAsync(p.GetIds()));
        bridge.CommandAsync("library.setInfo", async p =>
        {
            var ids = p.GetIds();
            await _tracks.SetInfoAsync(ids, ParseInfo(p));
            if (p.TryGetProperty("reset", out var reset) && reset.ValueKind == JsonValueKind.Array)
            {
                await _tracks.ResetInfoAsync(ids, [.. reset.EnumerateArray().Select(f => ParseField(f.GetString()))]);
            }

            await _controller.RefreshCurrentTrackAsync(ids);
            _bridge.Emit("library.changed");
        });
        bridge.CommandAsync("library.hide", async p =>
        {
            var ids = p.GetIds();
            await _stats.SetHiddenAsync(ids, true);
            await _controller.RemoveAsync(ids, unload: false);
            EmitTracksChanged();
        });
        bridge.CommandAsync("library.unhide", async p =>
        {
            await _stats.SetHiddenAsync(p.GetIds(), false);
            EmitTracksChanged();
        });
        bridge.QueryAsync("library.recycle", async p => await RecycleAsync(p.GetIds()));
        bridge.CommandAsync("library.forget", async p =>
        {
            await _tracks.ForgetAsync(p.GetIds());
            EmitTracksChanged();
        });
    }

    /// <summary>Raised on the UI thread after each scan pass that completed.</summary>
    public event EventHandler? ScanCompleted;

    /// <summary>Reads <c>{ text, sort, desc, filter, playlistId, bpm, scope, keepIds }</c> as sent by the UI.</summary>
    public static TrackQuery ParseQuery(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return new TrackQuery();
        }

        var text = p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var sort = p.TryGetProperty("sort", out var s) && Enum.TryParse<TrackSort>(s.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : TrackSort.Artist;
        var desc = p.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.True;
        var playlistId = p.TryGetProperty("playlistId", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : (long?)null;
        var scope = p.TryGetProperty("scope", out var sc) && Enum.TryParse<TrackScope>(sc.GetString(), ignoreCase: true, out var parsedScope)
            ? parsedScope
            : TrackScope.Library;
        var keepIds = p.GetIds("keepIds");
        return new TrackQuery(text, sort, desc, ParseFilter(p), playlistId, ParseBpmRange(p), scope, keepIds.Count > 0 ? keepIds : null);
    }

    /// <summary>The inverse of <see cref="ParseQuery"/>: a query in the shape the UI sends it.</summary>
    public static object ToContext(TrackQuery query) => new
    {
        query.Text,
        query.Sort,
        Desc = query.Descending,
        query.Filter,
        query.PlaylistId,
        query.Bpm,
        query.Scope,
    };

    /// <summary>Reads a <c>{ min, max, includeUnknown }</c> range; a missing or open one reads as null.</summary>
    public static BpmRange? ParseBpmRange(JsonElement p, string name = "bpm")
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var range = new BpmRange(
            Number(r, "min"),
            Number(r, "max"),
            r.TryGetProperty("includeUnknown", out var u) && u.ValueKind == JsonValueKind.True);
        return range.IsOpen ? null : range;
    }

    /// <summary>
    /// A query of the songs being played through lists the player's pool as it is right now; the
    /// list it was drawn from no longer matters.
    /// </summary>
    private TrackQuery WithPool(TrackQuery query) =>
        query.Scope == TrackScope.Playing
            ? new TrackQuery(query.Text, query.Sort, query.Descending, Scope: TrackScope.Playing, Ids: [.. _controller.Pool])
            : query;

    /// <summary>Reads <c>set: { title, artist, …, trackNumber }</c>: text or null, and numbers or null for year and track.</summary>
    private static Dictionary<TrackField, object?> ParseInfo(JsonElement p)
    {
        var values = new Dictionary<TrackField, object?>();
        if (!p.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in set.EnumerateObject())
        {
            values[ParseField(property.Name)] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number when property.Value.TryGetInt64(out var n) => n,
                _ => throw new ArgumentException($"'{property.Name}' has an unusable value."),
            };
        }

        return values;
    }

    private static TrackField ParseField(string? name) =>
        Enum.TryParse<TrackField>(name, ignoreCase: true, out var field) && Enum.IsDefined(field)
            ? field
            : throw new ArgumentException($"Unknown song field '{name}'.");

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : null;

    private static TrackFilter? ParseFilter(JsonElement p)
    {
        if (!p.TryGetProperty("filter", out var f) || f.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var filter = new TrackFilter
        {
            AllTags = f.GetIds("allTags"),
            AnyTags = f.GetIds("anyTags"),
            NoneTags = f.GetIds("noneTags"),
            Untagged = f.TryGetProperty("untagged", out var ut) && ut.ValueKind == JsonValueKind.True,
            BpmMin = Number(f, "bpmMin"),
            BpmMax = Number(f, "bpmMax"),
            IncludeUnknownBpm = f.TryGetProperty("includeUnknownBpm", out var u) && u.ValueKind == JsonValueKind.True,
        };
        return filter.IsEmpty ? null : filter;
    }

    /// <summary>Scans in the background. Requests made while a scan runs are folded into one follow-up scan.</summary>
    public void StartScan() => _ = ScanLoopAsync();

    /// <summary>
    /// Runs a change to the tracks table while no scan is running, since a scan works from its own
    /// snapshot of the table. Scans asked for meanwhile run afterwards.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _scanLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _scanLock.Release();
            if (_rescanRequested)
            {
                StartScan();
            }
        }
    }

    /// <summary>Scans (or joins the scan that is running, plus a follow-up) and waits for it to finish.</summary>
    public async Task ScanAndWaitAsync()
    {
        StartScan();
        await _scanLock.WaitAsync();
        _scanLock.Release();
    }

    public void Dispose() => _shutdown.Cancel();

    private async Task<IReadOnlyList<string>?> AddFoldersAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Add music folders", Multiselect = true };
        if (dialog.ShowDialog(_owner) != true)
        {
            return null;
        }

        foreach (var folder in dialog.FolderNames)
        {
            await _folders.AddAsync(folder);
        }

        StartScan();
        return await _folders.GetAllAsync();
    }

    /// <summary>
    /// Moves songs' files to the Recycle Bin and marks them missing. A song that is loaded is
    /// closed first, since Windows can't move a file that is open.
    /// </summary>
    private async Task<RecycleResult> RecycleAsync(List<long> ids)
    {
        await _controller.RemoveAsync(ids, unload: true);

        var recycled = new List<long>();
        var failed = new List<string>();
        foreach (var id in ids)
        {
            if (await _tracks.GetAsync(id) is not { } track)
            {
                continue;
            }

            if (NativeMethods.SendToRecycleBin(track.Path, _owner))
            {
                recycled.Add(id);
            }
            else
            {
                failed.Add(track.Title);
            }
        }

        await _stats.MarkMissingAsync(recycled);
        EmitTracksChanged();
        return new RecycleResult(recycled.Count, failed);
    }

    /// <summary>Songs left or joined the library, which changes tag and playlist counts too.</summary>
    public void EmitTracksChanged()
    {
        _bridge.Emit("library.changed");
        _bridge.Emit("tags.changed");
        _bridge.Emit("playlists.changed");
    }

    private async Task ScanLoopAsync()
    {
        _rescanRequested = true;
        if (!await _scanLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (_rescanRequested && !_shutdown.IsCancellationRequested)
            {
                _rescanRequested = false;
                SetStatus(_status with { Running = true, Processed = 0, Total = 0 });
                try
                {
                    var result = await _scanner.ScanAsync(new ScanReporter(this), _shutdown.Token);
                    SetStatus(_status with { Running = false, LastResult = result });
                    ScanCompleted?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SetStatus(_status with { Running = false });
                    _bridge.Emit("library.error", new { message = $"Scan failed: {ex.Message}" });
                }

                _bridge.Emit("library.changed");
            }
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void SetStatus(ScanStatus status)
    {
        _status = status;
        _bridge.Emit("library.scan", status);
    }

    public sealed record ScanStatus(bool Running, int Processed, int Total, ScanResult? LastResult);

    private sealed record RecycleResult(int Recycled, IReadOnlyList<string> Failed);

    /// <summary>
    /// Forwards scanner progress at most ~10 times a second, and lets the list refresh every
    /// couple of seconds so a long first scan fills in as it goes.
    /// </summary>
    private sealed class ScanReporter(LibraryApi api) : IProgress<ScanProgress>
    {
        private long _lastProgress;
        private long _lastRefresh = Environment.TickCount64;

        public void Report(ScanProgress value)
        {
            var now = Environment.TickCount64;
            if (now - _lastProgress >= 100 || value.Processed == value.Total)
            {
                _lastProgress = now;
                api.SetStatus(api._status with { Processed = value.Processed, Total = value.Total });
            }

            if (now - _lastRefresh >= 2000)
            {
                _lastRefresh = now;
                api._bridge.Emit("library.changed");
            }
        }
    }
}
