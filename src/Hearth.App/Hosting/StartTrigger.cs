using System.Runtime.InteropServices;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Hosting;

/// <summary>
/// Catches the two ways Windows' Start menu is opened — the Windows key on its
/// own, and a click on the Start button — and raises <see cref="Triggered"/>
/// instead.
///
/// Low-level hooks, on a thread of their own. Windows silently removes a
/// low-level hook whose callback is slow, so it must never share a thread
/// with UI work; the callbacks here only compare numbers and post.
///
/// The Windows key: Start opens when Win is released with nothing pressed in
/// between. The physical Win-up is swallowed, and an unassigned key (0xE8)
/// plus a synthetic Win-up are injected in its place — Windows then sees a
/// "Win + something" chord, opens nothing, and the key is not left stuck
/// down. Win+E, Win+D and every other chord pass through untouched.
///
/// The Start button: Windows 11 keeps a hidden legacy "Start" window inside
/// each taskbar whose rectangle tracks the visible button (measured on build
/// 26200: 454..485 x 1078..1110, the same as the XAML button's bounds), so a
/// click is matched against that without any UI Automation in the hook.
/// </summary>
internal sealed class StartTrigger : IDisposable
{
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;
    private readonly ManualResetEventSlim _ready = new();

    private bool _winDown;
    private bool _chorded;
    private bool _swallowNextUp;

    /// <summary>Raised on the hook thread; handlers must post elsewhere and return.</summary>
    public event Action<StartSource>? Triggered;

    /// <summary>
    /// Raised on the hook thread for each physical mouse press while
    /// <see cref="ReportPresses"/> is set (the menu is open), with screen
    /// coordinates. Handlers must post elsewhere and return.
    /// </summary>
    public event Action<int, int>? Pressed;

    /// <summary>Whether the Windows key and Start button open Hearth's menu.</summary>
    public volatile bool Intercept = true;

    public volatile bool ReportPresses;

    public enum StartSource { WindowsKey, StartButton }

    public StartTrigger()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Hearth.StartTrigger" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        Log.Write($"start trigger: keyboard hook {(_keyboardHook != IntPtr.Zero ? "on" : "FAILED")}, " +
                  $"mouse hook {(_mouseHook != IntPtr.Zero ? "on" : "FAILED")}");
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var injected = (info.flags & LLKHF_INJECTED) != 0;
        var message = (int)wParam;
        var isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isWin = info.vkCode is VK_LWIN or VK_RWIN;

        // Our own injected keys (and anyone else's) are never interpreted.
        if (injected || !Intercept)
        {
            _winDown = false;
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        if (isWin)
        {
            if (isDown)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _chorded = false;
                }
            }
            else
            {
                var lone = _winDown && !_chorded;
                _winDown = false;
                if (lone)
                {
                    // Replace the release with mask key + release, so Windows
                    // sees a chord and does not open its own Start.
                    SendMaskedRelease(info.vkCode);
                    Triggered?.Invoke(StartSource.WindowsKey);
                    return new IntPtr(1);
                }
            }
        }
        else if (_winDown && isDown)
        {
            _chorded = true;
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        var message = (int)wParam;
        if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL)
        {
            // Win+click and Win+scroll are chords too.
            if (_winDown) _chorded = true;
        }

        if (message is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                var overStart = message == WM_LBUTTONDOWN && Intercept && IsOverStartButton(info.pt);

                // A press on the Start button toggles the menu itself, so it is
                // not reported as an outside click too.
                if (ReportPresses && !overStart && message != WM_LBUTTONUP) Pressed?.Invoke(info.pt.X, info.pt.Y);

                if (overStart)
                {
                    _swallowNextUp = true;
                    Triggered?.Invoke(StartSource.StartButton);
                    return new IntPtr(1);
                }

                if (message == WM_LBUTTONUP && _swallowNextUp)
                {
                    _swallowNextUp = false;
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Checks every taskbar's Start button. Cheap: a few window lookups.</summary>
    private static bool IsOverStartButton(POINT pt)
    {
        return Hit(FindWindow("Shell_TrayWnd", null), pt) || AnySecondaryHit(pt);

        static bool AnySecondaryHit(POINT pt)
        {
            for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
                 bar != IntPtr.Zero;
                 bar = FindWindowEx(IntPtr.Zero, bar, "Shell_SecondaryTrayWnd", null))
            {
                if (Hit(bar, pt)) return true;
            }
            return false;
        }

        static bool Hit(IntPtr bar, POINT pt)
        {
            if (bar == IntPtr.Zero) return false;
            var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
            if (start == IntPtr.Zero || !GetWindowRect(start, out var rect)) return false;
            if (rect.Right - rect.Left <= 0) return false;
            return pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom &&
                   WindowFromPoint(pt) is var under && IsWithin(under, bar);
        }

        // Only when the taskbar itself is what was clicked — a window dragged
        // over the Start button's position must still get its click.
        static bool IsWithin(IntPtr hwnd, IntPtr bar) =>
            hwnd == bar || GetAncestor(hwnd, GA_ROOT) == bar;
    }

    private static void SendMaskedRelease(uint winKey)
    {
        var inputs = new INPUT[]
        {
            Key(VK_MASK, 0),
            Key(VK_MASK, KEYEVENTF_KEYUP),
            Key((ushort)winKey, KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort vk, uint flags) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
        };
    }

    /// <summary>
    /// Opens Windows' own Start menu, for the "Windows Start" button in
    /// Hearth's menu. Ctrl+Esc is not intercepted, so it reaches the shell.
    /// </summary>
    public static void OpenWindowsStart()
    {
        var inputs = new INPUT[]
        {
            Key(VK_CONTROL, 0), Key(VK_ESCAPE, 0),
            Key(VK_ESCAPE, KEYEVENTF_KEYUP), Key(VK_CONTROL, KEYEVENTF_KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort vk, uint flags) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } },
        };
    }

    /// <summary>
    /// Injects a no-op key press. Windows lets the process that produced the
    /// last input take the foreground, which the menu needs in order to have
    /// keyboard focus; a hook alone does not count.
    /// </summary>
    public static void ClaimForegroundRight()
    {
        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MASK } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    // ---- Native ---------------------------------------------------------

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_MASK = 0xE8; // unassigned
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Sized for the largest member (MOUSEINPUT) so SendInput accepts cbSize.
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
