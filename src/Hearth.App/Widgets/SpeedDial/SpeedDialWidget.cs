using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.App.Views;
using Hearth.Core.Icons;
using Microsoft.Win32;

namespace Hearth.App.Widgets.SpeedDial;

/// <summary>
/// A compact launcher for apps, websites, files and folders, drawn with the
/// home screen's icon style. Websites get a lettered tile in the same shape.
/// </summary>
public sealed class SpeedDialWidget : IWidget
{
    public string Id => "speeddial";
    public string Title => "Speed Dial";
    public (int Columns, int Rows) DefaultSpan => (4, 1);
    public (int Columns, int Rows) MinimumSpan => (2, 1);
    public double BoardHeight(bool wide) => wide ? 110 : 190;
    public int Order => 160;

    public FrameworkElement CreateView(WidgetContext context) => new SpeedDialView(context);

    private sealed class SpeedDialView : WidgetView
    {
        private const double MinTile = 70;

        private readonly WidgetHeader _header;
        private readonly WrapPanel _tiles = new();
        private readonly ScrollViewer _scroller;
        private bool _singleRow;

        public SpeedDialView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE734", "Speed Dial");
            GlyphButton? add = null;
            add = _header.AddAction("\uE710", "Add", () => ShowAddMenu(add!));

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            _scroller = WidgetLayout.Scroller(_tiles);
            layout.Children.Add(_scroller);
            SetBody(layout);

            // Short placements drop the header and become one row that
            // scrolls sideways; the + tile at the end stays.
            layout.SizeChanged += (_, _) =>
            {
                _singleRow = layout.ActualHeight < 150 * S;
                _header.Visibility = _singleRow ? Visibility.Collapsed : Visibility.Visible;
                _scroller.HorizontalScrollBarVisibility = _singleRow ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
                _scroller.VerticalScrollBarVisibility = _singleRow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
                SizeTiles();
            };
            _scroller.PreviewMouseWheel += (_, e) =>
            {
                if (!_singleRow) return;
                _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

            While(() => SpeedDialList.Changed += OnChanged, () => SpeedDialList.Changed -= OnChanged);
        }

        protected override void OnShown() => Rebuild();

        private void OnChanged() => Post(Rebuild);

        private double TileWidth
        {
            get
            {
                if (_singleRow) return MinTile * S;
                var width = _scroller.ActualWidth > 0 ? _scroller.ActualWidth : Context.PixelSize.Width - 32 * S;
                var columns = Math.Max(1, Math.Floor(width / (MinTile * S)));
                return Math.Floor(width / columns);
            }
        }

        private void SizeTiles()
        {
            var width = TileWidth;
            foreach (FrameworkElement tile in _tiles.Children) tile.Width = width;
        }

        private void Rebuild()
        {
            _tiles.Children.Clear();
            var entries = SpeedDialList.Entries;
            for (var i = 0; i < entries.Count; i++) _tiles.Children.Add(Tile(entries[i], i, entries.Count));

            Pressable? plus = null;
            plus = TileShell(AddIcon(), entries.Count == 0 ? "Add" : string.Empty, () => ShowAddMenu(plus!));
            plus.ToolTip = "Add an app, website, file or folder";
            _tiles.Children.Add(plus);
            SizeTiles();
        }

        private FrameworkElement Tile(DialEntry entry, int index, int count)
        {
            var icon = entry.Kind == DialKind.Web ? Monogram(entry) : WidgetFiles.Icon(Context, entry.ToItem(), IconPixels);
            var tile = TileShell(icon, entry.Name, entry.Launch);
            tile.ToolTip = $"{entry.Name}\n{entry.Target}";
            if (entry.Kind == DialKind.Path) tile.DragData = () => WidgetFiles.DragData(entry.Target);
            tile.ContextRequested = () => WidgetMenu.Show(tile, menu =>
            {
                menu.Items.Add(WidgetMenu.Item("Open", entry.Launch));
                if (index > 0) menu.Items.Add(WidgetMenu.Item("Move left", () => SpeedDialList.Move(entry.Id, -1)));
                if (index < count - 1) menu.Items.Add(WidgetMenu.Item("Move right", () => SpeedDialList.Move(entry.Id, 1)));
                menu.Items.Add(new Separator());
                menu.Items.Add(WidgetMenu.Item("Remove from Speed Dial", () => SpeedDialList.Remove(entry.Id)));
            });
            return tile;
        }

