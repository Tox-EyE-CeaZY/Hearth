using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Tablet;

/// <summary>
/// Recent apps: every open window as a live preview card. Tap a card to go
/// to it, swipe it up (or use its X) to close it, "Close all" to clear.
///
/// The previews are DWM thumbnails, the mechanism Alt+Tab and Task View use:
/// the compositor draws the real window into a rectangle of this one, live,
/// with no copying. They are drawn over the WPF content, so each card keeps
/// its title outside the preview rectangle.
/// </summary>
internal sealed class TaskSwitcher : Window
{
    private const double TitleHeight = 34;
    private const double Gap = 28;
    private const double CloseDistance = 140;
    private const double HeaderHeight = 64;

    private readonly Grid _root = new();
    private readonly Grid _backdrop = new();
    private readonly Canvas _cards = new();
    private readonly TextBlock _empty;
    private readonly List<Card> _live = [];
    private readonly DispatcherTimer _refresh;
    private IntPtr _hwnd;
    private bool _allowClose;
    private bool _hiding;
    private double _scale = 1;

    // The tray shade (see TrayPanel). Reading the tray raises the taskbar,
    // which takes focus for a moment; _trayBusy keeps that from closing Recents.
    private readonly TrayPanel _trayPanel = new();
    private readonly Grid _trayLayer = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _trayArrow;
    private bool _trayBusy;
    private int _trayGeneration;
    private static IReadOnlyList<TrayIcon>? _lastTray;
    private static bool _trayWasOpen;

