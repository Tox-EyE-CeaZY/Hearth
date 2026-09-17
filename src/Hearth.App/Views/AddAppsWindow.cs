using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.App.Controls;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Shell;

namespace Hearth.App.Views;

/// <summary>
/// The app drawer: every installed app, searchable, one click to put an app on
/// the home screen and another to take it off.
///
/// An ordinary window rather than a panel on the desktop, for the same reason
/// as the weather setup: Hearth's desktop window never takes keyboard focus,
/// and this is a type-to-search list.
/// </summary>
internal sealed class AddAppsWindow : Window
{
    /// <summary>What the drawer needs from the home screen.</summary>
    public interface IHome
    {
        Task<IReadOnlyList<LauncherItem>> GetInstalledAppsAsync();
        bool IsOnHome(LauncherItem app);
        void SetOnHome(LauncherItem app, bool onHome);
    }

    private const double IconPixels = 36;

    private readonly IHome _home;
    private readonly TextBox _search;
    private readonly TextBlock _searchHint;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBlock _summary;
    private readonly IconRenderOptions _iconOptions;

    private IReadOnlyList<LauncherItem> _apps = [];

    public AddAppsWindow(IHome home, string title = "Add apps",
        string intro = "Click an app to put it on your home screen. Click it again to take it off.")
    {
        _home = home;

        // Rendered exactly as the home screen renders them, so the tiles here
        // are the same cache entries the grid will use once an app is added.
        _iconOptions = App.Settings.ToRenderOptions(1.0);

        Title = $"{title} — Hearth";
        Width = 480;
        Height = 640;
        MinWidth = 360;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        DialogChrome.Apply(this);

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };

        var introText = Muted(intro);
        introText.Margin = new Thickness(0, 6, 0, 16);

        _search = new TextBox();
        _searchHint = Muted("Search apps");
        _searchHint.IsHitTestVisible = false;

        // Sits exactly where typed text starts (border 1 + padding 10 + the
        // text view's own 2), at the same size, so the caret lands on it.
        _searchHint.FontSize = 14;
        _searchHint.Margin = new Thickness(13, 0, 0, 0);
        var searchBox = new Grid { Children = { _search, _searchHint } };

        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        };
        _search.PreviewKeyDown += OnSearchKey;

        _status = Muted("Loading apps…");
        _status.Margin = new Thickness(0, 12, 0, 0);

        _list = new ListBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewKeyDown += OnListKey;

        _summary = Muted(string.Empty);

        var done = new Button { Content = "Done", MinWidth = 96, IsCancel = true, Style = (Style)Resources["Primary"] };
        done.Click += (_, _) => Close();

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        footer.Children.Add(_summary);

        var top = new StackPanel { Children = { heading, introText, searchBox, _status } };

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) =>
        {
            _search.Focus();
            await LoadAsync().ConfigureAwait(true);
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            _apps = await _home.GetInstalledAppsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("add apps: load", ex);
            _status.Text = $"Couldn't read your installed apps: {ex.Message}";
            return;
        }

        ApplyFilter();
    }

    /// <summary>Re-reads each visible row's state, e.g. after the drawer is reopened.</summary>
    public void RefreshStates()
    {
        foreach (var row in _list.Items.OfType<AppRow>()) row.Update(_home.IsOnHome(row.App));
    }

    private void ApplyFilter()
    {
        var query = _search.Text.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? _apps
            : AppSearch.Rank(_apps, query).ToList();

        // Rows are cheap and icons load only when a row is realised, so a new
        // set of rows per keystroke is fine for a few hundred apps.
        _list.ItemsSource = matches.Select(app => new AppRow(app, _home.IsOnHome(app), _iconOptions, Toggle)).ToList();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        _status.Text = _apps.Count == 0
            ? "No apps found."
            : matches.Count == 0
                ? $"No apps match \"{query}\"."
                : string.Empty;
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Toggle(AppRow row)
    {
        var onHome = !_home.IsOnHome(row.App);
        _home.SetOnHome(row.App, onHome);
        row.Update(_home.IsOnHome(row.App));

        _summary.Text = onHome ? $"Added {row.App.DisplayName}" : $"Removed {row.App.DisplayName}";
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _list.SelectedItem is AppRow row:
                Toggle(row);
                e.Handled = true;
                break;

            case Key.Down or Key.Up when _list.Items.Count > 0:
                var step = e.Key == Key.Down ? 1 : -1;
                _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + step, 0, _list.Items.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && _list.SelectedItem is AppRow row)
        {
            Toggle(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _list.SelectedIndex <= 0)
        {
            _search.Focus();
            e.Handled = true;
        }
        else if (e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Back)
        {
            // Typing in the list goes back to the search box.
            _search.Focus();
            _search.CaretIndex = _search.Text.Length;
        }
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = DialogChrome.Brush("#B3F2F2F4"),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>One app: icon, name, and an Add / Added pill.</summary>
    private sealed class AppRow : Border
    {
        private static readonly string AddGlyph = ((char)0xE710).ToString();
        private static readonly string AddedGlyph = ((char)0xE73E).ToString();
        private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly Border _pill;
        private readonly TextBlock _pillGlyph;
        private readonly TextBlock _pillText;
        private readonly IconRenderOptions _options;
        private bool _iconRequested;

        public LauncherItem App { get; }

        public AppRow(LauncherItem app, bool onHome, IconRenderOptions options, Action<AppRow> toggle)
        {
            App = app;
            _options = options;
            Padding = new Thickness(6, 5, 6, 5);
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            ToolTip = app.FileSystemPath ?? app.Target;

            var icon = new Image
            {
                Width = IconPixels,
                Height = IconPixels,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            var name = new TextBlock
            {
                Text = app.DisplayName,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _pillGlyph = new TextBlock { FontFamily = GlyphFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) };
            _pillText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            _pill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 12, 4),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pillGlyph, _pillText } },
            };

            var dock = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(_pill, Dock.Right);
            dock.Children.Add(icon);
            dock.Children.Add(_pill);
            dock.Children.Add(name);
            Child = dock;

            Update(onHome);

            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                toggle(this);
            };

            // Only rows that are actually shown ask for an icon.
            Loaded += async (_, _) =>
            {
                if (_iconRequested) return;
                _iconRequested = true;
                try
                {
                    icon.Source = await Hearth.App.App.Icons
                        .GetAsync(app, _options)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Error($"add apps: icon '{app.DisplayName}'", ex);
                }
            };
        }

        public void Update(bool onHome)
        {
            _pillGlyph.Text = onHome ? AddedGlyph : AddGlyph;
            _pillText.Text = onHome ? "Added" : "Add";
            _pill.Background = onHome ? DialogChrome.Brush("#FF8AB4F8") : DialogChrome.Brush("#FF2A2A31");
            var ink = onHome ? DialogChrome.Brush("#FF10182A") : DialogChrome.Brush("#FFF2F2F4");
            _pillGlyph.Foreground = ink;
            _pillText.Foreground = ink;
        }
    }
}
