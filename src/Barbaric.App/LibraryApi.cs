using System.Text.Json;
using System.Windows;
using Barbaric.App.Bridge;
using Barbaric.Core.Library;
using Microsoft.Win32;

namespace Barbaric.App;

/// <summary>Exposes the music library to the UI as <c>library.*</c> bridge methods and events.</summary>
public sealed class LibraryApi : IDisposable
{
    private readonly FolderRepository _folders;
    private readonly TrackRepository _tracks;
    private readonly LibraryScanner _scanner;
    private readonly WebBridge _bridge;
    private readonly Window _owner;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _rescanRequested;
    private ScanStatus _status = new(false, 0, 0, null);

    public LibraryApi(FolderRepository folders, TrackRepository tracks, LibraryScanner scanner, WebBridge bridge, Window owner)
    {
        _folders = folders;
        _tracks = tracks;
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
            var query = ParseQuery(p);
            var offset = p.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = p.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
            return await Task.Run(() => _tracks.QueryAsync(query, offset, limit));
        });
    }

    /// <summary>Reads <c>{ text, sort, desc }</c> as sent by the UI.</summary>
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
        return new TrackQuery(text, sort, desc);
    }

    /// <summary>Scans in the background. Requests made while a scan runs are folded into one follow-up scan.</summary>
    public void StartScan() => _ = ScanLoopAsync();

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
