using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Barbaric.App;

internal static class NativeMethods
{
    public const int SwHide = 0;
    public const int SwShowNormal = 1;
    public const int SwShowMinimized = 2;
    public const int SwShowMaximized = 3;
    public const int SwShowNoActivate = 4;
    public const int WpfRestoreToMaximized = 2;
    public const int WmHotkey = 0x0312;
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const nint HwndTopmost = -1;
    private const nint HwndNoTopmost = -2;

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

    /// <summary>
    /// Opens a folder in File Explorer with the given files in it selected, or brings an Explorer
    /// window already showing that folder forward.
    /// </summary>
    public static void RevealInExplorer(string folder, IReadOnlyList<string> files)
    {
        var folderPidl = ParseDisplayName(folder);
        var filePidls = new List<nint>(files.Count);
        try
        {
            if (folderPidl == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                var pidl = ParseDisplayName(file);
                if (pidl != 0)
                {
                    filePidls.Add(pidl);
                }
            }

            // Explorer takes full item IDs here as well as ones relative to the folder.
            SHOpenFolderAndSelectItems(folderPidl, (uint)filePidls.Count, [.. filePidls], 0);
        }
        finally
        {
            filePidls.ForEach(CoTaskMemFree);
            CoTaskMemFree(folderPidl);
        }
    }

    private static nint ParseDisplayName(string path) =>
        SHParseDisplayName(path, 0, out var pidl, 0, out _) == 0 ? pidl : 0;

    public static WindowPlacement? GetPlacement(Window window)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        return GetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement) ? placement : null;
    }

    public static void SetPlacement(Window window, WindowPlacement placement)
    {
        placement.Length = Marshal.SizeOf<WindowPlacement>();
        placement.Flags = 0;
        SetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement);
    }

    public static NativeRect GetBounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return rect;
    }

    /// <summary>Moves and sizes the window in device pixels, leaving its z-order and activation alone.</summary>
    public static void SetBounds(Window window, NativeRect rect) =>
        SetWindowPos(new WindowInteropHelper(window).Handle, 0, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate);

    /// <summary>The work area (screen minus taskbar) and DPI of the monitor the window is on.</summary>
    public static (NativeRect WorkArea, uint Dpi) CurrentMonitor(Window window) =>
        MonitorDetails(MonitorFromWindow(new WindowInteropHelper(window).Handle, MonitorDefaultToNearest));

    /// <summary>The monitor under a point, or null when the point is off every screen.</summary>
    public static (NativeRect WorkArea, uint Dpi)? MonitorAt(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint(x, y), MonitorDefaultToNull);
        return monitor == 0 ? null : MonitorDetails(monitor);
    }

    public static bool RegisterHotkey(nint hwnd, int id, uint modifiers, uint key, out int error)
    {
        var ok = RegisterHotKey(hwnd, id, modifiers, key);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    public static void UnregisterHotkey(nint hwnd, int id) => UnregisterHotKey(hwnd, id);

    /// <summary>
    /// Puts the window in the always-on-top band. HWND_TOPMOST alone is silently ignored for a window
    /// that was started in the background and never activated; stepping out of the band first works.
    /// </summary>
    public static void KeepOnTop(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static (NativeRect WorkArea, uint Dpi) MonitorDetails(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? (info.Work, dpi) : (info.Work, 96u);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folderPidl, uint count, nint[] itemPidls, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(nint memory);

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

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint(int x, int y)
{
    public int X = x;
    public int Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect(int left, int top, int right, int bottom)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowPlacement
{
    public int Length;
    public int Flags;
    public int ShowCmd;
    public NativePoint MinPosition;
    public NativePoint MaxPosition;
    public NativeRect NormalPosition;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;
}
