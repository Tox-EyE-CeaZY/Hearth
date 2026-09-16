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

        var index = 0;
        foreach (var id in group.ItemIds)
        {
            if (!_items.TryGetValue(id, out var item)) continue;

            var tile = CreateTile(item, surface, options);
            Canvas.SetLeft(tile, index % columns * surface.CellWidth);
            Canvas.SetTop(tile, index / columns * surface.CellHeight);
            tilesCanvas.Children.Add(tile);
            index++;
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
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.92, 0.92),
            Opacity = 0,
        };

        var estimatedHeight = contentHeight + padding * 2.6 + 30 * scale;
        Canvas.SetLeft(_folderPanel, surface.LocalBounds.X + (surface.LocalBounds.Width - _folderPanel.Width) / 2);
        Canvas.SetTop(_folderPanel, surface.LocalBounds.Y + Math.Max(padding, (surface.LocalBounds.Height - estimatedHeight) / 2));

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

        // A short grow-and-fade: enough to read as "opening", never enough to
        // make someone wait. Animating a transform and opacity stays on the
        // composition fast path.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        var grow = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        var fade = new DoubleAnimation(1.0, duration) { EasingFunction = ease };

        _folderPanel.BeginAnimation(OpacityProperty, fade);
        scrim.BeginAnimation(OpacityProperty, fade);
        ((ScaleTransform)_folderPanel.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)_folderPanel.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
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
    /// Pulls an item out of the open folder: the panel closes, and the item
    /// continues as an ordinary drag that lands through CompleteDrop.
    /// </summary>
    private void BeginFolderDrag(GridGroup folder, Point position)
    {
        if (_pressedElement is not IconTile { Item: { } item } tile || _folderSurface is not { } surface) return;

        var topLeft = tile.TranslatePoint(new Point(0, 0), ItemCanvas);
        var ghost = MakeGhost(Snapshot(tile), tile.ActualWidth, tile.ActualHeight);

        _drag = new DragSession
        {
            PlacementId = item.Id,
            FromFolder = folder,
            Ghost = ghost,
            Grab = position - topLeft,
            Footprint = new Size(tile.ActualWidth, tile.ActualHeight),
            Scale = surface.Scale,
        };

        _pressedElement = null;
        CloseFolder();

        OverlayCanvas.Children.Add(ghost);
        MoveGhost(position);
    }

    private void LaunchFromFolder(IconTile tile)
    {
        if (tile.Item is null) return;
        Hearth.Core.Shell.ShellLauncher.Launch(tile.Item);
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
