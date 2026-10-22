using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Hearth.App.Controls;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Interop;
using Hearth.Core.Layout;
using Hearth.Core.Settings;
using Hearth.Core.Shell;
using Hearth.Core.Threading;
using Hearth.App.Widgets;
using Hearth.Core.Wallpaper;

namespace Hearth.App.Views;

/// <summary>
/// The home screen itself: one window spanning every display, living inside
/// the wallpaper layer.
///
/// All layout maths is done in virtual-screen physical pixels rather than
/// WPF's device-independent units. The window spans monitors that may run at
/// different scale factors, so there is no single DIP-to-pixel ratio that
/// would be correct everywhere; working in raw pixels and scaling per monitor
/// is the only arrangement that stays sharp on a mixed-DPI setup.
/// </summary>
public partial class DesktopSurface : UserControl
{
    private readonly DesktopLayer _layer;
    private readonly WallpaperService _wallpaper = new();
    private readonly DesktopItemCatalog _desktopCatalog = new();
    private readonly AppsFolderCatalog _appsCatalog = new();

    private HomeLayout _layout = new();
    private readonly Dictionary<string, LauncherItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IconTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MonitorSurface> _tileSurfaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Widget views on the canvas, keyed back to their placement id.</summary>
    private readonly Dictionary<FrameworkElement, string> _widgetViews = [];
    private readonly List<MonitorSurface> _surfaces = [];

    private IDisposable? _desktopWatcher;
    private WallpaperWatcher? _wallpaperWatcher;

    /// <summary>Set once the catalogs have been read, so nothing lays out against an empty item set.</summary>
    private bool _itemsLoaded;
    private string _wallpaperSignature = string.Empty;
    private Point _virtualOrigin;
    private bool _detached;

    /// <summary>Everything needed to lay out one display.</summary>
    ///
    /// <remarks>
    /// <c>LocalBounds</c> is the whole display, which is what the wallpaper is
    /// painted over. <c>WorkBounds</c> is what is left after the docked bars —
    /// the Windows taskbar, Hearth's own bar, anyone else's app bar — and is
    /// what the icon grid is laid out in. They used to be the same, so the
    /// bottom row of icons sat underneath whatever was docked there.
    /// </remarks>
    private sealed record MonitorSurface(MonitorInfo Info, Rect LocalBounds, Rect WorkBounds, double Scale)
    {
        public int Columns { get; set; }
        public int Rows { get; set; }
        public double CellWidth { get; set; }
        public double CellHeight { get; set; }
        public double PadX { get; set; }
        public double PadY { get; set; }
    }

    public DesktopSurface(DesktopLayer layer)
    {
        InitializeComponent();
        _layer = layer;

        // HwndSource sizes and lays out its RootVisual from the HWND's client
        // area, so there is no window size to chase here — only the DPI
        // compensation that puts RootGrid into physical-pixel units.
        Loaded += async (_, _) => await StartAsync().ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        ApplyRootScale();

        _layout = HomeLayout.Load();
        BuildSurfaces();

        if (App.Settings.HideShellIcons) _layer.HideShellIcons();

        await RefreshItemsAsync().ConfigureAwait(true);

        _desktopWatcher = _desktopCatalog.Watch(() =>
            Dispatcher.InvokeAsync(async () => await RefreshItemsAsync().ConfigureAwait(true)));

        // Hearth draws the background itself, so a change made in Settings or
        // by a slideshow would otherwise only show up on the next start.
        _wallpaperWatcher = new WallpaperWatcher(() => Dispatcher.BeginInvoke(RefreshWallpaper));

        App.Badges.Changed += OnBadgesChanged;
        _ = Task.Run(App.Badges.Refresh);

        Hearth.App.Hosting.DesktopHost.WallpaperEngineModeChanged += OnWallpaperEngineModeChanged;
    }

