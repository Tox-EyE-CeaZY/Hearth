using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Hearth.App.Controls;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Views.Start;

/// <summary>
/// Hearth's Start menu: a 4:3 panel on a Mica backdrop with a search box and
/// four views — Pages (pinned apps, paged), All apps, Categories and Widgets.
///
/// One instance lives for the whole session and is hidden rather than closed,
/// so opening it is instant; widget views are the exception, built when their
/// tab is shown and dropped when the menu hides so their timers stop.
/// </summary>
internal sealed partial class StartMenuWindow : Window
{
    /// <summary>Design size in DIPs; shrunk to fit small screens, always 4:3.</summary>
    private const double DesignWidth = 880;
    private const double DesignHeight = 660;

    public const string PagesTab = "pages";
    public const string AllAppsTab = "all";
    public const string CategoriesTab = "categories";
    public const string WidgetsTab = "widgets";

    private readonly StartLayout _layout;
    private readonly Grid _root;
    private readonly TextBox _search;
    private readonly TextBlock _searchHint;
    private readonly Grid _content;
    private readonly Dictionary<string, TabPill> _tabs = [];
    private readonly Dictionary<string, FrameworkElement> _views = [];
    private readonly TranslateTransform _openSlide = new();
    private readonly Grid _backdrop = new();
    private readonly Border _avatar = new();
    private IntPtr _heldTaskbar;

    private Dictionary<string, LauncherItem> _apps = new(StringComparer.OrdinalIgnoreCase);
    private string _activeTab = PagesTab;
    private Grid _foreground = null!;
    private bool _allowClose;
    private bool _hiding;

    /// <summary>Set while a dialog of ours (widget setup) has focus, so losing it does not close the menu.</summary>
    private bool _suppressHide;

