using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.RecentFiles;

/// <summary>
/// The files and folders you used most recently, from Windows' own Recent
/// list. Click to open, drag out to use elsewhere.
/// </summary>
public sealed class RecentFilesWidget : IWidget
{
    public string Id => "recent";
    public string Title => "Recent Files";
    public (int Columns, int Rows) DefaultSpan => (3, 3);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => 250;
    public int Order => 150;

    public FrameworkElement CreateView(WidgetContext context) => new RecentView(context);

    private sealed class RecentView : WidgetView
    {
        private const int Limit = 25;

        private readonly WidgetHeader _header;
        private readonly StackPanel _rows = new();
        private readonly FrameworkElement _empty;
        private readonly List<Chip> _filters = [];
        private FileSystemWatcher? _watcher;
        private Timer? _debounce;
        private IReadOnlyList<RecentFile> _files = [];
        private int _filter; // 0 all, 1 files, 2 folders
        private int _loading;

        public RecentView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE81C", "Recent");
            _header.AddAction("\uE838", "Open Recent in Explorer", () => ShellLauncher.Open("shell:recent"));

            var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6 * S) };
            foreach (var (label, index) in new[] { ("All", 0), ("Files", 1), ("Folders", 2) })
            {
                var chip = new Chip(context, label, () => SetFilter(index), size: 11) { Margin = new Thickness(0, 0, 6 * S, 0) };
                _filters.Add(chip);
                filterRow.Children.Add(chip);
            }

            _empty = WidgetLayout.Empty(context, "\uE81C", "Files you open show up here");

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(filterRow, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(filterRow);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_rows), _empty } });
            SetBody(layout);

            layout.SizeChanged += (_, _) =>
                filterRow.Visibility = layout.ActualHeight >= 170 * S ? Visibility.Visible : Visibility.Collapsed;

            While(StartWatching, StopWatching);
            Every(TimeSpan.FromMinutes(1), Show);
        }

        protected override void OnShown()
        {
            SetFilter(_filter);
            Reload();
        }

        private void SetFilter(int filter)
        {
            _filter = filter;
            for (var i = 0; i < _filters.Count; i++) _filters[i].IsActive = i == filter;
            Show();
        }

        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(RecentList.Folder, "*.lnk")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                };
                _watcher.Created += OnDiskChange;
                _watcher.Changed += OnDiskChange;
                _watcher.Deleted += OnDiskChange;
                _watcher.Renamed += OnDiskChange;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                Log.Write($"recent files: can't watch: {ex.Message}");
            }
        }

        private void StopWatching()
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }

        private void OnDiskChange(object sender, FileSystemEventArgs e)
        {
            _debounce ??= new Timer(_ => Post(Reload));
            _debounce.Change(1000, Timeout.Infinite);
        }

        private async void Reload()
        {
            if (Interlocked.Exchange(ref _loading, 1) == 1) return;
            try
            {
                _files = await Task.Run(() => RecentList.Read(Limit)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("recent files", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _loading, 0);
            }

            using var theme = Themed();
            Show();
        }

        private void Show()
        {
            var files = _files.Where(f => _filter == 0 || f.IsFolder == (_filter == 2)).ToList();
            _empty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _rows.Children.Clear();
            foreach (var file in files) _rows.Children.Add(Row(file));
        }

        private FrameworkElement Row(RecentFile file)
        {
            var icon = WidgetFiles.Icon(Context, WidgetFiles.ItemFor(file.Path), 26 * S);
            icon.Margin = new Thickness(0, 0, 10 * S, 0);

            var name = WidgetChrome.Text(Context, file.Name, 12.5);
            var parent = Path.GetFileName(Path.GetDirectoryName(file.Path) ?? string.Empty);
            var detail = WidgetChrome.Text(Context,
                string.IsNullOrEmpty(parent) ? WidgetFormat.Ago(file.UsedUtc) : $"{parent} · {WidgetFormat.Ago(file.UsedUtc)}",
                10.5, WidgetChrome.Secondary);

            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);
            dock.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, detail } });

            var row = new Pressable(() => ShellLauncher.Open(file.Path))
            {
                CornerRadius = new CornerRadius(8 * S),
                Padding = new Thickness(6 * S, 4 * S, 6 * S, 4 * S),
                Margin = new Thickness(-6 * S, 0, -6 * S, 0),
                Child = dock,
                ToolTip = file.Path,
                DragData = () => WidgetFiles.DragData(file.Path),
            };
            row.ContextRequested = () => WidgetMenu.Show(row, menu =>
            {
                WidgetFiles.AddMenuItems(menu, file.Path);
                menu.Items.Add(new Separator());
                menu.Items.Add(WidgetMenu.Item("Remove from Recent", () =>
                {
                    File.Delete(file.LinkPath);
                    Reload();
                }));
            });
            return row;
        }
    }
}
