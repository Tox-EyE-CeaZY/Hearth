using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.Shelf;

/// <summary>
/// A drop target for files. Drop things on it to keep them handy (copied;
/// hold Shift to move), drag them back out to wherever they're going.
/// It holds either Hearth's own stash or a real folder such as Downloads.
/// </summary>
public sealed class ShelfWidget : IWidget, IConfigurableWidget
{
    public string Id => "shelf";
    public string Title => "Shelf";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => wide ? 170 : 210;
    public int Order => 120;

    public bool NeedsSetup => false;

    public bool Configure()
    {
        var window = new ShelfSetupWindow(ShelfFolder.Config);
        if (window.ShowDialog() != true || window.Result is null) return false;
        ShelfFolder.Configure(window.Result);
        return true;
    }

    public FrameworkElement CreateView(WidgetContext context) => new ShelfView(context, this);

    private sealed class ShelfView : WidgetView
    {
        private const int Limit = 100;

        private readonly WidgetHeader _header;
        private readonly WrapPanel _tiles = new();
        private readonly FrameworkElement _empty;
        private readonly Border _dropHint;
        private readonly TextBlock _dropText;

        public ShelfView(WidgetContext context, ShelfWidget widget) : base(context)
        {
            _header = new WidgetHeader(context, "\uE7B8", "Shelf");
            _header.AddAction("\uE838", "Open folder", () => ShellLauncher.Open(ShelfFolder.Location));
            _header.AddAction("\uE713", "Shelf settings", () => widget.Configure());

            _empty = WidgetLayout.Empty(context, "\uE896", "Drop files here to keep them handy");

            _dropText = WidgetChrome.Text(context, "Drop to copy here", 13, WidgetChrome.Accent, FontWeights.SemiBold);
            _dropText.HorizontalAlignment = HorizontalAlignment.Center;
            _dropText.VerticalAlignment = VerticalAlignment.Center;
            _dropHint = new Border
            {
                BorderBrush = WidgetChrome.Accent,
                BorderThickness = new Thickness(1.5 * S),
                CornerRadius = new CornerRadius(12 * S),
                Background = WidgetChrome.Hover,
                Child = _dropText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_tiles), _empty, _dropHint } });
            SetBody(layout);

            AllowDrop = true;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;

            While(() =>
            {
                ShelfFolder.Changed += OnChanged;
                ShelfFolder.BeginWatching();
            }, () =>
            {
                ShelfFolder.Changed -= OnChanged;
                ShelfFolder.EndWatching();
            });
        }

        protected override void OnShown() => Rebuild();

        private void OnChanged() => Post(Rebuild);

        private void Rebuild()
        {
            var entries = ShelfFolder.Entries(Limit + 1);
            _header.Title = ShelfFolder.Name;
            _header.Glyph = ShelfFolder.IsStash ? "\uE7B8" : "\uE8B7";
            _header.Detail = entries.Count == 0 ? string.Empty : entries.Count > Limit ? $"{Limit}+" : entries.Count.ToString();
            _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _tiles.Children.Clear();
            foreach (var path in entries.Take(Limit)) _tiles.Children.Add(Tile(path));
        }

        private FrameworkElement Tile(string path)
        {
            var item = WidgetFiles.ItemFor(path);
            var icon = WidgetFiles.Icon(Context, item, 40 * S);
            var name = WidgetChrome.Text(Context, item.DisplayName, 11);
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.MaxHeight = name.FontSize * 2.7;
            name.Margin = new Thickness(0, 4 * S, 0, 0);

            var tile = new Pressable(() => ShellLauncher.Open(path))
            {
                Width = 78 * S,
                CornerRadius = new CornerRadius(10 * S),
                Padding = new Thickness(4 * S, 6 * S, 4 * S, 4 * S),
                Child = new StackPanel { Children = { icon, name } },
                ToolTip = $"{item.DisplayName}\nDrag it out, or click to open",
                DragData = () => WidgetFiles.DragData(path),
            };
            tile.ContextRequested = () => WidgetMenu.Show(tile, menu =>
            {
                WidgetFiles.AddMenuItems(menu, path);
                menu.Items.Add(new Separator());
                menu.Items.Add(WidgetMenu.Item(ShelfFolder.IsStash ? "Remove from shelf" : "Delete",
                    () => ShellLauncher.Recycle(path, WidgetFiles.OwnerOf(this))));
            });
            return tile;
        }

        private static string[]? DroppedPaths(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

        private static bool WantsMove(DragEventArgs e) =>
            (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && (e.AllowedEffects & DragDropEffects.Move) != 0;

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (DroppedPaths(e) is not { Length: > 0 } paths || paths.All(IsOnShelf))
            {
                e.Effects = DragDropEffects.None;
                _dropHint.Visibility = Visibility.Collapsed;
                return;
            }

            var move = WantsMove(e);
            e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
            _dropText.Text = move ? $"Drop to move to {ShelfFolder.Name}" : $"Drop to copy to {ShelfFolder.Name}";
            _dropHint.Visibility = Visibility.Visible;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            // Leaving a child raises this too; only a real exit hides the hint.
            var p = e.GetPosition(this);
            if (p.X > 0 && p.Y > 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
            _dropHint.Visibility = Visibility.Collapsed;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            _dropHint.Visibility = Visibility.Collapsed;
            if (DroppedPaths(e) is not { Length: > 0 } paths) return;

            var move = WantsMove(e);
            e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
            ShelfFolder.Add(paths, move, WidgetFiles.OwnerOf(this));
        }

        private static bool IsOnShelf(string path) =>
            string.Equals(Path.GetDirectoryName(path.TrimEnd('\\')), ShelfFolder.Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
