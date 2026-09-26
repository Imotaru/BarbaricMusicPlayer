using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Barbaric.App.Bridge;
using Microsoft.Web.WebView2.Core;

namespace Barbaric.App;

/// <summary>Where the window was, in device pixels, and whether it was the mini-player.</summary>
public sealed record WindowSettings(
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool Maximized,
    bool Compact = false,
    int? CompactLeft = null,
    int? CompactTop = null);

/// <summary>The window frame: saved placement, the compact mini-player, and the background colour.</summary>
public partial class MainWindow
{
    private const string WindowKey = "window";
    private const string BackgroundKey = "background";
    private const string UiKey = "ui";
    private const double ResizeGrip = 5;
    private const double CompactWidth = 360;
    private const double CompactHeight = 96;
    private const double CompactMargin = 16;
    private const double NormalMinWidth = 480;
    private const double NormalMinHeight = 320;

    private WindowSettings? _savedWindow;

    /// <summary>The placement to go back to when leaving the mini-player.</summary>
    private WindowPlacement? _expanded;
    private bool _compact;
    private NativePoint? _compactAt;
    private string _background = "#121214";

    /// <summary>The UI's own preferences, kept as the JSON it sent so the host never has to understand them.</summary>
    private string? _uiPrefs;
    private string? _initialScriptId;
    private bool _initialScriptBusy;
    private bool _initialScriptDirty;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <summary>Applied before the window first shows, so it opens where and how it was left.</summary>
    private void LoadFrameSettings()
    {
        _savedWindow = _settings.Get<WindowSettings>(WindowKey);
        if (_savedWindow is { } saved && saved.Right > saved.Left && saved.Bottom > saved.Top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (saved.Maximized && !saved.Compact)
            {
                WindowState = WindowState.Maximized;
            }

            _compactAt = saved.CompactLeft is { } x && saved.CompactTop is { } y ? new NativePoint(x, y) : null;
        }
        else
        {
            _savedWindow = null;
        }

        if (_settings.Get<string>(BackgroundKey) is { } background && HexColor().IsMatch(background))
        {
            ApplyBackground(background);
        }

        _uiPrefs = ValidJson(_settings.GetRaw(UiKey));
    }

    /// <summary>Runs once the window handle exists but before it shows.</summary>
    private void RestorePlacement()
    {
        if (_savedWindow is not { } saved)
        {
            return;
        }

        // Windows moves a placement that is no longer on any screen back onto one.
        var placement = new WindowPlacement
        {
            ShowCmd = NativeMethods.SwHide,
            NormalPosition = new NativeRect(saved.Left, saved.Top, saved.Right, saved.Bottom),
        };
        NativeMethods.SetPlacement(this, placement);

        if (saved.Compact)
        {
            placement.ShowCmd = saved.Maximized ? NativeMethods.SwShowMaximized : NativeMethods.SwShowNormal;
            EnterCompact(placement);
        }
    }

    private void SaveFrameSettings()
    {
        if ((_compact ? _expanded : NativeMethods.GetPlacement(this)) is not { } placement)
        {
            return;
        }

        var compactAt = _compact ? TopLeft(NativeMethods.GetBounds(this)) : _compactAt;
        var normal = placement.NormalPosition;
        _settings.Save(WindowKey, new WindowSettings(
            normal.Left,
            normal.Top,
            normal.Right,
            normal.Bottom,
            IsMaximized(placement),
            _compact,
            compactAt?.X,
            compactAt?.Y));
    }

    private void SetCompact(bool compact)
    {
        if (compact == _compact)
        {
            return;
        }

        if (compact)
        {
            if (NativeMethods.GetPlacement(this) is { } placement)
            {
                EnterCompact(placement);
            }
        }
        else
        {
            LeaveCompact();
        }

        RefreshInitialScript();
    }

    private void EnterCompact(WindowPlacement expanded)
    {
        _expanded = expanded;
        _compact = true;
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        MinWidth = 0;
        MinHeight = 0;

        // No resize border or maximize: the mini-player has one size and just sits on top.
        ResizeMode = ResizeMode.CanMinimize;
        WindowChrome.SetWindowChrome(this, CreateChrome(resizeBorder: 0));
        Topmost = true;
        NativeMethods.KeepOnTop(this);
        NativeMethods.SetBounds(this, CompactBounds());
        UpdateFrameInset();
        EmitWindowState();
    }

    private void LeaveCompact()
    {
        _compactAt = TopLeft(NativeMethods.GetBounds(this));
        _compact = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, CreateChrome(ResizeGrip));
        MinWidth = NormalMinWidth;
        MinHeight = NormalMinHeight;

