using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Tablet;

/// <summary>
/// What Hearth has changed about the shell, written to disk before the change
/// is made, so a crash or a kill can still be undone: by the watchdog, by
/// the next start, or by <c>Hearth.exe --restore-shell</c> (which the quit
/// script runs after killing Hearth).
/// </summary>
internal sealed class ShellState
{
    public bool TaskbarHidden { get; set; }

    /// <summary>The taskbar's own auto-hide setting before Hearth turned it on.</summary>
    public bool TaskbarWasAutoHide { get; set; }

    public int OwnerProcessId { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "shell-state.json");

    public static ShellState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ShellState>(File.ReadAllText(FilePath)) ?? new ShellState()
                : new ShellState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ShellState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"shell state not saved: {ex.Message}");
        }
    }
}

/// <summary>
/// Hides the Windows taskbar for tablet mode, and puts it back.
///
/// Two steps, each reversible: the taskbar's own auto-hide is switched on (so
/// maximized windows get the whole screen, and Windows keeps managing the
/// work area), and the taskbar windows are hidden (so an auto-hidden bar does
/// not slide up when a finger or the pointer reaches the edge). The previous
/// auto-hide setting is recorded before anything changes.
/// </summary>
internal static class TaskbarControl
{
    private static volatile bool _hiddenByUs;
    private static volatile bool _enforcingPaused;

    /// <summary>While the tray is being read or clicked, the taskbar is allowed up.</summary>
    public static void PauseEnforcing(bool paused) => _enforcingPaused = paused;

    public static bool IsHiddenByUs
    {
        get => _hiddenByUs;
        private set => _hiddenByUs = value;
    }

    // Changing the taskbar's auto-hide waits on Explorer reflowing every
    // window (about two seconds measured), so tablet mode queues the work on
    // a background thread; the queue keeps hide and restore in order.
    private static readonly object QueueGate = new();
    private static Task _queue = Task.CompletedTask;

    private static Task Enqueue(string what, Action action)
    {
        lock (QueueGate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error($"tablet: taskbar {what}", ex); }
            }, TaskScheduler.Default);
            return _queue;
        }
    }

    /// <summary>Waits for queued taskbar work (at exit, so nothing is left half done).</summary>
    public static void Flush()
    {
        Task pending;
        lock (QueueGate) pending = _queue;
        pending.Wait(TimeSpan.FromSeconds(8));
    }

    /// <summary>Completes when the taskbar is hidden and the work area has grown to the full screen.</summary>
    public static Task HideInBackground()
    {
        IsHiddenByUs = true;
        return Enqueue("hide", Hide);
    }

    public static void RestoreInBackground()
    {
        IsHiddenByUs = false;
        Enqueue("restore", Restore);
    }

    public static void Hide()
    {
        var state = ShellState.Load();
        if (!state.TaskbarHidden)
        {
            state.TaskbarWasAutoHide = AutoHide;
            state.TaskbarHidden = true;
        }
        state.OwnerProcessId = Environment.ProcessId;
        state.Save();

        if (!AutoHide) AutoHide = true;
        foreach (var bar in Bars()) ShowWindow(bar, SW_HIDE);
        IsHiddenByUs = true;
        Log.Write($"tablet: taskbar hidden (auto-hide was {(state.TaskbarWasAutoHide ? "on" : "off")})");
    }

    /// <summary>
    /// Explorer shows its bars again on its own now and then (a notification,
    /// Win+T, a settings change); called on a timer while tablet mode is on.
    /// </summary>
    public static void EnforceHidden()
    {
        if (!IsHiddenByUs || _enforcingPaused) return;
        foreach (var bar in Bars())
        {
            if (!IsWindowVisible(bar)) continue;
            ShowWindow(bar, SW_HIDE);
        }
    }

    /// <summary>Called after Explorer restarts: its new taskbar starts visible.</summary>
    public static void Reapply()
    {
        if (!IsHiddenByUs) return;
        if (!AutoHide) AutoHide = true;
        foreach (var bar in Bars()) ShowWindow(bar, SW_HIDE);
        Log.Write("tablet: taskbar hidden again after an Explorer restart");
    }

    /// <summary>Shows the taskbar and restores its auto-hide setting. Safe to call at any time, from any process.</summary>
    public static void Restore()
    {
        if (!Task.CurrentId.HasValue) Flush();
        var state = ShellState.Load();
        foreach (var bar in Bars()) ShowWindow(bar, SW_SHOWNA);
        if (state.TaskbarHidden)
        {
            if (!state.TaskbarWasAutoHide && AutoHide) AutoHide = false;
            state.TaskbarHidden = false;
            state.Save();
            Log.Write($"tablet: taskbar restored (auto-hide {(state.TaskbarWasAutoHide ? "left on" : "turned off again")})");
        }
        IsHiddenByUs = false;
    }

    /// <summary>At start-up: a previous run that died with the taskbar hidden is undone.</summary>
    public static void RecoverFromPreviousRun()
    {
        var state = ShellState.Load();
        if (!state.TaskbarHidden) return;
        Log.Write($"tablet: previous run (pid {state.OwnerProcessId}) left the taskbar hidden; restoring");
        Restore();
    }

    public static bool AutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            var current = (long)SHAppBarMessage(ABM_GETSTATE, ref data);
            data.hWnd = FindWindow("Shell_TrayWnd", null);
            data.lParam = new IntPtr(value ? current | ABS_AUTOHIDE : current & ~ABS_AUTOHIDE);
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
    }

    public static IEnumerable<IntPtr> Bars()
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) yield return primary;
        for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
             bar != IntPtr.Zero;
             bar = FindWindowEx(IntPtr.Zero, bar, "Shell_SecondaryTrayWnd", null))
        {
            yield return bar;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const uint ABM_GETSTATE = 4;
    private const uint ABM_SETSTATE = 10;
    private const long ABS_AUTOHIDE = 1;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}

