using Barbaric.Core.Hotkeys;

namespace Barbaric.Core.Tests.Hotkeys;

public sealed class HotkeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+KeyN", HotkeyChord.Control | HotkeyChord.Alt, 'N')]
    [InlineData("Ctrl+Shift+ArrowRight", HotkeyChord.Control | HotkeyChord.Shift, 0x27)]
    [InlineData("Alt+Shift+Digit5", HotkeyChord.Alt | HotkeyChord.Shift, '5')]
    [InlineData("Win+Alt+Space", HotkeyChord.Win | HotkeyChord.Alt, 0x20)]
    [InlineData("Win+KeyP", HotkeyChord.Win, 'P')]
    [InlineData("Ctrl+Alt+Numpad7", HotkeyChord.Control | HotkeyChord.Alt, 0x67)]
    [InlineData("Ctrl+Alt+F5", HotkeyChord.Control | HotkeyChord.Alt, 0x74)]
    [InlineData("F13", 0u, 0x7C)]
    [InlineData("Shift+F24", HotkeyChord.Shift, 0x87)]
    public void SafeChords_Parse(string text, uint modifiers, uint key)
    {
        Assert.Equal(new HotkeyChord(modifiers, key), HotkeyChord.TryParse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KeyN")] // plain typing
    [InlineData("Ctrl+KeyC")] // one modifier: apps use these
    [InlineData("Shift+Alt")] // no key
    [InlineData("Shift+ArrowUp")] // Shift alone is text selection
    [InlineData("Ctrl+Alt+Slash")] // punctuation differs between layouts
    [InlineData("Ctrl+Alt+MediaPlayPause")] // media keys already reach us through the OS
    [InlineData("Ctrl+Ctrl+KeyN")]
    [InlineData("Hyper+Alt+KeyN")]
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("F5")] // a normal function key on its own
    public void UnsafeOrUnknownChords_AreRefused(string? text)
    {
        Assert.Null(HotkeyChord.TryParse(text));
    }
}
