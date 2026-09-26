using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Barbaric.App;

internal static class NativeMethods
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int SmCxFrame = 32;
    private const int SmCxPaddedBorder = 92;

    /// <summary>Asks Windows 11 to round the corners of our frameless window.</summary>
    public static void UseRoundedCorners(Window window)
    {
        var preference = DwmwcpRound;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>
    /// How far a maximized frameless window extends past the monitor edge, in DIPs,
    /// so content can be inset to stay on screen.
    /// </summary>
    public static Thickness MaximizedOverhang(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            return new Thickness(0);
        }

        var dpi = GetDpiForWindow(hwnd);
        var pixels = GetSystemMetricsForDpi(SmCxFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        return new Thickness(pixels * 96.0 / dpi);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