/// <summary>
/// A second, tiny Hearth process that waits for the main one to end. If it
/// ends without cleaning up (killed from Task Manager, a crash the runtime
/// could not catch), the watchdog shows the taskbar and desktop icons again.
/// It runs only while tablet mode has the taskbar hidden.
/// </summary>
internal static class Watchdog
{
    public const string Argument = "--watchdog";
    public const string RestoreArgument = "--restore-shell";

    private static string StopEventName(int pid) => $"Hearth.Watchdog.Stop.{pid}";

    private static Process? _child;

    public static void Start()
    {
        if (_child is { HasExited: false }) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            _child = Process.Start(new ProcessStartInfo(exe, $"{Argument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log.Write($"tablet: watchdog started (pid {_child?.Id})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"tablet: watchdog not started: {ex.Message}");
        }
    }

    /// <summary>Stops the watchdog once <paramref name="wait"/> returns (the restore it guards has finished), off the UI thread.</summary>
    public static void StopAfter(Action wait)
    {
        if (_child is null) return;
        _ = Task.Run(() =>
        {
            wait();
            Stop();
        });
    }

    public static void Stop()
    {
        if (_child is null) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName(Environment.ProcessId), out var stop))
            {
                using (stop) stop.Set();
            }
            if (!_child.WaitForExit(2000)) _child.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine(ex);
        }
        _child.Dispose();
        _child = null;
    }

    /// <summary>The watchdog process itself. Returns when the parent ends or asks it to stop.</summary>
    public static void Run(int parentId)
    {
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(parentId));
        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { return; }

        using (parent)
        using (var exited = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(parent.Handle, ownsHandle: false) })
        {
            var which = WaitHandle.WaitAny([stop, exited]);
            if (which == 0) return;
        }

        // The parent is gone. If it had the taskbar hidden, it did not get
        // to clean up.
        var state = ShellState.Load();
        if (state.TaskbarHidden && state.OwnerProcessId == parentId) RestoreShell();
    }

    /// <summary>Taskbar and desktop icons back, whatever state Hearth left them in.</summary>
    public static void RestoreShell()
    {
        TaskbarControl.Restore();
        ShowDesktopIcons();
    }

    private static void ShowDesktopIcons()
    {
        var view = IntPtr.Zero;
        Hearth.Core.Interop.Win32.EnumWindows((hwnd, _) =>
        {
            view = Hearth.Core.Interop.Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            return view == IntPtr.Zero;
        }, IntPtr.Zero);
        if (view != IntPtr.Zero) Hearth.Core.Interop.Win32.ShowWindow(view, Hearth.Core.Interop.Win32.SW_SHOWNA);
    }
}
