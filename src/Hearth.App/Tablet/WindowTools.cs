using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>Questions about other programs' windows, and the few things tablet mode does to them.</summary>
internal static class WindowTools
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
        "Shell_InputSwitchTopLevelWindow", "Windows.Internal.Shell.TabProxyWindow",
    };

    /// <summary>Roughly Alt+Tab's rules: visible, unowned, not a tool window, not cloaked, titled.</summary>
    public static bool IsSwitchable(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsOwnProcess(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;

        var ex = ExStyle(hwnd);
        var appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return false;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        }
        if (IsCloaked(hwnd)) return false;
        if (ShellClasses.Contains(ClassName(hwnd))) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        return GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top;
    }

    /// <summary>Switchable windows, most recently used first (EnumWindows returns Z-order).</summary>
    public static List<IntPtr> SwitchableWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsSwitchable(hwnd)) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>A real, resizable app window that makes sense maximized.</summary>
    public static bool CanMaximize(IntPtr hwnd)
    {
        if (!IsSwitchable(hwnd)) return false;
        var style = Style(hwnd);
        if ((style & WS_CHILD) != 0) return false;
        if ((style & WS_CAPTION) != WS_CAPTION) return false;
        if ((style & WS_THICKFRAME) == 0 || (style & WS_MAXIMIZEBOX) == 0) return false;
        if ((style & (WS_MAXIMIZE | WS_MINIMIZE)) != 0) return false;
        if ((ExStyle(hwnd) & WS_EX_TOPMOST) != 0) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        return ClassName(hwnd) != "#32770"; // dialogs
    }

    public static bool IsMaximized(IntPtr hwnd) => (Style(hwnd) & WS_MAXIMIZE) != 0;
    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    public static bool IsOwnProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[128];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static string Title(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>"chrome.exe"; for a Store app, the program behind the frame window when it can be found.</summary>
    public static string ProgramName(IntPtr hwnd)
    {
        var target = hwnd;
        if (ClassName(hwnd) == "ApplicationFrameWindow")
        {
            // The frame belongs to ApplicationFrameHost; the app is a child window from another process.
            GetWindowThreadProcessId(hwnd, out var framePid);
            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid == framePid) return true;
                target = child;
                return false;
            }, IntPtr.Zero);
        }
        var path = ProcessPath(target);
        return path.Length > 0 ? Path.GetFileName(path) : string.Empty;
    }

    public static string ProcessPath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return ProcessPathOf(pid);
    }

    /// <summary>The program file of a process, or "" when Windows won't say (protected processes).</summary>
    public static string ProcessPathOf(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return string.Empty;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : string.Empty;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The window's icon, or null. Asked with a timeout: a hung app must not hang Hearth.</summary>
    public static ImageSource? Icon(IntPtr hwnd)
    {
        var handle = IntPtr.Zero;
        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2 })
        {
            if (SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(kind), IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out handle) != IntPtr.Zero &&
                handle != IntPtr.Zero) break;
            handle = IntPtr.Zero;
        }
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hwnd, GCLP_HICON);
        if (handle == IntPtr.Zero) handle = GetClassLongPtr(hwnd, GCLP_HICONSM);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Brings a window forward, restoring it first if it is minimized.</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        Hosting.StartTrigger.ClaimForegroundRight();
        if (!SetForegroundWindow(hwnd))
        {
            // Background processes may not take the foreground; switching to
            // it is still allowed.
            SwitchToThisWindow(hwnd, true);
        }
    }

    /// <summary>Asks a window to close, as its close button would. False when Windows refuses (an elevated app).</summary>
    public static bool Close(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);

    public static bool Maximize(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);

    public static bool Restore(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);

    // ---- Keys -------------------------------------------------------------------

    /// <summary>Sends the Back key chosen for the program in front.</summary>
    public static void SendBack(TabletSettings settings)
    {
        var foreground = GetForegroundWindow();
        var program = foreground != IntPtr.Zero ? ProgramName(foreground) : string.Empty;
        var key = settings.BackKeys.TryGetValue(program, out var chosen) ? chosen : settings.DefaultBackKey;
        Hearth.Core.Diagnostics.Log.Write($"tablet: back ({key}) to '{program}'");
        switch (key)
        {
            case BackKey.AltLeft: SendChord(VK_MENU, VK_LEFT); break;
            case BackKey.BrowserBack: SendChord(VK_BROWSER_BACK); break;
            case BackKey.Escape: SendChord(VK_ESCAPE); break;
            case BackKey.Backspace: SendChord(VK_BACK); break;
        }
    }

    /// <summary>Win plus a key: Win+D (desktop), Win+A (quick settings), Win+N (notifications).</summary>
    public static void SendWinChord(char letter) => SendChord(VK_LWIN, (ushort)char.ToUpperInvariant(letter));

    /// <summary>Presses the keys in order and releases them in reverse. The Start hook ignores injected keys.</summary>
    public static void SendChord(params ushort[] keys)
    {
        var inputs = new List<INPUT>();
        foreach (var key in keys) inputs.Add(Key(key, 0));
        foreach (var key in keys.Reverse()) inputs.Add(Key(key, KEYEVENTF_KEYUP));
        var array = inputs.ToArray();
        SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort vk, uint flags)
        {
            // Arrow keys are "extended"; without the flag Alt+Left reads as numpad 4.
            if (vk is VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
        }
    }

    /// <summary>A left click at a screen point, then the pointer goes back where it was.</summary>
    public static void ClickAt(int x, int y)
    {
        GetCursorPos(out var old);
        SetCursorPos(x, y);
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } },
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        SetCursorPos(old.X, old.Y);
    }

    public const ushort VK_BACK = 0x08;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_UP = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_BROWSER_BACK = 0xA6;

    // ---- Native -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_MAXIMIZE = 0x01000000L;
    private const long WS_MINIMIZE = 0x20000000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const int DWMWA_CLOAKED = 14;
    private const int WM_GETICON = 0x007F;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_RESTORE = 0xF120;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static long Style(IntPtr hwnd) => (long)GetWindowLongPtr(hwnd, GWL_STYLE);
    private static long ExStyle(IntPtr hwnd) => (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, char[] buffer, int length);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
