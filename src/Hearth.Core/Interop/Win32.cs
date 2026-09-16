using System.Runtime.InteropServices;

namespace Hearth.Core.Interop;

/// <summary>
/// Raw user32/gdi32/shell32 surface. Nothing here makes decisions; it is a
/// transcription of the Win32 API so the rest of Core can stay readable.
/// </summary>
internal static partial class Win32
{
    // ---- Window discovery / manipulation -------------------------------

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    // DllImport, not LibraryImport: the source generator has no marshaller
    // for an [Out] char[] buffer.
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>
    /// Returns the OWNER for a WS_POPUP window and the parent only for a
    /// WS_CHILD one — so it reports 0 for a reparented top-level window and is
    /// useless for confirming an attach. Use <see cref="GetAncestor"/> instead.
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetParent(IntPtr hWnd);

    /// <summary>
    /// The reliable way to read a window's true parent, whatever its styles.
    /// </summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    internal const uint GA_PARENT = 1;
    internal const uint GA_ROOT = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWNA = 8;

    internal const uint SMTO_NORMAL = 0x0000;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// Undocumented Progman message. Asks Progman to spawn (or hand back) the
    /// WorkerW window that sits *behind* the desktop icon layer. This is the
    /// same mechanism Wallpaper Engine and Lively use.
    /// </summary>
    internal const uint WM_SPAWN_WORKERW = 0x052C;

    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowEnabled(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    internal const uint GW_HWNDPREV = 3;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    // ---- Window styles --------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const long WS_CHILD = 0x40000000L;
    internal const long WS_POPUP = unchecked((long)0x80000000L);
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_APPWINDOW = 0x00040000L;
    internal const long WS_EX_NOACTIVATE = 0x08000000L;

    // ---- Monitors / DPI -------------------------------------------------

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    internal const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    // ---- GDI (icon bitmap marshalling) ----------------------------------

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObject")]
    internal static extern int GetObjectDibSection(IntPtr hgdiobj, int cbBuffer, out DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        [Out] byte[]? lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    internal const uint DIB_RGB_COLORS = 0;
    internal const int BI_RGB = 0;

    // ---- Structs --------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx, cy;
        public SIZE(int w, int h) { cx = w; cy = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>
    /// A BITMAP plus the header it was created from. The header is the only
    /// place a DIB's row order is recorded: BITMAP.bmHeight is always positive.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0;
        public uint dsBitfields1;
        public uint dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // A 32bpp BI_RGB DIB has no palette, but GetDIBits still wants room
        // for the colour table it will not write.
        public uint bmiColors0;
        public uint bmiColors1;
        public uint bmiColors2;
    }
}
