namespace Barbaric.Core.Hotkeys;

/// <summary>
/// A system-wide shortcut as Windows' <c>RegisterHotKey</c> takes it, parsed from the UI's chord text
/// such as <c>Ctrl+Alt+KeyN</c> or <c>Ctrl+Shift+ArrowRight</c> (modifiers, then a DOM <c>KeyboardEvent.code</c>).
/// </summary>
public sealed record HotkeyChord(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x1;
    public const uint Control = 0x2;
    public const uint Shift = 0x4;
    public const uint Win = 0x8;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.Ordinal)
    {
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["ArrowLeft"] = 0x25,
        ["ArrowUp"] = 0x26,
        ["ArrowRight"] = 0x27,
        ["ArrowDown"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    };

    /// <summary>
    /// Reads a chord that is safe to take over for the whole system: Ctrl or Alt together with
    /// another modifier, anything with Win, or one of the spare keys F13–F24. Plain typing keys,
    /// single-modifier shortcuts apps rely on (Ctrl+C) and media keys are refused.
    /// </summary>
    public static HotkeyChord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+');
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part switch
            {
                "Ctrl" => Control,
                "Alt" => Alt,
                "Shift" => Shift,
                "Win" => Win,
                _ => 0u,
            };
            if (flag == 0 || (modifiers & flag) != 0)
            {
                return null;
            }

            modifiers |= flag;
        }

        if (KeyFor(parts[^1]) is not { } key)
        {
            return null;
        }

        var spareKey = key is >= 0x7C and <= 0x87; // F13–F24
        var strongModifiers = (modifiers & Win) != 0
            || ((modifiers & (Control | Alt)) != 0 && System.Numerics.BitOperations.PopCount(modifiers) >= 2);
        return spareKey || strongModifiers ? new HotkeyChord(modifiers, key) : null;
    }

    private static uint? KeyFor(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
        {
            return code[3];
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
        {
            return code[5];
        }

        if (code.Length == 7 && code.StartsWith("Numpad", StringComparison.Ordinal) && code[6] is >= '0' and <= '9')
        {
            return 0x60u + (uint)(code[6] - '0');
        }

        if (code.StartsWith('F') && int.TryParse(code.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            return 0x70u + (uint)(n - 1);
        }

        return NamedKeys.TryGetValue(code, out var key) ? key : null;
    }
}