    public StartMenuWindow(StartLayout layout)
    {
        _layout = layout;

        Title = "Start — Hearth";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = DesignWidth;
        Height = DesignHeight;
        Foreground = StartStyle.Text;
        FontFamily = StartStyle.Body;
        Resources = StartStyle.WindowResources();

        // The frame is extended over the whole window so the system backdrop
        // shows through; the client area itself paints nothing.
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // ---- search -----------------------------------------------------
        _search = new TextBox
        {
            Background = StartStyle.Panel,
            BorderBrush = StartStyle.CardEdge,
            Foreground = StartStyle.Text,
            CaretBrush = StartStyle.Text,
            SelectionBrush = StartStyle.Accent,
            FontSize = 14,
            Padding = new Thickness(40, 9, 12, 9),
        };
        _search.TextChanged += (_, _) => OnSearchChanged();
        _search.PreviewKeyDown += OnSearchKey;

        _searchHint = StartStyle.Label("Search apps, settings and files", 14, StartStyle.Faint);
        _searchHint.IsHitTestVisible = false;
        _searchHint.Margin = new Thickness(43, 0, 0, 0);

        var searchGlyph = StartStyle.GlyphText(StartStyle.Search, 14, StartStyle.Muted);
        searchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
        searchGlyph.Margin = new Thickness(15, 0, 0, 0);
        searchGlyph.IsHitTestVisible = false;

        var searchBox = new Grid { Margin = new Thickness(24, 22, 24, 0), Children = { _search, searchGlyph, _searchHint } };

        // ---- tabs -------------------------------------------------------
        var tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 14, 24, 8) };
        foreach (var (key, text) in new[]
                 {
                     (PagesTab, "Pages"), (AllAppsTab, "All apps"),
                     (CategoriesTab, "Categories"), (WidgetsTab, "Widgets"),
                 })
        {
            var pill = new TabPill(key, text);
            pill.Clicked += _ => ShowTab(key);
            _tabs[key] = pill;
            tabStrip.Children.Add(pill);
        }

        // ---- content ----------------------------------------------------
        _content = new Grid { Margin = new Thickness(24, 4, 24, 8), ClipToBounds = true };
        _views[PagesTab] = BuildPagesView();
        _views[AllAppsTab] = BuildAllAppsView();
        _views[CategoriesTab] = BuildCategoriesView();
        _views[WidgetsTab] = BuildWidgetsView();
        _views["search"] = BuildSearchView();
        foreach (var view in _views.Values)
        {
            view.Visibility = Visibility.Collapsed;
            _content.Children.Add(view);
        }

        // ---- footer -----------------------------------------------------
        var footer = BuildFooter();

        _root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        // The backdrop fills the window and stays put; everything else slides
        // in over it when the menu opens.
        var foreground = new Grid { RenderTransform = _openSlide, RowDefinitions = { } };
        foreach (var row in _root.RowDefinitions.ToList())
        {
            _root.RowDefinitions.Remove(row);
            foreground.RowDefinitions.Add(row);
        }
        Grid.SetRow(searchBox, 0);
        Grid.SetRow(tabStrip, 1);
        Grid.SetRow(_content, 2);
        Grid.SetRow(footer, 3);
        foreground.Children.Add(searchBox);
        foreground.Children.Add(tabStrip);
        foreground.Children.Add(_content);
        foreground.Children.Add(footer);
        _root.Children.Add(_backdrop);
        _root.Children.Add(foreground);
        _foreground = foreground;
        Content = _root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(OnHorizontalScroll);
            // Out of Alt+Tab, like the real Start menu.
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
            ApplyBackdropSetting();
        };

        Deactivated += (_, _) => HideMenuFor("deactivated", "");
        PreviewKeyDown += OnWindowKey;
        PreviewTextInput += OnWindowText;
        App.Badges.Changed += () => Dispatcher.InvokeAsync(RefreshBadges);
        App.Apps.Changed += () => { if (IsVisible) RefreshApps(); };

        ShowTab(PagesTab);
    }

    // ---- Showing and hiding ---------------------------------------------

    /// <summary>Opens on the display at <paramref name="screenPoint"/>, just above its taskbar.</summary>
    public async void ShowMenu(Point screenPoint)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) => marks.Add($"{what} {clock.ElapsedMilliseconds}");

        _hiding = false;
        _search.Text = string.Empty;
        _searchHint.Visibility = Visibility.Visible;
        ResetPages();
        Mark("pages");
        if (_openInEditMode)
        {
            _openInEditMode = false;
            SetEditing(true);
        }
        CloseCategory();
        _allScroll.ScrollToTop();
        _categoryCards.ScrollToTop();
        ShowTab(_activeTab);
        Mark("tabs");

        // Placed before it is shown, in physical pixels: WPF's own Left/Top are
        // DIPs of whichever display the window was last on, and positioning
        // after Show would flash the menu at its old spot.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var placement = PlaceOn(screenPoint);
        if (!IsVisible && placement is { } rect && App.Settings.StartBackdrop == StartBackdrop.Blur)
            CaptureBackdrop(rect);
        Mark("backdrop");

        HoldTaskbar(hwnd, placement?.Taskbar ?? IntPtr.Zero);

        if (!IsVisible) Show();
        Mark("show");
        StartTrigger.ClaimForegroundRight();
        Activate();
        SetForegroundWindow(hwnd);
        Mark("focus");

        // With the menu owned by an auto-hidden taskbar, giving the taskbar
        // focus for an instant brings it up, and ownership keeps it up until
        // the menu goes (measured: it stayed up throughout; without ownership
        // it slid away within a second). The hand-off waits on Explorer
        // (about 250 ms in real use), so it runs once the menu has drawn.
        if (_heldTaskbar != IntPtr.Zero)
        {
            _suppressHide = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (IsVisible && _heldTaskbar != IntPtr.Zero)
                    {
                        SetForegroundWindow(_heldTaskbar);
                        StartTrigger.ClaimForegroundRight();
                        SetForegroundWindow(hwnd);
                        _search.Focus();
                    }
                }
                finally
                {
                    // Deactivation from the hand-off arrives as queued
                    // messages; release the guard only after they are handled.
                    _ = Dispatcher.BeginInvoke(() => _suppressHide = false, System.Windows.Threading.DispatcherPriority.Background);
                }
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        _search.Focus();
        AnimateOpen();
        LoadAccountPicture();
        Mark("done");
        Log.Write($"start menu: shown, taskbar held={_heldTaskbar != IntPtr.Zero}, active={IsActive}; ms: {string.Join(", ", marks)}");
        _ = Dispatcher.BeginInvoke(() => Log.Write($"start menu: first frame idle at {clock.ElapsedMilliseconds} ms"),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var apps = await App.Apps.GetAsync().ConfigureAwait(true);
        if (apps.Count != _apps.Count || _apps.Count == 0) RefreshApps(apps);
        if (_activeTab == WidgetsTab) BuildWidgets();
    }

    /// <summary>
    /// Builds everything and lays it out once, off screen, so the first real
    /// open is as quick as later ones. Measured: a cold first open took about
    /// 670 ms to settle, a warm one 140 ms.
    /// </summary>
    public void Prewarm(IReadOnlyList<LauncherItem> apps)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        RefreshApps(apps);
        LoadAccountPicture();

        ShowActivated = false;
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, (int)DesignWidth, (int)DesignHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        Show();
        foreach (var view in _views.Values) view.Visibility = Visibility.Visible;
        _root.UpdateLayout();
        foreach (var (key, view) in _views) view.Visibility = key == _activeTab ? Visibility.Visible : Visibility.Collapsed;
        _root.UpdateLayout();
        Hide();
        ShowActivated = true;
    }

    public void HideMenu([System.Runtime.CompilerServices.CallerMemberName] string caller = "") => HideMenuFor(null, caller);

    private void HideMenuFor(string? reason, string caller)
    {
        if (_hiding || _suppressHide || !IsVisible) return;
        Log.Write($"start menu: hide ({reason ?? caller})");
        _hiding = true;
        ClearWidgets();
        ClearPages();
        CloseLetterJump();

        // Drop ownership first, or Windows hands focus to the taskbar as the
        // menu hides and the taskbar stays up.
        ReleaseTaskbar();
        Hide();
    }

    /// <summary>
    /// Called for every mouse press anywhere while the menu is open. Hearth's
    /// desktop never takes focus, so clicking it does not deactivate the menu;
    /// a press outside the menu (and outside Hearth's own popups) closes it.
    /// </summary>
    public void OnMousePressedAt(int x, int y)
    {
        if (!IsVisible || _suppressHide) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(hwnd, out var rect) && x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) return;

        // Context menus and tooltips are separate windows of this process.
        var under = WindowFromPoint(new POINT { X = x, Y = y });
        GetWindowThreadProcessId(under, out var owner);
        var ours = owner == Environment.ProcessId && under != DesktopHost.Current?.Handle;
        if (ours) return;

        HideMenuFor($"pressed outside at {x},{y}", "");
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideMenu();
        }
        base.OnClosing(e);
    }

    private readonly record struct Placement(Int32Rect Rect, double Scale, IntPtr Taskbar);

    /// <summary>
    /// Centres the menu above the taskbar of the display at the point, a
    /// Windows-sized gap clear of it. An auto-hidden taskbar reserves no space
    /// in the work area, so its shown height is measured from the window.
    /// </summary>
    private Placement? PlaceOn(Point screenPoint)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromPoint(new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var screen = info.rcMonitor;
        var work = info.rcWork;

        var taskbar = TaskbarOn(monitor);
        var barHeight = 0;
        var barAtTop = false;
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var bar))
        {
            barHeight = bar.Bottom - bar.Top;
            barAtTop = bar.Top <= screen.Top + 1 && bar.Bottom < screen.Bottom - barHeight;
        }

        var top = barAtTop ? Math.Max(work.Top, screen.Top + barHeight) : work.Top;
        var bottom = barAtTop ? work.Bottom : Math.Min(work.Bottom, screen.Bottom - barHeight);
        var areaWidth = work.Right - work.Left;
        var areaHeight = bottom - top;
        var gap = (int)Math.Round(12 * scale);

        // 4:3, at design size if it fits, with room for the gaps.
        var fit = Math.Min(1, Math.Min(areaWidth * 0.92 / (DesignWidth * scale), (areaHeight - gap * 2) / (DesignHeight * scale)));
        var width = (int)Math.Round(DesignWidth * scale * fit);
        var height = (int)Math.Round(DesignHeight * scale * fit);

        var x = work.Left + (areaWidth - width) / 2;
        var y = barAtTop ? top + gap : bottom - height - gap;

        // Content is laid out at design size and scaled down together, so the
        // grid never reflows into a different shape on a small screen.
        _root.LayoutTransform = fit < 1 ? new ScaleTransform(fit, fit) : Transform.Identity;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        return new Placement(new Int32Rect(x, y, width, height), scale * fit, taskbar);
    }

    /// <summary>The taskbar window on a display: the primary one, or a secondary one.</summary>
    private static IntPtr TaskbarOn(IntPtr monitor)
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST) == monitor) return primary;

        for (var bar = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
             bar != IntPtr.Zero;
             bar = FindWindowEx(IntPtr.Zero, bar, "Shell_SecondaryTrayWnd", null))
        {
            if (MonitorFromWindow(bar, MONITOR_DEFAULTTONEAREST) == monitor) return bar;
        }
        return primary;
    }

    // ---- Taskbar --------------------------------------------------------

    private static bool TaskbarAutoHides()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// Makes the menu an owned window of the taskbar, which is what keeps an
    /// auto-hidden taskbar up while the menu is open, as with Windows' Start.
    /// </summary>
    private void HoldTaskbar(IntPtr hwnd, IntPtr taskbar)
    {
        ReleaseTaskbar();
        if (taskbar == IntPtr.Zero || !TaskbarAutoHides()) return;
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, taskbar);
        _heldTaskbar = taskbar;
    }

    private void ReleaseTaskbar()
    {
        if (_heldTaskbar == IntPtr.Zero) return;
        SetWindowLongPtr(new WindowInteropHelper(this).Handle, GWLP_HWNDPARENT, IntPtr.Zero);
        _heldTaskbar = IntPtr.Zero;
    }

    // ---- Backdrop -------------------------------------------------------

    private void ApplyBackdropSetting()
    {
        var kind = App.Settings.StartBackdrop;
        var applied = DialogChrome.ApplyBackdrop(this, kind, dark: !StartStyle.IsLight);
        _backdrop.Children.Clear();
        _root.Background = kind != StartBackdrop.Blur && !applied ? StartStyle.Scrim : null;
    }

    private void CaptureBackdrop(Placement placement)
    {
        _backdrop.Children.Clear();
        const int marginDips = 60;
        var marginPixels = (int)Math.Round(marginDips * placement.Scale);
        var capture = StartBackdropCapture.Capture(placement.Rect, marginPixels);
        if (capture is null)
        {
            _backdrop.Children.Add(new System.Windows.Shapes.Rectangle { Fill = StartStyle.Scrim });
            return;
        }

        // The capture is in pixels of the display; the window lays out at
        // design size, so the margin is given in the same DIPs.
        _backdrop.Children.Add(StartBackdropCapture.Build(capture, marginDips, StartStyle.Tint));
    }

    // ---- Account picture --------------------------------------------------

    private bool _pictureLoaded;

    /// <summary>
    /// The Windows account picture, from the copies Windows keeps in
    /// C:\Users\Public\AccountPictures\{SID}. The initial stays if there is none.
    /// </summary>
    private void LoadAccountPicture()
    {
        if (_pictureLoaded) return;
        _pictureLoaded = true;
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return;
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } docs
                    ? System.IO.Path.GetDirectoryName(docs)!
                    : "C:" + System.IO.Path.DirectorySeparatorChar + "Users" + System.IO.Path.DirectorySeparatorChar + "Public",
                "AccountPictures", sid);
            if (!System.IO.Directory.Exists(folder)) return;

            var file = new System.IO.DirectoryInfo(folder).GetFiles("*-Image192.*")
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*-Image208.*"))
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*.jpg"))
                .Concat(new System.IO.DirectoryInfo(folder).GetFiles("*.png"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null) return;

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file.FullName);
            image.DecodePixelWidth = 96;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            _avatar.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _avatar.Child = null;
        }
        catch (Exception ex)
        {
            Log.Error("account picture", ex);
        }
    }

    private void AnimateOpen()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _openSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        _foreground.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });
    }

    // ---- Data -------------------------------------------------------------

    private void RefreshApps() => RefreshApps(App.Apps.Current);

    private void RefreshApps(IReadOnlyList<LauncherItem> apps)
    {
        _apps = apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        SeedPagesIfNeeded();
        RebuildPages();
        RebuildAllApps();
        RebuildCategories();
        if (_search.Text.Length > 0) OnSearchChanged();
    }

    private void RefreshBadges()
    {
        foreach (var tile in Descendants<StartTile>(_root)) tile.Tile.Badge = BadgeForTile(tile.Item);
    }

    private void SaveLayout()
    {
        try { _layout.Save(); }
        catch (Exception ex) { Log.Error("start layout save", ex); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    // ---- Tabs ---------------------------------------------------------------

    private void ShowTab(string key)
    {
        if (!_tabs.ContainsKey(key)) return;
        var leavingWidgets = _activeTab == WidgetsTab && key != WidgetsTab;
        _activeTab = key;
        foreach (var (tabKey, pill) in _tabs) pill.IsActive = tabKey == key;

        if (_search.Text.Length > 0) _search.Text = string.Empty;
        foreach (var (viewKey, view) in _views) view.Visibility = viewKey == key ? Visibility.Visible : Visibility.Collapsed;

        if (key != CategoriesTab) CloseCategory();
        if (leavingWidgets) ClearWidgets();
        if (key == WidgetsTab && IsVisible) BuildWidgets();
    }

    /// <summary>Opens on a given tab next time ("all", "pages", "categories", "widgets").</summary>
    public void SelectTab(string key)
    {
        if (key == "edit")
        {
            _activeTab = PagesTab;
            _openInEditMode = true;
            return;
        }
        if (_tabs.ContainsKey(key)) _activeTab = key;
    }

    private bool _openInEditMode;

    private void CycleTab(int step)
    {
        var keys = _tabs.Keys.ToList();
        var index = (keys.IndexOf(_activeTab) + step + keys.Count) % keys.Count;
        ShowTab(keys[index]);
    }

    // ---- Keyboard -------------------------------------------------------

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                if (_letterJump is { Visibility: Visibility.Visible }) CloseLetterJump();
                else if (CloseAppPicker()) { }
                else if (ClosePageFolder()) { }
                else if (_editing && _search.Text.Length == 0) SetEditing(false);
                else if (_search.Text.Length > 0) _search.Text = string.Empty;
                else if (_openCategory is not null) CloseCategory();
                else HideMenu();
                e.Handled = true;
                break;

            case Key.Tab when ctrl:
                CycleTab(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;

            case Key.PageDown or Key.PageUp when _activeTab == PagesTab && _search.Text.Length == 0:
                ChangePage(e.Key == Key.PageDown ? 1 : -1);
                e.Handled = true;
                break;

            case Key.Left or Key.Right when _activeTab == PagesTab && _search.Text.Length == 0 &&
                                            (Keyboard.FocusedElement is not TextBox || ReferenceEquals(Keyboard.FocusedElement, _search)):
                ChangePage(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Typing anywhere goes to the search box.</summary>
    private void OnWindowText(object sender, TextCompositionEventArgs e)
    {
        // Other text boxes (a folder name, the app picker) keep their typing.
        if (Keyboard.FocusedElement is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _search.Focus();
        _search.Text += e.Text;
        _search.CaretIndex = _search.Text.Length;
        e.Handled = true;
    }

    // ---- Launching and item menus --------------------------------------

    private void Launch(LauncherItem item)
    {
        HideMenu();
        if (!ShellLauncher.Launch(item)) Log.Write($"start: failed to launch '{item.DisplayName}'");
    }

    private void OpenTarget(string target)
    {
        HideMenu();
        if (!ShellLauncher.Open(target)) Log.Write($"start: failed to open '{target}'");
    }

    /// <summary>The right-click menu for any app or file shown in Start.</summary>
    private void ShowItemMenu(LauncherItem item, FrameworkElement anchor, Hearth.Core.Layout.GridGroup? inFolder = null)
    {
        var menu = StartStyle.NewMenu(anchor);

        if (item.Kind == LauncherItemKind.App && JumpListMenu.Add(menu, item.Id, () => HideMenu("jump list")))
            menu.Items.Add(new Separator());

        menu.Items.Add(Item("Open", () => Launch(item)));
        if (item.Kind is LauncherItemKind.App or LauncherItemKind.Shortcut)
            menu.Items.Add(Item("Run as administrator", () =>
            {
                HideMenu();
                ShellLauncher.RunAsAdministrator(item);
            }));
        if (item.FileSystemPath is not null)
            menu.Items.Add(Item("Open file location", () =>
            {
                HideMenu();
                ShellLauncher.OpenFileLocation(item);
            }));

        menu.Items.Add(new Separator());

        AddStartEntries(menu, item, inFolder);

        if (DesktopHost.Current?.Surface is { } desktop && item.Kind == LauncherItemKind.App)
        {
            var onHome = desktop.IsAppOnHome(item);
            menu.Items.Add(Item(onHome ? "Remove from desktop" : "Add to desktop", () => desktop.SetAppOnHome(item, !onHome)));
        }

        if (item.Kind == LauncherItemKind.App)
        {
            var current = CategoryOf(item);
            var move = new MenuItem { Header = "Category" };
            foreach (var category in AppCategorizer.All)
            {
                var entry = new MenuItem { Header = category, IsCheckable = true, IsChecked = category == current };
                entry.Click += (_, _) => SetCategory(item, category);
                move.Items.Add(entry);
            }
            menu.Items.Add(move);

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Uninstall...", () =>
            {
                HideMenu();
                ShellLauncher.OpenInstalledApps();
            }));
        }

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Footer ---------------------------------------------------------

    private FrameworkElement BuildFooter()
    {
        var name = UserInfo.DisplayName;
        var avatar = _avatar;
        avatar.Width = 30;
        avatar.Height = 30;
        avatar.CornerRadius = new CornerRadius(15);
        avatar.Background = StartStyle.Accent;
        avatar.Margin = new Thickness(0, 0, 10, 0);
        avatar.Child = new TextBlock
        {
            Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?",
            Foreground = StartStyle.OnAccent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var user = new PressableBorder(radius: 6)
        {
            Padding = new Thickness(8, 5, 12, 5),
            ToolTip = "Account settings",
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { avatar, StartStyle.Label(name, 13.5) } },
        };
        user.Clicked += _ => OpenTarget("ms-settings:yourinfo");

        var explorer = new ChromeButton(StartStyle.Folder, tooltip: "File Explorer", glyphSize: 16);
        explorer.Clicked += _ => OpenTarget("explorer.exe");

        var settings = new ChromeButton(StartStyle.Settings, tooltip: "Settings", glyphSize: 16);
        settings.Clicked += _ => OpenTarget("ms-settings:");

        var options = new ChromeButton(StartStyle.More, tooltip: "Start menu options", glyphSize: 16);
        options.Clicked += b => ShowOptionsMenu(b);

        var power = new ChromeButton(StartStyle.Power, tooltip: "Power", glyphSize: 16);
        power.Clicked += b => ShowPowerMenu(b);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { explorer, settings, options, power },
        };

        var dock = new DockPanel { Margin = new Thickness(16, 8, 16, 8), LastChildFill = false };
        DockPanel.SetDock(user, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(user);
        dock.Children.Add(right);

        return new Border
        {
            Background = StartStyle.Footer,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = dock,
        };
    }

    private void ShowPowerMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.Items.Add(Item("Lock", () => { HideMenu(); PowerActions.Lock(); }));
        menu.Items.Add(Item("Sleep", () => { HideMenu(); PowerActions.Sleep(); }));
        menu.Items.Add(Item("Sign out", () => { HideMenu(); PowerActions.SignOut(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Restart", () => { HideMenu(); PowerActions.Restart(); }));
        menu.Items.Add(Item("Shut down", () => { HideMenu(); PowerActions.ShutDown(); }));
        menu.IsOpen = true;
    }

    private void ShowOptionsMenu(FrameworkElement anchor)
    {
        var settings = App.Settings;
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;

        var backdrop = new MenuItem { Header = "Background" };
        foreach (var (kind, label) in new[]
                 {
                     (StartBackdrop.Blur, "Blur what's behind"),
                     (StartBackdrop.Mica, "Mica (wallpaper only)"),
                     (StartBackdrop.MicaAlt, "Mica Alt"),
                     (StartBackdrop.Acrylic, "Acrylic (needs transparency effects on)"),
                 })
        {
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = settings.StartBackdrop == kind };
            entry.Click += (_, _) =>
            {
                settings.StartBackdrop = kind;
                settings.Save();
                ApplyBackdropSetting();
                if (kind == StartBackdrop.Blur) HideMenu(); // the snapshot is taken as the menu opens
            };
            backdrop.Items.Add(entry);
        }
        menu.Items.Add(backdrop);

        var replace = new MenuItem { Header = "Open with the Windows key and Start button", IsCheckable = true, IsChecked = settings.ReplaceStartMenu };
        replace.Click += (_, _) => StartMenuController.Current?.SetReplaceStart(!settings.ReplaceStartMenu);
        menu.Items.Add(replace);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open Windows Start", () =>
        {
            HideMenu();
            StartMenuController.Current?.OpenWindowsStart();
        }));

        menu.IsOpen = true;
    }

    // ---- Native ---------------------------------------------------------

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
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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
    private const long ABS_AUTOHIDE = 1;
    private const int GWLP_HWNDPARENT = -8;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
}
