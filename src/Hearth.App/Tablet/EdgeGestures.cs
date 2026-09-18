using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>
/// Swipes in from the screen edges, without elevation (plan, section 6.1).
///
/// A thin, nearly invisible, never-activated window lies along each edge that
/// has an action. A touch that starts there lands on Hearth; once it has
/// moved far enough inwards the edge's action runs. A tap that doesn't turn
/// into a swipe is passed on as a click to whatever is underneath.
///
/// The mouse is kept out of the way: the moment a real mouse pointer is over
/// a strip, the strip becomes click-through (until the pointer leaves), so
/// scrollbars and buttons at the screen edge work as usual. WPF marks touch
/// and pen input promoted to mouse events with a StylusDevice, which is what
/// tells the two apart.
///
/// Limits, which the UIAccess helper removes later (section 6.3): only swipes
/// that start at an edge are seen, and Windows' own edge swipes may win on
/// hardware that has them.
/// </summary>
internal sealed class EdgeGestures : IDisposable
{
    private readonly TabletMode _mode;
    private readonly List<EdgeStrip> _strips = [];

    public EdgeGestures(TabletMode mode) => _mode = mode;

    public bool IsRunning => _strips.Count > 0;

    public void Start(TabletSettings settings, ScreenEdge? navigationBarEdge)
    {
        Stop();
        foreach (var edge in new[] { ScreenEdge.Left, ScreenEdge.Right, ScreenEdge.Top, ScreenEdge.Bottom })
        {
            var action = settings.ActionFor(edge);
            if (action == EdgeAction.None) continue;
            // The navigation bar already owns its edge.
            if (edge == navigationBarEdge) continue;
            var strip = new EdgeStrip(this, edge, settings.EdgeGesturesWithMouse);
            strip.Show();
            _strips.Add(strip);
        }
        Log.Write($"tablet: edge gestures on ({string.Join(", ", _strips.Select(s => s.Edge))})");
    }

    public void Stop()
    {
        if (_strips.Count == 0) return;
        foreach (var strip in _strips) strip.CloseStrip();
        _strips.Clear();
        Log.Write("tablet: edge gestures off");
    }

    /// <summary>Lays the strips out again (display change, navigation bar moved).</summary>
    public void Reposition()
    {
        foreach (var strip in _strips) strip.Place();
    }

    internal void Run(ScreenEdge edge)
    {
        var action = _mode.Settings.ActionFor(edge);
        Log.Write($"tablet: swipe from {edge} -> {action}");
        switch (action)
        {
            case EdgeAction.TaskSwitcher: _mode.ShowTaskSwitcher(); break;
            case EdgeAction.Start: _mode.OpenStart(); break;
            case EdgeAction.QuickSettings: WindowTools.SendWinChord('A'); break;
            case EdgeAction.Notifications: WindowTools.SendWinChord('N'); break;
            case EdgeAction.Desktop: _mode.GoHome(); break;
        }
    }

    internal bool ActionIsDragToClose(ScreenEdge edge) => _mode.Settings.ActionFor(edge) == EdgeAction.CloseApp;

    public void Dispose() => Stop();

    // ---- Primary display ------------------------------------------------------------

    internal static (Rect Pixels, double Scale) PrimaryDisplay()
    {
        var monitor = MonitorFromPoint(new WindowTools.POINT(), MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var r = info.rcMonitor;
        return (new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public WindowTools.RECT rcMonitor;
        public WindowTools.RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WindowTools.POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

/// <summary>One edge's strip.</summary>
internal sealed class EdgeStrip : Window
{
    private const double TriggerDistance = 48;
    private const double TapSlop = 10;

    private readonly EdgeGestures _owner;
    private readonly bool _withMouse;
    private readonly DispatcherTimer _mouseWatch;
    private IntPtr _hwnd;
    private bool _clickThrough;
    private bool _allowClose;

    private Point? _start;          // screen pixels
    private bool _fired;
    private bool _moved;
    private DragToClose? _drag;

    public ScreenEdge Edge { get; }

    public EdgeStrip(EdgeGestures owner, ScreenEdge edge, bool withMouse)
    {
        _owner = owner;
        _withMouse = withMouse;
        Edge = edge;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Alpha 1: invisible, but still hit-testable (alpha 0 is not).
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = $"Hearth edge ({edge})";
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;

        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

        _mouseWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _mouseWatch.Tick += (_, _) => ReleaseClickThroughIfAway();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetExStyle(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, true);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Place();
        };
    }

    public void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (screen, scale) = EdgeGestures.PrimaryDisplay();
        var thick = (int)Math.Round((Edge == ScreenEdge.Top ? 6 : 10) * scale);
        // The middle 80% only: corners keep their close buttons and Start-like targets.
        var alongX = (int)(screen.Width * 0.1);
        var alongY = (int)(screen.Height * 0.1);
        var (x, y, w, h) = Edge switch
        {
            ScreenEdge.Left => ((int)screen.Left, (int)screen.Top + alongY, thick, (int)screen.Height - alongY * 2),
            ScreenEdge.Right => ((int)screen.Right - thick, (int)screen.Top + alongY, thick, (int)screen.Height - alongY * 2),
            ScreenEdge.Top => ((int)screen.Left + alongX, (int)screen.Top, (int)screen.Width - alongX * 2, thick),
            _ => ((int)screen.Left + alongX, (int)screen.Bottom - thick, (int)screen.Width - alongX * 2, thick),
        };
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ---- Keeping the mouse out --------------------------------------------------------

    private static bool FromTouchOrPen(MouseEventArgs e) => e.StylusDevice is not null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is not null)
        {
            if (FromTouchOrPen(e) || _withMouse) Track(e);
            return;
        }
        if (!FromTouchOrPen(e) && !_withMouse) SetClickThrough(true);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!FromTouchOrPen(e) && !_withMouse)
        {
            // Pressed without a hover first (should not happen); pass it on.
            var point = ScreenPoint(e);
            SetClickThrough(true);
            WindowTools.ClickAt((int)point.X, (int)point.Y);
            e.Handled = true;
            return;
        }
        _start = ScreenPoint(e);
        _fired = false;
        _moved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_start is not { } start) return;
        var end = ScreenPoint(e);
        _start = null;
        ReleaseMouseCapture();
        e.Handled = true;

