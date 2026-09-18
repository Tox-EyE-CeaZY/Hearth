using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearth.Core.Diagnostics;
using Microsoft.Win32;

namespace Hearth.App.Tablet;

/// <summary>One notification-area icon: its tooltip, and where it lives (the taskbar, or the hidden-icons flyout).</summary>
internal sealed record TrayIcon(string Name, bool Hidden, int Index, ImageSource? Image)
{
    /// <summary>The tooltip's first line, for display.</summary>
    public string Title => Name.Split((char)10, 2)[0].Trim();
}

/// <summary>
/// Reads and clicks the Windows notification-area ("tray") icons, so tablet
/// mode can offer them while the taskbar is hidden.
///
/// Windows 11 draws the tray in XAML; its icons are only reachable through
/// native UI Automation (the managed System.Windows.Automation client sees
/// none of it). Measured on build 26200:
/// - visible icons are buttons with AutomationId "NotifyItemIcon" in
///   Shell_TrayWnd; hidden ones exist only while the overflow flyout
///   (TopLevelWindowForOverflowXamlIsland) is open, opened by the
///   "SystemTrayIcon" button just before the first NotifyItemIcon;
/// - nothing is exposed while the taskbar is hidden or slid away (auto-hide),
///   so the taskbar is raised first, by giving it focus, as Start does;
/// - Invoke is a left click; IUIAutomationElement3.ShowContextMenu opens the
///   app's own right-click menu.
/// Images come from HKCU\Control Panel\NotifyIconSettings, where Windows
/// keeps a PNG snapshot of each icon with the program's path.
///
/// Called only from a background thread (UI Automation clients should not
/// run on a UI thread). The interfaces are called through their vtables,
/// slot numbers taken from UIAutomationClient.h (SDK 10.0.26100).
/// </summary>
internal static class TrayIcons
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>All icons, visible first. Hidden ones need the flyout opened for a moment.</summary>
    public static async Task<List<TrayIcon>> ReadAsync(bool includeHidden)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(includeHidden)).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Clicks an icon (<paramref name="menu"/>: opens its right-click menu instead).</summary>
    public static async Task<bool> ActivateAsync(TrayIcon icon, bool menu)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Activate(icon, menu)).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---- Reading ----------------------------------------------------------------------

    private static List<TrayIcon> Read(bool includeHidden)
    {
        var clock = Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) => marks.Add($"{what} {clock.ElapsedMilliseconds}");
        using var uia = Uia.Create();
        using var raised = RaisedTaskbar.Raise();
        Mark("raised");
        var result = new List<TrayIcon>();
        var images = IconImages.Load();
        Mark("images");

        var bar = uia.Buttons(raised.Taskbar);
        Mark("taskbar");
        var visible = bar.Where(b => b.Id == "NotifyItemIcon").ToList();
        for (var i = 0; i < visible.Count; i++) result.Add(new TrayIcon(visible[i].Name, false, i, images.Find(visible[i].Name)));

        if (includeHidden && OpenOverflow(uia, bar, offScreen: true) is { } overflow)
        {
            var inFlyout = uia.Buttons(overflow);
            var hidden = inFlyout.Where(b => b.Id == "NotifyItemIcon").ToList();
            for (var i = 0; i < hidden.Count; i++) result.Add(new TrayIcon(hidden[i].Name, true, i, images.Find(hidden[i].Name)));
            foreach (var b in inFlyout) b.Dispose();
            Mark("flyout");
            CloseOverflow(overflow, bar);
            Mark("closed");
        }
        foreach (var b in bar) b.Dispose();

        Log.Write($"tray: {result.Count(r => !r.Hidden)} shown and {result.Count(r => r.Hidden)} hidden icons; ms: {string.Join(", ", marks)}");
        return result;
    }

    private static bool Activate(TrayIcon icon, bool menu)
    {
        using var uia = Uia.Create();
        using var raised = RaisedTaskbar.Raise();
        var bar = uia.Buttons(raised.Taskbar);
        IntPtr overflow = IntPtr.Zero;
        List<Uia.Button> pool;
        if (icon.Hidden)
        {
            // Left on screen: apps place their popups next to the icon.
            overflow = OpenOverflow(uia, bar, offScreen: false) ?? IntPtr.Zero;
            pool = overflow == IntPtr.Zero ? [] : uia.Buttons(overflow);
        }
        else
        {
            pool = bar;
        }

        var candidates = pool.Where(b => b.Id == "NotifyItemIcon").ToList();
        var target = Match(candidates, icon);
        var done = false;
        if (target is null)
        {
            Log.Write($"tray: '{icon.Title}' is gone");
        }
        else if (menu)
        {
            done = target.ShowContextMenu();
            Log.Write($"tray: menu for '{icon.Title}' {(done ? "opened" : "failed")}");
        }
        else
        {
            done = target.Invoke();
            Log.Write($"tray: clicked '{icon.Title}' {(done ? "" : "(failed)")}");
            if (overflow != IntPtr.Zero)
            {
                Thread.Sleep(250);
                CloseOverflow(overflow, bar);
            }
        }

        // A menu for a hidden icon belongs to the open flyout; it closes with the menu.
        if (menu) raised.KeepRaisedFor(TimeSpan.FromSeconds(1));
        foreach (var b in pool.Concat(bar).Distinct()) b.Dispose();
        return done;
    }

    /// <summary>
    /// Finds the icon again. Tooltips change (Task Manager's shows live CPU
    /// use), so: the same text, or else the same position with the same first
    /// word, or else the longest shared beginning.
    /// </summary>
    private static Uia.Button? Match(List<Uia.Button> buttons, TrayIcon icon)
    {
        var exact = buttons.FirstOrDefault(b => b.Name == icon.Name);
        if (exact is not null) return exact;

        static string FirstWord(string s) => s.Trim().Split(' ', (char)10, '-')[0];
        if (icon.Index < buttons.Count && FirstWord(buttons[icon.Index].Name).Equals(FirstWord(icon.Name), StringComparison.OrdinalIgnoreCase))
            return buttons[icon.Index];

        static int Shared(string a, string b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
            return n;
        }
        var best = buttons.MaxBy(b => Shared(b.Name.Trim(), icon.Name.Trim()));
        return best is not null && Shared(best.Name.Trim(), icon.Name.Trim()) >= 4 ? best : null;
    }

    private const string OverflowClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>
    /// Opens the hidden-icons flyout, moved off screen when it is only being
    /// read (tablet mode's own panel is what the user looks at). Returns its
    /// window, or null.
    /// </summary>
    private static IntPtr? OpenOverflow(Uia uia, List<Uia.Button> bar, bool offScreen)
    {
        var existing = FindWindow(OverflowClass, null);
        if (existing != IntPtr.Zero && IsWindowVisible(existing)) return existing;

        var chevron = Chevron(bar);
        if (chevron is null || !chevron.Invoke()) return null;

        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1500)
        {
            var window = FindWindow(OverflowClass, null);
            if (window != IntPtr.Zero && IsWindowVisible(window))
            {
                if (offScreen) SetWindowPos(window, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                Thread.Sleep(120); // the flyout fills in its buttons as it opens
                return window;
            }
            Thread.Sleep(30);
        }
        return null;
    }

    /// <summary>The hidden-icons button: the tray button just before the first app icon (its name is localized).</summary>
    private static Uia.Button? Chevron(List<Uia.Button> bar)
    {
        var firstIcon = bar.FindIndex(b => b.Id == "NotifyItemIcon");
        return bar.Take(firstIcon < 0 ? bar.Count : firstIcon).FirstOrDefault(b => b.Id == "SystemTrayIcon");
    }

    /// <summary>Closes the flyout with its own button (it toggles); Escape if that did not do it.</summary>
    private static void CloseOverflow(IntPtr window, List<Uia.Button> bar)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window)) return;
        Chevron(bar)?.Invoke();
        for (var i = 0; i < 10 && IsWindowVisible(window); i++) Thread.Sleep(30);
        if (!IsWindowVisible(window)) return;
        PostMessage(window, WM_KEYDOWN, new IntPtr(VK_ESCAPE), new IntPtr(0x00010001));
        PostMessage(window, WM_KEYUP, new IntPtr(VK_ESCAPE), new IntPtr(unchecked((int)0xC0010001)));
    }

    // ---- Raising the taskbar ----------------------------------------------------------

    /// <summary>
    /// Brings the (hidden, auto-hidden) taskbar up for as long as it is
    /// needed. In tablet mode the navigation bar covers where it appears.
    /// </summary>
    private sealed class RaisedTaskbar : IDisposable
    {
        public IntPtr Taskbar { get; }
        private readonly bool _wasHidden;
        private TimeSpan _linger;

        private RaisedTaskbar(IntPtr taskbar, bool wasHidden)
        {
            Taskbar = taskbar;
            _wasHidden = wasHidden;
        }

        public static RaisedTaskbar Raise()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            var wasHidden = !IsWindowVisible(taskbar);
            TaskbarControl.PauseEnforcing(true);
            if (wasHidden) ShowWindow(taskbar, SW_SHOWNA);

            if (TaskbarControl.AutoHide && !IsUp(taskbar))
            {
                Hosting.StartTrigger.ClaimForegroundRight();
                SetForegroundWindow(taskbar);
                var clock = Stopwatch.StartNew();
                while (!IsUp(taskbar) && clock.ElapsedMilliseconds < 800) Thread.Sleep(20);
                Thread.Sleep(60);
            }
            return new RaisedTaskbar(taskbar, wasHidden);
        }

        /// <summary>Whether any part of the bar is on its display (an auto-hidden bar sits almost entirely off it).</summary>
        private static bool IsUp(IntPtr taskbar)
        {
            if (!GetWindowRect(taskbar, out var bar)) return false;
            var monitor = MonitorFromWindow(taskbar, 2);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            var height = bar.Bottom - bar.Top;
            var onScreen = Math.Min(bar.Bottom, info.rcMonitor.Bottom) - Math.Max(bar.Top, info.rcMonitor.Top);
            return onScreen >= height - 2;
        }

        public void KeepRaisedFor(TimeSpan time) => _linger = time;

        public void Dispose()
        {
            if (_linger > TimeSpan.Zero)
            {
                // Hand the taskbar back later, off this thread.
                var wasHidden = _wasHidden;
                var taskbar = Taskbar;
                _ = Task.Delay(_linger).ContinueWith(_ => Lower(taskbar, wasHidden), TaskScheduler.Default);
                return;
            }
            Lower(Taskbar, _wasHidden);
        }

        private static void Lower(IntPtr taskbar, bool wasHidden)
        {
            if (wasHidden && TaskbarControl.IsHiddenByUs) ShowWindow(taskbar, SW_HIDE);
            TaskbarControl.PauseEnforcing(false);
        }
    }

    // ---- Icon images ------------------------------------------------------------------

    /// <summary>The icon snapshots Windows keeps, matched to icons by tooltip or program.</summary>
    private sealed class IconImages
    {
        private readonly List<(string Tooltip, string Program, HashSet<string> Words, bool Running, byte[] Png)> _entries = [];

        public static IconImages Load()
        {
            var images = new IconImages();
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
            {
                using (process) running.Add(process.ProcessName + ".exe");
            }

            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                if (root is null) return images;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("IconSnapshot") is not byte[] { Length: > 8 } png) continue;
                    var path = ResolveKnownFolder(key.GetValue("ExecutablePath") as string ?? "");
                    var program = Path.GetFileName(path);
                    var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    AddWords(words, Path.GetFileNameWithoutExtension(path));
                    AddWords(words, key.GetValue("InitialTooltip") as string);
                    if (File.Exists(path))
                    {
                        try
                        {
                            var version = FileVersionInfo.GetVersionInfo(path);
                            AddWords(words, version.ProductName);
                            AddWords(words, version.FileDescription);
                        }
                        catch (FileNotFoundException) { }
                    }
                    images._entries.Add(((key.GetValue("InitialTooltip") as string ?? "").Trim(), program, words, running.Contains(program), png));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Write($"tray: icon snapshots unreadable: {ex.Message}");
            }
            return images;
        }

        private static void AddWords(HashSet<string> words, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            words.Add(text.Trim());
            foreach (var word in text.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4 && !Generic.Contains(word)) words.Add(word);
            }
        }

        private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "microsoft", "host", "service", "application", "desktop", "update", "helper",
            "container", "client", "launcher", "setup", "tray", "system", "settings", "display",
        };

        public ImageSource? Find(string tooltip)
        {
            var name = tooltip.Trim();
            var first = name.Split((char)10, 2)[0].Trim();

            // Only programs that are running can have an icon now.
            var live = _entries.Where(e => e.Running).ToList();
            var byTooltip = live.FirstOrDefault(e => e.Tooltip.Length > 0 &&
                (e.Tooltip.Equals(first, StringComparison.OrdinalIgnoreCase) || first.StartsWith(e.Tooltip, StringComparison.OrdinalIgnoreCase)));
            if (byTooltip.Png is not null) return Decode(byTooltip.Png);

            var byWord = live
                .Select(e => (Entry: e, Score: e.Words.Where(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)).Select(w => w.Length).DefaultIfEmpty(0).Max()))
                .Where(x => x.Score >= 4)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            return byWord.Entry.Png is not null ? Decode(byWord.Entry.Png) : null;
        }

        private static ImageSource? Decode(byte[] png)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(png);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>"{6D809377-...}\VLC\vlc.exe" to a real path.</summary>
        private static string ResolveKnownFolder(string path)
        {
            if (!path.StartsWith('{')) return path;
            var close = path.IndexOf('}');
            if (close < 0 || !Guid.TryParse(path[..(close + 1)], out var id)) return path;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var folder) != 0) return path;
            try
            {
                return Marshal.PtrToStringUni(folder) + path[(close + 1)..];
            }
            finally
            {
                Marshal.FreeCoTaskMem(folder);
            }
        }
    }

    // ---- UI Automation, by vtable ------------------------------------------------------

    private sealed unsafe class Uia : IDisposable
    {
        // IUIAutomation (IUnknown + index): ElementFromHandle 3, CreateTrueCondition 18.
        private const int ElementFromHandle = 6;
        private const int CreateTrueCondition = 21;
        // IUIAutomationElement: FindAll 3, GetCurrentPattern 13, get_CurrentName 20,
        // get_CurrentAutomationId 26.
        private const int FindAll = 6;
        private const int GetCurrentPattern = 16;
        private const int CurrentName = 23;
        private const int CurrentAutomationId = 29;
        // IUIAutomationElement3: after 82 + 6 inherited methods, ShowContextMenu is first.
        private const int ShowContextMenuSlot = 3 + 82 + 6;
        private const int InvokeSlot = 3;
        private const int TreeScopeDescendants = 4;
        private const int InvokePatternId = 10000;

        private static readonly Guid ClsidCUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
        private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
        private static readonly Guid IidElement3 = new("8471DF34-AEE0-4A01-A7DE-7DB9AF12C296");

        private readonly IntPtr _automation;
        private readonly IntPtr _condition;

        private Uia(IntPtr automation, IntPtr condition)
        {
            _automation = automation;
            _condition = condition;
        }

        public static Uia Create()
        {
            var clsid = ClsidCUIAutomation8;
            var iid = IidIUIAutomation;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out var automation));
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)Slot(automation, CreateTrueCondition))(automation, out var condition);
            if (hr != 0)
            {
                Marshal.Release(automation);
                Marshal.ThrowExceptionForHR(hr);
            }
            return new Uia(automation, condition);
        }

        /// <summary>Every element under a window, as buttons with a name and automation id.</summary>
        public List<Button> Buttons(IntPtr hwnd)
        {
            var list = new List<Button>();
            if (hwnd == IntPtr.Zero) return list;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)Slot(_automation, ElementFromHandle))(_automation, hwnd, out var root) != 0) return list;
            try
            {
                if (((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, out IntPtr, int>)Slot(root, FindAll))(root, TreeScopeDescendants, _condition, out var array) != 0 || array == IntPtr.Zero) return list;
                try
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, out int, int>)Slot(array, 3))(array, out var count);
                    for (var i = 0; i < count; i++)
                    {
                        if (((delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)Slot(array, 4))(array, i, out var element) != 0) continue;
                        var id = ReadString(element, CurrentAutomationId);
                        if (id is not ("NotifyItemIcon" or "SystemTrayIcon"))
                        {
                            Marshal.Release(element);
                            continue;
                        }
                        list.Add(new Button(element, id, ReadString(element, CurrentName)));
                    }
                }
                finally
                {
                    Marshal.Release(array);
                }
            }
            finally
            {
                Marshal.Release(root);
            }
            return list;
        }

        private static string ReadString(IntPtr element, int slot)
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)Slot(element, slot))(element, out var bstr) != 0) return string.Empty;
            var text = Marshal.PtrToStringBSTR(bstr);
            Marshal.FreeBSTR(bstr);
            return text ?? string.Empty;
        }

        public sealed class Button(IntPtr element, string id, string name) : IDisposable
        {
            private IntPtr _element = element;
            public string Id { get; } = id;
            public string Name { get; } = name;

            public bool Invoke()
            {
                if (_element == IntPtr.Zero) return false;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)Slot(_element, GetCurrentPattern))(_element, InvokePatternId, out var pattern) != 0 || pattern == IntPtr.Zero)
                    return false;
                try
                {
                    return ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(pattern, InvokeSlot))(pattern) == 0;
                }
                finally
                {
                    Marshal.Release(pattern);
                }
            }

            public bool ShowContextMenu()
            {
                if (_element == IntPtr.Zero) return false;
                var iid = IidElement3;
                if (Marshal.QueryInterface(_element, ref iid, out var element3) != 0) return false;
                try
                {
                    return ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(element3, ShowContextMenuSlot))(element3) == 0;
                }
                finally
                {
                    Marshal.Release(element3);
                }
            }

            public void Dispose()
            {
                if (_element == IntPtr.Zero) return;
                Marshal.Release(_element);
                _element = IntPtr.Zero;
            }
        }

        private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

        public void Dispose()
        {
            Marshal.Release(_condition);
            Marshal.Release(_automation);
        }
    }

    // ---- Native -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int CLSCTX_INPROC_SERVER = 1;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int VK_ESCAPE = 0x1B;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr instance);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
