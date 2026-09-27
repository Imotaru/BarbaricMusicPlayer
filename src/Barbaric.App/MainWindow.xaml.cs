using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Barbaric.App.Bridge;
using Barbaric.Core.Analysis;
using Barbaric.Core.Audio;
using Barbaric.Core.Library;
using Barbaric.Core.Playback;
using Microsoft.Web.WebView2.Core;

namespace Barbaric.App;

public partial class MainWindow : Window
{
    private const string VirtualHost = "barbaric.example";

    private readonly AudioEngine _engine = new();
    private readonly LibraryDatabase _database;
    private readonly SettingsStore _settings;
    private readonly GlobalHotkeys _hotkeys;
    private WebBridge? _bridge;
    private PlaybackController? _controller;
    private PlayerApi? _player;
    private LibraryApi? _library;
    private BpmApi? _bpm;
    private LoudnessApi? _loudness;
    private MediaControls? _media;

    public MainWindow()
    {
        InitializeComponent();

        // BARBARIC_LIBRARY_DB points development and test runs at a throwaway library (and its settings).
        try
        {
            _database = new LibraryDatabase(DatabasePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The music library could not be opened.\n\n{ex.Message}", "Barbaric Music Player");
            Environment.Exit(1);
            throw;
        }

        _settings = new SettingsStore(new SettingsRepository(_database));
        _settings.WriteFailed += (_, ex) => _bridge?.Emit("library.error", new { message = $"Couldn't save settings: {ex.Message}" });
        _hotkeys = new GlobalHotkeys(_settings, RunHotkey);
        LoadFrameSettings();

        SourceInitialized += (_, _) =>
        {
            NativeMethods.UseRoundedCorners(this);
            RestorePlacement();
            UpdateFrameInset();
            _hotkeys.Attach(new WindowInteropHelper(this).Handle);
        };

        // The WebView is the whole UI; route keyboard focus into it so shortcuts work without a click.
        Activated += (_, _) => WebView.Focus();
        StateChanged += (_, _) =>
        {
            // Win+Up and the like: the mini-player doesn't maximize.
            if (_compact && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                return;
            }

            UpdateFrameInset();
            EmitWindowState();

            // Music keeps playing in the host, so the UI can shed memory while minimized.
            if (WebView.CoreWebView2 is { } core)
            {
                core.MemoryUsageTargetLevel = WindowState == WindowState.Minimized
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        };
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _hotkeys.Dispose();
            _media?.Dispose();
            _bpm?.Dispose();
            _loudness?.Dispose();
            _library?.Dispose();
            _player?.Dispose();

            // Off the UI thread: waiting on it here would block the continuations the save needs.
            if (_controller is { } controller)
            {
                Task.Run(controller.CloseAsync).Wait(TimeSpan.FromSeconds(2));
                controller.Dispose();
            }

            _engine.Dispose();
        };
    }

    private static string DatabasePath =>
        Environment.GetEnvironmentVariable("BARBARIC_LIBRARY_DB") ?? LibraryDatabase.DefaultPath;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Read while the window still has its placement; written before the process goes away.
        SaveFrameSettings();
        _player?.SaveQueue();
        _settings.Flush();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // A scratch library also gets its own browser profile, so test runs never touch the real one.
            var dataDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!, "WebView2");

