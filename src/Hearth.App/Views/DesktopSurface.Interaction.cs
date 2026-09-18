using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearth.App.Controls;
using Hearth.App.Widgets;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Interop;
using Hearth.Core.Layout;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Views;

/// <summary>
/// Pointer handling, drag and drop, and menus.
///
/// One drag model covers everything that moves: icons, folder tiles, widgets,
/// and items pulled out of an open folder. Repositioning is direct
/// manipulation rather than OLE drag-and-drop — OLE would round-trip through
/// the shell for a move that never leaves this window. OLE is still enabled so
/// drops *from* Explorer work.
/// </summary>
public partial class DesktopSurface
{
    /// <summary>Something the user has picked up.</summary>
    private sealed class DragSession
    {
        public required string PlacementId { get; init; }

        /// <summary>The on-grid element being moved; dimmed while in flight.</summary>
        public FrameworkElement? Origin { get; init; }

        /// <summary>Set when the item was pulled out of an open folder.</summary>
        public GridGroup? FromFolder { get; init; }

        public required FrameworkElement Ghost { get; init; }

        /// <summary>Pointer offset from the ghost's top-left.</summary>
        public required Vector Grab { get; init; }

        public required Size Footprint { get; init; }
        public int ColumnSpan { get; init; } = 1;
        public int RowSpan { get; init; } = 1;
        public double Scale { get; init; } = 1;

        public bool IsWidget { get; init; }
        public bool Unlocked { get; init; }
    }

    /// <summary>A widget being resized by its grip.</summary>
    private sealed class ResizeSession
    {
        public required WidgetFrame Frame { get; init; }
        public required IWidget Widget { get; init; }
        public required MonitorSurface Surface { get; init; }
        public required Size StartSize { get; init; }
        public required Point StartPointer { get; init; }
        public int Columns { get; set; }
        public int Rows { get; set; }
    }

    private ResizeSession? _resize;

    private FrameworkElement? _pressedElement;
    private string? _pressedId;
    private Point _pressOrigin;
    private DragSession? _drag;
    private IconTile? _dropTarget;

    /// <summary>Movement before a press becomes a drag rather than a click.</summary>
    private static readonly double DragThreshold = SystemParameters.MinimumHorizontalDragDistance;

    /// <summary>
    /// How close, as a fraction of icon size, the dragged icon's centre must be
    /// to another icon's centre for a drop to mean "put these in a folder"
    /// rather than "swap places".
    /// </summary>
    private const double MergeRadius = 0.55;

    // ---- Pointer --------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // A drag whose mouse-up never arrived must not leave its ghost behind:
        // that orphan is what looked like a duplicated icon.
        if (_drag is not null) CancelDrag();

        if (_openFolder is not null)
        {
            OnFolderPress(e);
            return;
        }

        var position = e.GetPosition(ItemCanvas);
        var (element, id) = HitTestElement(position);

        if (element is WidgetFrame frame && frame.IsOverGrip(ItemCanvas.InputHitTest(position) as DependencyObject))
        {
            BeginResize(frame, position);
            return;
        }

        _pressedElement = element;
        _pressedId = id;
        _pressOrigin = position;

        if (element is IconTile tile)
        {
            SelectOnly(tile);
        }
        else
        {
            ClearSelection();
        }

        if (element is not null) ItemCanvas.CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var position = e.GetPosition(ItemCanvas);

        if (_resize is not null)
        {
            UpdateResize(position);
            return;
        }

        if (_drag is not null)
        {
            MoveGhost(position);
            UpdateDropTarget(position);
            return;
        }

