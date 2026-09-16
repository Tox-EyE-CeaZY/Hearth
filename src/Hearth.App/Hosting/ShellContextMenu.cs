using System.IO;
using System.Runtime.InteropServices;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Hosting;

/// <summary>
/// The real Windows context menu — the one Explorer shows — for a file, an
/// installed app, or the desktop background.
///
/// Hearth's own menus cover the common cases; this is the escape hatch that
/// keeps every Windows feature reachable: Open with, Send to, Cut, Copy,
/// Paste, New, Delete, Uninstall, Display settings, Personalize, and whatever
/// shell extensions (7-Zip, Git, Dropbox, graphics drivers) have added.
///
/// Mechanics: IContextMenu fills a native HMENU, TrackPopupMenuEx shows it,
/// and the chosen command is handed back to the shell. Submenus such as "Open
/// with" and "Send to" populate lazily and owner-draw their icons, which is
/// why their window messages must be forwarded to IContextMenu2/3 while the
/// menu is up (see <see cref="TryHandleMenuMessage"/>).
/// </summary>
internal static class ShellContextMenu
{
    private static IContextMenu2? _active2;
    private static IContextMenu3? _active3;

    /// <summary>Shows the shell menu for a parsing name (a path, or shell:AppsFolder\AUMID).</summary>
    public static void ShowForItem(IntPtr owner, string parsingName, int screenX, int screenY)
    {
        object? item = null;
        IntPtr menuPtr = IntPtr.Zero;
        try
        {
            ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ShellNative.IID_IShellItem, out item);
            if (item is not IShellItem shellItem) return;

            shellItem.BindToHandler(IntPtr.Zero, BHID_SFUIObject, IID_IContextMenu, out menuPtr);
            if (menuPtr == IntPtr.Zero) return;

            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            Track(owner, menu, screenX, screenY, CMF_NORMAL | ExtendedFlag());
            Marshal.FinalReleaseComObject(menu);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or FileNotFoundException)
        {
            Log.Write($"shell menu for '{parsingName}' failed: {ex.Message}");
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }

    /// <summary>The desktop background's menu: New, Paste, Display settings, and so on.</summary>
    public static void ShowForDesktopBackground(IntPtr owner, int screenX, int screenY)
    {
        IShellFolder? desktop = null;
        IntPtr menuPtr = IntPtr.Zero;
        try
        {
            if (SHGetDesktopFolder(out desktop) != 0 || desktop is null) return;

            var iid = IID_IContextMenu;
            if (desktop.CreateViewObject(owner, ref iid, out menuPtr) != 0 || menuPtr == IntPtr.Zero) return;

            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
            Track(owner, menu, screenX, screenY, CMF_NORMAL | ExtendedFlag());
            Marshal.FinalReleaseComObject(menu);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Write($"desktop background menu failed: {ex.Message}");
        }
        finally
        {
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (desktop is not null) Marshal.FinalReleaseComObject(desktop);
        }
    }

    /// <summary>
    /// Forwards the messages lazy submenus depend on. Called from the owner
    /// window's WndProc; returns true when the shell handled the message.
    /// </summary>
    public static bool TryHandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return false;

        try
        {
            if (_active3 is not null)
                return _active3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (_active2 is not null && msg != WM_MENUCHAR)
                return _active2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch (COMException)
        {
            // An extension misbehaving inside its own submenu must not take
            // the desktop down.
        }
        return false;
    }

    private static void Track(IntPtr owner, IContextMenu menu, int x, int y, uint flags)
    {
        var hmenu = CreatePopupMenu();
        if (hmenu == IntPtr.Zero) return;

        try
        {
            if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0) return;

            _active3 = menu as IContextMenu3;
            _active2 = _active3 is null ? menu as IContextMenu2 : null;

            // A tracked menu only closes on an outside click if its owner is
            // the foreground window. Hearth never activates on its own, so it
            // takes foreground for the length of the menu (permitted: the user
            // has just clicked it) and hands it back afterwards.
            DesktopHost.Current?.BeginKeyboardInput();
            var command = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner, IntPtr.Zero);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DesktopHost.Current?.EndKeyboardInput();

            if (command < FirstCommand) return;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | CMIC_MASK_ASYNCOK,
                hwnd = owner,
                lpVerb = (IntPtr)(command - FirstCommand),
                lpVerbW = (IntPtr)(command - FirstCommand),
                nShow = SW_SHOWNORMAL,
                ptInvoke = new Win32.POINT(x, y),
            };

            var hr = menu.InvokeCommand(ref info);
            if (hr < 0) Log.Write($"shell command {command - FirstCommand} failed: 0x{hr:X8}");
        }
        finally
        {
            _active2 = null;
            _active3 = null;
            DestroyMenu(hmenu);
        }
    }

    /// <summary>Shift held adds the extended verbs, exactly as in Explorer.</summary>
    private static uint ExtendedFlag() =>
        (GetKeyState(VK_SHIFT) & 0x8000) != 0 ? CMF_EXTENDEDVERBS : 0;

    // ---- Interop ----------------------------------------------------------

    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const uint CMIC_MASK_ASYNCOK = 0x00100000;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int SW_SHOWNORMAL = 1;
    private const int VK_SHIFT = 0x10;

    private const int WM_NULL = 0x0000;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_MENUCHAR = 0x0120;

    private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public Win32.POINT ptInvoke;
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    /// <summary>Only the methods up to CreateViewObject are declared; vtable order is what matters.</summary>
    [ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, IntPtr pszDisplayName, IntPtr pchEaten, IntPtr ppidl, IntPtr pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, IntPtr riid, IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, IntPtr riid, IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
