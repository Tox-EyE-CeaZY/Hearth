using System.Runtime.InteropServices;
using System.Windows.Interop;
using Hearth.App.Views;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Hosting;

/// <summary>
/// Owns the window Hearth's UI lives in: a top-level window pinned to the very
/// bottom of the Z-order, directly above the shell's own desktop.
///
/// It is deliberately NOT a child of Explorer's desktop window, which is the
/// approach every wallpaper-app guide describes. Verified on Windows 11 build
/// 26200 with a plain GDI window, no WPF involved:
///
///   child of Progman   input works, pixels never appear
///   child of WorkerW   no input (WorkerW is WS_DISABLED), no pixels
///   top-level, bottom  input works, pixels appear
///
/// Explorer now draws the desktop through its own compositor, and Progman does
/// not composite foreign child windows at all. That produced the most
/// misleading failure in this project — tiles you could click and launch that
/// were never drawn — and no amount of WPF-side fixing could have helped.
/// </summary>
public sealed class DesktopHost : IDisposable
{
    private readonly DesktopLayer _layer = new();
    private HwndSource? _source;
    private DesktopSurface? _surface;
    private uint _taskbarCreatedMessage;
    private bool _disposed;

    public DesktopSurface? Surface => _surface;

    /// <summary>The live host, for the few UI paths that need window-level control.</summary>
    public static DesktopHost? Current { get; private set; }

    private WinEventDelegate? _foregroundCallback;
    private IntPtr _foregroundHook;
    private bool _raisedOverDesktop;
    private bool _keyboardEnabled;

    public bool Start()
    {
        var (x, y, width, height) = VirtualScreen();

        var parameters = new HwndSourceParameters("Hearth")
        {
            // Created hidden: see MarkNotFullScreen, which has to run before
            // the window is first shown.
            WindowStyle = unchecked((int)(WS_POPUP | WS_CLIPCHILDREN)),

            // TOOLWINDOW keeps it out of Alt+Tab and the taskbar. NOACTIVATE is
            // what keeps it at the bottom: an activated window is raised, and a
            // click on the desktop must never bring Hearth above your apps.
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,

            PositionX = x,
            PositionY = y,
            Width = width,
            Height = height,

            // Per-pixel opacity would force the layered-window path and drop
            // hardware rendering; Hearth paints the wallpaper itself instead.
            UsesPerPixelOpacity = false,
        };

        _source = new HwndSource(parameters);
        MarkNotFullScreen(_source.Handle);
        ShowWindow(_source.Handle, SW_SHOWNA);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _source.AddHook(WndProc);

        _surface = new DesktopSurface(_layer);
        _source.RootVisual = _surface;

        SendToBottom();

        // Kept in a field: the native hook holds only a function pointer, and
        // a collected delegate would crash the process on the next event.
        _foregroundCallback = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT);

