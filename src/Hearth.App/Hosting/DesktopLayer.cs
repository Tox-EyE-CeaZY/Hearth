using System.Diagnostics;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Hosting;

/// <summary>
/// Finds the shell's desktop window and controls its icon layer.
///
/// Hearth sits inside the window that hosts SHELLDLL_DefView and hides that
/// view, so the real icons are replaced rather than covered up. Nothing here
/// writes to the registry or changes the shell: hiding is a window-visibility
/// toggle, the files on the desktop are untouched, and Explorer keeps running
/// throughout. Creating the child window itself is <see cref="DesktopHost"/>'s
/// job.
/// </summary>
public sealed class DesktopLayer : IDisposable
{
    private IntPtr _defView;
    private IntPtr _wallpaperWorkerW;
    private bool _iconsHidden;
    private bool _wallpaperHidden;
    private bool _disposed;

    public DesktopLayer()
    {
        // Last line of defence. If Hearth goes down holding the icons hidden,
        // the user is staring at an empty desktop with no obvious way back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreAll();
    }

    /// <summary>
    /// Locates the window Hearth should live inside: the one that currently
    /// hosts SHELLDLL_DefView.
    ///
    /// This is deliberately NOT the wallpaper WorkerW, which is the obvious
    /// target and the one every wallpaper-app guide names. On Windows 11 that
    /// WorkerW carries <c>WS_DISABLED</c> — the shell marks the wallpaper layer
    /// inert — and a child of a disabled window receives no mouse input at all.
    /// Fine for animated wallpaper; fatal for a home screen you click.
    ///
    /// Hearth replaces the *icon* layer, not the wallpaper, so it belongs
    /// exactly where the icon layer lives. Finding the DefView host by search
    /// rather than assuming Progman also keeps older arrangements working,
    /// where DefView can migrate into a WorkerW of its own.
    /// </summary>
    public static IntPtr FindDesktopHost()
    {
        var host = IntPtr.Zero;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;
            host = hwnd;
            return false;
        }, IntPtr.Zero);

        if (host != IntPtr.Zero && Win32.IsWindowEnabled(host)) return host;

        if (host != IntPtr.Zero)
            Log.Write($"DefView host 0x{host:X} is disabled; falling back to Progman");

        var progman = Win32.FindWindow("Progman", null);
        return progman != IntPtr.Zero && Win32.IsWindowEnabled(progman) ? progman : host;
    }

    /// <summary>Drops cached handles after an Explorer restart invalidates them.</summary>
    public void ForgetHandles()
    {
        _defView = IntPtr.Zero;
        _wallpaperWorkerW = IntPtr.Zero;
        _iconsHidden = false;
        _wallpaperHidden = false;
    }

    /// <summary>
    /// Hides the shell's wallpaper window.
    ///
    /// This is not cosmetic tidying — without it Hearth flickers into view and
    /// is erased again. That WorkerW is a *sibling* of our window inside the
    /// desktop host, and it does not carry <c>WS_CLIPSIBLINGS</c>: a window
    /// without that style paints straight over any sibling it overlaps. So
    /// every time the shell repaints the wallpaper it wipes whatever we drew,
    /// and the desktop goes blank a moment after it appears.
    ///
    /// Hearth renders the wallpaper itself (see WallpaperService), so the
    /// shell's copy is redundant anyway. Hiding it costs nothing visually,
    /// removes a full-screen double-paint, and is reversed on exit exactly like
    /// the icon layer.
    /// </summary>
    public void HideShellWallpaper(IntPtr host)
    {
        if (_wallpaperHidden) return;

        if (_wallpaperWorkerW == IntPtr.Zero)
            _wallpaperWorkerW = Win32.FindWindowEx(host, IntPtr.Zero, "WorkerW", null);

        if (_wallpaperWorkerW == IntPtr.Zero)
        {
            Log.Write("no wallpaper WorkerW under the host; nothing to hide");
            return;
        }

        Win32.ShowWindow(_wallpaperWorkerW, Win32.SW_HIDE);
        _wallpaperHidden = true;
        Log.Write($"shell wallpaper hidden (WorkerW 0x{_wallpaperWorkerW:X})");
    }

    /// <summary>
    /// Re-hides the wallpaper WorkerW if the shell has created a new one.
    ///
    /// It does not exist from boot. Progman paints the wallpaper itself until
    /// something sends it 0x052C — and Windows does that on its own whenever it
    /// animates a wallpaper change or a slideshow step. The new WorkerW lacks
    /// WS_CLIPSIBLINGS and wipes Hearth the moment it paints, so a hide done
    /// once at startup is not enough. Cheap enough to poll: one FindWindowEx.
    /// </summary>
    public void EnforceShellWallpaperHidden(IntPtr host)
    {
        var worker = Win32.FindWindowEx(host, IntPtr.Zero, "WorkerW", null);
        if (worker == IntPtr.Zero || !Win32.IsWindowVisible(worker)) return;

        _wallpaperWorkerW = worker;
        Win32.ShowWindow(worker, Win32.SW_HIDE);
        _wallpaperHidden = true;
        Log.Write($"shell wallpaper WorkerW appeared (0x{worker:X}); hidden");
    }

    public void RestoreShellWallpaper()
    {
        if (!_wallpaperHidden) return;
        _wallpaperHidden = false;

        if (_wallpaperWorkerW == IntPtr.Zero || !Win32.IsWindow(_wallpaperWorkerW)) return;
        Win32.ShowWindow(_wallpaperWorkerW, Win32.SW_SHOWNA);
        Debug.WriteLine("[Hearth] shell wallpaper restored");
    }

    /// <summary>
    /// Hides Explorer's icon view. This only toggles window visibility — no
    /// files move, no registry keys change, and Explorer rebuilds the view
    /// untouched the moment we show it again.
    /// </summary>
    public void HideShellIcons()
    {
        if (_iconsHidden) return;
        if (_defView == IntPtr.Zero) _defView = FindDefView();
        if (_defView == IntPtr.Zero) return;

        Win32.ShowWindow(_defView, Win32.SW_HIDE);
        _iconsHidden = true;
        Log.Write($"shell icons hidden (DefView 0x{_defView:X})");
    }

    public void RestoreShellIcons()
    {
        if (!_iconsHidden) return;
        _iconsHidden = false;

        if (_defView == IntPtr.Zero || !Win32.IsWindow(_defView)) _defView = FindDefView();
        if (_defView == IntPtr.Zero) return;

        // SW_SHOWNA: put it back without stealing focus from whatever the user
        // is doing as we shut down.
        Win32.ShowWindow(_defView, Win32.SW_SHOWNA);
        Debug.WriteLine("[Hearth] shell icons restored");
    }

    private static IntPtr FindDefView()
    {
        var result = IntPtr.Zero;

        Win32.EnumWindows((hwnd, _) =>
        {
            var defView = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            result = defView;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Puts every shell layer Hearth hid back the way it was.</summary>
    public void RestoreAll()
    {
        RestoreShellWallpaper();
        RestoreShellIcons();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreAll();
    }
}