    /// <summary>Wallpaper Engine started or stopped: draw or stop drawing Hearth's own background.</summary>
    private void OnWallpaperEngineModeChanged(bool active) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_detached) return;
            PaintWallpaper();
        });

    /// <summary>
    /// Compensates for WPF's own DPI scaling so that one unit inside RootGrid
    /// is exactly one physical pixel.
    /// </summary>
    private void ApplyRootScale()
    {
        var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (scale <= 0) scale = 1;
        RootScale.ScaleX = 1.0 / scale;
        RootScale.ScaleY = 1.0 / scale;

        Log.Write($"root scale: dpiScale={scale:F2}, rootScale={RootScale.ScaleX:F3}");
    }

    public void DetachFromDesktop()
    {
        if (_detached) return;
        _detached = true;

        _desktopWatcher?.Dispose();
        _desktopWatcher = null;
        _wallpaperWatcher?.Dispose();
        _wallpaperWatcher = null;
        _workAreaTimer?.Stop();
        App.Badges.Changed -= OnBadgesChanged;
        Hearth.App.Hosting.DesktopHost.WallpaperEngineModeChanged -= OnWallpaperEngineModeChanged;

        TrySaveLayout();
    }

    /// <summary>Everything on the home screen that is not hidden, for the bar's desktop stack.</summary>
    internal IEnumerable<LauncherItem> VisibleItems() =>
        _items.Values.Where(i => !_layout.IsHidden(i.Id));

    /// <summary>Rebuilds after an Explorer restart or a display change.</summary>
    internal void Rebuild() => RebuildEverything();

    /// <summary>
    /// System broadcasts reach the host window, not this control; the
    /// wallpaper watcher wants to hear about WM_SETTINGCHANGE, which is what
    /// the Settings app sends after a background change.
    /// </summary>
    internal void OnSystemMessage(int message, IntPtr wParam)
    {
        _wallpaperWatcher?.OnWindowMessage(message, wParam);

        // A bar docking or undocking (Hearth's own, or anyone else's) changes
        // how much room the grid has, so the grid is worked out again.
        if (message == WM_SETTINGCHANGE && (int)wParam == SPI_SETWORKAREA) ScheduleWorkAreaRebuild();
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETWORKAREA = 0x002F;

    private System.Windows.Threading.DispatcherTimer? _workAreaTimer;

    /// <summary>
    /// Claiming a strip makes Windows broadcast several times over a second or
    /// two while it reflows every window, so the grid is rebuilt once at the
    /// end rather than on each one.
    /// </summary>
    private void ScheduleWorkAreaRebuild()
    {
        if (_workAreaTimer is null)
        {
            _workAreaTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _workAreaTimer.Tick += (_, _) =>
            {
                _workAreaTimer.Stop();
                if (_detached) return;
                Log.Write("work area changed; laying the grid out again");
                RebuildEverything();
            };
        }
        _workAreaTimer.Stop();
        _workAreaTimer.Start();
    }

    /// <summary>
    /// Repaints the background after the user changed it, without disturbing
    /// anything else on screen.
    ///
    /// The watcher is deliberately noisy - a slideshow rewrites the same
    /// registry values on every step, and the theme engine touches its cache
    /// for reasons of its own - so the signature decides whether there is
    /// really anything new to draw. A change to the displays themselves is
    /// handed to the full rebuild, because the grid depends on it.
    /// </summary>
    internal void RefreshWallpaper()
    {
        if (_detached) return;
        try
        {
            var signature = _wallpaper.Signature();
            if (signature == _wallpaperSignature) return;

            var monitors = SortedMonitors();
            var sameDisplays = monitors.Count == _surfaces.Count &&
                monitors.Zip(_surfaces).All(pair =>
                    pair.First.DeviceId == pair.Second.Info.DeviceId &&
                    pair.First.Bounds == pair.Second.Info.Bounds);

            if (!sameDisplays)
            {
                Log.Write("wallpaper: the displays changed with it; rebuilding");
                RebuildEverything();
                return;
            }

            // Same displays, new picture: swap each plate's brush and leave the
            // tiles, widgets and any open folder exactly where they are.
            for (var i = 0; i < _surfaces.Count; i++)
            {
                var kept = _surfaces[i];
                var replacement = new MonitorSurface(monitors[i], kept.LocalBounds, kept.WorkBounds, kept.Scale)
                {
                    Columns = kept.Columns,
                    Rows = kept.Rows,
                    CellWidth = kept.CellWidth,
                    CellHeight = kept.CellHeight,
                    PadX = kept.PadX,
                    PadY = kept.PadY,
                };
                _surfaces[i] = replacement;
                foreach (var key in _tileSurfaces.Where(e => ReferenceEquals(e.Value, kept)).Select(e => e.Key).ToList())
                    _tileSurfaces[key] = replacement;
            }
            PaintWallpaper();
            _wallpaperSignature = signature;
            Log.Write("wallpaper: repainted");
        }
        catch (Exception ex)
        {
            Log.Error("wallpaper refresh", ex);
        }
    }

    // ---- Monitors and wallpaper -----------------------------------------

    /// <summary>
    /// Primary display first. IDesktopWallpaper returns monitors in device
    /// order, which is not screen order - here it hands back the secondary
    /// panel first, so everything would be laid out on the wrong display.
    /// Windows guarantees the primary monitor's top-left is the virtual
    /// origin, which makes it identifiable without another API call.
    /// </summary>
    private List<MonitorInfo> SortedMonitors() => _wallpaper.GetMonitors()
        .OrderByDescending(m => m.Bounds.X == 0 && m.Bounds.Y == 0)
        .ThenBy(m => m.Bounds.Y)
        .ThenBy(m => m.Bounds.X)
        .ToList();

    private void BuildSurfaces()
    {
        _surfaces.Clear();

        _virtualOrigin = new Point(
            Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
            Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN));

        foreach (var monitor in SortedMonitors())
        {
            var local = new Rect(
                monitor.Bounds.X - _virtualOrigin.X,
                monitor.Bounds.Y - _virtualOrigin.Y,
                monitor.Bounds.Width,
                monitor.Bounds.Height);

            var work = WorkAreaFor(monitor.Bounds);
            var localWork = new Rect(
                work.X - _virtualOrigin.X,
                work.Y - _virtualOrigin.Y,
                work.Width,
                work.Height);

            var surface = new MonitorSurface(monitor, local, localWork, GetScaleFor(monitor.Bounds));
            ComputeGrid(surface);
            _surfaces.Add(surface);
        }

        PaintWallpaper();
        _wallpaperSignature = _wallpaper.Signature();

        foreach (var surface in _surfaces)
        {
            var wallpaperPath = surface.Info.WallpaperPath ?? "<none>";
            Log.Write($"surface {surface.Info.DeviceId}: local={surface.LocalBounds}, " +
                      $"scale={surface.Scale:F2}, wallpaper={wallpaperPath}");
        }
    }

    /// <summary>One rectangle per display, plus the scrim over all of them.</summary>
    private void PaintWallpaper()
    {
        WallpaperCanvas.Children.Clear();

        // Wallpaper Engine is drawing the background underneath Hearth: paint
        // nothing (no plates, no dim), so it shows through the transparent window.
        if (Hearth.App.Hosting.DesktopHost.WallpaperEngineActive)
        {
            ScrimRect.Fill = Brushes.Transparent;
            ScrimRect.Opacity = 0;
            return;
        }

        var position = _wallpaper.GetPosition();
        var background = _wallpaper.GetBackgroundColor();

        foreach (var surface in _surfaces)
        {
            var local = surface.LocalBounds;
            var plate = new Rectangle
            {
                Width = local.Width,
                Height = local.Height,
                Fill = _wallpaper.BuildBrush(surface.Info, position, background),
            };
            Canvas.SetLeft(plate, local.X);
            Canvas.SetTop(plate, local.Y);
            WallpaperCanvas.Children.Add(plate);
        }

        // Dim baked into the brush alpha rather than set as element Opacity:
        // non-unit Opacity on a full-surface element can make WPF render it
        // through an intermediate layer the size of every display combined.
        var dim = (byte)Math.Round(Math.Clamp(App.Settings.WallpaperDim, 0, 1) * 255);
        var scrim = new SolidColorBrush(Color.FromArgb(dim, 0, 0, 0));
        scrim.Freeze();
        ScrimRect.Fill = scrim;
        ScrimRect.Opacity = 1;
    }

    /// <summary>
    /// What is left of a display after everything docked to it. Falls back to
    /// the whole display if the monitor cannot be identified, which is no
    /// worse than the behaviour this replaced.
    /// </summary>
    private static Rect WorkAreaFor(Rect bounds)
    {
        var centre = new Win32.POINT(
            (int)(bounds.X + bounds.Width / 2),
            (int)(bounds.Y + bounds.Height / 2));

        var monitor = Win32.MonitorFromPoint(centre, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return bounds;

        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return bounds;

        var work = new Rect(
            info.rcWork.Left,
            info.rcWork.Top,
            Math.Max(1, info.rcWork.Right - info.rcWork.Left),
            Math.Max(1, info.rcWork.Bottom - info.rcWork.Top));

        // A work area that is not inside the display is not one worth trusting.
        work = bounds.Contains(work) || bounds.IntersectsWith(work) ? work : bounds;

        // An auto-hiding Hearth bar reserves nothing from the shell — that is
        // what spares every other window a resize each time it hides — but
        // the home screen is Hearth's own layer, not "every other window",
        // and the bar is a real strip of pixels the instant intellihide
        // reveals it. Left out of this, the grid laid tiles right up to the
        // bare edge, and a reveal covered whatever had landed there.
        if (Bar.BarService.Current is { IsRunning: true } bar && bar.UnreservedMarginFor(monitor) is { } margin)
        {
            var thickness = margin.Thickness * GetScaleFor(bounds);
            work = margin.Edge switch
            {
                ScreenEdge.Top => new Rect(work.X, work.Y + thickness, work.Width, Math.Max(1, work.Height - thickness)),
                ScreenEdge.Bottom => new Rect(work.X, work.Y, work.Width, Math.Max(1, work.Height - thickness)),
                ScreenEdge.Left => new Rect(work.X + thickness, work.Y, Math.Max(1, work.Width - thickness), work.Height),
                _ => new Rect(work.X, work.Y, Math.Max(1, work.Width - thickness), work.Height),
            };
        }

        return work;
    }

    /// <summary>Per-monitor DPI, so a 4K secondary display gets real pixels.</summary>
    private static double GetScaleFor(Rect bounds)
    {
        var centre = new Win32.POINT(
            (int)(bounds.X + bounds.Width / 2),
            (int)(bounds.Y + bounds.Height / 2));

        var monitor = Win32.MonitorFromPoint(centre, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 1.0;

        return Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            ? dpiX / 96.0
            : 1.0;
    }

    private static void ComputeGrid(MonitorSurface surface)
    {
        var settings = App.Settings;
        var scale = surface.Scale;

        var iconBox = settings.IconSize * (1 + 0.10 * 2) * scale;
        var tileWidth = Math.Max(iconBox, settings.IconSize * 1.6 * scale);
        var tileHeight = iconBox + (settings.ShowLabels ? (6 + 34) * scale : 0);

        var gap = settings.GridGap * scale;
        surface.CellWidth = tileWidth + gap;
        surface.CellHeight = tileHeight + gap;

        // iOS packs tight to the top-left; Android breathes a little more.
        var edge = (settings.Style == HomeStyle.Ios ? 20 : 32) * scale;

        var area = surface.WorkBounds;
        surface.Columns = Math.Max(1, (int)((area.Width - edge * 2 + gap) / surface.CellWidth));
        surface.Rows = Math.Max(1, (int)((area.Height - edge * 2 + gap) / surface.CellHeight));

        // Centre the leftover space so the grid does not hug one edge. The
        // padding is measured from the work area's own corner, which is what
        // keeps the first row clear of a bar docked at the top and the last
        // clear of one at the bottom.
        var usedWidth = surface.Columns * surface.CellWidth - gap;
        var usedHeight = surface.Rows * surface.CellHeight - gap;
        surface.PadX = (area.X - surface.LocalBounds.X) + edge +
                       Math.Max(0, (area.Width - edge * 2 - usedWidth) / 2);
        surface.PadY = (area.Y - surface.LocalBounds.Y) + edge + (settings.Style == HomeStyle.Ios
            ? 0
            : Math.Max(0, (area.Height - edge * 2 - usedHeight) / 2));
    }

    // ---- Items ----------------------------------------------------------

    private async Task RefreshItemsAsync()
    {
        var settings = App.Settings;

        // Read on this thread: the catalog work below runs on its own.
        var pinned = new HashSet<string>(_layout.Pinned, StringComparer.OrdinalIgnoreCase);
        var readApps = settings.IncludeInstalledApps || pinned.Count > 0 || _installedApps is null;
        IReadOnlyList<LauncherItem>? apps = null;

        List<LauncherItem> discovered;
        try
        {
            // StaTask, not Task.Run: the shell's COM objects are
            // apartment-threaded and the thread pool is MTA. Enumerating
            // AppsFolder from an MTA thread is what made "Show installed apps"
            // fail.
            discovered = await StaTask.Run(() =>
            {
                var list = new List<LauncherItem>(_desktopCatalog.Enumerate());
                Log.Write($"desktop catalog: {list.Count} item(s)");

                if (readApps)
                {
                    apps = _appsCatalog.Enumerate();
                    Log.Write($"apps catalog: {apps.Count} app(s), {pinned.Count} added by hand");
                    list.AddRange(settings.IncludeInstalledApps
                        ? apps
                        : apps.Where(app => pinned.Contains(app.Id)));
                }
                return list;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("RefreshItemsAsync", ex);
            MessageBox.Show($"Hearth could not read your apps:\n\n{ex.Message}",
                "Hearth", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (apps is not null)
        {
            _installedApps = apps;
            App.Apps.Update(apps);
        }

        _items.Clear();
        foreach (var item in discovered) _items[item.Id] = item;

        // Only now is the item set real; before this a relayout would hand
        // Reconcile an empty live set (see RelayoutTiles).
        if (_items.Count > 0) _itemsLoaded = true;

        RelayoutTiles();
        await ResolveAppIdsAsync().ConfigureAwait(true);
    }

    private void RelayoutTiles()
    {
        if (_surfaces.Count == 0) return;

        // Before the first catalog read there are no items, and laying out
        // with none of them would hand Reconcile an empty live set. Reconcile
        // guards against that too, but nothing good comes of getting there.
        if (!_itemsLoaded) return;

        ItemCanvas.Children.Clear();
        _tiles.Clear();
        _tileSurfaces.Clear();
        _widgetViews.Clear();

        // Widget placements have no catalog entry behind them, so they have to
        // be declared live explicitly or Reconcile would treat every one as a
        // vanished item and sweep it away.
        // Hidden items are simply not live: Reconcile takes them off the grid
        // and out of any folder, and puts them back when they are unhidden.
        var liveIds = _items.Keys.Where(id => !_layout.IsHidden(id)).ToList();
        // Every arrangement counts, so a widget on a display that is not
        // connected right now is carried over at its own size, not forgotten.
        liveIds.AddRange(_layout.AllPlacements
            .Select(p => p.ItemId)
            .Where(id => WidgetRegistry.FromPlacementId(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var displays = _surfaces
            .Select(sf => new DisplayGrid(sf.Info.DeviceId,
                (int)Math.Round(sf.LocalBounds.Width), (int)Math.Round(sf.LocalBounds.Height),
                sf.Columns, sf.Rows))
            .ToList();

        // Each set of displays (and grid size) has its own saved arrangement.
        // Switch first: the covered-cell pass below works on the active one.
        var switched = _layout.Activate(displays);
        if (switched) Log.Write($"arrangement: {_layout.ActiveArrangement}");

        // Unlocked widgets store pixel bounds; the cells they cover depend on
        // the current grid, so recompute before anything is placed around them.
        foreach (var surface in _surfaces)
        {
            foreach (var placement in _layout.ForMonitor(surface.Info.DeviceId).Placements.Where(p => p.Unlocked))
                UpdateCoveredCells(placement, surface);
        }

        if (_layout.Reconcile(liveIds, displays, out var unplaced) || switched)
            TrySaveLayout();

        foreach (var surface in _surfaces)
        {
            var monitorLayout = _layout.ForMonitor(surface.Info.DeviceId);
            var options = App.Settings.ToRenderOptions(surface.Scale);

            foreach (var placement in monitorLayout.Placements)
            {
                if (placement.ParentGroupId is not null) continue;

                if (WidgetRegistry.FromPlacementId(placement.ItemId) is { } widget)
                {
                    var view = PlaceWidget(widget, placement, surface);
                    _widgetViews[view] = placement.ItemId;
                    _tileSurfaces[placement.ItemId] = surface;
                    continue;
                }

                IconTile tile;
                if (_layout.FindGroupByPlacement(placement.ItemId) is { } group)
                {
                    tile = CreateFolderTile(group, surface, options);
                }
                else if (_items.TryGetValue(placement.ItemId, out var item))
                {
                    tile = CreateTile(item, surface, options);
                }
                else
                {
                    continue;
                }

                PositionTile(tile, surface, placement.Column, placement.Row);
                ItemCanvas.Children.Add(tile);
                _tiles[placement.ItemId] = tile;
                _tileSurfaces[placement.ItemId] = surface;
            }
        }

        ApplyBadges();

        Log.Write($"relayout: {_tiles.Count} tile(s) across {_surfaces.Count} display(s), " +
                  $"{unplaced} item(s) did not fit");
    }

    private FrameworkElement PlaceWidget(IWidget widget, GridPlacement placement, MonitorSurface surface)
    {
        var bounds = WidgetBounds(placement, surface);

        var context = new WidgetContext
        {
            PixelSize = bounds.Size,
            Scale = surface.Scale,
            DarkTheme = App.Settings.DarkTheme,
        };

        // A widget that fails to build leaves a gap rather than taking Hearth
        // (and the desktop icons it hides) down with it.
        FrameworkElement view;
        try
        {
            using var theme = WidgetChrome.Scope(context);
            view = widget.CreateView(context);
        }
        catch (Exception ex)
        {
            Log.Error($"widget {widget.Id}", ex);
            view = new Border();
        }

        var frame = new WidgetFrame(placement.ItemId, view, surface.Scale, placement.Unlocked)
        {
            Width = bounds.Width,
            Height = bounds.Height,
        };

        Canvas.SetLeft(frame, bounds.X);
        Canvas.SetTop(frame, bounds.Y);
        ItemCanvas.Children.Add(frame);
        return frame;
    }

    /// <summary>
    /// Where a widget is drawn, in canvas coordinates. Locked widgets span
    /// whole cells including the inter-cell gap, so they read as one surface
    /// rather than a row of tiles with holes punched between. Unlocked ones sit
    /// wherever they were put, kept on their display.
    /// </summary>
    private static Rect WidgetBounds(GridPlacement placement, MonitorSurface surface)
    {
        if (placement.Unlocked && placement.Free is { } free)
        {
            var width = Math.Min(free.Width, surface.LocalBounds.Width);
            var height = Math.Min(free.Height, surface.LocalBounds.Height);
            var x = Math.Clamp(free.X, 0, surface.LocalBounds.Width - width);
            var y = Math.Clamp(free.Y, 0, surface.LocalBounds.Height - height);
            return new Rect(surface.LocalBounds.X + x, surface.LocalBounds.Y + y, width, height);
        }

        var gap = App.Settings.GridGap * surface.Scale;
        return new Rect(
            surface.LocalBounds.X + surface.PadX + placement.Column * surface.CellWidth,
            surface.LocalBounds.Y + surface.PadY + placement.Row * surface.CellHeight,
            Math.Max(1, placement.ColumnSpan) * surface.CellWidth - gap,
            Math.Max(1, placement.RowSpan) * surface.CellHeight - gap);
    }

    /// <summary>
    /// Records which grid cells an unlocked widget covers, so icons keep out
    /// from under it. A cell counts as covered if the widget overlaps the part
    /// of the cell an icon would occupy (not the gap after it).
    /// </summary>
    private static void UpdateCoveredCells(GridPlacement placement, MonitorSurface surface)
    {
        if (placement.Free is not { } free) return;

        var gap = App.Settings.GridGap * surface.Scale;
        int firstColumn = -1, lastColumn = -1, firstRow = -1, lastRow = -1;

        for (var column = 0; column < surface.Columns; column++)
        {
            var left = surface.PadX + column * surface.CellWidth;
            if (free.X < left + surface.CellWidth - gap && free.X + free.Width > left)
            {
                if (firstColumn < 0) firstColumn = column;
                lastColumn = column;
            }
        }

        for (var row = 0; row < surface.Rows; row++)
        {
            var top = surface.PadY + row * surface.CellHeight;
            if (free.Y < top + surface.CellHeight - gap && free.Y + free.Height > top)
            {
                if (firstRow < 0) firstRow = row;
                lastRow = row;
            }
        }

        if (firstColumn < 0 || firstRow < 0)
        {
            // Sitting entirely in the margins: covers nothing.
            placement.Column = 0;
            placement.Row = 0;
            placement.ColumnSpan = 0;
            placement.RowSpan = 0;
            return;
        }

        placement.Column = firstColumn;
        placement.Row = firstRow;
        placement.ColumnSpan = lastColumn - firstColumn + 1;
        placement.RowSpan = lastRow - firstRow + 1;
    }

    /// <summary>
    /// Moves icons out from under a widget, each to the nearest free cell. If a
    /// display is full, the icon is left unplaced and Reconcile finds it room
    /// on another display.
    /// </summary>
    private static void EvictFromUnder(MonitorLayout layout, MonitorSurface surface, GridPlacement widget)
    {
        if (widget.ColumnSpan == 0 || widget.RowSpan == 0) return;

        var displaced = layout.Placements.Where(p =>
                !ReferenceEquals(p, widget) &&
                WidgetRegistry.FromPlacementId(p.ItemId) is null &&
                p.Column >= widget.Column && p.Column < widget.Column + widget.ColumnSpan &&
                p.Row >= widget.Row && p.Row < widget.Row + widget.RowSpan)
            .ToList();

        foreach (var icon in displaced) layout.Placements.Remove(icon);

        foreach (var icon in displaced)
        {
            var cell = NearestFreeCell(layout, surface, icon.Column, icon.Row);
            if (cell is null) continue;
            icon.Column = cell.Value.Column;
            icon.Row = cell.Value.Row;
            layout.Placements.Add(icon);
        }
    }

    private MonitorSurface? SurfaceFor(MonitorLayout layout) =>
        _surfaces.FirstOrDefault(sf =>
            string.Equals(sf.Info.DeviceId, layout.MonitorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drops a widget into the first cell run that fits it. Returns false when
    /// there is no room, which the caller reports rather than silently
    /// stacking widgets on top of each other.
    /// </summary>
    internal bool AddWidget(IWidget widget)
    {
        if (_surfaces.Count == 0) return false;

        var surface = _surfaces[0];
        var monitorLayout = _layout.ForMonitor(surface.Info.DeviceId);
        var id = WidgetRegistry.ToPlacementId(widget);

        if (monitorLayout.Find(id) is not null) return true; // already placed

        var (spanColumns, spanRows) = widget.DefaultSpan;

        for (var row = 0; row <= surface.Rows - spanRows; row++)
        {
            for (var column = 0; column <= surface.Columns - spanColumns; column++)
            {
                if (!IsRegionFree(monitorLayout, column, row, spanColumns, spanRows)) continue;

                monitorLayout.Placements.Add(new GridPlacement
                {
                    ItemId = id,
                    Column = column,
                    Row = row,
                    ColumnSpan = spanColumns,
                    RowSpan = spanRows,
                });

                TrySaveLayout();
                RelayoutTiles();
                return true;
            }
        }

        return false;
    }

    internal void RemoveWidget(IWidget widget)
    {
        var id = WidgetRegistry.ToPlacementId(widget);
        var removed = _layout.RemoveEverywhere(id);
        if (removed == 0) return;

        TrySaveLayout();
        RelayoutTiles();
    }

    internal bool HasWidget(IWidget widget)
    {
        var id = WidgetRegistry.ToPlacementId(widget);
        return _layout.AllPlacements.Any(p => p.ItemId == id);
    }

    private static bool IsRegionFree(MonitorLayout layout, int column, int row, int columns, int rows)
    {
        for (var y = row; y < row + rows; y++)
        {
            for (var x = column; x < column + columns; x++)
            {
                if (layout.IsOccupied(x, y)) return false;
            }
        }
        return true;
    }

    private IconTile CreateTile(LauncherItem item, MonitorSurface surface, IconRenderOptions options)
    {
        options = options with { Fit = _layout.FitFor(item.Id), Source = _layout.SourceFor(item.Id) };

        var tile = new IconTile
        {
            Item = item,
            IconSize = App.Settings.IconSize * surface.Scale,
            ShowLabel = App.Settings.ShowLabels,
            ToolTip = item.DisplayName,
            Badge = BadgeFor(item.Id),
        };

        // Icons stream in as they render; the grid is interactive immediately
        // rather than waiting on a few hundred shell extractions.
        _ = LoadIconAsync(tile, item, options);
        return tile;
    }

    private IconTile CreateFolderTile(GridGroup group, MonitorSurface surface, IconRenderOptions options)
    {
        var tile = new IconTile
        {
            Item = new LauncherItem
            {
                Id = group.PlacementId,
                DisplayName = group.Name,
                Kind = LauncherItemKind.Group,
            },
            IconSize = App.Settings.IconSize * surface.Scale,
            ShowLabel = App.Settings.ShowLabels,
            Shape = App.Settings.IconShape,
            ToolTip = group.Name,
        };

        _ = LoadFolderPreviewsAsync(tile, group, options);
        return tile;
    }

    private async Task LoadFolderPreviewsAsync(IconTile tile, GridGroup group, IconRenderOptions options)
    {
        var previews = new List<ImageSource>(4);
        foreach (var id in group.ItemIds.Take(4))
        {
            if (!_items.TryGetValue(id, out var item)) continue;
            try
            {
                var icon = await App.Icons.GetAsync(item, options with { Fit = _layout.FitFor(id), Source = _layout.SourceFor(id) }).ConfigureAwait(true);
                if (icon is not null) previews.Add(icon);
            }
            catch (Exception ex)
            {
                Log.Error($"folder preview '{item.DisplayName}'", ex);
            }
        }
        tile.Previews = previews;
    }

    /// <summary>Width a tile occupies at a given display scale.</summary>
    private static double TileWidth(double scale) =>
        Math.Max(App.Settings.IconSize * 1.2, App.Settings.IconSize * 1.6) * scale;

    private static double TileHeight(double scale) =>
        (App.Settings.IconSize * 1.2 + (App.Settings.ShowLabels ? 40 : 0)) * scale;

    private static async Task LoadIconAsync(IconTile tile, LauncherItem item, IconRenderOptions options)
    {
        try
        {
            var icon = await App.Icons.GetAsync(item, options).ConfigureAwait(true);
            if (icon is not null)
            {
                tile.IconSource = icon;
            }
            else
            {
                Log.Write($"  icon NULL for '{item.DisplayName}'");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"icon load '{item.DisplayName}'", ex);
        }
    }

    private static void PositionTile(IconTile tile, MonitorSurface surface, int column, int row)
    {
        var x = surface.LocalBounds.X + surface.PadX + column * surface.CellWidth;
        var y = surface.LocalBounds.Y + surface.PadY + row * surface.CellHeight;
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);
    }

    private void RebuildEverything()
    {
        BuildSurfaces();
        RelayoutTiles();
    }

    private void TrySaveLayout()
    {
        try { _layout.Save(); }
        catch (Exception ex) { Debug.WriteLine($"[Hearth] layout save failed: {ex.Message}"); }
    }
}