            // Until the page paints, the browser shows its default background; set that way (the only
            // way that also covers the first frames), it's the theme's rather than a dark or white flash.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF" + _background.TrimStart('#'));
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await WebView.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is required but was not found.", Title);
            Close();
            return;
        }

        var core = WebView.CoreWebView2;
        ConfigureSettings(core.Settings);
        ApplyColorScheme();

        var startUri = await ResolveStartUriAsync(core);
        if (startUri is null)
        {
            MessageBox.Show(
                this,
                "No UI found. Start the dev server (npm run dev in ui/) or build it (npm run build).",
                Title);
            Close();
            return;
        }

        var origin = new Uri(startUri.GetLeftPart(UriPartial.Authority));

        // The UI is the only thing this WebView should ever show.
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
                || Uri.Compare(target, origin, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;

        var tracks = new TrackRepository(_database);
        var stats = new PlayStatsRepository(_database);
        _controller = new PlaybackController(_engine, tracks, stats, SynchronizationContext.Current);

        _bridge = new WebBridge(core, Dispatcher, origin);
        RegisterWindowApi(_bridge);
        _hotkeys.RegisterApi(_bridge);
        _player = new PlayerApi(_engine, _controller, _bridge, _settings, this);
        _library = new LibraryApi(
            new FolderRepository(_database), tracks, stats, _controller, new LibraryScanner(_database), _bridge, this);
        _ = new TagApi(new TagRepository(_database), _bridge);
        _ = new PlaylistApi(new PlaylistRepository(_database), _bridge);
        _bpm = new BpmApi(new BpmBackgroundAnalyzer(tracks), tracks, _bridge);
        _loudness = new LoudnessApi(new LoudnessBackgroundAnalyzer(tracks), tracks, _controller, _player, _bridge, Dispatcher);
        _ = new BackupApi(new LibraryBackup(_database), _library, _settings, ApplyBackupSettings, _bridge, this);
        _media = MediaControls.TryCreate(new WindowInteropHelper(this).Handle, _player);
        TaskbarButtons.Attach(this, _player);

        // Analysis waits for the startup scan, and each rescan hands it the songs it found.
        _library.ScanCompleted += (_, _) =>
        {
            _bpm.StartBackground();
            _loudness.StartBackground();
        };

        // "Open with": a file path on the command line starts playing immediately; otherwise the
        // last queue comes back, paused. Either happens before the page asks for the player state.
        var args = Environment.GetCommandLineArgs();
        var openWith = args.Length > 1 && File.Exists(args[1]) ? args[1] : null;
        if (openWith is null)
        {
            await _player.RestoreQueueAsync();
        }

        _initialScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(InitialScript());
        core.Navigate(startUri.ToString());
        WebView.Focus();

        // Pick up files added, moved or deleted while the app was closed.
        _library.StartScan();

        if (openWith is not null)
        {
            await _player.OpenAsync(openWith);
        }
    }

    /// <summary>Puts settings from a backup into effect, the same way changing them here would.</summary>
    private void ApplyBackupSettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (settings.TryGetValue(UiKey, out var ui) && ui.ValueKind == JsonValueKind.Object)
        {
            _uiPrefs = ui.GetRawText();
            _settings.SaveRaw(UiKey, _uiPrefs);
            RefreshInitialScript();
        }

        // The limit first, so it doesn't clamp a volume that already fits under it.
        if (settings.TryGetValue(PlayerApi.VolumeLimitKey, out var limit) && limit.ValueKind == JsonValueKind.Number)
        {
            _player?.SetVolumeLimit(limit.GetDouble());
        }

        if (settings.TryGetValue(PlayerApi.VolumeKey, out var volume) && volume.ValueKind == JsonValueKind.Number)
        {
            _player?.SetVolume(volume.GetDouble());
        }

        if (settings.TryGetValue(PlayerApi.LoopTrackKey, out var loop) && loop.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _player?.SetLoop(loop.GetBoolean());
        }

        if (settings.TryGetValue(PlayerApi.NormalizeKey, out var normalize) && normalize.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _player?.SetNormalize(normalize.GetBoolean());
        }

        if (settings.TryGetValue(PlayerApi.WeighByLengthKey, out var weigh) && weigh.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _ = _player?.SetWeighByLengthAsync(weigh.GetBoolean());
        }

        if (settings.TryGetValue(GlobalHotkeys.SettingsKey, out var hotkeys) && hotkeys.ValueKind == JsonValueKind.Object)
        {
            _hotkeys.ReplaceAll(hotkeys.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!));
        }
    }

    /// <summary>What a global hotkey does. Hotkeys can fire before the UI is up; the player ones wait for it.</summary>
    private void RunHotkey(string command)
    {
        switch (command)
        {
            case GlobalHotkeys.ShowWindow:
                if (WindowState == WindowState.Minimized)
                {
                    SystemCommands.RestoreWindow(this);
                }

                Activate();
                return;
            case GlobalHotkeys.MiniPlayer:
                SetCompact(!_compact);
                return;
        }

        if (_player is not { } player)
        {
            return;
        }

        switch (command)
        {
            case GlobalHotkeys.PlayPause: player.Run(player.TogglePlayPauseAsync); break;
            case GlobalHotkeys.Next: player.Run(player.NextAsync); break;
            case GlobalHotkeys.Previous: player.Run(player.PreviousAsync); break;
            case GlobalHotkeys.Shuffle: player.Run(player.ToggleShuffleAsync); break;
            case GlobalHotkeys.Loop: player.ToggleLoop(); break;
            case GlobalHotkeys.SeekForward: player.SeekBy(5); break;
            case GlobalHotkeys.SeekBack: player.SeekBy(-5); break;
            case GlobalHotkeys.VolumeUp: player.StepVolume(1); break;
            case GlobalHotkeys.VolumeDown: player.StepVolume(-1); break;
        }
    }

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.IsNonClientRegionSupportEnabled = true; // enables CSS `app-region: drag` for the title bar
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if DEBUG
        settings.AreDevToolsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
    }

    /// <summary>
    /// Debug builds prefer the Vite dev server (hot reload); otherwise the bundled UI in wwwroot is served
    /// from a virtual host.
    /// </summary>
    private static async Task<Uri?> ResolveStartUriAsync(CoreWebView2 core)
    {
#if DEBUG
        var devServer = new Uri(Environment.GetEnvironmentVariable("BARBARIC_UI_URL") ?? "http://localhost:5173/");
        if (await IsReachableAsync(devServer))
        {
            return devServer;
        }
#else
        await Task.CompletedTask;
#endif

        var uiDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(uiDir, "index.html")))
        {
            return null;
        }

        core.SetVirtualHostNameToFolderMapping(VirtualHost, uiDir, CoreWebView2HostResourceAccessKind.DenyCors);
        return new Uri($"https://{VirtualHost}/index.html");
    }

    private static async Task<bool> IsReachableAsync(Uri uri)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        try
        {
            using var response = await http.GetAsync(uri);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
