using System.Runtime.InteropServices;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>
/// Opens app windows maximized in tablet mode, as Windows 10 did, and
/// optionally puts them back when tablet mode ends.
///
/// Each window is maximized at most once per tablet session, so un-maximizing
/// one on purpose sticks. Maximizing goes through WM_SYSCOMMAND, exactly as
/// the title bar button does, which apps already handle; Windows refuses the
/// message for elevated apps, and those are left alone (the helper will
/// handle them; see the plan, section 2).
/// </summary>
internal sealed class WindowManager : IDisposable
{
    private readonly Func<TabletSettings> _settings;
    private readonly HashSet<IntPtr> _seen = [];
    private readonly HashSet<IntPtr> _maximized = [];
    private readonly Queue<(IntPtr Hwnd, DateTime Due)> _pending = new();
    private readonly DispatcherTimer _timer;
    private WinEventDelegate? _callback;
    private IntPtr _hook;
    private IntPtr _foregroundHook;

    public WindowManager(Func<TabletSettings> settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Drain();
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning) return;
        _callback = OnWinEvent;
        // Out of context: the callback runs on this (UI) thread's message loop.
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // A second chance for windows that were not app-like when shown (no title yet).
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        var existing = 0;
        if (_settings().MaximizeExisting)
        {
            foreach (var hwnd in WindowTools.SwitchableWindows())
            {
                if (WindowTools.IsMinimized(hwnd)) continue;
                if (TryMaximize(hwnd)) existing++;
            }
        }
        foreach (var hwnd in WindowTools.SwitchableWindows()) _seen.Add(hwnd);
        Log.Write($"tablet: auto-maximize on ({existing} open windows maximized)");
    }

    public void Stop(bool restoreSizes)
    {
        if (!IsRunning && _maximized.Count == 0) return;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        _timer.Stop();
        _pending.Clear();

        var restored = 0;
        if (restoreSizes)
        {
            foreach (var hwnd in _maximized)
            {
                if (!IsWindow(hwnd) || !WindowTools.IsMaximized(hwnd)) continue;
                if (WindowTools.Restore(hwnd)) restored++;
            }
        }
        _maximized.Clear();
        _seen.Clear();
        Log.Write($"tablet: auto-maximize off ({restored} windows restored)");
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (_seen.Contains(hwnd)) return;

        // Many apps restore their saved placement just after showing; acting
        // a moment later means Hearth has the last word.
        _pending.Enqueue((hwnd, DateTime.UtcNow.AddMilliseconds(120)));
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Drain()
    {
        var now = DateTime.UtcNow;
        while (_pending.Count > 0 && _pending.Peek().Due <= now)
        {
            var (hwnd, _) = _pending.Dequeue();
            if (_seen.Contains(hwnd) || !IsWindow(hwnd)) continue;
            if (!WindowTools.IsSwitchable(hwnd)) continue; // not an app window (yet); a later show may qualify
            _seen.Add(hwnd);
            TryMaximize(hwnd);
        }
        if (_pending.Count == 0) _timer.Stop();
    }

    private bool TryMaximize(IntPtr hwnd)
    {
        if (!WindowTools.CanMaximize(hwnd)) return false;

        var program = WindowTools.ProgramName(hwnd);
        if (_settings().MaximizeExceptions.Any(e => string.Equals(e, program, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!WindowTools.Maximize(hwnd))
        {
            Log.Write($"tablet: could not maximize {program} (elevated?)");
            return false;
        }
        _maximized.Add(hwnd);
        return true;
    }

    public void Dispose() => Stop(restoreSizes: false);

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}
