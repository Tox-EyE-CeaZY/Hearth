using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;
using Hearth.Core.Threading;

namespace Hearth.App.Views.Start;

/// <summary>
/// Search: apps first, then Settings pages, then files from the Windows
/// Search index (which arrive a moment later), then the web. Enter opens the
/// highlighted row; Up and Down move the highlight.
/// </summary>
internal sealed partial class StartMenuWindow
{
    private const int MaxApps = 8;
    private const int MaxSettings = 4;
    private const int MaxFiles = 8;

    private StackPanel _results = null!;
    private ScrollViewer _resultsScroll = null!;
    private readonly List<ResultRow> _rows = [];
    private int _selectedRow = -1;
    private CancellationTokenSource? _fileSearch;
    private int _searchGeneration;

    private FrameworkElement BuildSearchView()
    {
        _results = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
        _resultsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.VerticalOnly,
            Content = _results,
        };
        return _resultsScroll;
    }

    private void OnSearchChanged()
    {
        var query = _search.Text.Trim();
        _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _fileSearch?.Cancel();

        if (query.Length == 0)
        {
            _views["search"].Visibility = Visibility.Collapsed;
            if (_views.TryGetValue(_activeTab, out var active)) active.Visibility = Visibility.Visible;
            if (_activeTab == WidgetsTab && IsVisible && _widgetLeft.Children.Count + _widgetRight.Children.Count + _widgetWide.Children.Count == 0) BuildWidgets();
            return;
        }

        foreach (var (key, view) in _views) view.Visibility = key == "search" ? Visibility.Visible : Visibility.Collapsed;
        ClearWidgets();
        CloseLetterJump();

        var generation = ++_searchGeneration;
        _results.Children.Clear();
        _rows.Clear();
        _selectedRow = -1;
        _resultsScroll.ScrollToTop();

        if (LooksLikeLocation(query))
        {
            AddSection("Open");
            AddRow(new ResultRow(query, "Open this location", null, StartStyle.Open), () => OpenTarget(Environment.ExpandEnvironmentVariables(query)));
        }

        var apps = AppSearch.Rank(_apps.Values, query).Take(MaxApps).ToList();
        if (apps.Count > 0)
        {
            AddSection("Apps");
            foreach (var app in apps)
            {
                var row = new ResultRow(app.DisplayName, CategoryOf(app), app);
                row.RightClicked += r => ShowItemMenu(app, r);
                AddRow(row, () => Launch(app));
            }
        }

        var pages = SettingsPages.Match(query, MaxSettings).ToList();
        if (pages.Count > 0)
        {
            AddSection("Settings");
            foreach (var (name, uri) in pages)
                AddRow(new ResultRow(name, "Windows Settings", null, StartStyle.Settings), () => OpenTarget(uri));
        }

        // Files arrive a moment after the rest and are inserted above the web
        // row, which therefore stays last.
        var web = BuildWebRow(query);
        AddSection("Web");
        AddRow(web.Row, web.Open);

        SelectRow(0);
        _ = SearchFilesAsync(query, generation);
    }

    private (ResultRow Row, Action Open) BuildWebRow(string query)
    {
        var row = new ResultRow($"Search the web for \"{query}\"", null, null, StartStyle.Globe);
        var url = string.Format(App.Settings.WebSearchUrl, Uri.EscapeDataString(query));
        return (row, () => OpenTarget(url));
    }

    private async Task SearchFilesAsync(string query, int generation)
    {
        // A short pause, so typing a word does not start a query per letter.
        var cancel = new CancellationTokenSource();
        _fileSearch = cancel;
        try
        {
            await Task.Delay(160, cancel.Token).ConfigureAwait(true);
            var files = await StaTask.Run(() => IndexSearch.Search(query, MaxFiles, cancel.Token)).ConfigureAwait(true);
            if (cancel.IsCancellationRequested || generation != _searchGeneration || files.Count == 0) return;

            // Insert before the Web section.
            var webIndex = _results.Children.Count - 2;
            var header = SectionHeader("Files");
            _results.Children.Insert(webIndex++, header);
            var selected = _selectedRow >= 0 && _selectedRow < _rows.Count ? _rows[_selectedRow] : null;
            var insertAt = _rows.Count - 1;

            foreach (var file in files)
            {
                var detail = Path.GetDirectoryName(file.FileSystemPath) ?? string.Empty;
                var row = new ResultRow(file.DisplayName, detail, file);
                row.RightClicked += r => ShowItemMenu(file, r);
                row.Clicked += _ => Launch(file);
                _results.Children.Insert(webIndex++, row);
                _rows.Insert(insertAt++, row);
            }

            if (selected is not null) SelectRow(_rows.IndexOf(selected));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("start file search", ex);
        }
    }

    private static bool LooksLikeLocation(string query) =>
        query.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ||
        query.StartsWith('%') ||
        (query.Length >= 3 && query[1] == ':' && (query[2] == Path.DirectorySeparatorChar || query[2] == Path.AltDirectorySeparatorChar)) ||
        query.StartsWith(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal);

    private void AddSection(string title) => _results.Children.Add(SectionHeader(title));

    private static TextBlock SectionHeader(string title)
    {
        var header = StartStyle.Label(title, 12, StartStyle.Muted, FontWeights.SemiBold);
        header.Margin = new Thickness(8, 10, 0, 4);
        return header;
    }

    private void AddRow(ResultRow row, Action open)
    {
        row.Clicked += _ => open();
        row.Tag = open;
        _results.Children.Add(row);
        _rows.Add(row);
    }

    private void SelectRow(int index)
    {
        if (_rows.Count == 0)
        {
            _selectedRow = -1;
            return;
        }

        index = Math.Clamp(index, 0, _rows.Count - 1);
        if (_selectedRow >= 0 && _selectedRow < _rows.Count) _rows[_selectedRow].IsSelected = false;
        _selectedRow = index;
        _rows[index].IsSelected = true;
        _rows[index].BringIntoView();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (_search.Text.Length == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                SelectRow(_selectedRow + 1);
                e.Handled = true;
                break;
            case Key.Up:
                SelectRow(_selectedRow - 1);
                e.Handled = true;
                break;
            case Key.Enter when _selectedRow >= 0 && _selectedRow < _rows.Count:
                var row = _rows[_selectedRow];
                if (row.Tag is Action open) open();
                else row.RaiseClick();
                e.Handled = true;
                break;
        }
    }
}
