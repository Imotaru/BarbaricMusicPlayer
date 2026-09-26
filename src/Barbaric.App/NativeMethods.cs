using System.IO;
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

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoErrorUi = 0x400;
    private const ushort FofWantNukeWarning = 0x4000;

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

    /// <summary>
    /// Moves a file to the Recycle Bin. Where it can't be recycled (a network drive, say), Windows
    /// asks before deleting it for good.
    /// </summary>
    /// <returns>Whether the file is gone.</returns>
    public static bool SendToRecycleBin(string path, Window owner)
    {
        var operation = new ShFileOpStruct
        {
            Hwnd = new WindowInteropHelper(owner).Handle,
            Func = FoDelete,

            // A list of paths, each null-terminated, ending with an extra null.
            From = path + "\0",
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
        };
        SHFileOperation(ref operation);
        return !File.Exists(path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }
}