    public TaskSwitcher()
    {
        Title = "Recent apps — Hearth";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = StartStyle.Body;
        Foreground = StartStyle.Text;
        Background = new SolidColorBrush(StartStyle.IsLight ? Color.FromRgb(0xE8, 0xE8, 0xEC) : Color.FromRgb(0x14, 0x14, 0x18));
        Resources = StartStyle.WindowResources();

        _empty = StartStyle.Label("No recent apps", 20, StartStyle.Muted);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Visibility = Visibility.Collapsed;

        var closeAll = new PressableBorder(StartStyle.Card, StartStyle.CardHover, radius: 18)
        {
            Padding = new Thickness(18, 8, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24),
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            Child = StartStyle.Label("Close all", 14, weight: FontWeights.SemiBold),
        };
        closeAll.Clicked += _ => CloseAll();

        // Header: title on the left, the tray pill on the right.
        _trayArrow = StartStyle.GlyphText(StartStyle.Glyph(0xE70D), 11);
        _trayArrow.Margin = new Thickness(8, 1, 0, 0);
        var trayPill = new PressableBorder(StartStyle.Card, StartStyle.CardHover, radius: 18)
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            ToolTip = "Apps in the notification area",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { StartStyle.GlyphText(StartStyle.Glyph(0xE71D), 14), SpacedLabel("Tray"), _trayArrow },
            },
        };
        trayPill.Clicked += _ => ToggleTray();
        var header = new DockPanel { Height = HeaderHeight, Margin = new Thickness(Gap, 0, Gap, 0), LastChildFill = false, VerticalAlignment = VerticalAlignment.Top };
        var heading = StartStyle.Label("Recent apps", 20, weight: FontWeights.SemiBold);
        DockPanel.SetDock(heading, Dock.Left);
        DockPanel.SetDock(trayPill, Dock.Right);
        header.Children.Add(heading);
        header.Children.Add(trayPill);

        // The shade sits over the cards with a dim behind it; a tap on the dim closes it.
        var dim = new Border { Background = StartStyle.Dim };
        dim.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseTray();
        };
        _trayPanel.Margin = new Thickness(Gap, HeaderHeight, Gap, 0);
        _trayPanel.Chosen += OnTrayChosen;
        _trayLayer.Children.Add(dim);
        _trayLayer.Children.Add(_trayPanel);

        _root.Children.Add(_backdrop);
        _root.Children.Add(_cards);
        _root.Children.Add(_empty);
        _root.Children.Add(closeAll);
        _root.Children.Add(_trayLayer);
        _root.Children.Add(header);
        Content = _root;

        // A press on the empty background goes home, as on a phone.
        _cards.Background = Brushes.Transparent;
        _cards.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource == _cards) HideSwitcher();
        };

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (IsVisible) Rebuild();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        };
        Deactivated += (_, _) =>
        {
            if (!_trayBusy) HideSwitcher();
        };
        PreviewKeyDown += OnKey;
        SizeChanged += (_, _) => Layout();
    }

    public void Toggle()
    {
        if (IsVisible) HideSwitcher();
        else ShowSwitcher();
    }

    public void ShowSwitcher()
    {
        _hiding = false;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();

        // The display the finger is on: the primary one on a tablet.
        WindowTools.GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var work = info.rcWork;
        _scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        _backdrop.Children.Clear();
        var capture = StartBackdropCapture.Capture(new Int32Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), 0);
        if (capture is not null) _backdrop.Children.Add(StartBackdropCapture.Build(capture, 0, StartStyle.Tint));

        SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        if (!IsVisible) Show();
        StartTrigger.ClaimForegroundRight();
        Activate();
        SetForegroundWindow(hwnd);

        Rebuild();
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        if (_trayWasOpen) OpenTray();
        Log.Write($"tablet: recent apps shown, {_live.Count} windows");
    }

    public void HideSwitcher()
    {
        if (_hiding || !IsVisible) return;
        _hiding = true;
        _trayGeneration++;
        _trayBusy = false;
        _trayLayer.Visibility = Visibility.Collapsed;
        _trayArrow.Text = StartStyle.Glyph(0xE70D);
        ClearCards();
        Hide();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _trayLayer.Visibility == Visibility.Visible:
                CloseTray();
                e.Handled = true;
                break;
            case Key.Escape:
                HideSwitcher();
                e.Handled = true;
                break;
            case Key.Enter when _live.Count > 0:
                Go(_live[0]);
                e.Handled = true;
                break;
        }
    }

    // ---- Cards --------------------------------------------------------------------

    private sealed class Card
    {
        public required IntPtr Window;
        public required Border Chrome;
        public required TranslateTransform Offset;
        public IntPtr Thumbnail;
        public Rect Preview; // DIPs, relative to the window
        public Size Source;
    }

    private void Rebuild()
    {
        ClearCards();
        foreach (var hwnd in WindowTools.SwitchableWindows())
        {
            if (hwnd == _hwnd) continue;
            var card = CreateCard(hwnd);
            if (card is not null) _live.Add(card);
        }
        _empty.Visibility = _live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Layout();
    }

    private Card? CreateCard(IntPtr hwnd)
    {
        if (DwmRegisterThumbnail(_hwnd, hwnd, out var thumbnail) != 0) return null;
        DwmQueryThumbnailSourceSize(thumbnail, out var size);

        var offset = new TranslateTransform();
        var icon = WindowTools.Icon(hwnd);
        var header = new DockPanel { Height = TitleHeight, LastChildFill = true, VerticalAlignment = VerticalAlignment.Top };

        var close = new PressableBorder(radius: 14)
        {
            Width = 28,
            Height = 28,
            ToolTip = "Close",
            Child = StartStyle.GlyphText(StartStyle.Glyph(0xE711), 12),
        };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(2, 0, 8, 0) };
            DockPanel.SetDock(image, Dock.Left);
            header.Children.Add(image);
        }
        header.Children.Add(StartStyle.Label(WindowTools.Title(hwnd), 13, weight: FontWeights.SemiBold));

        var chrome = new Border
        {
            Background = Brushes.Transparent,
            RenderTransform = offset,
            // The preview is drawn by DWM below the title row, over this card.
            Child = header,
            Cursor = Cursors.Hand,
        };

        var card = new Card
        {
            Window = hwnd,
            Chrome = chrome,
            Offset = offset,
            Thumbnail = thumbnail,
            Source = new Size(Math.Max(1, size.cx), Math.Max(1, size.cy)),
        };
        close.Clicked += _ => CloseCard(card);
        AttachSwipe(card);
        _cards.Children.Add(chrome);
        return card;
    }

    /// <summary>
    /// Lays the cards out in the grid that makes previews largest, keeping
    /// each window's shape. Previews are placed in physical pixels.
    /// </summary>
    private void Layout()
    {
        if (_live.Count == 0 || ActualWidth <= 0) return;

        var areaWidth = ActualWidth - Gap * 2;
        var areaHeight = ActualHeight - Gap * 2 - 80 - HeaderHeight; // room for the header and "Close all"
        var aspect = _live.Average(c => c.Source.Width / c.Source.Height);
        aspect = Math.Clamp(aspect, 0.5, 2.4);

        var best = (Columns: 1, Width: 0.0);
        for (var columns = 1; columns <= _live.Count; columns++)
        {
            var rows = (int)Math.Ceiling(_live.Count / (double)columns);
            var width = Math.Min(
                (areaWidth - Gap * (columns - 1)) / columns,
                ((areaHeight - Gap * (rows - 1)) / rows - TitleHeight) * aspect);
            if (width > best.Width) best = (columns, width);
        }

        var cardWidth = Math.Min(best.Width, areaWidth * 0.45);
        var previewHeight = cardWidth / aspect;
        var cardHeight = previewHeight + TitleHeight;
        var columnsUsed = best.Columns;
        var rowsUsed = (int)Math.Ceiling(_live.Count / (double)columnsUsed);
        var top = HeaderHeight + Gap + (areaHeight - (rowsUsed * cardHeight + (rowsUsed - 1) * Gap)) / 2;

        for (var i = 0; i < _live.Count; i++)
        {
            var row = i / columnsUsed;
            var inRow = Math.Min(columnsUsed, _live.Count - row * columnsUsed);
            var rowWidth = inRow * cardWidth + (inRow - 1) * Gap;
            var left = (ActualWidth - rowWidth) / 2 + (i - row * columnsUsed) * (cardWidth + Gap);
            var y = top + row * (cardHeight + Gap);

            var card = _live[i];
            card.Chrome.Width = cardWidth;
            card.Chrome.Height = cardHeight;
            Canvas.SetLeft(card.Chrome, left);
            Canvas.SetTop(card.Chrome, y);

            // Fit the window's own shape inside the preview area.
            var sourceAspect = card.Source.Width / card.Source.Height;
            var w = Math.Min(cardWidth, previewHeight * sourceAspect);
            var h = w / sourceAspect;
            card.Preview = new Rect(left + (cardWidth - w) / 2, y + TitleHeight + (previewHeight - h) / 2, w, h);
            UpdateThumbnail(card, 1);
        }
    }

    private void UpdateThumbnail(Card card, double opacity)
    {
        if (card.Thumbnail == IntPtr.Zero) return;
        var r = card.Preview;
        r.Offset(card.Offset.X, card.Offset.Y);
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = new RECT
            {
                Left = (int)Math.Round(r.Left * _scale),
                Top = (int)Math.Round(r.Top * _scale),
                Right = (int)Math.Round(r.Right * _scale),
                Bottom = (int)Math.Round(r.Bottom * _scale),
            },
            // DWM draws previews above everything, so they go while the shade is open.
            fVisible = _trayLayer.Visibility != Visibility.Visible,
            opacity = (byte)Math.Clamp(opacity * 255, 0, 255),
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(card.Thumbnail, ref props);
    }

    // ---- Tap and swipe ------------------------------------------------------------

    private void AttachSwipe(Card card)
    {
        Point? start = null;
        var moved = false;

        card.Chrome.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInsideCloseButton(source, card.Chrome)) return;
            start = e.GetPosition(this);
            moved = false;
            card.Chrome.CaptureMouse();
            e.Handled = true;
        };
        card.Chrome.MouseMove += (_, e) =>
        {
            if (start is not { } origin || !card.Chrome.IsMouseCaptured) return;
            var dy = e.GetPosition(this).Y - origin.Y;
            if (Math.Abs(dy) > 8) moved = true;
            if (!moved) return;
            // Only upwards moves the card; downwards resists.
            card.Offset.Y = dy < 0 ? dy : dy * 0.2;
            var fade = 1 - Math.Clamp(-card.Offset.Y / (CloseDistance * 2), 0, 0.8);
            card.Chrome.Opacity = fade;
            UpdateThumbnail(card, fade);
        };
        card.Chrome.MouseLeftButtonUp += (_, e) =>
        {
            if (start is null) return;
            start = null;
            card.Chrome.ReleaseMouseCapture();
            e.Handled = true;

            if (!moved)
            {
                Go(card);
                return;
            }
            if (-card.Offset.Y >= CloseDistance)
            {
                CloseCard(card);
                return;
            }
            card.Offset.Y = 0;
            card.Chrome.Opacity = 1;
            UpdateThumbnail(card, 1);
        };
        card.Chrome.LostMouseCapture += (_, _) =>
        {
            if (start is null) return;
            start = null;
            card.Offset.Y = 0;
            card.Chrome.Opacity = 1;
            UpdateThumbnail(card, 1);
        };
    }

    private static bool IsInsideCloseButton(DependencyObject source, DependencyObject chrome)
    {
        for (var node = source; node is not null && node != chrome; node = VisualTreeHelper.GetParent(node))
        {
            if (node is PressableBorder) return true;
        }
        return false;
    }

    private void Go(Card card)
    {
        var target = card.Window;
        HideSwitcher();
        WindowTools.Activate(target);
    }

    private void CloseCard(Card card)
    {
        if (!WindowTools.Close(card.Window))
        {
            Log.Write($"tablet: could not close '{WindowTools.Title(card.Window)}' (elevated?)");
            card.Offset.Y = 0;
            card.Chrome.Opacity = 1;
            UpdateThumbnail(card, 1);
            return;
        }
        RemoveCard(card);
        Layout();
        // The app may ask to save first, or refuse; look again shortly.
        _refresh.Stop();
        _refresh.Start();
    }

    private void CloseAll()
    {
        foreach (var card in _live.ToList())
        {
            if (WindowTools.Close(card.Window)) RemoveCard(card);
        }
        Layout();
        _refresh.Stop();
        _refresh.Start();
        if (_live.Count == 0) _empty.Visibility = Visibility.Visible;
    }

    private void RemoveCard(Card card)
    {
        if (card.Thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(card.Thumbnail);
        card.Thumbnail = IntPtr.Zero;
        _cards.Children.Remove(card.Chrome);
        _live.Remove(card);
    }

    private void ClearCards()
    {
        foreach (var card in _live.ToList()) RemoveCard(card);
        _cards.Children.Clear();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        ClearCards();
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideSwitcher();
        }
        base.OnClosing(e);
    }

    // ---- Tray shade -----------------------------------------------------------------

    private static TextBlock SpacedLabel(string text)
    {
        var label = StartStyle.Label(text, 14, weight: FontWeights.SemiBold);
        label.Margin = new Thickness(8, 0, 0, 1);
        return label;
    }

    /// <summary>Recents with the tray shade already down.</summary>
    public void ShowWithTray()
    {
        _trayWasOpen = true;
        if (IsVisible) OpenTray();
        else ShowSwitcher();
    }

    private void ToggleTray()
    {
        if (_trayLayer.Visibility == Visibility.Visible) CloseTray();
        else OpenTray();
    }

    private async void OpenTray()
    {
        _trayWasOpen = true;
        _trayLayer.Visibility = Visibility.Visible;
        _trayArrow.Text = StartStyle.Glyph(0xE70E);
        RefreshThumbnails();
        _trayPanel.ShowLoading(_lastTray);

        var generation = ++_trayGeneration;
        _trayBusy = true;
        try
        {
            var icons = await TrayIcons.ReadAsync(includeHidden: true).ConfigureAwait(true);
            if (generation != _trayGeneration) return;
            _lastTray = icons;
            _trayPanel.ShowIcons(icons);
        }
        catch (Exception ex)
        {
            Log.Error("tray read", ex);
            if (generation == _trayGeneration) _trayPanel.ShowError();
        }
        finally
        {
            if (generation == _trayGeneration && IsVisible)
            {
                // Reading raised the taskbar and gave it focus; take it back.
                StartTrigger.ClaimForegroundRight();
                Activate();
                SetForegroundWindow(_hwnd);
                _ = Dispatcher.BeginInvoke(() => _trayBusy = false, DispatcherPriority.Background);
            }
        }
    }

    private void CloseTray()
    {
        _trayGeneration++;
        _trayWasOpen = false;
        _trayLayer.Visibility = Visibility.Collapsed;
        _trayArrow.Text = StartStyle.Glyph(0xE70D);
        RefreshThumbnails();
        _ = Dispatcher.BeginInvoke(() => _trayBusy = false, DispatcherPriority.Background);
    }

    private void RefreshThumbnails()
    {
        foreach (var card in _live) UpdateThumbnail(card, 1);
    }

    /// <summary>Recents goes away first, so the app's window or menu is what shows.</summary>
    private async void OnTrayChosen(TrayIcon icon, bool menu)
    {
        HideSwitcher();
        try
        {
            if (!await TrayIcons.ActivateAsync(icon, menu).ConfigureAwait(true))
                Log.Write($"tray: '{icon.Title}' could not be {(menu ? "opened for its menu" : "clicked")}");
        }
        catch (Exception ex)
        {
            Log.Error("tray activate", ex);
        }
    }

    // ---- Native -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    private const uint DWM_TNP_RECTDESTINATION = 0x1;
    private const uint DWM_TNP_OPACITY = 0x4;
    private const uint DWM_TNP_VISIBLE = 0x8;
    private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WindowTools.POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
