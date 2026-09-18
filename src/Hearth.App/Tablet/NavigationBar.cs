using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>
/// Tablet mode's navigation bar: Back, Home and Recents in the middle, the
/// keyboard and Start at one end, quick settings, notifications and the clock
/// at the other.
///
/// It is a registered app bar (ABM_NEW), like the taskbar it stands in for,
/// so maximized windows stop at its edge. It never takes focus: Back has to
/// reach the app that is in front, and a bar that activated would be the app
/// in front.
/// </summary>
internal sealed class NavigationBar : Window
{
    private readonly TabletMode _mode;
    private readonly ScreenEdge _edge;
    private readonly double _thickness;
    private readonly uint _callbackMessage;
    private readonly DispatcherTimer _clockTimer;
    private TextBlock? _clock;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _allowClose;
    private bool _docking;
    private bool _dockAgain;
    private DispatcherTimer? _redock;
    private RECT _docked;

    public NavigationBar(TabletMode mode, TabletSettings settings)
    {
        _mode = mode;
        _edge = settings.NavigationBarEdge;
        _thickness = Math.Clamp(settings.NavigationBarSize, 36, 96);
        _callbackMessage = RegisterWindowMessage("Hearth.NavigationBar.AppBar");

        Title = "Hearth navigation bar";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(StartStyle.IsLight ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x21));
        FontFamily = StartStyle.Body;
        Foreground = StartStyle.Text;
        Resources = StartStyle.WindowResources();
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        Content = Build(settings);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Register();
            Dock();
        };
        Loaded += (_, _) =>
        {
            UpdateClock();
            _clockTimer.Start();
        };
    }

    private bool Vertical => _edge is ScreenEdge.Left or ScreenEdge.Right;

    private FrameworkElement Build(TabletSettings settings)
    {
        var start = Group();
        var middle = Group();
        var end = Group();

        if (settings.NavShowStart)
            start.Children.Add(Button(0xF0E2, "Start", () => _mode.OpenStart()));
        if (settings.NavShowKeyboard)
            start.Children.Add(Button(0xE765, "Touch keyboard", TouchKeyboard.Toggle));

        middle.Children.Add(Button(0xE72B, "Back", () => WindowTools.SendBack(_mode.Settings)));
        middle.Children.Add(Button(0xE80F, "Home", () => _mode.GoHome()));
        middle.Children.Add(Button(0xE7C4, "Recent apps", () => _mode.ShowTaskSwitcher()));

        if (settings.NavShowQuickSettings)
            end.Children.Add(Button(0xE706, "Quick settings", () => WindowTools.SendWinChord('A')));
        if (settings.NavShowNotifications)
            end.Children.Add(Button(0xEA8F, "Notifications", () => WindowTools.SendWinChord('N')));
        if (settings.NavShowClock)
        {
            _clock = new TextBlock
            {
                FontSize = 13,
                Foreground = StartStyle.Text,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = Vertical ? new Thickness(0, 6, 0, 6) : new Thickness(10, 0, 12, 0),
            };
            var clock = new PressableBorder(radius: 6) { Child = _clock, Padding = new Thickness(4) };
            clock.Clicked += _ => WindowTools.SendWinChord('N');
            end.Children.Add(clock);
        }

        var grid = new Grid();
        if (Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            start.VerticalAlignment = VerticalAlignment.Top;
            end.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(middle, 1);
            Grid.SetRow(end, 2);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            start.HorizontalAlignment = HorizontalAlignment.Left;
            end.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(middle, 1);
            Grid.SetColumn(end, 2);
        }
        middle.HorizontalAlignment = HorizontalAlignment.Center;
        middle.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(start);
        grid.Children.Add(middle);
        grid.Children.Add(end);

        // Right-click (or press and hold) the bar itself for its own options.
        var surface = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = _edge switch
            {
                ScreenEdge.Bottom => new Thickness(0, 1, 0, 0),
                ScreenEdge.Top => new Thickness(0, 0, 0, 1),
                ScreenEdge.Left => new Thickness(0, 0, 1, 0),
                _ => new Thickness(1, 0, 0, 0),
            },
            Child = grid,
        };
        surface.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowBarMenu(surface);
        };
        return surface;

        StackPanel Group() => new()
        {
            Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal,
            Margin = Vertical ? new Thickness(0, 4, 0, 4) : new Thickness(6, 0, 6, 0),
        };
    }

    private FrameworkElement Button(int glyph, string tooltip, Action action)
    {
        var size = _thickness - 8;
        var button = new PressableBorder(radius: 8)
        {
            Width = Vertical ? size : size * 1.5,
            Height = size,
            Margin = new Thickness(2),
            ToolTip = tooltip,
            Child = StartStyle.GlyphText(StartStyle.Glyph(glyph), Math.Round(_thickness * 0.36)),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Clicked += _ =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"navigation bar: {tooltip}", ex); }
        };
        return button;
    }

    private void ShowBarMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = _edge == ScreenEdge.Top
            ? System.Windows.Controls.Primitives.PlacementMode.Bottom
            : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Items.Add(MenuItem("Tablet settings...", () => Views.SettingsScreen.SettingsWindow.Open("tablet")));
        menu.Items.Add(MenuItem("Leave tablet mode", () => _mode.Toggle()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Hide navigation bar", () =>
        {
            _mode.Settings.NavigationBar = false;
            _mode.SaveSettingsAndApply();
        }));
        menu.IsOpen = true;

        static MenuItem MenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void UpdateClock()
    {
        if (_clock is null) return;
        var now = DateTime.Now;
        _clock.Text = Vertical
            ? now.ToString("t", System.Globalization.CultureInfo.CurrentCulture).Replace(" ", Environment.NewLine)
            : now.ToString("t", System.Globalization.CultureInfo.CurrentCulture) + Environment.NewLine +
              now.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
        _clock.FontSize = Vertical ? 11 : 12;
    }

    // ---- App bar ------------------------------------------------------------------

    private void Register()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        if (!_registered) Log.Write("tablet: navigation bar could not register as an app bar");
        else Log.Write($"tablet: navigation bar registered in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Reserves the bar's strip on the primary display: ask Windows where it
    /// may go (other app bars may already be there), then claim it.
    /// </summary>
    public async void Dock()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_docking)
        {
            // Asked again mid-claim (the taskbar finished hiding): claim again afterwards.
            _dockAgain = true;
            return;
        }
        _docking = true;
        _dockAgain = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            var thickness = (int)Math.Round(_thickness * scale);

            var data = NewData();
            data.uEdge = _edge switch
            {
                ScreenEdge.Left => ABE_LEFT,
                ScreenEdge.Top => ABE_TOP,
                ScreenEdge.Right => ABE_RIGHT,
                _ => ABE_BOTTOM,
            };
            data.rc = Strip(info.rcMonitor, thickness);

            // Explorer tells every app bar when any of them moves, this one
            // included; claiming the same strip again would only echo.
            if (Same(data.rc, _docked)) return;

            // Shown at once where it will almost certainly end up; the claim
            // itself waits on Explorer rearranging every window (measured at
            // about two seconds with a hidden taskbar), so it runs off the UI thread.
            Place(data.rc);
            // With the taskbar hidden by tablet mode the bar belongs on the
            // screen edge itself. Windows' suggestion can still leave room for
            // the taskbar it is hiding (a Surface Pro 7+ showed a gap under the
            // bar), so it is only asked when the taskbar is still there.
            var flush = TaskbarControl.IsHiddenByUs;
            var wanted = data.rc;
            if (_registered)
            {
                var registered = data;
                data = await Task.Run(() =>
                {
                    var request = registered;
                    if (!flush) request.rc = Strip(info.rcMonitor, thickness, fromQuery: true, request);
                    SHAppBarMessage(ABM_SETPOS, ref request);
                    return request;
                }).ConfigureAwait(true);
                if (!IsLoaded && !IsVisible) return;
            }
            var r = flush ? wanted : data.rc;
            _docked = r;
            Place(r);
            var m = info.rcMonitor;
            Log.Write($"tablet: navigation bar at ({r.Left},{r.Top})-({r.Right},{r.Bottom}), {_edge}, " +
                      $"screen ({m.Left},{m.Top})-({m.Right},{m.Bottom}) at {scale * 100:F0}%, " +
                      $"{(flush ? "flush with the edge" : "where Windows put it")}, {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Error("navigation bar dock", ex);
        }
        finally
        {
            _docking = false;
            if (_dockAgain && IsVisible)
            {
                _dockAgain = false;
                _docked = default;
                Dock();
            }
        }
    }

    /// <summary>The bar's strip along its edge; with <paramref name="fromQuery"/>, after asking Windows where there is room.</summary>
    private RECT Strip(RECT screen, int thickness, bool fromQuery = false, APPBARDATA query = default)
    {
        var rc = screen;
        if (fromQuery)
        {
            query.rc = screen;
            SHAppBarMessage(ABM_QUERYPOS, ref query);
            rc = query.rc;
        }
        switch (_edge)
        {
            case ScreenEdge.Left: rc.Right = rc.Left + thickness; break;
            case ScreenEdge.Right: rc.Left = rc.Right - thickness; break;
            case ScreenEdge.Top: rc.Bottom = rc.Top + thickness; break;
            default: rc.Top = rc.Bottom - thickness; break;
        }
        return rc;
    }

    private void Place(RECT r) =>
        SetWindowPos(_hwnd, HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    private static bool Same(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Notifications arrive in bursts; dock once they stop.</summary>
    private void ScheduleDock()
    {
        if (_redock is null)
        {
            _redock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _redock.Tick += (_, _) =>
            {
                _redock.Stop();
                Dock();
            };
        }
        _redock.Stop();
        _redock.Start();
    }

    /// <summary>Claims the strip again even if it looks unchanged (Explorer restarted, displays changed).</summary>
    public void Redock()
    {
        _docked = default;
        Dock();
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0 && (uint)msg == _callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    ScheduleDock();
                    break;
                case ABN_FULLSCREENAPP:
                    // A full-screen app (a video, a game) covers the bar, as it would the taskbar.
                    var fullScreen = lParam != IntPtr.Zero;
                    SetWindowPos(_hwnd, fullScreen ? HWND_BOTTOM : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                _docked = default;
                ScheduleDock();
                break;
        }
        return IntPtr.Zero;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _clockTimer.Stop();
        _redock?.Stop();
        if (_registered)
        {
            // Giving the strip back also waits on Explorer (about two seconds
            // measured); the window can go meanwhile.
            _registered = false;
            var data = NewData();
            _ = Task.Run(() =>
            {
                var request = data;
                SHAppBarMessage(ABM_REMOVE, ref request);
            });
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _redock?.Stop();
        if (_registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data);
            _registered = false;
        }
        base.OnClosed(e);
    }

    // ---- Native -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint ABM_NEW = 0;
    private const uint ABM_REMOVE = 1;
    private const uint ABM_QUERYPOS = 2;
    private const uint ABM_SETPOS = 3;
    private const int ABN_POSCHANGED = 1;
    private const int ABN_FULLSCREENAPP = 2;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int MA_NOACTIVATE = 3;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
