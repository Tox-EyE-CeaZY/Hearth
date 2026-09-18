using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Widgets;
using Hearth.Core.Diagnostics;
using Hearth.Core.Layout;
using Hearth.Core.Notifications;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Views.Start;

/// <summary>
/// Pages: Start's own home screens. Each page is a framed 7 x 4 grid holding
/// apps, folders and widgets; the first set is copied from the desktop, after
/// which the two are arranged independently.
///
/// Turning pages: the arrows, the dots, the mouse wheel, Left/Right and
/// PageUp/PageDown, or a horizontal swipe (touch, or mouse on empty space).
/// Arranging: drag a tile or a widget (by its handle) to a free cell; drop an
/// app onto another app to make a folder, or onto a folder to add it. Holding
/// a dragged item over an arrow turns the page, and past the last page makes
/// a new one.
/// </summary>
internal sealed partial class StartMenuWindow
{
    private const double SwipeThreshold = 70;
    private const double CellGap = 10;
    private const double PageIcon = 48;
    private const string DragFormat = "HearthStartPlacement";

    private Grid _pageHost = null!;
    private StackPanel _dots = null!;
    private ChromeButton _previous = null!;
    private ChromeButton _next = null!;
    private TextBlock _pagesEmpty = null!;
    private Grid _pageFolder = null!;
    private WrapPanel _pageFolderTiles = null!;
    private GridGroup? _openFolder;
    private FrameworkElement? _currentPage;
    private int _pageIndex;
    private DateTime _lastWheel;

    private Point? _swipeStart;
    private bool _swiping;
    private DispatcherTimer? _dragFlip;
    private Rectangle? _dropHint;

    private List<MonitorLayout> Pages => _layout.Pages ?? [];
    private int PageCount => Math.Max(1, Pages.Count);

    private static DesktopSurface? Desktop => DesktopHost.Current?.Surface;

    private FrameworkElement BuildPagesView()
    {
        _previous = PageArrow(StartStyle.ChevronLeft, "Previous page", -1);
        _next = PageArrow(StartStyle.ChevronRight, "Next page", 1);

        _pageHost = new Grid
        {
            ClipToBounds = true,
            Background = Brushes.Transparent,
            IsManipulationEnabled = true,
            Margin = new Thickness(4, 0, 4, 0),
        };
        _pageHost.MouseWheel += OnPageWheel;
        _pageHost.ManipulationStarting += OnManipulationStarting;
        _pageHost.ManipulationDelta += OnManipulationDelta;
        _pageHost.ManipulationCompleted += OnManipulationCompleted;
        _pageHost.PreviewMouseLeftButtonDown += OnSwipeMouseDown;
        _pageHost.PreviewMouseMove += OnSwipeMouseMove;
        _pageHost.PreviewMouseLeftButtonUp += OnSwipeMouseUp;
        _pageHost.SizeChanged += (_, e) =>
        {
            if (IsVisible && (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 1 ||
                              (_fullScreen && Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 1))) RebuildPages();
        };

        _pagesEmpty = StartStyle.Label("This page is empty. Drag apps here, or right-click an app anywhere in Start and choose Add to Start.",
            13, StartStyle.Muted);
        _pagesEmpty.TextWrapping = TextWrapping.Wrap;
        _pagesEmpty.TextAlignment = TextAlignment.Center;
        _pagesEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _pagesEmpty.MaxWidth = 360;
        _pagesEmpty.IsHitTestVisible = false;
        _pagesEmpty.Visibility = Visibility.Collapsed;

        _dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Height = 16,
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_previous, 0);
        Grid.SetColumn(_pageHost, 1);
        Grid.SetColumn(_pagesEmpty, 1);
        Grid.SetColumn(_next, 2);
        row.Children.Add(_previous);
        row.Children.Add(_pageHost);
        row.Children.Add(_pagesEmpty);
        row.Children.Add(_next);

        var footer = BuildPagesFooter();
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(row);

