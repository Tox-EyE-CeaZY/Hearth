using System.Runtime.InteropServices;

namespace Hearth.Core.Interop;

// ---- Enums ------------------------------------------------------------

/// <summary>Which flavour of a shell item's name to retrieve.</summary>
internal enum SIGDN : uint
{
    NormalDisplay = 0x00000000,
    /// <summary>For AppsFolder children this is the AppUserModelID.</summary>
    ParentRelativeParsing = 0x80018001,
    DesktopAbsoluteParsing = 0x80028000,
    ParentRelativeEditing = 0x80031001,
    DesktopAbsoluteEditing = 0x8004c000,
    FileSysPath = 0x80058000,
    Url = 0x80068000,
}

[Flags]
internal enum SIIGBF
{
    ResizeToFit = 0x00,
    /// <summary>Return a larger source image rather than upscaling a small one.</summary>
    BiggerSizeOk = 0x01,
    MemoryOnly = 0x02,
    /// <summary>Never substitute a document thumbnail for the app icon.</summary>
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly = 0x10,
    ScaleUp = 0x100,
}

// ---- Interfaces -------------------------------------------------------

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("70629033-e363-4a28-a567-0db78006e6d7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IShellItem[] rgelt, out uint pceltFetched);
    [PreserveSig]
    int Skip(uint celt);
    [PreserveSig]
    int Reset();
    void Clone(out IEnumShellItems ppenum);
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    /// <summary>
    /// Returns a 32bpp HBITMAP with *premultiplied* alpha. Caller owns it and
    /// must DeleteObject it.
    /// </summary>
    [PreserveSig]
    int GetImage(Win32.SIZE size, SIIGBF flags, out IntPtr phbm);
}

/// <summary>
/// The supported way to read the user's wallpaper. Handles per-monitor
/// wallpapers and slideshow state, which SPI_GETDESKWALLPAPER does not.
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
                      [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
                      [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
    void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
    void GetMonitorDevicePathCount(out uint count);
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Win32.RECT displayRect);
    void SetBackgroundColor(uint color);
    void GetBackgroundColor(out uint color);
    void SetPosition(DesktopWallpaperPosition position);
    void GetPosition(out DesktopWallpaperPosition position);
    // Remaining slideshow methods intentionally omitted; vtable order is what
    // matters and nothing below GetPosition is called.
}

public enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

[ComImport]
[Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperClass { }

// ---- Free functions ---------------------------------------------------

internal static partial class ShellNative
{
    /// <summary>BHID_EnumItems — binds a container item to an IEnumShellItems.</summary>
    internal static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    internal static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    internal static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    internal static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    internal const uint SEE_MASK_ASYNCOK = 0x00100000;

    /// <summary>Reads and frees a CoTaskMem string returned by GetDisplayName.</summary>
    internal static string TakeCoTaskMemString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(ptr) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }
}
