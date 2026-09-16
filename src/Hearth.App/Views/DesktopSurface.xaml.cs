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
    private Point _virtualOrigin;
    private bool _detached;

    /// <summary>Everything needed to lay out one display.</summary>
    private sealed record MonitorSurface(MonitorInfo Info, Rect LocalBounds, double Scale)
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
    }

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

        TrySaveLayout();
    }

    /// <summary>Rebuilds after an Explorer restart or a display change.</summary>
    internal void Rebuild() => RebuildEverything();

    // ---- Monitors and wallpaper -----------------------------------------

    private void BuildSurfaces()
    {
        _surfaces.Clear();
        WallpaperCanvas.Children.Clear();

        _virtualOrigin = new Point(
            Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
            Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN));

        var position = _wallpaper.GetPosition();
        var background = _wallpaper.GetBackgroundColor();

        // Primary display first. IDesktopWallpaper returns monitors in device
        // order, which is not screen order — here it hands back the secondary
        // panel first, so everything would be laid out on the wrong display.
        // Windows guarantees the primary monitor's top-left is the virtual
        // origin, which makes it identifiable without another API call.
        var monitors = _wallpaper.GetMonitors()
            .OrderByDescending(m => m.Bounds.X == 0 && m.Bounds.Y == 0)
            .ThenBy(m => m.Bounds.Y)
            .ThenBy(m => m.Bounds.X)
            .ToList();

        foreach (var monitor in monitors)
        {
            var local = new Rect(
                monitor.Bounds.X - _virtualOrigin.X,
                monitor.Bounds.Y - _virtualOrigin.Y,
                monitor.Bounds.Width,
                monitor.Bounds.Height);

            var surface = new MonitorSurface(monitor, local, GetScaleFor(monitor.Bounds));
            ComputeGrid(surface);
            _surfaces.Add(surface);

            var plate = new Rectangle
            {
                Width = local.Width,
                Height = local.Height,
                Fill = _wallpaper.BuildBrush(monitor, position, background),
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

        foreach (var surface in _surfaces)
        {
            var wallpaperPath = surface.Info.WallpaperPath ?? "<none>";
            Log.Write($"surface {surface.Info.DeviceId}: local={surface.LocalBounds}, " +
                      $"scale={surface.Scale:F2}, wallpaper={wallpaperPath}");
        }
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

        surface.Columns = Math.Max(1, (int)((surface.LocalBounds.Width - edge * 2 + gap) / surface.CellWidth));
        surface.Rows = Math.Max(1, (int)((surface.LocalBounds.Height - edge * 2 + gap) / surface.CellHeight));

        // Centre the leftover space so the grid does not hug one edge.
        var usedWidth = surface.Columns * surface.CellWidth - gap;
        var usedHeight = surface.Rows * surface.CellHeight - gap;
        surface.PadX = edge + Math.Max(0, (surface.LocalBounds.Width - edge * 2 - usedWidth) / 2);
        surface.PadY = edge + (settings.Style == HomeStyle.Ios
            ? 0
            : Math.Max(0, (surface.LocalBounds.Height - edge * 2 - usedHeight) / 2));
    }

    // ---- Items ----------------------------------------------------------

    private async Task RefreshItemsAsync()
    {
        var settings = App.Settings;

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

                if (settings.IncludeInstalledApps)
                {
                    var apps = _appsCatalog.Enumerate();
                    Log.Write($"apps catalog: {apps.Count} app(s)");
                    list.AddRange(apps);
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

        _items.Clear();
        foreach (var item in discovered) _items[item.Id] = item;

        RelayoutTiles();
    }

    private void RelayoutTiles()
    {
        if (_surfaces.Count == 0) return;

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
        liveIds.AddRange(_layout.Monitors
            .SelectMany(m => m.Placements)
            .Select(p => p.ItemId)
            .Where(id => WidgetRegistry.FromPlacementId(id) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var displays = _surfaces
            .Select(sf => (sf.Info.DeviceId, sf.Columns, sf.Rows))
            .ToList();

        // Unlocked widgets store pixel bounds; the cells they cover depend on
        // the current grid, so recompute before anything is placed around them.
        foreach (var surface in _surfaces)
        {
            foreach (var placement in _layout.ForMonitor(surface.Info.DeviceId).Placements.Where(p => p.Unlocked))
                UpdateCoveredCells(placement, surface);
        }

        if (_layout.Reconcile(liveIds, displays, out var unplaced))
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

        Log.Write($"relayout: {_tiles.Count} tile(s) across {_surfaces.Count} display(s), " +
                  $"{unplaced} item(s) did not fit");
    }

    private FrameworkElement PlaceWidget(IWidget widget, GridPlacement placement, MonitorSurface surface)
    {
        var bounds = WidgetBounds(placement, surface);

        var view = widget.CreateView(new WidgetContext
        {
            PixelSize = bounds.Size,
            Scale = surface.Scale,
            DarkTheme = App.Settings.DarkTheme,
        });

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
        var removed = _layout.Monitors.Sum(m => m.Placements.RemoveAll(p => p.ItemId == id));
        if (removed == 0) return;

        TrySaveLayout();
        RelayoutTiles();
    }

    internal bool HasWidget(IWidget widget)
    {
        var id = WidgetRegistry.ToPlacementId(widget);
        return _layout.Monitors.Any(m => m.Find(id) is not null);
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
        options = options with { Fit = _layout.FitFor(item.Id) };

        var tile = new IconTile
        {
            Item = item,
            IconSize = App.Settings.IconSize * surface.Scale,
            ShowLabel = App.Settings.ShowLabels,
            ToolTip = item.DisplayName,
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
                var icon = await App.Icons.GetAsync(item, options with { Fit = _layout.FitFor(id) }).ConfigureAwait(true);
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
