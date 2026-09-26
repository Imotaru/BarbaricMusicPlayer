using System.Windows.Interop;
using Barbaric.App.Bridge;
using Barbaric.Core.Hotkeys;

namespace Barbaric.App;

/// <summary>
/// Opt-in shortcuts that work while another app has focus, registered with Windows for the main
/// window. Media keys don't need these: they reach the player through the OS media controls.
/// </summary>
public sealed class GlobalHotkeys(SettingsStore settings, Action<string> run) : IDisposable
{
    public const string PlayPause = "player.toggle";
    public const string Next = "player.next";
    public const string Previous = "player.previous";
    public const string SeekForward = "player.seekForward";
    public const string SeekBack = "player.seekBack";
    public const string VolumeUp = "player.volumeUp";
    public const string VolumeDown = "player.volumeDown";
    public const string Shuffle = "player.shuffle";
    public const string Loop = "player.loop";
    public const string MiniPlayer = "window.compact";
    public const string ShowWindow = "window.show";

    /// <summary>The commands that can have a global hotkey. Each one's hotkey id is its index + 1.</summary>
    public static readonly string[] Commands =
        [PlayPause, Next, Previous, SeekForward, SeekBack, VolumeUp, VolumeDown, Shuffle, Loop, MiniPlayer, ShowWindow];

    private const string SettingsKey = "globalHotkeys";
    private const uint NoRepeat = 0x4000;

    private readonly Dictionary<string, string> _bindings =
        settings.Get<Dictionary<string, string>>(SettingsKey) ?? [];
    private readonly HashSet<string> _inUse = [];
    private nint _hwnd;
    private bool _suspended;

    /// <summary>Hooks the window and registers the saved hotkeys; call once its handle exists.</summary>
    public void Attach(nint hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        foreach (var command in Commands)
        {
            Register(command);
        }
    }

    public void RegisterApi(WebBridge bridge)
    {
        bridge.Query("hotkeys.getGlobal", _ => State());
        bridge.Query("hotkeys.setGlobal", p =>
        {
            var command = p.GetProperty("id").GetString();
            var keys = p.TryGetProperty("keys", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null;
            return new { result = Set(command, keys), state = State() };
        });

        // Recording a new shortcut in the settings must see keys a hotkey would otherwise swallow.
        bridge.Command("hotkeys.suspend", p => Suspend(p.GetProperty("on").GetBoolean()));
    }

    public void Dispose()
    {
        foreach (var command in Commands)
        {
            Unregister(command);
        }

        HwndSource.FromHwnd(_hwnd)?.RemoveHook(WndProc);
    }

    /// <returns><c>ok</c>, <c>inUse</c> (another app holds it) or <c>invalid</c>.</returns>
    private string Set(string? command, string? keys)
    {
        if (command is null || !Commands.Contains(command) || (keys is not null && HotkeyChord.TryParse(keys) is null))
        {
            return "invalid";
        }

        Unregister(command);
        if (keys is null)
        {
            _bindings.Remove(command);
        }
        else
        {
            // One chord, one command.
            foreach (var other in _bindings.Where(b => b.Key != command && b.Value == keys).Select(b => b.Key).ToList())
            {
                Unregister(other);
                _bindings.Remove(other);
            }

            _bindings[command] = keys;
        }

        settings.Save(SettingsKey, _bindings);
        Register(command);
        return _inUse.Contains(command) ? "inUse" : "ok";
    }

    private void Suspend(bool on)
    {
        if (on == _suspended)
        {
            return;
        }

        foreach (var command in Commands)
        {
            Unregister(command);
        }

        _suspended = on;
        foreach (var command in Commands)
        {
            Register(command);
        }
    }

    private object State() =>
        Commands.Select(c => new { id = c, keys = _bindings.GetValueOrDefault(c), inUse = _inUse.Contains(c) }).ToList();

    private void Register(string command)
    {
        _inUse.Remove(command);
        if (_hwnd == 0 || _suspended || !_bindings.TryGetValue(command, out var keys) || HotkeyChord.TryParse(keys) is not { } chord)
        {
            return;
        }

        // Holding a key down would otherwise toggle, skip or shrink over and over; seeking and volume should repeat.
        var repeats = command is SeekForward or SeekBack or VolumeUp or VolumeDown;
        if (!NativeMethods.RegisterHotkey(_hwnd, IdOf(command), chord.Modifiers | (repeats ? 0 : NoRepeat), chord.VirtualKey, out var error)
            && error == NativeMethods.ErrorHotkeyAlreadyRegistered)
        {
            _inUse.Add(command);
        }
    }

    private void Unregister(string command)
    {
        if (_hwnd != 0)
        {
            NativeMethods.UnregisterHotkey(_hwnd, IdOf(command));
        }
    }

    private static int IdOf(string command) => Array.IndexOf(Commands, command) + 1;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey && wParam is > 0 && (int)wParam <= Commands.Length)
        {
            handled = true;
            run(Commands[(int)wParam - 1]);
        }

        return 0;
    }
}