        Current = this;
        Log.Write($"host: top-level hwnd=0x{_source.Handle:X} at ({x},{y}) size {width}x{height}");
        return true;
    }

    /// <summary>
    /// Tells Explorer this is not a full-screen app.
    ///
    /// A borderless window covering a whole display looks exactly like a game
    /// to the shell. With the desktop in front, SHQueryUserNotificationState
    /// reported QUNS_BUSY (a full-screen app is running), which suppresses
    /// notifications and stops an auto-hidden taskbar from coming up on hover —
    /// the user had to press Win to reach it. The "NonRudeHWND" property is the
    /// shell's own opt-out. Explorer reads it when the window is shown, so it
    /// must be set before that: measured, setting it on a visible window
    /// changes nothing until the window is hidden and shown again.
    /// </summary>
    private static void MarkNotFullScreen(IntPtr hwnd)
    {
        if (!SetProp(hwnd, "NonRudeHWND", new IntPtr(1)))
            Log.Write("host: could not set NonRudeHWND; an auto-hidden taskbar may not appear on hover");
    }

    private static (int X, int Y, int Width, int Height) VirtualScreen() => (
        Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    private void SendToBottom()
    {
        if (_source is null) return;
        _raisedOverDesktop = false;
        Win32.SetWindowPos(_source.Handle, Win32.HWND_BOTTOM, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    // ---- Show desktop (Win+D) -------------------------------------------

    /// <summary>
    /// Follows the shell's desktop when it is brought forward.
    ///
    /// Win+D does not minimise Hearth — measured, it stays visible and
    /// un-iconic. Windows instead raises Progman from the bottom of the stack to
    /// just under the taskbar and makes it the foreground window, which buries
    /// Hearth underneath the real desktop. So when the desktop becomes
    /// foreground, Hearth lifts itself to the top of the normal band (still
    /// below the topmost taskbar), and as soon as anything else becomes
    /// foreground it goes back to the bottom where it belongs.
    /// </summary>
    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (_source is null || hwnd == _source.Handle) return;

        // Hearth's own windows (the Start menu, the Add apps drawer) are part
        // of the desktop experience: opening one must not bury the desktop.
        GetWindowThreadProcessId(hwnd, out var owner);
        if (owner == Environment.ProcessId) return;

        var className = ClassOf(hwnd);
        var isDesktop = className is "Progman" or "WorkerW";

        if (isDesktop)
        {
            RaiseOverDesktop();

            // Explorer can finish raising itself after the event fires; one
            // more pass shortly afterwards settles the order.
            // Explorer's show-desktop animation keeps adjusting the order for a
            // short while after the event, so re-check a few times.
            var passes = 0;
            var settle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            settle.Tick += (_, _) =>
            {
                if (++passes >= 6 || !_raisedOverDesktop) settle.Stop();
                if (_raisedOverDesktop) RaiseOverDesktop();
            };
            settle.Start();
        }
        else if (_raisedOverDesktop && !_keyboardEnabled)
        {
            SendToBottom();
        }
    }

    /// <summary>
    /// Places Hearth immediately above the raised desktop.
    ///
    /// HWND_TOP does not work here: Windows accepts the call from a background
    /// process, reports success, and changes nothing. Inserting relative to a
    /// named window is not subject to that restriction — so name the window
    /// currently just above Progman, and slot in underneath it.
    /// </summary>
    private void RaiseOverDesktop()
    {
        if (_source is null) return;
        _raisedOverDesktop = true;

        var desktop = Win32.FindWindow("Progman", null);
        if (desktop == IntPtr.Zero) return;

        var above = Win32.GetWindow(desktop, Win32.GW_HWNDPREV);
        if (above == _source.Handle) return; // already in place

        var insertAfter = above == IntPtr.Zero ? Win32.HWND_TOP : above;
        Win32.SetWindowPos(_source.Handle, insertAfter, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new char[64];
        var length = Win32.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    // ---- Keyboard focus on demand ---------------------------------------

    /// <summary>
    /// Lets the window take focus, for text entry such as renaming a folder.
    /// Hearth is otherwise non-activating, which also means it never receives
    /// a keystroke.
    /// </summary>
    /// <summary>Whether <paramref name="visual"/> is shown in the desktop window (not, say, the Start menu).</summary>
    public bool Owns(System.Windows.Media.Visual visual) =>
        _source is not null && ReferenceEquals(System.Windows.PresentationSource.FromVisual(visual), _source);

    public void BeginKeyboardInput()
    {
        if (_source is null || _keyboardEnabled) return;
        _keyboardEnabled = true;

        var exStyle = (long)Win32.GetWindowLongPtr(_source.Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_source.Handle, Win32.GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_NOACTIVATE));

        // Allowed: this process has just received the click that asked for it.
        Win32.SetForegroundWindow(_source.Handle);
    }

    public void EndKeyboardInput()
    {
        if (_source is null || !_keyboardEnabled) return;
        _keyboardEnabled = false;

        var exStyle = (long)Win32.GetWindowLongPtr(_source.Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_source.Handle, Win32.GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_NOACTIVATE));

        // While typing, foreground changes were deliberately ignored. Decide
        // now: stay lifted only if the desktop (or Hearth itself) is in front.
        var foreground = Win32.GetForegroundWindow();
        var desktopInFront = foreground == _source.Handle || ClassOf(foreground) is "Progman" or "WorkerW";
        if (!(_raisedOverDesktop && desktopInFront)) SendToBottom();
    }

    /// <summary>
    /// Re-covers every display after a resolution change or a monitor being
    /// plugged in or removed.
    /// </summary>
    private void Resize()
    {
        if (_source is null) return;
        var (x, y, width, height) = VirtualScreen();
        Win32.SetWindowPos(_source.Handle, Win32.HWND_BOTTOM, x, y, width, height, Win32.SWP_NOACTIVATE);
    }

    private System.Windows.Threading.DispatcherTimer? _displayTimer;

    /// <summary>
    /// Plugging or unplugging a display arrives as a burst of changes, and the
    /// ones in the middle describe setups that never settle (a display still
    /// listed with no area, the primary not yet moved). Rebuilding only once
    /// the burst is over means Hearth lays out for the real result, once.
    /// </summary>
    private void ScheduleDisplayRebuild()
    {
        if (_displayTimer is null)
        {
            _displayTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _displayTimer.Tick += (_, _) =>
            {
                _displayTimer.Stop();
                Log.Write("display change settled; resizing surface");
                Resize();
                _surface?.Rebuild();
            };
        }

        _displayTimer.Stop();
        _displayTimer.Start();
    }

    /// <summary>The window handle, for things that need a native owner (shell menus).</summary>
    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ShellContextMenu.TryHandleMenuMessage(msg, wParam, lParam, out var menuResult))
        {
            handled = true;
            return menuResult;
        }

        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                if (!_raisedOverDesktop) PinToBottom(lParam);
                break;

            case WM_MOUSEACTIVATE:
                // Belt and braces with WS_EX_NOACTIVATE: clicking the desktop
                // must not activate, because activation raises the window.
                // The exception is text entry, which needs focus.
                if (_keyboardEnabled) break;
                handled = true;
                return new IntPtr(MA_NOACTIVATE);

            case WM_DISPLAYCHANGE:
                ScheduleDisplayRebuild();
                break;

            default:
                if (msg != 0 && (uint)msg == _taskbarCreatedMessage)
                {
                    // Explorer restarted. Our window survives (it is not
                    // Explorer's child), but the icon layer is brand new and
                    // visible again, so it has to be hidden afresh.
                    Log.Write("Explorer restarted; re-hiding shell icons");
                    _layer.ForgetHandles();
                    if (App.Settings.HideShellIcons) _layer.HideShellIcons();
                    SendToBottom();
                }
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Rewrites any Z-order change aimed at this window so it lands at the
    /// bottom instead. The shell and the window manager both try to bring
    /// windows forward at various points; this is what keeps Hearth behind
    /// every application regardless of who asked.
    /// </summary>
    private static void PinToBottom(IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & Win32.SWP_NOZORDER) != 0) return;

        pos.hwndInsertAfter = Win32.HWND_BOTTOM;
        Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        if (ReferenceEquals(Current, this)) Current = null;
        _displayTimer?.Stop();

        _surface?.DetachFromDesktop();
        _layer.Dispose();

        if (_source is not null)
        {
            RemoveProp(_source.Handle, "NonRudeHWND");
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private const long WS_POPUP = 0x80000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPCHILDREN = 0x02000000L;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int MA_NOACTIVATE = 3;

    private const int SW_SHOWNA = 8;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
}
