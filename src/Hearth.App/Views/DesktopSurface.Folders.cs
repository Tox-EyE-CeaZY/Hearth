using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Hearth.App.Controls;
using Hearth.App.Hosting;
using Hearth.Core.Layout;

namespace Hearth.App.Views;

/// <summary>
/// Home-screen folders: the panel that opens when a folder tile is clicked.
///
/// These are Android/iOS folders, not Explorer folders — an arrangement of
/// launcher items, with nothing moved on disk. The panel opens over a dimmed
/// desktop on the display the folder lives on; clicking outside closes it,
/// clicking an item launches it, and dragging an item out drops it back onto
/// the grid through the same drop rules as any other drag.
/// </summary>
public partial class DesktopSurface
{
    private GridGroup? _openFolder;
    private MonitorSurface? _folderSurface;
    private GridGroup? _pressedFromFolder;
    private Border? _folderPanel;
    private TextBox? _folderTitle;

    /// <summary>The open folder's tiles in their current order, and their grid.</summary>
    private readonly List<IconTile> _folderTiles = [];
    private Canvas? _folderGrid;
    private int _folderColumns;
    private bool _renaming;

    private static readonly Brush FolderScrim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush FolderPanelFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1E, 0x23)));
    private static readonly Brush FolderPanelEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TitleSelection = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0xA8, 0xFF)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void OpenFolder(GridGroup group, MonitorSurface surface)
    {
        CloseFolder();
        _openFolder = group;
        _folderSurface = surface;

        var scale = surface.Scale;
        var count = Math.Max(1, group.ItemIds.Count);

        // Square-ish, like Android: 2 columns for a pair, growing to 5.
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(count)), 2, 5);
        var rows = (int)Math.Ceiling(count / (double)columns);

        var gap = App.Settings.GridGap * scale;
        var padding = 26 * scale;
        var contentWidth = columns * surface.CellWidth - gap;
        var contentHeight = rows * surface.CellHeight - gap;

        var tilesCanvas = new Canvas { Width = contentWidth, Height = contentHeight };
        var options = App.Settings.ToRenderOptions(scale);

        _folderGrid = tilesCanvas;
        _folderColumns = columns;
        _folderTiles.Clear();

        foreach (var id in group.ItemIds)
        {
            if (!_items.TryGetValue(id, out var item)) continue;

            var tile = CreateTile(item, surface, options);
            Canvas.SetLeft(tile, _folderTiles.Count % columns * surface.CellWidth);
            Canvas.SetTop(tile, _folderTiles.Count / columns * surface.CellHeight);
            tilesCanvas.Children.Add(tile);
            _folderTiles.Add(tile);
        }

        _folderTitle = new TextBox
        {
            Text = group.Name,
            FontSize = 22 * scale,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = TitleSelection,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, padding * 0.6),
            Cursor = Cursors.IBeam,
            ToolTip = "Click to rename",
        };
        _folderTitle.PreviewMouseLeftButtonDown += (_, _) => BeginRename(selectAll: false);
        _folderTitle.KeyDown += OnTitleKeyDown;
        _folderTitle.LostKeyboardFocus += (_, _) => CommitRename();

        var stack = new StackPanel();
        stack.Children.Add(_folderTitle);
        stack.Children.Add(tilesCanvas);

        _folderPanel = new Border
        {
            Width = contentWidth + padding * 2,
            Background = FolderPanelFill,
            BorderBrush = FolderPanelEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28 * scale),
            Padding = new Thickness(padding),
            Child = stack,
            Opacity = 0,
        };

        var estimatedHeight = contentHeight + padding * 2.6 + 30 * scale;
        var bounds = surface.LocalBounds;
        var panelLeft = bounds.X + (bounds.Width - _folderPanel.Width) / 2;
        var panelTop = bounds.Y + Math.Max(padding, (bounds.Height - estimatedHeight) / 2);

        _tiles.TryGetValue(group.PlacementId, out var folderTile);
        var hasTile = folderTile is { ActualWidth: > 0 };
        var from = hasTile ? folderTile!.TranslatePoint(new Point(0, 0), ItemCanvas) : default;

        // Unless the user wants folders centred, the panel opens over its own
        // tile, kept inside the display, so the pointer is already among the
        // items instead of having to travel to the middle of the screen.
        if (hasTile && !App.Settings.CenterOpenFolders)
        {
            var tileCentre = from + new Vector(folderTile!.ActualWidth / 2, folderTile.ActualHeight / 2);
            panelLeft = Math.Clamp(tileCentre.X - _folderPanel.Width / 2,
                bounds.X + padding, Math.Max(bounds.X + padding, bounds.Right - padding - _folderPanel.Width));
            panelTop = Math.Clamp(tileCentre.Y - estimatedHeight / 2,
                bounds.Y + padding, Math.Max(bounds.Y + padding, bounds.Bottom - padding - estimatedHeight));
        }

        Canvas.SetLeft(_folderPanel, panelLeft);
        Canvas.SetTop(_folderPanel, panelTop);

        // The panel starts on top of the folder tile, the size of the tile, and
        // grows out to where it opens, like an app opening from its icon.
        // Scaling about the top-left corner keeps the arithmetic to one offset;
        // the panel's height is not known yet, and does not need to be.
        var zoom = new ScaleTransform(0.92, 0.92);
        var shift = new TranslateTransform();
        _folderPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        if (hasTile)
        {
            zoom.ScaleX = zoom.ScaleY = folderTile!.ActualWidth / _folderPanel.Width;
            shift.X = from.X - panelLeft;
            shift.Y = from.Y - panelTop;
            _folderPanel.RenderTransformOrigin = new Point(0, 0);
        }
        _folderPanel.RenderTransform = new TransformGroup { Children = { zoom, shift } };

        var scrim = new Rectangle
        {
            Width = ItemCanvas.ActualWidth,
            Height = ItemCanvas.ActualHeight,
            Fill = FolderScrim,
            Opacity = 0,
        };

        FolderLayer.Children.Add(scrim);
        FolderLayer.Children.Add(_folderPanel);
        FolderLayer.Visibility = Visibility.Visible;

        // A short grow-and-move: long enough to see where the panel came from,
        // never enough to make someone wait. The fade is quicker than the
        // motion, so the panel is visible for most of its flight. Animating a
        // transform and opacity stays on the composition fast path.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(100)) { EasingFunction = ease };

        _folderPanel.BeginAnimation(OpacityProperty, fade);
        scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, duration) { EasingFunction = ease });
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, duration) { EasingFunction = ease });
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, duration) { EasingFunction = ease });
    }

    private void CloseFolder()
    {
        if (_openFolder is null) return;

        CommitRename();

        FolderLayer.Children.Clear();
        FolderLayer.Visibility = Visibility.Collapsed;
        _openFolder = null;
        _folderSurface = null;
        _folderPanel = null;
        _folderTitle = null;
        _folderGrid = null;
        _folderTiles.Clear();
    }

    // ---- Pointer inside an open folder ----------------------------------

    private void OnFolderPress(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_folderPanel is null || source is null || !IsWithin(source, _folderPanel))
        {
            CloseFolder();
            return;
        }

        if (FindAncestor<IconTile>(source) is not { Item: not null } tile)
        {
            // Panel background: a click away from the name finishes a rename.
            CommitRename();
            return;
        }

        _pressedElement = tile;
        _pressedId = tile.Item.Id;
        _pressedFromFolder = _openFolder;
        _pressOrigin = e.GetPosition(ItemCanvas);
        ItemCanvas.CaptureMouse();
    }

    private void OnFolderRightClick(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (_folderPanel is null || source is null || !IsWithin(source, _folderPanel))
        {
            CloseFolder();
            return;
        }

        if (FindAncestor<IconTile>(source) is { Item: { } item } && _openFolder is { } folder)
            ShowItemMenu(item, folder);
    }

    /// <summary>
    /// Picks an item up inside the open folder. The panel stays open while the
    /// pointer is over it, and the item reorders within the folder, as on
    /// Android; UpdateFolderDrag closes the panel once the pointer leaves it,
    /// and from then on the item is an ordinary drag that lands through
    /// CompleteDrop.
    /// </summary>
    private void BeginFolderDrag(GridGroup folder, Point position)
    {
        if (_pressedElement is not IconTile { Item: { } item } tile || _folderSurface is not { } surface) return;

        var topLeft = tile.TranslatePoint(new Point(0, 0), ItemCanvas);
        var ghost = MakeGhost(Snapshot(tile), tile.ActualWidth, tile.ActualHeight);

        _drag = new DragSession
        {
            PlacementId = item.Id,
            Origin = tile,
            FromFolder = folder,
            Ghost = ghost,
            Grab = position - topLeft,
            Footprint = new Size(tile.ActualWidth, tile.ActualHeight),
            Scale = surface.Scale,
        };

        _pressedElement = null;

        // The tile stays in its slot, dimmed, marking where the item will land.
        tile.Opacity = 0.35;
        OverlayCanvas.Children.Add(ghost);
        MoveGhost(position);
    }

    /// <summary>
    /// A drag out of a folder while the panel is still open: over the panel
    /// the item takes the slot under it and the others shuffle along; off the
    /// panel, the folder closes and the item heads for the desktop.
    /// </summary>
    private void UpdateFolderDrag(DragSession drag, Point position)
    {
        if (_folderPanel is null || _folderGrid is null || _folderSurface is not { } surface) return;

        var overPanel = ItemCanvas.TranslatePoint(position, _folderPanel);
        if (overPanel.X < 0 || overPanel.Y < 0 ||
            overPanel.X > _folderPanel.ActualWidth || overPanel.Y > _folderPanel.ActualHeight)
        {
            CloseFolder();
            UpdateDropTarget(position);
            return;
        }

        if (drag.Origin is not IconTile dragged) return;
        var from = _folderTiles.IndexOf(dragged);
        if (from < 0) return;

        // Aim with the ghost's centre, not the pointer: that is what the user sees.
        var centre = position - drag.Grab + new Vector(drag.Footprint.Width / 2, drag.Footprint.Height / 2);
        var inGrid = ItemCanvas.TranslatePoint(centre, _folderGrid);
        var rows = (int)Math.Ceiling(_folderTiles.Count / (double)_folderColumns);
        var column = Math.Clamp((int)Math.Floor(inGrid.X / surface.CellWidth), 0, _folderColumns - 1);
        var row = Math.Clamp((int)Math.Floor(inGrid.Y / surface.CellHeight), 0, rows - 1);
        var to = Math.Min(row * _folderColumns + column, _folderTiles.Count - 1);
        if (to == from) return;

        _folderTiles.RemoveAt(from);
        _folderTiles.Insert(to, dragged);
        ArrangeFolderTiles();
    }

    /// <summary>Slides every tile in the open folder to the slot its order gives it.</summary>
    private void ArrangeFolderTiles()
    {
        if (_folderSurface is not { } surface) return;

        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (var i = 0; i < _folderTiles.Count; i++)
        {
            var tile = _folderTiles[i];
            tile.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(i % _folderColumns * surface.CellWidth, duration) { EasingFunction = ease });
            tile.BeginAnimation(Canvas.TopProperty,
                new DoubleAnimation(i / _folderColumns * surface.CellHeight, duration) { EasingFunction = ease });
        }
    }

    /// <summary>Puts the open folder's tiles back in the folder's saved order.</summary>
    private void ResetFolderOrder()
    {
        if (_openFolder is null) return;

        var saved = _openFolder.ItemIds;
        _folderTiles.Sort((a, b) => saved.IndexOf(a.Item!.Id).CompareTo(saved.IndexOf(b.Item!.Id)));
        ArrangeFolderTiles();
    }

    /// <summary>
    /// A drag that ended inside the open folder: saves the order the tiles are
    /// now in. Ids with no tile (an item not currently loaded) keep their
    /// place at the end rather than being dropped from the folder.
    /// </summary>
    private void CommitFolderOrder(GridGroup folder)
    {
        var shown = _folderTiles.Select(t => t.Item!.Id).ToList();
        var rest = folder.ItemIds.Where(id => !shown.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var order = shown.Concat(rest).ToList();
        if (order.SequenceEqual(folder.ItemIds, StringComparer.OrdinalIgnoreCase)) return;

        folder.ItemIds.Clear();
        folder.ItemIds.AddRange(order);
        TrySaveLayout();

        // The folder tile previews the first few items, so redraw the grid;
        // the open panel is on its own layer and is left as it is.
        RelayoutTiles();
    }

    private void LaunchFromFolder(IconTile tile)
    {
        if (tile.Item is null) return;
        Services.AppLauncher.Launch(tile.Item);
        CloseFolder();
    }

    private void RemoveFromFolder(GridGroup group, string itemId)
    {
        var spot = _layout.Locate(group.PlacementId);
        group.ItemIds.Remove(itemId);

        if (spot is { } located)
        {
            var surface = _surfaces.FirstOrDefault(sf =>
                string.Equals(sf.Info.DeviceId, located.Layout.MonitorId, StringComparison.OrdinalIgnoreCase));
            var cell = surface is null
                ? null
                : NearestFreeCell(located.Layout, surface, located.Placement.Column, located.Placement.Row);

            // No free cell nearby: Reconcile places it wherever there is room.
            if (cell is { } free)
                located.Layout.Placements.Add(new GridPlacement { ItemId = itemId, Column = free.Column, Row = free.Row });
        }

        CloseFolder();
        TrySaveLayout();
        RelayoutTiles();
    }

    // ---- Renaming -------------------------------------------------------

    /// <summary>
    /// Hearth's window never activates, so it never receives keystrokes. For
    /// the length of a rename it is allowed to take focus, then goes back to
    /// being scenery.
    /// </summary>
    private void BeginRename(bool selectAll)
    {
        if (_folderTitle is null) return;

        if (!_renaming)
        {
            _renaming = true;
            DesktopHost.Current?.BeginKeyboardInput();
        }

        var title = _folderTitle;
        Dispatcher.BeginInvoke(() =>
        {
            title.Focus();
            Keyboard.Focus(title);
            if (selectAll) title.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_folderTitle is not null && _openFolder is not null) _folderTitle.Text = _openFolder.Name;
            CommitRename();
            e.Handled = true;
        }
    }

    private void CommitRename()
    {
        if (!_renaming) return;
        _renaming = false;

        if (_folderTitle is not null && _openFolder is not null)
        {
            var name = _folderTitle.Text.Trim();
            if (name.Length == 0) name = "Folder";
            _folderTitle.Text = name;

            if (!string.Equals(name, _openFolder.Name, StringComparison.Ordinal))
            {
                _openFolder.Name = name;
                TrySaveLayout();

                if (_tiles.TryGetValue(_openFolder.PlacementId, out var tile) && tile.Item is not null)
                {
                    tile.Item.DisplayName = name;
                    tile.ToolTip = name;
                    tile.InvalidateLabel();
                }
            }
        }

        Keyboard.ClearFocus();
        DesktopHost.Current?.EndKeyboardInput();
    }

    // ---- Tree helpers ---------------------------------------------------

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = ParentOf(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = ParentOf(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    /// <summary>
    /// Visual parent where there is one; logical parent otherwise. Text runs
    /// inside a TextBox are not Visuals, and a click can originate on one.
    /// </summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
