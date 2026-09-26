using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;

namespace Barbaric.App;

/// <summary>
/// Hooks the player up to Windows' media controls: the hardware media keys, the volume flyout and
/// the lock screen show what's playing and control it.
/// </summary>
public sealed class MediaControls : IDisposable
{
    /// <summary>Windows moves the flyout's progress bar on its own; this just keeps it from drifting.</summary>
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);

    private readonly SystemMediaTransportControls _controls;
    private readonly PlayerApi _player;
    private readonly DispatcherTimer _timelineTimer;
    private string? _shownPath;
    private int _artGeneration;

    private MediaControls(SystemMediaTransportControls controls, PlayerApi player)
    {
        _controls = controls;
        _player = player;
        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsStopEnabled = false;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionChangeRequested;
        _player.Changed += OnPlayerChanged;
        _player.Seeked += OnSeeked;
        _timelineTimer = new DispatcherTimer(TimelineInterval, DispatcherPriority.Background, (_, _) => UpdateTimeline(), Dispatcher.CurrentDispatcher);
        Update();
    }

    /// <summary>Null when the OS media controls aren't available (they're optional; the player works without them).</summary>
    public static MediaControls? TryCreate(nint hwnd, PlayerApi player)
    {
        try
        {
            return new MediaControls(SystemMediaTransportControlsInterop.GetForWindow(hwnd), player);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media controls unavailable: {ex}");
            return null;
        }
    }

    public void Dispose()
    {
        _timelineTimer.Stop();
        _player.Changed -= OnPlayerChanged;
        _player.Seeked -= OnSeeked;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
        _controls.IsEnabled = false;
    }

    private void OnPlayerChanged(object? sender, EventArgs e) => Update();

    private void OnSeeked(object? sender, EventArgs e) => UpdateTimeline();

    // Both arrive on a thread-pool thread; the player lives on the UI thread.
    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: _player.Run(_player.PlayAsync); break;
            case SystemMediaTransportControlsButton.Pause: _player.Run(() => { _player.Pause(); return Task.CompletedTask; }); break;
            case SystemMediaTransportControlsButton.Next: _player.Run(_player.NextAsync); break;
            case SystemMediaTransportControlsButton.Previous: _player.Run(_player.PreviousAsync); break;
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _player.Run(() =>
        {
            _player.Seek(args.RequestedPlaybackPosition);
            return Task.CompletedTask;
        });

    private void Update()
    {
        var now = _player.NowPlaying;
        _controls.PlaybackStatus = now.Path is null ? MediaPlaybackStatus.Closed
            : now.Playing ? MediaPlaybackStatus.Playing
            : now.Paused ? MediaPlaybackStatus.Paused
            : MediaPlaybackStatus.Stopped;
        _controls.IsNextEnabled = now.HasNext;
        _controls.IsPreviousEnabled = now.Path is not null;
        _timelineTimer.IsEnabled = now.Playing;

        if (now.Path != _shownPath)
        {
            _shownPath = now.Path;
            ShowTrack(now);
        }

        UpdateTimeline();
    }

    private void ShowTrack(NowPlaying now)
    {
        var updater = _controls.DisplayUpdater;
        updater.ClearAll();
        if (now.Path is null)
        {
            updater.Update();
            return;
        }

        // The type has to be set before the music properties can be.
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = now.Title ?? "";
        updater.MusicProperties.Artist = now.Artist ?? "";
        updater.MusicProperties.AlbumTitle = now.Album ?? "";
        updater.Update();
        _ = ShowArtAsync(now.Path, ++_artGeneration);
    }

    /// <summary>Adds the song's embedded cover once it's read, unless another song has started by then.</summary>
    private async Task ShowArtAsync(string path, int generation)
    {
        try
        {
            var art = await Task.Run(() => ReadArt(path));
            if (art is null || generation != _artGeneration)
            {
                return;
            }

            // From memory: a reference to the file itself would keep it open, and it couldn't be recycled.
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(art);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            if (generation == _artGeneration)
            {
                _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                _controls.DisplayUpdater.Update();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No cover for {path}: {ex.Message}");
        }
    }

    private static byte[]? ReadArt(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)?.Data.Data
            ?? file.Tag.Pictures.FirstOrDefault()?.Data.Data;
    }

    private void UpdateTimeline()
    {
        var now = _player.NowPlaying;
        var end = now.Duration > TimeSpan.Zero ? now.Duration : TimeSpan.Zero;
        _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = end,
            MaxSeekTime = end,
            Position = now.Position < end ? now.Position : end,
        });
    }
}