        private double IconPixels => 40 * S;

        private Pressable TileShell(FrameworkElement icon, string label, Action? onClick)
        {
            var name = WidgetChrome.Text(Context, label, 11);
            name.TextAlignment = TextAlignment.Center;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 4 * S, 0, 0);
            name.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            icon.HorizontalAlignment = HorizontalAlignment.Center;

            return new Pressable(onClick)
            {
                CornerRadius = new CornerRadius(10 * S),
                Padding = new Thickness(4 * S, 5 * S, 4 * S, 4 * S),
                Child = new StackPanel { Children = { icon, name } },
            };
        }

        private FrameworkElement AddIcon() => new Border
        {
            Width = IconPixels,
            Height = IconPixels,
            CornerRadius = new CornerRadius(IconPixels / 2),
            BorderBrush = WidgetChrome.Faint,
            BorderThickness = new Thickness(1.5 * S),
            Child = WidgetChrome.Glyph(Context, "\uE710", 14, WidgetChrome.Secondary),
        };

        /// <summary>A website's first letter on a colour picked from its host, in the home screen's icon shape.</summary>
        private FrameworkElement Monogram(DialEntry entry)
        {
            var host = Uri.TryCreate(entry.Target, UriKind.Absolute, out var uri) ? uri.Host : entry.Target;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            var letter = (entry.Name.Length > 0 ? entry.Name : host).Trim()[..1].ToUpperInvariant();

            // String hash codes change every run; this one mustn't.
            var hash = 2166136261u;
            foreach (var c in host.ToLowerInvariant()) hash = (hash ^ c) * 16777619u;
            var hue = hash % 360;
            var fill = WidgetChrome.Frozen(new SolidColorBrush(FromHsl(hue, 0.55, Context.DarkTheme ? 0.42 : 0.50)));

            var text = WidgetChrome.Text(Context, letter, 18, Brushes.White, FontWeights.SemiBold, display: true);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;

            return new Grid
            {
                Width = IconPixels,
                Height = IconPixels,
                Background = fill,
                Clip = IconShape.Get(App.Settings.IconShape, IconPixels),
                Children = { text },
            };
        }

        private static Color FromHsl(double hue, double saturation, double lightness)
        {
            var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
            var m = lightness - c / 2;
            var (r, g, b) = hue switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        private void ShowAddMenu(FrameworkElement anchor) => WidgetMenu.Show(anchor, menu =>
        {
            menu.Items.Add(WidgetMenu.Item("Apps...", () =>
                new AddAppsWindow(new SpeedDialList.AppPicker(), "Speed Dial apps",
                    "Click an app to add it to Speed Dial. Click it again to take it off.").ShowDialog()));
            menu.Items.Add(WidgetMenu.Item("Website...", () =>
            {
                var window = new WebsiteWindow();
                if (window.ShowDialog() == true) SpeedDialList.Add(DialKind.Web, window.SiteName, window.Address);
            }));
            menu.Items.Add(WidgetMenu.Item("File...", () =>
            {
                var dialog = new OpenFileDialog { Title = "Add a file to Speed Dial", Multiselect = true };
                if (dialog.ShowDialog() != true) return;
                foreach (var file in dialog.FileNames) SpeedDialList.AddPath(file);
            }));
            menu.Items.Add(WidgetMenu.Item("Folder...", () =>
            {
                var dialog = new OpenFolderDialog { Title = "Add a folder to Speed Dial", Multiselect = true };
                if (dialog.ShowDialog() != true) return;
                foreach (var folder in dialog.FolderNames) SpeedDialList.AddPath(folder);
            }));
        });
    }
}