        return new Grid { Children = { dock, BuildPageFolder(), BuildAppPicker() } };
    }

    private ChromeButton PageArrow(string glyph, string tooltip, int step)
    {
        var arrow = new ChromeButton(glyph, tooltip: tooltip, glyphSize: 16)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 26, 6, 26),
            AllowDrop = true,
        };
        arrow.Clicked += _ => ChangePage(step);

        // Holding a dragged item here turns the page; past the last page it
        // opens a new one to drop onto.
        arrow.DragEnter += (_, e) =>
        {
            e.Effects = DragDropEffects.Move;
            _dragFlip?.Stop();
            _dragFlip = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _dragFlip.Tick += (_, _) =>
            {
                if (step > 0 && !HasNextSpread && Pages[^1].Placements.Count > 0)
                {
                    _layout.AddPage();
                    UpdatePageChrome();
                }
                ChangePage(step);
            };
            _dragFlip.Start();
        };
        arrow.DragLeave += (_, _) => _dragFlip?.Stop();
        arrow.Drop += (_, _) => _dragFlip?.Stop();
        return arrow;
    }

    // ---- First pages ------------------------------------------------------

    /// <summary>
    /// Once only: one page per desktop display, holding what that display
    /// holds, in reading order, each widget at its own size where it fits.
    /// Folders are copied, so the two can change independently afterwards.
    /// </summary>
    private void SeedPagesIfNeeded()
    {
        if (_layout.Pages is not null || Desktop is not { } desktop || _apps.Count == 0) return;

        _layout.Pages = [];
        foreach (var home in desktop.GetHomePages())
        {
            var start = _layout.Pages.Count;
            _layout.AddPage();
            foreach (var entry in home.Entries.OrderBy(e => Math.Floor(e.Y)).ThenBy(e => e.X))
            {
                string id;
                if (entry.Widget is { } widget) id = WidgetRegistry.ToPlacementId(widget);
                else if (entry.Group is { } group)
                {
                    var copy = new GridGroup { Id = Guid.NewGuid().ToString("N")[..12], Name = group.Name, ItemIds = [.. group.ItemIds] };
                    _layout.Groups.Add(copy);
                    id = copy.PlacementId;
                }
                else if (entry.Item is { } item) id = item.Id;
                else continue;

                // Widgets keep their shape, capped so one cannot swallow a page.
                var columns = entry.Widget is null ? 1 : Math.Clamp((int)Math.Round(entry.Width), 2, 4);
                var rows = entry.Widget is null ? 1 : Math.Clamp((int)Math.Round(entry.Height), 2, 3);
                _layout.Place(id, columns, rows, start);
            }
        }

        if (_layout.Pages.Count == 0) _layout.AddPage();
        Log.Write($"start pages: copied {_layout.Pages.Count} page(s) from the desktop");
        SaveLayout();
    }

    // ---- Building pages -----------------------------------------------------

    private void ResetPages()
    {
        _pageIndex = 0;
        _editing = false;
        _editToggle.Child = EditToggleContent();
        _editBar.Visibility = Visibility.Collapsed;
        CloseAppPicker();
        ClosePageFolder();
        RebuildPages();
    }

    private void RebuildPages()
    {
        if (_apps.Count > 0 && _layout.Prune(ItemExists)) SaveLayout();

        UpdateSpread();
        _pageIndex = SpreadStart(Math.Clamp(_pageIndex, 0, PageCount - 1));
        _pageHost.Children.Clear();
        _neighbour = null;
        _neighbourIndex = -1;
        _currentPage = BuildPage(_pageIndex);
        _pageHost.Children.Add(_currentPage);
        UpdatePageChrome();
        if (_openFolder is not null) OpenPageFolder(_openFolder);
    }

    private bool ItemExists(string id) => StartItems.Resolve(id, _apps) is not null;

    /// <summary>Removing the page unloads its widgets, which stops their timers.</summary>
    private void ClearPages()
    {
        _pageHost?.Children.Clear();
        _currentPage = null;
    }

    private void SavePages()
    {
        // Trailing empty pages go; the first page always stays.
        while (Pages.Count > 1 && Pages[^1].Placements.Count == 0 && _pageIndex < Pages.Count - 1) Pages.RemoveAt(Pages.Count - 1);
        SaveLayout();
        RebuildPages();
    }

    private Badge BadgeForTile(LauncherItem item) =>
        Desktop?.BadgeForItem(item.Id) ?? App.Badges.For(item.Id);

    private (double Width, double Height) PageSize() => _fullScreen
        ? SpreadPageSize()
        : (_pageHost.ActualWidth > 0 ? _pageHost.ActualWidth : 760,
           _pageHost.ActualHeight > 0 ? _pageHost.ActualHeight : 420);

    private (double Width, double Height) CellSize()
    {
        var (width, height) = PageSize();
        return ((width - CellGap) / StartLayout.Columns, (height - CellGap) / StartLayout.Rows);
    }

    private FrameworkElement BuildSinglePage(int index)
    {
        var (width, height) = PageSize();
        var (cellWidth, cellHeight) = CellSize();

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent,
            AllowDrop = true,
            Tag = index,
        };
        canvas.DragOver += OnPageDragOver;
        canvas.DragLeave += (_, _) => HideDropHint();
        canvas.Drop += OnPageDrop;
        canvas.MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource != canvas && e.OriginalSource is not Ellipse) return;
            e.Handled = true;
            ShowPageMenu(canvas);
        };

        // The frame, and a dot at each cell corner so the grid reads without
        // boxing every cell in.
        var frame = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(14),
            Background = StartStyle.Card,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(frame);
        if (_editing) AddEditCells(canvas, index < Pages.Count ? Pages[index] : null, cellWidth, cellHeight);
        else for (var c = 1; c < StartLayout.Columns; c++)
        {
            for (var r = 1; r < StartLayout.Rows; r++)
            {
                var dot = new Ellipse { Width = 3, Height = 3, Fill = StartStyle.Faint, Opacity = 0.5, IsHitTestVisible = false };
                Canvas.SetLeft(dot, CellGap / 2 + c * cellWidth - 1.5);
                Canvas.SetTop(dot, CellGap / 2 + r * cellHeight - 1.5);
                canvas.Children.Add(dot);
            }
        }

        var page = index < Pages.Count ? Pages[index] : null;

        foreach (var placement in page?.Placements.ToList() ?? [])
        {
            var element = BuildPlacement(placement, cellWidth, cellHeight);
            if (element is null) continue;
            Canvas.SetLeft(element, CellGap / 2 + placement.Column * cellWidth + CellGap / 2);
            Canvas.SetTop(element, CellGap / 2 + placement.Row * cellHeight + CellGap / 2);
            element.Width = Math.Max(1, placement.ColumnSpan * cellWidth - CellGap);
            element.Tag = placement.ItemId;
            element.Height = Math.Max(1, placement.RowSpan * cellHeight - CellGap);
            canvas.Children.Add(element);
        }

        return new Border { Child = canvas, RenderTransform = new TranslateTransform() };
    }

    private FrameworkElement? BuildPlacement(GridPlacement placement, double cellWidth, double cellHeight)
    {
        if (WidgetRegistry.FromPlacementId(placement.ItemId) is { } widget)
        {
            var size = new Size(placement.ColumnSpan * cellWidth - CellGap, placement.RowSpan * cellHeight - CellGap);
            var view = BuildPageWidget(widget, placement, size);
            return _editing ? WrapForEditing(view, placement, widget) : view;
        }

        FrameworkElement element;
        if (_layout.FindGroup(placement.ItemId) is { } group) element = BuildFolderTile(group);
        else if (StartItems.Resolve(placement.ItemId, _apps) is { } item) element = BuildPageTile(item, inFolder: null);
        else return null;

        if (_editing) return WrapForEditing(element, placement, widget: null);
        MakeDraggable(element, placement.ItemId);
        return element;
    }

    private StartTile BuildPageTile(LauncherItem item, GridGroup? inFolder)
    {
        var tile = new StartTile(item, PageIcon, showLabel: true, Desktop?.FitFor(item.Id) ?? Core.Icons.IconFit.Auto)
        {
            Padding = new Thickness(2, 6, 2, 2),
        };
        tile.Tile.Badge = BadgeForTile(item);
        tile.Clicked += _ =>
        {
            if (!_editing) Launch(item);
        };
        tile.RightClicked += t => ShowItemMenu(item, t, inFolder);
        return tile;
    }

    /// <summary>
    /// A widget at its span. Its own controls keep working, so it is moved by a
    /// handle in the corner, which also carries its menu.
    /// </summary>
    private FrameworkElement BuildPageWidget(IWidget widget, GridPlacement placement, Size size)
    {
        var view = StartWidgets.Create(widget, size, placement.ColumnSpan, placement.RowSpan);
        if (_editing) return view; // edit mode gives it its own controls

        var handle = new ChromeButton(StartStyle.Glyph(0xE759), tooltip: "Drag to move. Right-click for options.", glyphSize: 12)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 2, 2, 0),
            Background = StartStyle.Field,
            Opacity = 0,
        };
        handle.RightClicked += h => ShowWidgetMenu(widget, placement, h);
        handle.Clicked += h => ShowWidgetMenu(widget, placement, h);
        MakeDraggable(handle, placement.ItemId);

        var host = new Grid { Children = { view, handle } };
        host.MouseEnter += (_, _) => handle.Opacity = 1;
        host.MouseLeave += (_, _) => handle.Opacity = 0;
        return host;
    }

    /// <summary>A folder: the same preview tile as the desktop, opening in place.</summary>
    private StartTile BuildFolderTile(GridGroup group)
    {
        var folder = new LauncherItem { Id = group.PlacementId, DisplayName = group.Name, Kind = LauncherItemKind.Group };
        var tile = new StartTile(folder, PageIcon) { Padding = new Thickness(2, 6, 2, 2) };
        tile.Tile.Shape = App.Settings.IconShape;
        tile.Tile.FolderPadBrush = StartStyle.Field;
        var edge = new Pen(StartStyle.CardEdge, 1);
        edge.Freeze();
        tile.Tile.FolderPadEdge = edge;
        tile.Tile.Badge = FolderBadge(group);
        tile.Clicked += _ => OpenPageFolder(group);
        tile.RightClicked += t => ShowFolderMenu(group, t);
        tile.Loaded += async (_, _) =>
        {
            if (tile.Tile.Previews is not null) return;
            var previews = new List<ImageSource>(4);
            foreach (var item in FolderItems(group).Take(4))
            {
                try
                {
                    var options = StartStyle.IconOptions(tile, PageIcon) with { Fit = Desktop?.FitFor(item.Id) ?? Core.Icons.IconFit.Auto };
                    if (await App.Icons.GetAsync(item, options).ConfigureAwait(true) is { } icon) previews.Add(icon);
                }
                catch (Exception ex)
                {
                    Log.Error($"start folder preview '{item.DisplayName}'", ex);
                }
            }
            tile.Tile.Previews = previews;
        };
        return tile;
    }

    private IEnumerable<LauncherItem> FolderItems(GridGroup group) =>
        group.ItemIds.Select(id => StartItems.Resolve(id, _apps)).OfType<LauncherItem>();

    private Badge FolderBadge(GridGroup group)
    {
        var total = 0;
        var dot = false;
        foreach (var item in FolderItems(group))
        {
            var badge = BadgeForTile(item);
            total += badge.Count;
            dot |= badge.Dot;
        }
        return new Badge(total, dot && total == 0);
    }

    private void UpdatePageChrome()
    {
        var count = PageCount;
        var shown = _pageIndex < Pages.Count ? Pages[_pageIndex] : null;
        _pagesEmpty.Visibility = Spread == 1 && (shown is null || shown.Placements.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        _previous.Opacity = HasPreviousSpread ? 1 : 0.25;
        _next.Opacity = HasNextSpread ? 1 : 0.25;

        _dots.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            var page = i;
            var current = i >= _pageIndex && i < _pageIndex + Spread;
            var dot = new Rectangle
            {
                Width = current ? 18 : 7,
                Height = 7,
                RadiusX = 3.5,
                RadiusY = 3.5,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = current ? StartStyle.Accent : StartStyle.Faint,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = $"Page {i + 1}",
            };
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                GoToPage(page);
            };
            _dots.Children.Add(dot);
        }

        _deletePage.Visibility = Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddPageAndShow()
    {
        _layout.AddPage();
        SaveLayout();
        UpdatePageChrome();
        GoToPage(Pages.Count - 1);
    }

    // ---- Menus ----------------------------------------------------------------

    private void ShowPageMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);

        var widgets = new MenuItem { Header = "Add widget" };
        foreach (var widget in WidgetRegistry.All)
        {
            var entry = new MenuItem { Header = widget.Title };
            entry.Click += (_, _) => AddWidgetToPage(widget);
            widgets.Items.Add(entry);
        }
        menu.Items.Add(widgets);
        menu.Items.Add(Item("New page", AddPageAndShow));
        menu.Items.Add(Item(_editing ? "Done editing" : "Edit pages", () => SetEditing(!_editing)));

        var menuPage = PageIndexOf(anchor, _pageIndex);
        if (Pages.Count > 1 && menuPage < Pages.Count)
        {
            var page = Pages[menuPage];
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(page.Placements.Count == 0 ? "Delete this page" : "Delete this page (its items leave Start)", () =>
            {
                foreach (var placement in page.Placements)
                {
                    if (_layout.FindGroup(placement.ItemId) is { } group) _layout.Groups.Remove(group);
                }
                Pages.Remove(page);
                _pageIndex = Math.Max(0, _pageIndex - 1);
                SavePages();
            }));
        }

        menu.IsOpen = true;
    }

    private void AddWidgetToPage(IWidget widget)
    {
        if (widget is IConfigurableWidget { NeedsSetup: true } setup)
        {
            _suppressHide = true;
            Topmost = false;
            try
            {
                if (!setup.Configure()) return;
            }
            finally
            {
                _suppressHide = false;
                Topmost = true;
                Activate();
            }
        }

        var id = WidgetRegistry.ToPlacementId(widget);
        _layout.Pages?.ForEach(p => p.Placements.RemoveAll(x => x.ItemId == id));
        var (columns, rows) = widget.DefaultSpan;
        var placed = _layout.Place(id, Math.Min(columns, 4), Math.Min(rows, 3), _pageIndex);
        _pageIndex = Pages.FindIndex(p => p.Placements.Contains(placed));
        SavePages();
    }

    private void ShowWidgetMenu(IWidget widget, GridPlacement placement, FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);

        var size = new MenuItem { Header = "Size" };
        foreach (var (columns, rows) in new[] { (2, 2), (3, 2), (4, 2), (2, 3), (3, 3), (4, 3), (4, 4) })
        {
            var (minColumns, minRows) = widget.MinimumSpan;
            if (columns < minColumns || rows < minRows) continue;
            var entry = new MenuItem
            {
                Header = $"{columns} x {rows}",
                IsCheckable = true,
                IsChecked = placement.ColumnSpan == columns && placement.RowSpan == rows,
            };
            entry.Click += (_, _) => ResizeWidget(placement, columns, rows);
            size.Items.Add(entry);
        }
        menu.Items.Add(size);

        if (widget is IConfigurableWidget configurable)
        {
            menu.Items.Add(Item("Settings...", () =>
            {
                _suppressHide = true;
                Topmost = false;
                try { configurable.Configure(); }
                finally
                {
                    _suppressHide = false;
                    Topmost = true;
                    Activate();
                }
                RebuildPages();
            }));
        }

        menu.Items.Add(Item("Remove from Start", () =>
        {
            _layout.Remove(placement.ItemId);
            SavePages();
        }));
        menu.IsOpen = true;
    }

    /// <summary>Resizes in place if the cells are free, otherwise finds the widget room elsewhere.</summary>
    private void ResizeWidget(GridPlacement placement, int columns, int rows)
    {
        if (_layout.Locate(placement.ItemId) is not { } spot) return;
        var page = spot.Page;
        page.Placements.Remove(placement);

        var fits = placement.Column + columns <= StartLayout.Columns &&
                   placement.Row + rows <= StartLayout.Rows &&
                   page.IsRegionFree(placement.Column, placement.Row, columns, rows);
        if (fits)
        {
            placement.ColumnSpan = columns;
            placement.RowSpan = rows;
            page.Placements.Add(placement);
        }
        else
        {
            var moved = _layout.Place(placement.ItemId, columns, rows, Pages.IndexOf(page));
            _pageIndex = Pages.FindIndex(p => p.Placements.Contains(moved));
        }
        SavePages();
    }

    private void ShowFolderMenu(GridGroup group, FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Items.Add(Item("Open", () => OpenPageFolder(group)));
        menu.Items.Add(Item("Ungroup", () => Ungroup(group)));
        menu.Items.Add(Item("Remove from Start", () =>
        {
            _layout.Remove(group.PlacementId);
            SavePages();
        }));
        menu.IsOpen = true;
    }

    private void Ungroup(GridGroup group)
    {
        if (_layout.Locate(group.PlacementId) is not { } spot) return;
        var pageIndex = Pages.IndexOf(spot.Page);
        spot.Page.Placements.Remove(spot.Placement);
        _layout.Groups.Remove(group);
        foreach (var id in group.ItemIds) _layout.Place(id, fromPage: pageIndex);
        ClosePageFolder();
        SavePages();
    }

    /// <summary>Start's own entries for the item menu.</summary>
    private void AddStartEntries(ContextMenu menu, LauncherItem item, GridGroup? inFolder)
    {
        if (inFolder is not null)
        {
            menu.Items.Add(Item("Remove from folder", () =>
            {
                inFolder.ItemIds.RemoveAll(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
                var pageIndex = _layout.Locate(inFolder.PlacementId) is { } spot ? Pages.IndexOf(spot.Page) : 0;
                _layout.Place(item.Id, fromPage: Math.Max(0, pageIndex));
                _layout.Tidy(inFolder);
                if (!_layout.Groups.Contains(inFolder)) ClosePageFolder();
                SavePages();
            }));
        }

        // Folders are made here rather than by dropping one app on another,
        // which on a full page made folders by accident.
        if (_layout.Locate(item.Id) is { } spot)
        {
            var folders = new MenuItem { Header = "Move to folder" };
            folders.Items.Add(Item("New folder", () =>
            {
                var group = new GridGroup { Id = Guid.NewGuid().ToString("N")[..12], Name = "Folder", ItemIds = [item.Id] };
                _layout.Groups.Add(group);
                spot.Placement.ItemId = group.PlacementId;
                SaveLayout();
                RebuildPages();
                OpenPageFolder(group);
                _folderName.Focus();
                _folderName.SelectAll();
            }));
            foreach (var group in _layout.Groups.Where(g => _layout.Locate(g.PlacementId) is not null))
            {
                folders.Items.Add(Item(group.Name, () =>
                {
                    spot.Page.Placements.Remove(spot.Placement);
                    if (!group.ItemIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) group.ItemIds.Add(item.Id);
                    SavePages();
                }));
            }
            menu.Items.Add(folders);
        }

        if (_layout.Contains(item.Id))
        {
            menu.Items.Add(Item("Remove from Start", () =>
            {
                _layout.Remove(item.Id);
                SavePages();
            }));
        }
        else
        {
            menu.Items.Add(Item("Add to Start", () =>
            {
                _layout.Place(item.Id, fromPage: _pageIndex);
                SaveLayout();
                RebuildPages();
            }));
        }
    }

    // ---- Drag and drop --------------------------------------------------------

    private void MakeDraggable(FrameworkElement element, string placementId)
    {
        Point? pressed = null;
        element.PreviewMouseLeftButtonDown += (_, e) => pressed = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(element);
            if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            pressed = null;
            _swipeStart = null; // a drag is not a swipe
            element.Opacity = 0.4;
            try
            {
                DragDrop.DoDragDrop(element, new DataObject(DragFormat, placementId), DragDropEffects.Move);
            }
            finally
            {
                element.Opacity = 1;
                HideDropHint();
                _dragFlip?.Stop();
            }
            e.Handled = true;
        };
    }

    /// <summary>The cell under the pointer, and the cells the dragged item would cover.</summary>
    private (int Column, int Row, GridPlacement? Dragged)? DropTarget(Canvas canvas, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string id) return null;
        var (cellWidth, cellHeight) = CellSize();
        var point = e.GetPosition(canvas);
        var dragged = _layout.Locate(id)?.Placement;

        // Multi-cell items are held by their top-left handle area; aim the
        // item's top-left at the pointer's cell, kept on the page.
        var column = (int)Math.Floor((point.X - CellGap / 2) / cellWidth);
        var row = (int)Math.Floor((point.Y - CellGap / 2) / cellHeight);
        var spanColumns = dragged?.ColumnSpan ?? 1;
        var spanRows = dragged?.RowSpan ?? 1;
        if (spanColumns > 1) column -= spanColumns - 1;
        column = Math.Clamp(column, 0, StartLayout.Columns - spanColumns);
        row = Math.Clamp(row, 0, StartLayout.Rows - spanRows);
        return (column, row, dragged);
    }

    /// <summary>What a drop at the pointer would do.</summary>
    private enum DropKind { None, Move, Swap }

    /// <summary>The item a swap would displace, found among the page's placements.</summary>
    private GridPlacement? SwapPartner(MonitorLayout page, string draggedId, int column, int row, int columns, int rows)
    {
        // Everything overlapping the target cells, other than the dragged item.
        var overlapping = page.Placements.Where(p =>
            p.ItemId != draggedId &&
            p.Column < column + columns && column < p.Column + p.ColumnSpan &&
            p.Row < row + rows && row < p.Row + p.RowSpan).ToList();

        // A swap needs exactly one item of the same size, sitting exactly there.
        return overlapping is [var only] &&
               only.Column == column && only.Row == row &&
               only.ColumnSpan == columns && only.RowSpan == rows
            ? only
            : null;
    }

    private (DropKind Kind, GridPlacement? Partner) ClassifyDrop(int pageIndex, string id, int column, int row, GridPlacement? dragged)
    {
        if (pageIndex >= Pages.Count) return (DropKind.None, null);
        var page = Pages[pageIndex];
        var columns = dragged?.ColumnSpan ?? 1;
        var rows = dragged?.RowSpan ?? 1;

        var free = true;
        for (var r = row; r < row + rows && free; r++)
        {
            for (var c = column; c < column + columns && free; c++)
            {
                if (page.IsOccupied(c, r, id)) free = false;
            }
        }
        if (free) return (DropKind.Move, null);

        // Swapping needs somewhere to send the other item: where the dragged one came from.
        if (dragged is null) return (DropKind.None, null);
        var partner = SwapPartner(page, id, column, row, columns, rows);
        return partner is null ? (DropKind.None, null) : (DropKind.Swap, partner);
    }

    // ---- Swap preview: the displaced item slides to where the dragged one was.

    private FrameworkElement? _swapPreview;
    private Point _swapHome;

    private void PreviewSwap(Canvas canvas, GridPlacement? partner, GridPlacement? dragged)
    {
        var element = partner is null ? null : canvas.Children.OfType<FrameworkElement>().FirstOrDefault(e => Equals(e.Tag, partner.ItemId));
        if (ReferenceEquals(element, _swapPreview)) return;
        EndSwapPreview();
        if (element is null || dragged is null) return;

        var (cellWidth, cellHeight) = CellSize();
        var draggedOnThisPage = _layout.Locate(dragged.ItemId)?.Page == Pages[PageIndexOf(canvas, _pageIndex)];

        _swapPreview = element;
        _swapHome = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        if (draggedOnThisPage)
        {
            element.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(CellGap + dragged.Column * cellWidth, duration) { EasingFunction = ease });
            element.BeginAnimation(Canvas.TopProperty,
                new DoubleAnimation(CellGap + dragged.Row * cellHeight, duration) { EasingFunction = ease });
        }
        else
        {
            // Going to another page: it fades to show it will leave this one.
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, duration));
        }
    }

    private void EndSwapPreview()
    {
        if (_swapPreview is not { } element) return;
        _swapPreview = null;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        element.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(_swapHome.X, duration) { EasingFunction = ease });
        element.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(_swapHome.Y, duration) { EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
    }

    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var canvas = (Canvas)sender;
        if (DropTarget(canvas, e) is not { } target || e.Data.GetData(DragFormat) is not string id)
        {
            e.Effects = DragDropEffects.None;
            HideDropHint();
            return;
        }

        var (kind, partner) = ClassifyDrop(PageIndexOf(canvas, _pageIndex), id, target.Column, target.Row, target.Dragged);
        if (kind == DropKind.None)
        {
            e.Effects = DragDropEffects.None;
            HideDropHint();
            return;
        }

        e.Effects = DragDropEffects.Move;
        PreviewSwap(canvas, partner, target.Dragged);

        var (cellWidth, cellHeight) = CellSize();
        var columns = target.Dragged?.ColumnSpan ?? 1;
        var rows = target.Dragged?.RowSpan ?? 1;

        _dropHint ??= new Rectangle
        {
            RadiusX = 10,
            RadiusY = 10,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        _dropHint.Stroke = StartStyle.Accent;
        _dropHint.Fill = StartStyle.Selected;
        _dropHint.StrokeDashArray = kind == DropKind.Swap ? [3, 2] : null;
        _dropHint.Width = columns * cellWidth - CellGap;
        _dropHint.Height = rows * cellHeight - CellGap;
        Canvas.SetLeft(_dropHint, CellGap + target.Column * cellWidth);
        Canvas.SetTop(_dropHint, CellGap + target.Row * cellHeight);
        if (_dropHint.Parent != canvas)
        {
            (_dropHint.Parent as Canvas)?.Children.Remove(_dropHint);
            canvas.Children.Add(_dropHint);
        }
    }

    private void HideDropHint()
    {
        (_dropHint?.Parent as Canvas)?.Children.Remove(_dropHint);
        EndSwapPreview();
    }

    /// <summary>
    /// A drop moves the item to free cells, or swaps it with a same-sized item
    /// already there (which goes to where the dragged item was, on its page).
    /// Folders are made from the right-click menu, not by dropping.
    /// </summary>
    private void OnPageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _swapPreview = null; // the page is rebuilt; nothing to animate back
        HideDropHint();
        _dragFlip?.Stop();
        var canvas = (Canvas)sender;
        if (DropTarget(canvas, e) is not { } target || e.Data.GetData(DragFormat) is not string id) return;
        var dropPage = PageIndexOf(canvas, _pageIndex);
        if (dropPage >= Pages.Count || _layout.Locate(id) is not { } source) return;

        var page = Pages[dropPage];
        var (kind, partner) = ClassifyDrop(dropPage, id, target.Column, target.Row, target.Dragged);
        if (kind == DropKind.None) return;

        var fromPage = source.Page;
        var fromColumn = source.Placement.Column;
        var fromRow = source.Placement.Row;

        fromPage.Placements.Remove(source.Placement);
        if (kind == DropKind.Swap && partner is not null)
        {
            page.Placements.Remove(partner);
            partner.Column = fromColumn;
            partner.Row = fromRow;
            fromPage.Placements.Add(partner);
        }

        source.Placement.Column = target.Column;
        source.Placement.Row = target.Row;
        page.Placements.Add(source.Placement);
        SavePages();
    }

    // ---- Page turning ---------------------------------------------------------

    /// <summary>Space between two pages while they slide side by side.</summary>
    private const double PageSpacing = 24;

    /// <summary>The page next to the current one, shown while a swipe is in progress.</summary>
    private FrameworkElement? _neighbour;
    private int _neighbourIndex = -1;

    private double PageStride => Math.Max(1, _pageHost.ActualWidth) + PageSpacing;

    private static TranslateTransform ShiftOf(FrameworkElement page) => (TranslateTransform)page.RenderTransform;

    private void ChangePage(int step) => GoToPage(_pageIndex + step * Spread);

    /// <summary>
    /// Turns to a page. If a swipe already has that page alongside, the two
    /// carry on from where the fingers left them rather than starting over
    /// from a page-width away.
    /// </summary>
    private void GoToPage(int index)
    {
        var target = SpreadStart(Math.Clamp(index, 0, PageCount - 1));
        if (target == _pageIndex)
        {
            SnapBack();
            return;
        }

        var direction = Math.Sign(target - _pageIndex);
        var old = _currentPage;
        ClosePageFolder();

        FrameworkElement incoming;
        if (_neighbour is not null && _neighbourIndex == target)
        {
            incoming = _neighbour;
        }
        else
        {
            DropNeighbour();
            incoming = BuildPage(target);
            ShiftOf(incoming).X = direction * PageStride;
            _pageHost.Children.Add(incoming);
        }
        _neighbour = null;
        _neighbourIndex = -1;

        _pageIndex = target;
        _currentPage = incoming;

        // Quicker the less there is left to travel.
        var remaining = Math.Abs(ShiftOf(incoming).X) / PageStride;
        var duration = TimeSpan.FromMilliseconds(90 + 170 * Math.Clamp(remaining, 0, 1));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Both pages travel the same distance, so they stay side by side.
        var travel = -ShiftOf(incoming).X;
        Slide(incoming, 0, duration, ease);
        if (old is not null)
        {
            Slide(old, ShiftOf(old).X + travel, duration, ease, () =>
            {
                if (!ReferenceEquals(old, _currentPage)) _pageHost.Children.Remove(old);
            });
        }

        UpdatePageChrome();
    }

    /// <summary>
    /// Slides a page. <paramref name="completed"/> is attached before the
    /// animation starts: WPF copies a timeline's Completed handlers into the
    /// clock when it begins, so one added afterwards never runs.
    /// </summary>
    private static void Slide(FrameworkElement page, double to, TimeSpan duration, IEasingFunction ease, Action? completed = null)
    {
        var shift = ShiftOf(page);
        var animation = new DoubleAnimation(shift.X, to, duration) { EasingFunction = ease };
        if (completed is not null) animation.Completed += (_, _) => completed();
        shift.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void SnapBack()
    {
        if (_currentPage is null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        Slide(_currentPage, 0, duration, ease);

        if (_neighbour is { } neighbour)
        {
            var away = Math.Sign(_neighbourIndex - _pageIndex) * PageStride;
            Slide(neighbour, away, duration, ease, () =>
            {
                if (ReferenceEquals(neighbour, _neighbour)) DropNeighbour();
            });
        }
    }

    private void DropNeighbour()
    {
        if (_neighbour is not null) _pageHost.Children.Remove(_neighbour);
        _neighbour = null;
        _neighbourIndex = -1;
    }

    /// <summary>
    /// Follows a finger, the mouse or the touchpad, with the page it is
    /// heading for alongside; past the first or last page it resists.
    /// </summary>
    private void DragPageBy(double totalX)
    {
        if (_currentPage is null) return;
        var toward = totalX < 0 ? _pageIndex + Spread : _pageIndex - Spread;
        var atEdge = totalX == 0 || toward < 0 || toward >= PageCount;

        var shift = ShiftOf(_currentPage);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = atEdge ? totalX * 0.25 : totalX;

        if (atEdge)
        {
            DropNeighbour();
            return;
        }

        if (_neighbourIndex != toward)
        {
            DropNeighbour();
            _neighbour = BuildPage(toward);
            _neighbourIndex = toward;
            _pageHost.Children.Add(_neighbour);
        }

        var neighbourShift = ShiftOf(_neighbour!);
        neighbourShift.BeginAnimation(TranslateTransform.XProperty, null);
        neighbourShift.X = totalX + Math.Sign(toward - _pageIndex) * PageStride;
    }

    // ---- Two-finger touchpad swipe ----------------------------------------------

    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>Pixels the pages move per unit of horizontal scroll.</summary>
    private const double ScrollToPixels = 1.0;

    private double _scrollSwipe;
    private DispatcherTimer? _scrollSwipeEnd;
    private bool _scrollSwallowing;

    /// <summary>
    /// A two-finger sideways swipe on a precision touchpad arrives as
    /// horizontal wheel messages, which WPF does not surface. The pages follow
    /// the fingers side by side. Once the swipe has gone a third of the way the
    /// page turns straight away, and the rest of that gesture (including the
    /// touchpad's momentum) is swallowed, so one swipe turns one page without
    /// waiting for the momentum to die out. A short swipe springs back when
    /// the messages stop.
    /// </summary>
    private IntPtr OnHorizontalScroll(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL) return IntPtr.Zero;
        if (_activeTab != PagesTab || _search.Text.Length > 0 || _pageFolder.Visibility == Visibility.Visible || PageCount <= 1)
            return IntPtr.Zero;

        handled = true;
        RestartScrollEndTimer();
        if (_scrollSwallowing) return new IntPtr(1);

        // Positive is "scroll right", which is the fingers moving left: the
        // next page comes in from the right.
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        _scrollSwipe -= delta * ScrollToPixels;

        var commit = Math.Max(SwipeThreshold, _pageHost.ActualWidth / 3);
        if (Math.Abs(_scrollSwipe) >= commit)
        {
            var step = _scrollSwipe < 0 ? 1 : -1;
            _scrollSwipe = 0;
            _scrollSwallowing = true;
            if (step > 0 ? HasNextSpread : HasPreviousSpread) ChangePage(step);
            else SnapBack();
            return new IntPtr(1);
        }

        DragPageBy(_scrollSwipe);
        return new IntPtr(1);
    }

    /// <summary>The gesture has ended when the messages pause.</summary>
    private void RestartScrollEndTimer()
    {
        if (_scrollSwipeEnd is null)
        {
            _scrollSwipeEnd = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _scrollSwipeEnd.Tick += (_, _) =>
            {
                _scrollSwipeEnd!.Stop();
                var total = _scrollSwipe;
                _scrollSwipe = 0;
                if (_scrollSwallowing)
                {
                    _scrollSwallowing = false;
                    return;
                }
                FinishSwipe(total);
            };
        }
        _scrollSwipeEnd.Stop();
        _scrollSwipeEnd.Start();
    }

    private void FinishSwipe(double totalX)
    {
        if (totalX <= -SwipeThreshold && HasNextSpread) ChangePage(1);
        else if (totalX >= SwipeThreshold && HasPreviousSpread) ChangePage(-1);
        else SnapBack();
    }

    private void OnPageWheel(object sender, MouseWheelEventArgs e)
    {
        if (PageCount <= 1) return;
        e.Handled = true;
        // One page per notch burst: a free-spinning wheel would flick through them all.
        if (DateTime.UtcNow - _lastWheel < TimeSpan.FromMilliseconds(280)) return;
        _lastWheel = DateTime.UtcNow;
        ChangePage(e.Delta < 0 ? 1 : -1);
    }

    // ---- Touch --------------------------------------------------------------

    private DateTime _touchStarted;

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = _pageHost;
        e.Mode = ManipulationModes.TranslateX;
        _touchStarted = DateTime.UtcNow;
        e.Handled = true;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        DragPageBy(e.CumulativeManipulation.Translation.X);
        e.Handled = true;
    }

    private void OnManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        e.Handled = true;
        var total = e.TotalManipulation.Translation.X;

        // Manipulation-enabled elements do not turn touches into clicks, so a
        // tap (or a press-and-hold, for the menu) is handled here.
        if (Math.Abs(total) < 10)
        {
            SnapBack();
            var target = FindParent<PressableBorder>(_pageHost.InputHitTest(e.ManipulationOrigin) as DependencyObject);
            if (target is null) return;
            if (DateTime.UtcNow - _touchStarted > TimeSpan.FromMilliseconds(550)) target.RaiseRightClick();
            else target.RaiseClick();
            return;
        }

        FinishSwipe(total);
    }

    // ---- Mouse swipe (press on empty space and drag) ---------------------------

    private void OnSwipeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null) return; // touch is handled by manipulation
        if (FindParent<PressableBorder>(e.OriginalSource as DependencyObject) is not null) return; // tiles drag instead
        if (FindParent<Grid>(e.OriginalSource as DependencyObject) is { } grid && grid != _pageHost && grid.Parent is Canvas) return; // widgets
        _swipeStart = e.GetPosition(_pageHost);
        _swiping = false;
    }

    private void OnSwipeMouseMove(object sender, MouseEventArgs e)
    {
        if (_swipeStart is not { } start || e.LeftButton != MouseButtonState.Pressed || PageCount <= 1) return;
        var dx = e.GetPosition(_pageHost).X - start.X;
        if (!_swiping && Math.Abs(dx) > SystemParameters.MinimumHorizontalDragDistance * 3)
        {
            _swiping = true;
            _pageHost.CaptureMouse();
        }
        if (_swiping) DragPageBy(dx);
    }

    private void OnSwipeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_swipeStart is not { } start) return;
        _swipeStart = null;
        if (!_swiping) return;
        _swiping = false;
        _pageHost.ReleaseMouseCapture();
        FinishSwipe(e.GetPosition(_pageHost).X - start.X);
        e.Handled = true;
    }

    // ---- Folders --------------------------------------------------------------

    private TextBox _folderName = null!;

    private FrameworkElement BuildPageFolder()
    {
        _folderName = new TextBox
        {
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            FontFamily = StartStyle.Display,
            Foreground = StartStyle.Text,
            CaretBrush = StartStyle.Text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "Click to rename",
        };
        _folderName.LostKeyboardFocus += (_, _) => CommitFolderName();
        _folderName.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitFolderName();
            _search.Focus();
        };
        _pageFolderTiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };

        var card = new Border
        {
            Background = StartStyle.Panel,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20, 16, 20, 16),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.3 },
            Child = new StackPanel
            {
                Children =
                {
                    _folderName,
                    new ScrollViewer
                    {
                        MaxHeight = 340,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = _pageFolderTiles,
                    },
                },
            },
        };
        card.MouseLeftButtonUp += (_, e) => e.Handled = true;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        card.RenderTransform = new TransformGroup { Children = { _folderScale, _folderShift } };
        _folderCard = card;
        _folderDim = new Rectangle { Fill = StartStyle.Dim };

        _pageFolder = new Grid
        {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Children = { _folderDim, card },
        };
        _pageFolder.MouseLeftButtonUp += (_, _) => ClosePageFolder();
        return _pageFolder;
    }

    private Border _folderCard = null!;
    private Rectangle _folderDim = null!;
    private readonly ScaleTransform _folderScale = new();
    private readonly TranslateTransform _folderShift = new();
    private int _folderAnimation;

    /// <summary>The folder's tile on the current page, to grow from and shrink back into.</summary>
    private StartTile? FolderTileOf(GridGroup group) =>
        _currentPage is null ? null : Descendants<StartTile>(_currentPage).FirstOrDefault(t => t.Item.Id == group.PlacementId);

    /// <summary>
    /// Animates the folder panel between its tile and its open place: scale
    /// from the tile's size, travel from the tile's centre, fade, and dim the
    /// page behind. Without a tile (it scrolled away) it zooms from the centre.
    /// </summary>
    private void AnimateFolder(bool opening, Action? done = null)
    {
        var generation = ++_folderAnimation;
        _pageFolder.UpdateLayout();

        var from = Rect.Empty;
        if (_openFolder is not null && FolderTileOf(_openFolder) is { IsVisible: true } tile)
        {
            var origin = tile.TransformToVisual(_pageFolder).Transform(new Point(0, 0));
            from = new Rect(origin, new Size(tile.ActualWidth, tile.ActualHeight));
        }

        var cardWidth = Math.Max(1, _folderCard.ActualWidth);
        var cardHeight = Math.Max(1, _folderCard.ActualHeight);
        // The card rests centred in the panel.
        var cardCentre = new Point(_pageFolder.ActualWidth / 2, _pageFolder.ActualHeight / 2);

        var closedScale = from.IsEmpty ? 0.6 : Math.Min(from.Width / cardWidth, from.Height / cardHeight);
        var closedX = from.IsEmpty ? 0 : from.X + from.Width / 2 - cardCentre.X;
        var closedY = from.IsEmpty ? 0 : from.Y + from.Height / 2 - cardCentre.Y;

        var duration = TimeSpan.FromMilliseconds(opening ? 280 : 200);
        IEasingFunction ease = opening
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };

        DoubleAnimation Animate(double closed, double open) => new(opening ? closed : open, opening ? open : closed, duration)
        {
            EasingFunction = ease,
        };

        // Hide the tile it grew from while the folder is open, as if it opened;
        // it reappears once the panel has shrunk back into it.
        var source = _openFolder is null ? null : FolderTileOf(_openFolder);
        if (opening && source is not null) source.Opacity = 0;

        // A closing panel must not catch clicks while it shrinks.
        _pageFolder.IsHitTestVisible = opening;

        var scale = Animate(closedScale, 1);
        // Attached before the animation begins, or it never fires (see Slide).
        scale.Completed += (_, _) =>
        {
            if (!opening && source is not null) source.Opacity = 1;
            if (generation == _folderAnimation) done?.Invoke();
        };
        _folderScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _folderScale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(closedScale, 1));
        _folderShift.BeginAnimation(TranslateTransform.XProperty, Animate(closedX, 0));
        _folderShift.BeginAnimation(TranslateTransform.YProperty, Animate(closedY, 0));
        _folderDim.BeginAnimation(OpacityProperty, Animate(0, 1));

        // The panel's contents appear a little after it starts to grow, and
        // are gone early on the way back, so it never shows squashed icons.
        var fade = new DoubleAnimation(opening ? 0 : 1, opening ? 1 : 0, TimeSpan.FromMilliseconds(opening ? 180 : 140))
        {
            BeginTime = opening ? TimeSpan.FromMilliseconds(60) : TimeSpan.Zero,
        };
        _folderCard.BeginAnimation(OpacityProperty, fade);
    }

    private void OpenPageFolder(GridGroup group)
    {
        var alreadyOpen = _pageFolder.Visibility == Visibility.Visible && ReferenceEquals(_openFolder, group);
        _openFolder = group;
        _folderName.Text = group.Name;
        _pageFolderTiles.Children.Clear();
        foreach (var item in FolderItems(group))
        {
            var tile = BuildPageTile(item, group);
            tile.Width = 112;
            tile.Height = 104;
            tile.Margin = new Thickness(2);
            _pageFolderTiles.Children.Add(tile);
        }
        _pageFolder.Visibility = Visibility.Visible;

        // A refresh of an already open folder (after a rename, say) does not replay the opening.
        if (!alreadyOpen) AnimateFolder(opening: true);
        else if (FolderTileOf(group) is { } source) source.Opacity = 0;
    }

    private void CommitFolderName()
    {
        if (_openFolder is null) return;
        var name = _folderName.Text.Trim();
        if (name.Length == 0 || name == _openFolder.Name) return;
        _openFolder.Name = name;
        SaveLayout();
        var reopen = _openFolder;
        RebuildPages();
        _openFolder = reopen;
    }

    private bool ClosePageFolder()
    {
        // Not open, or already closing.
        if (_pageFolder is not { Visibility: Visibility.Visible } || _openFolder is null)
        {
            _openFolder = null;
            return false;
        }
        CommitFolderName();
        AnimateFolder(opening: false, done: () =>
        {
            _pageFolder.Visibility = Visibility.Collapsed;
            _pageFolderTiles.Children.Clear();
        });
        _openFolder = null;
        return true;
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