        if (_pressedElement is null || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = position - _pressOrigin;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        if (_pressedFromFolder is { } folder)
        {
            BeginFolderDrag(folder, position);
        }
        else if (_pressedId is not null)
        {
            BeginGridDrag(_pressedElement, _pressedId, position);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // Claim the press BEFORE releasing capture. Releasing raises
        // LostMouseCapture, which cancels any drag still marked in progress —
        // so the order here is the difference between a drop and a cancel.
        var element = _pressedElement;
        var folderPress = _pressedFromFolder;
        var drag = _drag;
        var resize = _resize;
        var position = e.GetPosition(ItemCanvas);
        _resize = null;

        _pressedElement = null;
        _pressedId = null;
        _pressedFromFolder = null;
        _drag = null;

        if (ItemCanvas.IsMouseCaptured) ItemCanvas.ReleaseMouseCapture();

        if (resize is not null)
        {
            CommitResize(resize);
            return;
        }

        if (drag is not null)
        {
            ClearDragVisuals(drag);
            CompleteDrop(drag, position);
            return;
        }

        if (folderPress is not null)
        {
            if (element is IconTile inFolder && App.Settings.LaunchOnSingleClick) LaunchFromFolder(inFolder);
            return;
        }

        if (element is IconTile tile && App.Settings.LaunchOnSingleClick) Activate(tile);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (App.Settings.LaunchOnSingleClick || _openFolder is not null) return;

        if (HitTestElement(e.GetPosition(ItemCanvas)).Element is IconTile tile) Activate(tile);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;

        if (_openFolder is not null)
        {
            OnFolderRightClick(e);
            return;
        }

        var (element, id) = HitTestElement(e.GetPosition(ItemCanvas));

        // Shift+right-click goes straight to the Windows menu, as in Explorer.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (element is IconTile { IsFolder: false, Item: { } shiftItem })
                ShowShellMenu(shiftItem);
            else if (element is null)
                ShowShellBackgroundMenu();
            return;
        }

        switch (element)
        {
            case IconTile { IsFolder: true } folderTile:
                SelectOnly(folderTile);
                ShowFolderTileMenu(folderTile);
                break;

            case IconTile { Item: not null } tile:
                SelectOnly(tile);
                ShowItemMenu(tile.Item);
                break;

            case not null when id is not null && WidgetRegistry.FromPlacementId(id) is { } widget:
                ShowWidgetMenu(widget);
                break;

            default:
                ClearSelection();
                ShowBackgroundMenu(e.GetPosition(ItemCanvas));
                break;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Capture can be taken away mid-drag — a notification, Alt+Tab, the
        // pointer leaving the window on an odd monitor seam. Without this the
        // mouse-up never arrives and the ghost stays pinned to the desktop.
        if (_resize is not null)
        {
            _resize.Frame.IsResizing = false;
            _resize = null;
            RelayoutTiles(); // snap the frame back to its saved size
            return;
        }

        if (_drag is null) return;
        CancelDrag();
        _pressedElement = null;
        _pressedId = null;
        _pressedFromFolder = null;
    }

    // ---- Dragging -------------------------------------------------------

    private void BeginGridDrag(FrameworkElement element, string placementId, Point position)
    {
        var located = _layout.Locate(placementId);
        if (located is null || !_tileSurfaces.TryGetValue(placementId, out var surface)) return;

        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        var ghost = MakeGhost(Snapshot(element), element.ActualWidth, element.ActualHeight);

        _drag = new DragSession
        {
            PlacementId = placementId,
            Origin = element,
            Ghost = ghost,
            Grab = position - new Point(left, top),
            Footprint = new Size(element.ActualWidth, element.ActualHeight),
            ColumnSpan = Math.Max(1, located.Value.Placement.ColumnSpan),
            RowSpan = Math.Max(1, located.Value.Placement.RowSpan),
            Scale = surface.Scale,
            IsWidget = WidgetRegistry.FromPlacementId(placementId) is not null,
            Unlocked = located.Value.Placement.Unlocked,
        };

        // The origin stays in place, dimmed, so the cell it came from stays
        // legible while the ghost is in flight.
        element.Opacity = 0.35;
        OverlayCanvas.Children.Add(ghost);
        MoveGhost(position);
    }

    /// <summary>
    /// A picture of the element as it looks right now, taken before it is
    /// dimmed. Rendered through a VisualBrush because RenderTargetBitmap on an
    /// element directly includes its canvas offset and comes out blank.
    /// </summary>
    private static ImageSource Snapshot(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FrameworkElement MakeGhost(ImageSource? source, double width, double height) => new Image
    {
        Source = source,
        Width = width,
        Height = height,
        Opacity = 0.92,
        Stretch = Stretch.Fill,
        IsHitTestVisible = false,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1.08, 1.08),
    };

    private void MoveGhost(Point position)
    {
        if (_drag is null) return;
        Canvas.SetLeft(_drag.Ghost, position.X - _drag.Grab.X);
        Canvas.SetTop(_drag.Ghost, position.Y - _drag.Grab.Y);
    }

    private void CancelDrag()
    {
        if (_drag is null) return;
        ClearDragVisuals(_drag);
        _drag = null;
    }

    private void ClearDragVisuals(DragSession drag)
    {
        OverlayCanvas.Children.Remove(drag.Ghost);
        if (drag.Origin is not null) drag.Origin.Opacity = 1.0;
        SetDropTarget(null);
    }

    /// <summary>The point used to decide which cell a drop lands in.</summary>
    private static Point AnchorOf(DragSession drag, Point pointer, MonitorSurface? hint)
    {
        var topLeft = pointer - drag.Grab;
        if (!drag.IsWidget)
        {
            // Icon centre, not tile centre: the icon is what the user aims.
            return new Point(
                topLeft.X + drag.Footprint.Width / 2,
                topLeft.Y + App.Settings.IconSize * 1.2 * drag.Scale / 2);
        }

        // Widgets anchor on the centre of their top-left cell, so the widget
        // lands where its top-left corner visibly is.
        var cellW = hint?.CellWidth ?? drag.Footprint.Width;
        var cellH = hint?.CellHeight ?? drag.Footprint.Height;
        return new Point(topLeft.X + cellW / 2, topLeft.Y + cellH / 2);
    }

    private MonitorSurface? SurfaceAt(Point point) =>
        _surfaces.FirstOrDefault(sf => sf.LocalBounds.Contains(point));

    private static (int Column, int Row) PointToCell(MonitorSurface surface, Point position)
    {
        var x = position.X - surface.LocalBounds.X - surface.PadX;
        var y = position.Y - surface.LocalBounds.Y - surface.PadY;

        // Floor: tiles are drawn from their cell's top-left, so a point inside
        // a tile belongs to that tile's cell.
        var column = (int)Math.Floor(x / surface.CellWidth);
        var row = (int)Math.Floor(y / surface.CellHeight);

        return (Math.Clamp(column, 0, surface.Columns - 1),
                Math.Clamp(row, 0, surface.Rows - 1));
    }

    private static GridPlacement? OccupantAt(MonitorLayout layout, int column, int row, string ignoreId) =>
        layout.Placements.FirstOrDefault(p =>
            p.ParentGroupId is null &&
            !string.Equals(p.ItemId, ignoreId, StringComparison.OrdinalIgnoreCase) &&
            column >= p.Column && column < p.Column + p.ColumnSpan &&
            row >= p.Row && row < p.Row + p.RowSpan);

    /// <summary>
    /// Would dropping here make (or add to) a folder? True when the dragged
    /// icon is centred over another single icon, and the dragged thing is
    /// neither a folder nor a widget — folders do not nest.
    /// </summary>
    private bool IsMergeDrop(DragSession drag, Point anchor, MonitorSurface surface, GridPlacement occupant)
    {
        if (drag.IsWidget || occupant.ColumnSpan > 1 || occupant.RowSpan > 1) return false;
        if (drag.PlacementId.StartsWith(HomeLayout.GroupPrefix, StringComparison.Ordinal)) return false;

        var iconBox = App.Settings.IconSize * 1.2 * surface.Scale;
        var centre = new Point(
            surface.LocalBounds.X + surface.PadX + occupant.Column * surface.CellWidth + TileWidth(surface.Scale) / 2,
            surface.LocalBounds.Y + surface.PadY + occupant.Row * surface.CellHeight + iconBox / 2);

        return (anchor - centre).Length <= App.Settings.IconSize * surface.Scale * MergeRadius;
    }

    private void UpdateDropTarget(Point pointer)
    {
        if (_drag is null) return;

        var anchor = AnchorOf(_drag, pointer, SurfaceAt(pointer));
        var surface = SurfaceAt(anchor);
        if (surface is null)
        {
            SetDropTarget(null);
            return;
        }

        var cell = PointToCell(surface, anchor);
        var occupant = OccupantAt(_layout.ForMonitor(surface.Info.DeviceId), cell.Column, cell.Row, _drag.PlacementId);

        SetDropTarget(occupant is not null
                      && IsMergeDrop(_drag, anchor, surface, occupant)
                      && _tiles.TryGetValue(occupant.ItemId, out var target)
            ? target
            : null);
    }

    private void SetDropTarget(IconTile? tile)
    {
        if (ReferenceEquals(_dropTarget, tile)) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = tile;
        if (tile is not null) tile.IsDropTarget = true;
    }

    /// <summary>
    /// Applies a drop. Rules, Android-style:
    /// empty cell → move there; centred over an icon → make a folder;
    /// centred over a folder → add to it; otherwise over an icon → swap;
    /// over a widget, or anywhere a widget will not fit → nothing happens.
    /// </summary>
    private void CompleteDrop(DragSession drag, Point pointer)
    {
        var anchor = AnchorOf(drag, pointer, SurfaceAt(pointer));
        var to = SurfaceAt(anchor);
        if (to is null) return; // dead space between offset displays

        var (column, row) = PointToCell(to, anchor);
        var toLayout = _layout.ForMonitor(to.Info.DeviceId);
        var source = drag.FromFolder is null ? _layout.Locate(drag.PlacementId) : null;
        if (drag.FromFolder is null && source is null) return;

        if (drag.IsWidget && source is { } widgetSource)
        {
            if (drag.Unlocked)
            {
                // Free placement: wherever the ghost is, kept on its display.
                var target = SurfaceAt(pointer) ?? to;
                var targetLayout = _layout.ForMonitor(target.Info.DeviceId);
                var topLeft = pointer - drag.Grab;
                var rect = widgetSource.Placement.Free
                    ?? new FreeRect { Width = drag.Footprint.Width, Height = drag.Footprint.Height };

                rect.X = Math.Clamp(topLeft.X - target.LocalBounds.X, 0, Math.Max(0, target.LocalBounds.Width - rect.Width));
                rect.Y = Math.Clamp(topLeft.Y - target.LocalBounds.Y, 0, Math.Max(0, target.LocalBounds.Height - rect.Height));
                widgetSource.Placement.Free = rect;

                widgetSource.Layout.Placements.Remove(widgetSource.Placement);
                targetLayout.Placements.Add(widgetSource.Placement);
                UpdateCoveredCells(widgetSource.Placement, target);
                EvictFromUnder(targetLayout, target, widgetSource.Placement);
                Commit();
                return;
            }

            column = Math.Min(column, to.Columns - drag.ColumnSpan);
            row = Math.Min(row, to.Rows - drag.RowSpan);
            if (column < 0 || row < 0) return; // this display is too small for it

            // Another widget in the way blocks the move; icons just step aside.
            for (var y = row; y < row + drag.RowSpan; y++)
                for (var x = column; x < column + drag.ColumnSpan; x++)
                    if (OccupantAt(toLayout, x, y, drag.PlacementId) is { } blocker &&
                        WidgetRegistry.FromPlacementId(blocker.ItemId) is not null) return;

            MovePlacement(widgetSource, toLayout, column, row);
            EvictFromUnder(toLayout, to, widgetSource.Placement);
            Commit();
            return;
        }

        var occupant = OccupantAt(toLayout, column, row, drag.PlacementId);

        if (occupant is null)
        {
            if (drag.FromFolder is { } folder)
            {
                folder.ItemIds.Remove(drag.PlacementId);
                toLayout.Placements.Add(new GridPlacement { ItemId = drag.PlacementId, Column = column, Row = row });
            }
            else
            {
                MovePlacement(source!.Value, toLayout, column, row);
            }
            Commit();
            return;
        }

        if (occupant.ColumnSpan > 1 || occupant.RowSpan > 1) return; // never displace a widget

        if (IsMergeDrop(drag, anchor, to, occupant))
        {
            // Dropping an item back onto the folder it came from changes nothing.
            // Both sides must be non-null for that to mean anything: for an
            // ordinary icon-onto-icon drop both are null, and ReferenceEquals
            // (null, null) is true — which silently cancelled every new folder.
            if (drag.FromFolder is not null &&
                ReferenceEquals(_layout.FindGroupByPlacement(occupant.ItemId), drag.FromFolder)) return;

            Detach(drag, source);
            if (_layout.FindGroupByPlacement(occupant.ItemId) is { } existing)
            {
                existing.ItemIds.Add(drag.PlacementId);
            }
            else
            {
                var created = _layout.CreateGroup("Folder", [occupant.ItemId, drag.PlacementId]);
                occupant.ItemId = created.PlacementId;
            }
            Commit();
            return;
        }

        if (source is { } from)
        {
            // Swap — across displays as well as within one.
            toLayout.Placements.Remove(occupant);
            (occupant.Column, occupant.Row) = (from.Placement.Column, from.Placement.Row);
            from.Layout.Placements.Add(occupant);
            MovePlacement(from, toLayout, column, row);
            Commit();
            return;
        }

        // Pulled out of a folder onto an occupied cell: there is no cell to
        // swap with, so take the nearest free one instead.
        var free = NearestFreeCell(toLayout, to, column, row);
        if (free is null || drag.FromFolder is null) return;

        drag.FromFolder.ItemIds.Remove(drag.PlacementId);
        toLayout.Placements.Add(new GridPlacement { ItemId = drag.PlacementId, Column = free.Value.Column, Row = free.Value.Row });
        Commit();

        void Commit()
        {
            TrySaveLayout();
            // Every icon is in the memory cache, so a full relayout is cheap and
            // keeps the tile-to-display bookkeeping honest. It also runs
            // Reconcile, which dissolves a folder left with a single item.
            RelayoutTiles();
        }
    }

    private void Detach(DragSession drag, (MonitorLayout Layout, GridPlacement Placement)? source)
    {
        if (drag.FromFolder is { } folder) folder.ItemIds.Remove(drag.PlacementId);
        else if (source is { } from) from.Layout.Placements.Remove(from.Placement);
    }

    private static void MovePlacement(
        (MonitorLayout Layout, GridPlacement Placement) from, MonitorLayout to, int column, int row)
    {
        from.Layout.Placements.Remove(from.Placement);
        from.Placement.Column = column;
        from.Placement.Row = row;
        to.Placements.Add(from.Placement);
    }

    private static (int Column, int Row)? NearestFreeCell(MonitorLayout layout, MonitorSurface surface, int column, int row)
    {
        var reach = Math.Max(surface.Columns, surface.Rows);
        for (var ring = 0; ring <= reach; ring++)
        {
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring) continue;
                    var x = column + dx;
                    var y = row + dy;
                    if (x < 0 || y < 0 || x >= surface.Columns || y >= surface.Rows) continue;
                    if (!layout.IsOccupied(x, y)) return (x, y);
                }
            }
        }
        return null;
    }

    // ---- Hit testing, selection, launching ------------------------------

    /// <summary>The tile or widget under a point, with its placement id.</summary>
    private (FrameworkElement? Element, string? Id) HitTestElement(Point position)
    {
        var hit = ItemCanvas.InputHitTest(position) as DependencyObject;
        while (hit is not null && !ReferenceEquals(hit, ItemCanvas))
        {
            if (hit is IconTile tile) return (tile, tile.Item?.Id);
            if (hit is WidgetFrame frame) return (frame, frame.PlacementId);
            hit = VisualTreeHelper.GetParent(hit);
        }
        return (null, null);
    }

    private void SelectOnly(IconTile tile)
    {
        foreach (var other in _tiles.Values) other.IsSelected = ReferenceEquals(other, tile);
    }

    private void ClearSelection()
    {
        foreach (var tile in _tiles.Values) tile.IsSelected = false;
    }

    /// <summary>Launches an app, or opens a folder.</summary>
    private void Activate(IconTile tile)
    {
        if (tile.Item is not { } item) return;

        if (tile.IsFolder)
        {
            if (_layout.FindGroupByPlacement(item.Id) is { } group && _tileSurfaces.TryGetValue(item.Id, out var surface))
                OpenFolder(group, surface);
            return;
        }

        if (!Services.AppLauncher.Launch(item))
            Log.Write($"failed to launch '{item.DisplayName}'");
    }

    // ---- Menus ----------------------------------------------------------

    private void ShowItemMenu(LauncherItem item, GridGroup? inFolder = null)
    {
        var menu = new ContextMenu { PlacementTarget = ItemCanvas };

        // The app's own jump list first, as on the taskbar.
        if (AddJumpList(menu, item)) menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor("Open", () =>
        {
            Services.AppLauncher.Launch(item);
            if (inFolder is not null) CloseFolder();
        }));

        if (item.Kind is LauncherItemKind.App or LauncherItemKind.Shortcut)
            menu.Items.Add(MenuItemFor("Run as administrator", () => ShellLauncher.RunAsAdministrator(item)));

        if (item.FileSystemPath is not null)
            menu.Items.Add(MenuItemFor("Open file location", () => ShellLauncher.OpenFileLocation(item)));

        menu.Items.Add(new Separator());

        if (inFolder is not null)
            menu.Items.Add(MenuItemFor("Remove from folder", () => RemoveFromFolder(inFolder, item.Id)));

        var style = new MenuItem { Header = "Icon style" };
        var current = _layout.FitFor(item.Id);
        foreach (var (fit, label) in new[]
        {
            (IconFit.Auto, "Automatic"),
            (IconFit.Fill, "Fill shape"),
            (IconFit.FillZoomed, "Fill shape (zoomed)"),
            (IconFit.Fit, "Fit on background"),
        })
        {
            style.Items.Add(CheckableMenuItem(label, current == fit, () => SetIconFit(item.Id, fit)));
        }
        menu.Items.Add(style);

        if (IsRemovablePin(item))
            menu.Items.Add(MenuItemFor("Remove from home", () => RemoveFromHome(item)));
        else
            menu.Items.Add(MenuItemFor("Hide from desktop", () => HideItem(item)));

        if (IsDeletable(item))
            menu.Items.Add(MenuItemFor("Delete", () => DeleteItem(item)));
        else if (item.Kind == LauncherItemKind.App)
            menu.Items.Add(MenuItemFor("Uninstall...", ShellLauncher.OpenInstalledApps));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Properties", () => ShellLauncher.ShowProperties(item)));
        menu.Items.Add(MenuItemFor("Show more options", () => ShowShellMenu(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Refresh icons", () =>
        {
            App.Icons.ClearMemoryCache();
            RelayoutTiles();
        }));

        OpenMenu(menu);
    }

    // ---- Hiding, deleting, and the Windows menu -------------------------

    private void SetIconFit(string itemId, IconFit fit)
    {
        if (fit == IconFit.Auto) _layout.IconFits.Remove(itemId);
        else _layout.IconFits[itemId] = fit;

        TrySaveLayout();

        // The fit is part of the icon cache key, so the new rendering is
        // produced on relayout; if the folder panel is open, rebuild it too.
        var openFolder = _openFolder;
        var folderSurface = _folderSurface;
        RelayoutTiles();
        if (openFolder is not null && folderSurface is not null) OpenFolder(openFolder, folderSurface);
    }

    private void HideItem(LauncherItem item)
    {
        if (_layout.IsHidden(item.Id)) return;
        _layout.Hidden.Add(item.Id);
        CloseFolder();
        TrySaveLayout();
        RelayoutTiles();
    }

    private void UnhideItem(string itemId)
    {
        if (_layout.Hidden.RemoveAll(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase)) == 0) return;
        TrySaveLayout();
        RelayoutTiles();
    }

    private void UnhideAll()
    {
        if (_layout.Hidden.Count == 0) return;
        _layout.Hidden.Clear();
        TrySaveLayout();
        RelayoutTiles();
    }

    /// <summary>
    /// Only things that genuinely live on the desktop can be deleted from it.
    /// Installed apps come from the Start menu; deleting their shortcut there
    /// would not uninstall anything, so they get Uninstall instead.
    /// </summary>
    private bool IsDeletable(LauncherItem item) =>
        item.Kind is LauncherItemKind.File or LauncherItemKind.Folder or LauncherItemKind.Shortcut &&
        item.FileSystemPath is { } path &&
        (IsUnder(path, _desktopCatalog.UserDesktop) || IsUnder(path, _desktopCatalog.CommonDesktop));

    private static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(root) &&
        string.Equals(Path.GetDirectoryName(path), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private async void DeleteItem(LauncherItem item)
    {
        if (item.FileSystemPath is not { } path) return;

        CloseFolder();
        var owner = Hosting.DesktopHost.Current?.Handle ?? IntPtr.Zero;

        // Windows shows its own confirmation here if the user has asked for
        // one, and the file goes to the Recycle Bin either way.
        Hosting.DesktopHost.Current?.BeginKeyboardInput();
        var deleted = ShellLauncher.Recycle(path, owner);
        Hosting.DesktopHost.Current?.EndKeyboardInput();

        if (!deleted)
        {
            Log.Write($"delete of '{path}' did not complete");
            return;
        }

        // The folder watcher would notice too; refreshing now removes the
        // tile immediately instead of after the debounce.
        await RefreshItemsAsync().ConfigureAwait(true);
    }

    private static string ParsingNameOf(LauncherItem item) =>
        item.Kind == LauncherItemKind.App ? $@"shell:AppsFolder\{item.Target}" : item.FileSystemPath ?? item.Target ?? item.Id;

    private static void ShowShellMenu(LauncherItem item)
    {
        if (Hosting.DesktopHost.Current is not { } host) return;
        Win32.GetCursorPos(out var cursor);
        Hosting.ShellContextMenu.ShowForItem(host.Handle, ParsingNameOf(item), cursor.X, cursor.Y);
    }

    private static void ShowShellBackgroundMenu()
    {
        if (Hosting.DesktopHost.Current is not { } host) return;
        Win32.GetCursorPos(out var cursor);
        Hosting.ShellContextMenu.ShowForDesktopBackground(host.Handle, cursor.X, cursor.Y);
    }

    private void ShowFolderTileMenu(IconTile tile)
    {
        if (tile.Item is null || _layout.FindGroupByPlacement(tile.Item.Id) is not { } group) return;

        var menu = new ContextMenu { PlacementTarget = ItemCanvas };
        menu.Items.Add(MenuItemFor("Open folder", () => Activate(tile)));
        menu.Items.Add(MenuItemFor("Rename", () =>
        {
            Activate(tile);
            BeginRename(selectAll: true);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Ungroup", () => Ungroup(group)));
        OpenMenu(menu);
    }

    private void ShowWidgetMenu(IWidget widget)
    {
        var id = WidgetRegistry.ToPlacementId(widget);
        var unlocked = _layout.Locate(id)?.Placement.Unlocked ?? false;

        var menu = new ContextMenu { PlacementTarget = ItemCanvas };
        menu.Items.Add(CheckableMenuItem("Unlocked (free size and position)", unlocked,
            () => SetWidgetUnlocked(widget, !unlocked)));
        menu.Items.Add(MenuItemFor("Reset size", () => ResetWidgetSize(widget)));

        if (widget is IConfigurableWidget configurable)
        {
            menu.Items.Add(MenuItemFor($"{widget.Title} settings...", () =>
            {
                if (configurable.Configure()) RelayoutTiles();
            }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor($"Remove {widget.Title} widget", () => RemoveWidget(widget)));
        OpenMenu(menu);
    }

    private void SetWidgetUnlocked(IWidget widget, bool unlock)
    {
        if (_layout.Locate(WidgetRegistry.ToPlacementId(widget)) is not { } spot) return;
        if (SurfaceFor(spot.Layout) is not { } surface) return;

        var placement = spot.Placement;
        var bounds = WidgetBounds(placement, surface);

        if (unlock)
        {
            // Start exactly where it is now, so unlocking never moves anything.
            placement.Free = new FreeRect
            {
                X = bounds.X - surface.LocalBounds.X,
                Y = bounds.Y - surface.LocalBounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
            };
            placement.Unlocked = true;
            UpdateCoveredCells(placement, surface);
        }
        else
        {
            // Snap to the nearest cells that fit its current size.
            var gap = App.Settings.GridGap * surface.Scale;
            var (minColumns, minRows) = widget.MinimumSpan;
            var columns = Math.Clamp((int)Math.Round((bounds.Width + gap) / surface.CellWidth), minColumns, Math.Max(minColumns, surface.Columns));
            var rows = Math.Clamp((int)Math.Round((bounds.Height + gap) / surface.CellHeight), minRows, Math.Max(minRows, surface.Rows));
            var (column, row) = PointToCell(surface,
                new Point(bounds.X + surface.CellWidth / 2, bounds.Y + surface.CellHeight / 2));

            placement.Unlocked = false;
            placement.Free = null;
            placement.Column = Math.Clamp(column, 0, Math.Max(0, surface.Columns - columns));
            placement.Row = Math.Clamp(row, 0, Math.Max(0, surface.Rows - rows));
            placement.ColumnSpan = columns;
            placement.RowSpan = rows;
        }

        EvictFromUnder(spot.Layout, surface, placement);
        TrySaveLayout();
        RelayoutTiles();
    }

    private void ResetWidgetSize(IWidget widget)
    {
        if (_layout.Locate(WidgetRegistry.ToPlacementId(widget)) is not { } spot) return;
        if (SurfaceFor(spot.Layout) is not { } surface) return;

        var placement = spot.Placement;
        var (columns, rows) = widget.DefaultSpan;
        var gap = App.Settings.GridGap * surface.Scale;

        if (placement.Unlocked && placement.Free is { } free)
        {
            free.Width = columns * surface.CellWidth - gap;
            free.Height = rows * surface.CellHeight - gap;
            UpdateCoveredCells(placement, surface);
        }
        else
        {
            placement.ColumnSpan = Math.Max(1, Math.Min(columns, surface.Columns - placement.Column));
            placement.RowSpan = Math.Max(1, Math.Min(rows, surface.Rows - placement.Row));
        }

        EvictFromUnder(spot.Layout, surface, placement);
        TrySaveLayout();
        RelayoutTiles();
    }

    // ---- Resizing widgets -----------------------------------------------

    private void BeginResize(WidgetFrame frame, Point position)
    {
        if (WidgetRegistry.FromPlacementId(frame.PlacementId) is not { } widget) return;
        if (!_tileSurfaces.TryGetValue(frame.PlacementId, out var surface)) return;

        _resize = new ResizeSession
        {
            Frame = frame,
            Widget = widget,
            Surface = surface,
            StartSize = new Size(frame.ActualWidth, frame.ActualHeight),
            StartPointer = position,
        };

        frame.IsResizing = true;
        ItemCanvas.CaptureMouse();
    }

    /// <summary>
    /// Live preview while the grip is dragged. Locked widgets snap to whole
    /// cells as they go; unlocked ones follow the pointer, never shrinking
    /// below the widget's minimum and never growing off their display.
    /// </summary>
    private void UpdateResize(Point position)
    {
        if (_resize is not { } r) return;

        var surface = r.Surface;
        var gap = App.Settings.GridGap * surface.Scale;
        var (minColumns, minRows) = r.Widget.MinimumSpan;
        var minWidth = minColumns * surface.CellWidth - gap;
        var minHeight = minRows * surface.CellHeight - gap;

        var left = Canvas.GetLeft(r.Frame);
        var top = Canvas.GetTop(r.Frame);
        var maxWidth = Math.Max(minWidth, surface.LocalBounds.Right - left);
        var maxHeight = Math.Max(minHeight, surface.LocalBounds.Bottom - top);

        var width = Math.Clamp(r.StartSize.Width + (position.X - r.StartPointer.X), minWidth, maxWidth);
        var height = Math.Clamp(r.StartSize.Height + (position.Y - r.StartPointer.Y), minHeight, maxHeight);

        if (!r.Frame.IsUnlocked && _layout.Locate(r.Frame.PlacementId) is { } spot)
        {
            var maxColumns = Math.Max(minColumns, surface.Columns - spot.Placement.Column);
            var maxRows = Math.Max(minRows, surface.Rows - spot.Placement.Row);
            r.Columns = Math.Clamp((int)Math.Round((width + gap) / surface.CellWidth), minColumns, maxColumns);
            r.Rows = Math.Clamp((int)Math.Round((height + gap) / surface.CellHeight), minRows, maxRows);
            width = r.Columns * surface.CellWidth - gap;
            height = r.Rows * surface.CellHeight - gap;
        }

        r.Frame.Width = width;
        r.Frame.Height = height;
    }

    private void CommitResize(ResizeSession r)
    {
        r.Frame.IsResizing = false;
        if (_layout.Locate(r.Frame.PlacementId) is not { } spot)
        {
            RelayoutTiles();
            return;
        }

        var placement = spot.Placement;

        if (placement.Unlocked)
        {
            placement.Free ??= new FreeRect
            {
                X = Canvas.GetLeft(r.Frame) - r.Surface.LocalBounds.X,
                Y = Canvas.GetTop(r.Frame) - r.Surface.LocalBounds.Y,
            };
            placement.Free.Width = r.Frame.Width;
            placement.Free.Height = r.Frame.Height;
            UpdateCoveredCells(placement, r.Surface);
        }
        else if (r.Columns > 0 && r.Rows > 0)
        {
            // Another widget in the new area blocks the resize; icons step aside.
            for (var y = placement.Row; y < placement.Row + r.Rows; y++)
            {
                for (var x = placement.Column; x < placement.Column + r.Columns; x++)
                {
                    if (OccupantAt(spot.Layout, x, y, placement.ItemId) is { } blocker &&
                        WidgetRegistry.FromPlacementId(blocker.ItemId) is not null)
                    {
                        RelayoutTiles();
                        return;
                    }
                }
            }

            placement.ColumnSpan = r.Columns;
            placement.RowSpan = r.Rows;
        }

        EvictFromUnder(spot.Layout, r.Surface, placement);
        TrySaveLayout();
        RelayoutTiles();
    }


    /// <summary>
    /// Empties a folder back onto the grid, its items taking the folder's cell
    /// and the free cells nearest it.
    /// </summary>
    private void Ungroup(GridGroup group)
    {
        var located = _layout.Locate(group.PlacementId);
        if (located is not { } spot) return;

        var surface = _surfaces.FirstOrDefault(sf =>
            string.Equals(sf.Info.DeviceId, spot.Layout.MonitorId, StringComparison.OrdinalIgnoreCase));

        var items = group.ItemIds.ToList();
        spot.Layout.Placements.Remove(spot.Placement);
        _layout.Groups.Remove(group);

        foreach (var id in items)
        {
            var cell = surface is null ? null : NearestFreeCell(spot.Layout, surface, spot.Placement.Column, spot.Placement.Row);
            if (cell is null) continue; // Reconcile will find it a home elsewhere
            spot.Layout.Placements.Add(new GridPlacement { ItemId = id, Column = cell.Value.Column, Row = cell.Value.Row });
        }

        TrySaveLayout();
        RelayoutTiles();
    }

    private void ShowBackgroundMenu(Point at)
    {
        var settings = App.Settings;
        var menu = new ContextMenu { PlacementTarget = ItemCanvas };

        // Adding things is the most common reason to right-click empty space.
        menu.Items.Add(MenuItemFor("Add apps...", () => OpenAddApps(at)));
        menu.Items.Add(MenuItemFor("Open Start menu", () => Hosting.StartMenuController.Current?.Open()));
        menu.Items.Add(MenuItemFor("Settings...", () => SettingsScreen.SettingsWindow.Open()));

        if (Tablet.TabletMode.Current is { } tablet)
        {
            var mode = new MenuItem { Header = tablet.IsActive ? "Tablet mode (on)" : "Tablet mode (off)" };
            mode.Items.Add(MenuItemFor(tablet.IsActive ? "Leave tablet mode now" : "Enter tablet mode now", tablet.Toggle));
            mode.Items.Add(new Separator());
            foreach (var (value, label) in new[]
                     {
                         (TabletModeSetting.Auto, "Auto (follow the keyboard and mouse)"),
                         (TabletModeSetting.On, "Always on"),
                         (TabletModeSetting.Off, "Always off"),
                     })
            {
                mode.Items.Add(CheckableMenuItem(label, tablet.Settings.Mode == value, () => tablet.SetMode(value)));
            }
            mode.Items.Add(new Separator());
            mode.Items.Add(MenuItemFor("Tablet settings...", () => SettingsScreen.SettingsWindow.Open("tablet")));
            menu.Items.Add(mode);
        }
        menu.Items.Add(new Separator());

        // Shape is the single highest-leverage setting, so it comes first.
        var shapes = new MenuItem { Header = "Icon shape" };
        foreach (var shape in Enum.GetValues<IconShapeKind>())
        {
            shapes.Items.Add(CheckableMenuItem(shape.ToString(), settings.IconShape == shape, () =>
            {
                settings.IconShape = shape;
                ApplySettingsChange(reRenderIcons: true);
            }));
        }
        menu.Items.Add(shapes);

        var sizes = new MenuItem { Header = "Icon size" };
        foreach (var size in new double[] { 48, 56, 64, 72, 88, 104 })
        {
            sizes.Items.Add(CheckableMenuItem($"{size:F0} px", Math.Abs(settings.IconSize - size) < 0.5, () =>
            {
                settings.IconSize = size;
                ApplySettingsChange(reRenderIcons: true);
            }));
        }
        menu.Items.Add(sizes);

        var style = new MenuItem { Header = "Home style" };
        foreach (var value in Enum.GetValues<HomeStyle>())
        {
            style.Items.Add(CheckableMenuItem(value.ToString(), settings.Style == value, () =>
            {
                settings.Style = value;
                ApplySettingsChange(reRenderIcons: false);
            }));
        }
        menu.Items.Add(style);

        var widgets = new MenuItem { Header = "Widgets" };
        foreach (var widget in WidgetRegistry.All)
        {
            var present = HasWidget(widget);
            widgets.Items.Add(CheckableMenuItem(widget.Title, present, () =>
            {
                if (present)
                {
                    RemoveWidget(widget);
                    return;
                }

                // Widgets that cannot show anything until configured ask first;
                // cancelling the setup cancels adding the widget.
                if (widget is IConfigurableWidget { NeedsSetup: true } setup && !setup.Configure()) return;

                if (!AddWidget(widget))
                {
                    MessageBox.Show(
                        $"There is no free space on the grid for the {widget.Title} widget " +
                        $"({widget.DefaultSpan.Columns}x{widget.DefaultSpan.Rows} cells).",
                        "Hearth", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }));
        }
        menu.Items.Add(widgets);

        menu.Items.Add(new Separator());

        menu.Items.Add(CheckableMenuItem("Show labels", settings.ShowLabels, () =>
        {
            settings.ShowLabels = !settings.ShowLabels;
            ApplySettingsChange(reRenderIcons: false);
        }));

        menu.Items.Add(CheckableMenuItem("Uniform backgrounds", settings.UniformBackgrounds, () =>
        {
            settings.UniformBackgrounds = !settings.UniformBackgrounds;
            ApplySettingsChange(reRenderIcons: true);
        }));

        menu.Items.Add(CheckableMenuItem("Show installed apps", settings.IncludeInstalledApps, async () =>
        {
            settings.IncludeInstalledApps = !settings.IncludeInstalledApps;
            settings.Save();
            await RefreshItemsAsync().ConfigureAwait(true);
        }));

        var hidden = new MenuItem { Header = "Hidden items" };
        if (_layout.Hidden.Count == 0)
        {
            hidden.Items.Add(new MenuItem { Header = "Nothing is hidden", IsEnabled = false });
        }
        else
        {
            foreach (var id in _layout.Hidden.ToList())
            {
                var name = _items.TryGetValue(id, out var hiddenItem) ? hiddenItem.DisplayName : Path.GetFileName(id);
                hidden.Items.Add(MenuItemFor($"Show {name}", () => UnhideItem(id)));
            }
            hidden.Items.Add(new Separator());
            hidden.Items.Add(MenuItemFor("Show everything", UnhideAll));
        }
        menu.Items.Add(hidden);

        menu.Items.Add(CheckableMenuItem("Use Hearth's Start menu", settings.ReplaceStartMenu, () =>
            Hosting.StartMenuController.Current?.SetReplaceStart(!settings.ReplaceStartMenu)));

        menu.Items.Add(CheckableMenuItem("Hide Windows desktop icons", settings.HideShellIcons, () =>
        {
            settings.HideShellIcons = !settings.HideShellIcons;
            if (settings.HideShellIcons) _layer.HideShellIcons();
            else _layer.RestoreShellIcons();
            settings.Save();
        }));

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor("Rebuild all icons", () =>
        {
            App.Icons.ClearDiskCache();
            RelayoutTiles();
        }));

        menu.Items.Add(MenuItemFor("Refresh", async () => await RefreshItemsAsync().ConfigureAwait(true)));
        menu.Items.Add(MenuItemFor("Show more options", ShowShellBackgroundMenu));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Exit Hearth", () =>
        {
            DetachFromDesktop();
            Application.Current.Shutdown();
        }));

        OpenMenu(menu);
    }

    internal void ApplySettingsChange(bool reRenderIcons)
    {
        App.Settings.Save();

        // Icon geometry is part of the cache key, so a shape or size change
        // needs the in-memory tiles dropped; the disk entries for the new
        // settings are still valid and will be reused if they exist.
        if (reRenderIcons) App.Icons.ClearMemoryCache();

        RebuildEverything();
    }

    /// <summary>
    /// Opens a context menu so that it will actually close again.
    ///
    /// Clicking the desktop already closes the menu: WPF captures the mouse
    /// within its own subtree and sees that click. What it cannot see is a
    /// click that lands on another application, because Hearth never activates
    /// and so is never told it was deactivated.
    ///
    /// Foregrounding the popup was the obvious fix and is wrong — WPF reads the
    /// resulting focus change as a dismissal and the menu vanishes the instant
    /// it appears. So leave focus alone and just watch: if the foreground
    /// window changes while the menu is up, the user has gone somewhere else
    /// and the menu should follow.
    /// </summary>
    internal static void OpenMenu(ContextMenu menu)
    {
        var foregroundAtOpen = Win32.GetForegroundWindow();

        var watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };

        watchdog.Tick += (_, _) =>
        {
            if (Win32.GetForegroundWindow() == foregroundAtOpen) return;
            watchdog.Stop();
            menu.IsOpen = false;
        };

        menu.Closed += (_, _) => watchdog.Stop();

        menu.IsOpen = true;
        watchdog.Start();
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem MenuItemFor(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action().ConfigureAwait(true);
        return item;
    }

    private static MenuItem CheckableMenuItem(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem CheckableMenuItem(string header, bool isChecked, Func<Task> action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += async (_, _) => await action().ConfigureAwait(true);
        return item;
    }

    // ---- Drops from Explorer --------------------------------------------

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var destination = _desktopCatalog.UserDesktop;
        var copied = false;

        foreach (var path in paths)
        {
            try
            {
                var target = Path.Combine(destination, Path.GetFileName(path));

                // Already on the desktop: the watcher has it, nothing to do.
                if (string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(target) || Directory.Exists(target)) continue;

                // Copy rather than move. A move is what Explorer does within a
                // volume, but getting that wrong relocates a user's file on a
                // mis-drop, and there is no undo here yet.
                if (Directory.Exists(path)) continue;

                File.Copy(path, target);
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Hearth] drop failed for '{path}': {ex.Message}");
            }
        }

        if (copied) await RefreshItemsAsync().ConfigureAwait(true);
    }
}