        if (_drag is not null)
        {
            _drag.Finish(end);
            _drag = null;
            return;
        }

        if (!_moved && !_fired)
        {
            // A tap: it was meant for the app under the strip.
            SetClickThrough(true);
            WindowTools.ClickAt((int)start.X, (int)start.Y);
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_start is null) return;
        _start = null;
        _drag?.Cancel();
        _drag = null;
    }

    private void Track(MouseEventArgs e)
    {
        if (_start is not { } start || _fired) return;
        var now = ScreenPoint(e);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var inward = Edge switch
        {
            ScreenEdge.Left => now.X - start.X,
            ScreenEdge.Right => start.X - now.X,
            ScreenEdge.Top => now.Y - start.Y,
            _ => start.Y - now.Y,
        } / scale;
        if (Math.Abs(now.X - start.X) / scale > TapSlop || Math.Abs(now.Y - start.Y) / scale > TapSlop) _moved = true;

        if (_owner.ActionIsDragToClose(Edge))
        {
            if (_drag is null && inward > TriggerDistance / 2) _drag = DragToClose.Begin(now);
            _drag?.Move(now);
            return;
        }

        if (inward >= TriggerDistance)
        {
            _fired = true;
            _start = null;
            ReleaseMouseCapture();
            _owner.Run(Edge);
        }
    }

    private Point ScreenPoint(MouseEventArgs e)
    {
        var local = e.GetPosition(this);
        return PointToScreen(local);
    }

    private void SetClickThrough(bool on)
    {
        if (_clickThrough == on || _hwnd == IntPtr.Zero) return;
        _clickThrough = on;
        SetExStyle(WS_EX_TRANSPARENT, on);
        if (on) _mouseWatch.Start();
        else _mouseWatch.Stop();
    }

    /// <summary>Hit-testable again once the pointer has moved well away from the strip.</summary>
    private void ReleaseClickThroughIfAway()
    {
        if (!WindowTools.GetCursorPos(out var cursor) || !WindowTools.GetWindowRect(_hwnd, out var r)) return;
        const int margin = 40;
        var near = cursor.X >= r.Left - margin && cursor.X < r.Right + margin &&
                   cursor.Y >= r.Top - margin && cursor.Y < r.Bottom + margin;
        if (!near && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) SetClickThrough(false);
    }

    private void SetExStyle(long bits, bool on)
    {
        var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = on ? ex | bits : ex & ~bits;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void CloseStrip()
    {
        _allowClose = true;
        _mouseWatch.Stop();
        _drag?.Cancel();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int VK_LBUTTON = 0x01;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}

/// <summary>
/// Windows 10's drag down from the top: the app in front shrinks under the
/// finger; let go near the bottom to close it, at the left or right edge to
/// snap it to that half, anywhere else to leave it be.
/// </summary>
internal sealed class DragToClose
{
    private readonly IntPtr _target;
    private readonly Window _overlay;
    private readonly Border _closeZone;
    private readonly Rect _screen; // physical pixels
    private readonly double _scale;
    private IntPtr _thumbnail;
    private Size _source;

    private DragToClose(IntPtr target, Rect screen, double scale)
    {
        _target = target;
        _screen = screen;
        _scale = scale;

        _closeZone = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = screen.Height / scale * 0.2,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xE8, 0x11, 0x23)),
            Opacity = 0,
            Child = new TextBlock
            {
                Text = "Release to close",
                Foreground = Brushes.White,
                FontSize = 18,
                FontFamily = StartStyle.Body,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14)),
            Content = new Grid { Children = { _closeZone } },
            Left = -32000,
            Top = -32000,
            Width = 1,
            Height = 1,
        };
    }

    public static DragToClose? Begin(Point at)
    {
        var target = WindowTools.GetForegroundWindow();
        if (target == IntPtr.Zero || !WindowTools.IsSwitchable(target)) return null;

        var (screen, scale) = EdgeGestures.PrimaryDisplay();
        var drag = new DragToClose(target, screen, scale);
        drag.Show();
        drag.Move(at);
        Log.Write($"tablet: drag to close '{WindowTools.Title(target)}'");
        return drag;
    }

    private void Show()
    {
        var capture = StartBackdropCapture.Capture(new Int32Rect((int)_screen.X, (int)_screen.Y, (int)_screen.Width, (int)_screen.Height), 0);
        if (capture is not null && _overlay.Content is Grid grid)
            grid.Children.Insert(0, StartBackdropCapture.Build(capture, 0, new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x14))));

        var hwnd = new WindowInteropHelper(_overlay).EnsureHandle();
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        // Click-through: the strip keeps the pointer capture throughout.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
        SetWindowPos(hwnd, HWND_TOPMOST, (int)_screen.X, (int)_screen.Y, (int)_screen.Width, (int)_screen.Height, SWP_NOACTIVATE);
        _overlay.Show();

        if (DwmRegisterThumbnail(hwnd, _target, out _thumbnail) == 0)
        {
            DwmQueryThumbnailSourceSize(_thumbnail, out var size);
            _source = new Size(Math.Max(1, size.cx), Math.Max(1, size.cy));
        }
    }

    public void Move(Point at)
    {
        if (_thumbnail == IntPtr.Zero) return;
        var progress = Math.Clamp((at.Y - _screen.Top) / _screen.Height, 0, 1);
        var shrink = 1 - Math.Min(0.55, progress * 1.2);
        var width = _screen.Width * shrink;
        var height = width * _source.Height / _source.Width;
        if (height > _screen.Height * shrink)
        {
            height = _screen.Height * shrink;
            width = height * _source.Width / _source.Height;
        }
        var left = at.X - _screen.X - width / 2;
        var top = at.Y - _screen.Y - height * 0.1;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE,
            rcDestination = new WindowTools.RECT
            {
                Left = (int)left,
                Top = (int)top,
                Right = (int)(left + width),
                Bottom = (int)(top + height),
            },
            fVisible = true,
        };
        DwmUpdateThumbnailProperties(_thumbnail, ref props);
        _closeZone.Opacity = InCloseZone(at) ? 1 : 0.35;
    }

    private bool InCloseZone(Point at) => at.Y > _screen.Bottom - _screen.Height * 0.2;

    public void Finish(Point at)
    {
        var edgeZone = _screen.Width * 0.08;
        if (InCloseZone(at))
        {
            Log.Write($"tablet: drag to close -> closing '{WindowTools.Title(_target)}'");
            WindowTools.Close(_target);
        }
        else if (at.X < _screen.Left + edgeZone) Snap(left: true);
        else if (at.X > _screen.Right - edgeZone) Snap(left: false);
        Cancel();
    }

    private void Snap(bool left)
    {
        var work = SplitView.WorkArea();
        SplitView.Place(_target, left
            ? new Rect(work.Left, work.Top, work.Width / 2, work.Height)
            : new Rect(work.Left + work.Width / 2, work.Top, work.Width / 2, work.Height));
        WindowTools.Activate(_target);
    }

    public void Cancel()
    {
        if (_thumbnail != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = IntPtr.Zero;
        }
        _overlay.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public WindowTools.RECT rcDestination;
        public WindowTools.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x1;
    private const uint DWM_TNP_VISIBLE = 0x8;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Puts a window on part of the primary display's work area (snap, split view).</summary>
internal static class SplitView
{
    public static Rect WorkArea()
    {
        var monitor = MonitorFromPoint(new WindowTools.POINT(), 1);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var r = info.rcWork;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// Restores the window (a maximized window ignores moves), then sizes it so
    /// its visible frame, not its invisible resize border, fills the area.
    /// </summary>
    public static void Place(IntPtr hwnd, Rect area)
    {
        ShowWindow(hwnd, SW_RESTORE);
        var inset = new WindowTools.RECT();
        if (WindowTools.GetWindowRect(hwnd, out var outer) &&
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<WindowTools.RECT>()) == 0)
        {
            inset.Left = visible.Left - outer.Left;
            inset.Top = visible.Top - outer.Top;
            inset.Right = outer.Right - visible.Right;
            inset.Bottom = outer.Bottom - visible.Bottom;
        }
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)area.Left - inset.Left, (int)area.Top - inset.Top,
            (int)area.Width + inset.Left + inset.Right, (int)area.Height + inset.Top + inset.Bottom,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public WindowTools.RECT rcMonitor;
        public WindowTools.RECT rcWork;
        public uint dwFlags;
    }

    private const int SW_RESTORE = 9;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WindowTools.POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out WindowTools.RECT value, int size);
}
