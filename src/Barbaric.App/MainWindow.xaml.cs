using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
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
    private const double ResizeGrip = 5;

    private readonly AudioEngine _engine = new();
    private WebBridge? _bridge;
    private PlaybackController? _controller;
    private PlayerApi? _player;
    private LibraryApi? _library;
    private BpmApi? _bpm;

    public MainWindow()
    {
        InitializeComponent();
        UpdateFrameInset();

        SourceInitialized += (_, _) => NativeMethods.UseRoundedCorners(this);

        // The WebView is the whole UI; route keyboard focus into it so shortcuts work without a click.
        Activated += (_, _) => WebView.Focus();
        StateChanged += (_, _) =>
        {
            UpdateFrameInset();
            _bridge?.Emit("window.state", WindowSnapshot());

            // Music keeps playing in the host, so the UI can shed memory while minimized.
            if (WebView.CoreWebView2 is { } core)
            {
                core.MemoryUsageTargetLevel = WindowState == WindowState.Minimized
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        };
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closed += (_, _) =>
        {
            _bpm?.Dispose();
            _library?.Dispose();
            _player?.Dispose();
            _controller?.Dispose();
            _engine.Dispose();
        };
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BarbaricMusicPlayer",
                "WebView2");
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

        // BARBARIC_LIBRARY_DB points development and test runs at a throwaway library.
        var database = new LibraryDatabase(
            Environment.GetEnvironmentVariable("BARBARIC_LIBRARY_DB") ?? LibraryDatabase.DefaultPath);
        var tracks = new TrackRepository(database);
        _controller = new PlaybackController(_engine, tracks, SynchronizationContext.Current);

        _bridge = new WebBridge(core, Dispatcher, origin);
        RegisterWindowApi(_bridge);
        _player = new PlayerApi(_engine, _controller, _bridge, this);
        _library = new LibraryApi(new FolderRepository(database), tracks, new LibraryScanner(database), _bridge, this);
        _ = new TagApi(new TagRepository(database), _bridge);
        _ = new PlaylistApi(new PlaylistRepository(database), _bridge);
        _bpm = new BpmApi(new BpmBackgroundAnalyzer(tracks), tracks, _bridge);

        // Analysis waits for the startup scan, and each rescan hands it the songs it found.
        _library.ScanCompleted += (_, _) => _bpm.StartBackground();

        core.Navigate(startUri.ToString());
        WebView.Focus();

        // Pick up files added, moved or deleted while the app was closed.
        _library.StartScan();

        // "Open with": a file path on the command line starts playing immediately.
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            await _player.OpenAsync(args[1]);
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

    private void RegisterWindowApi(WebBridge bridge)
    {
        bridge.Query("window.getState", _ => WindowSnapshot());
        bridge.Command("window.minimize", _ => WindowState = WindowState.Minimized);
        bridge.Command("window.toggleMaximize", _ =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        bridge.Command("window.close", _ => Close());

        // Keeps the native resize border the same color as the UI theme.
        bridge.Command("window.setBackground", p =>
        {
            var color = (Color)ColorConverter.ConvertFromString(p.GetProperty("color").GetString()!);
            Background = new SolidColorBrush(color);
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        });
    }

    private object WindowSnapshot() => new { maximized = WindowState == WindowState.Maximized };

    private void UpdateFrameInset() =>
        WebView.Margin = WindowState == WindowState.Maximized
            ? NativeMethods.MaximizedOverhang(this)
            : new Thickness(ResizeGrip);
}