        if (_expanded is { } placement)
        {
            var maximized = IsMaximized(placement);
            // Expanding shouldn't pull the window in front of whatever the user is doing.
            placement.ShowCmd = NativeMethods.SwShowNoActivate;
            NativeMethods.SetPlacement(this, placement);
            if (maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        UpdateFrameInset();
        EmitWindowState();
    }

    /// <summary>Where the mini-player was last time, or the bottom-right corner of the current screen.</summary>
    private NativeRect CompactBounds()
    {
        if (_compactAt is { } at && NativeMethods.MonitorAt(at.X + 8, at.Y + 8) is { } monitor)
        {
            var (width, height) = CompactSize(monitor.Dpi);
            var work = monitor.WorkArea;
            var left = Math.Clamp(at.X, work.Left, Math.Max(work.Left, work.Right - width));
            var top = Math.Clamp(at.Y, work.Top, Math.Max(work.Top, work.Bottom - height));
            return new NativeRect(left, top, left + width, top + height);
        }

        var (area, dpi) = NativeMethods.CurrentMonitor(this);
        var (w, h) = CompactSize(dpi);
        var margin = (int)Math.Round(CompactMargin * dpi / 96);
        return new NativeRect(area.Right - w - margin, area.Bottom - h - margin, area.Right - margin, area.Bottom - margin);
    }

    private static (int Width, int Height) CompactSize(uint dpi) =>
        ((int)Math.Round(CompactWidth * dpi / 96), (int)Math.Round(CompactHeight * dpi / 96));

    private static NativePoint TopLeft(NativeRect rect) => new(rect.Left, rect.Top);

    private static bool IsMaximized(WindowPlacement placement) =>
        placement.ShowCmd == NativeMethods.SwShowMaximized
        || (placement.ShowCmd == NativeMethods.SwShowMinimized && (placement.Flags & NativeMethods.WpfRestoreToMaximized) != 0);

    private static WindowChrome CreateChrome(double resizeBorder) => new()
    {
        CaptionHeight = 0,
        ResizeBorderThickness = new Thickness(resizeBorder),
        GlassFrameThickness = new Thickness(0),
        CornerRadius = new CornerRadius(0),
        UseAeroCaptionButtons = false,
    };

    private void UpdateFrameInset() =>
        WebView.Margin = _compact ? new Thickness(0)
            : WindowState == WindowState.Maximized ? NativeMethods.MaximizedOverhang(this)
            : new Thickness(ResizeGrip);

    private void RegisterWindowApi(WebBridge bridge)
    {
        bridge.Query("window.getState", _ => WindowSnapshot());
        bridge.Command("window.minimize", _ => WindowState = WindowState.Minimized);
        bridge.Command("window.toggleMaximize", _ =>
        {
            if (!_compact)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
        });
        bridge.Command("window.close", _ => Close());
        bridge.Command("window.setCompact", p => SetCompact(p.GetProperty("on").GetBoolean()));

        // Keeps the native resize border the same color as the UI theme.
        bridge.Command("window.setBackground", p =>
        {
            var color = p.GetProperty("color").GetString();
            if (color is null || !HexColor().IsMatch(color) || color.Equals(_background, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyBackground(color);
            _settings.Save(BackgroundKey, _background);
            RefreshInitialScript();
        });

        bridge.Query("settings.getUi", _ =>
        {
            if (_uiPrefs is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(_uiPrefs);
            return document.RootElement.Clone();
        });
        bridge.Command("settings.setUi", p =>
        {
            _uiPrefs = p.GetProperty("value").GetRawText();
            _settings.SaveRaw(UiKey, _uiPrefs);
            RefreshInitialScript();
        });
    }

    private object WindowSnapshot() => new { maximized = WindowState == WindowState.Maximized, compact = _compact };

    private void EmitWindowState() => _bridge?.Emit("window.state", WindowSnapshot());

    private void ApplyBackground(string hex)
    {
        _background = hex.ToLowerInvariant();
        var color = (Color)ColorConverter.ConvertFromString(_background);
        Background = new SolidColorBrush(color);
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        ApplyColorScheme();
    }

    /// <summary>Light or dark to match the theme, so the browser's own defaults (before the page paints) match too.</summary>
    private void ApplyColorScheme()
    {
        if (WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        var color = (Color)ColorConverter.ConvertFromString(_background);
        var light = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255 > 0.5;
        core.Profile.PreferredColorScheme = light ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
    }

    /// <summary>
    /// Settings the page needs before its first paint (theme, mini-player), set as a global by a
    /// script that runs ahead of the page's own. It is replaced whenever they change, so a reload
    /// starts from the current ones.
    /// </summary>
    private string InitialScript()
    {
        var initial = new JsonObject
        {
            ["ui"] = _uiPrefs is null ? null : JsonNode.Parse(_uiPrefs),
            ["background"] = _background,
            ["compact"] = _compact,
        };
        return $"window.__barbaricInitial = {initial.ToJsonString()};";
    }

    private async void RefreshInitialScript()
    {
        if (WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        if (_initialScriptBusy)
        {
            _initialScriptDirty = true;
            return;
        }

        _initialScriptBusy = true;
        try
        {
            do
            {
                _initialScriptDirty = false;
                var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(InitialScript());
                if (_initialScriptId is { } old)
                {
                    core.RemoveScriptToExecuteOnDocumentCreated(old);
                }

                _initialScriptId = id;
            }
            while (_initialScriptDirty);
        }
        catch (Exception ex)
        {
            // Only a later reload would notice: it would briefly show the older settings.
            System.Diagnostics.Debug.WriteLine($"Couldn't update the startup script: {ex}");
        }
        finally
        {
            _initialScriptBusy = false;
        }
    }

    private static string? ValidJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
