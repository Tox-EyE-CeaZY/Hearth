using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;

namespace Hearth.App.Views.Start;

/// <summary>All apps (A to Z with a letter jump) and Categories (cards that open into a list).</summary>
internal sealed partial class StartMenuWindow
{
    private const string OtherLetter = "#";

    // ---- All apps ---------------------------------------------------------

    private ScrollViewer _allScroll = null!;
    private StackPanel _allList = null!;
    private Grid _letterJump = null!;
    private UniformGrid _letterGrid = null!;
    private readonly Dictionary<string, FrameworkElement> _letterHeaders = [];

    private FrameworkElement BuildAllAppsView()
    {
        _allList = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
        _allScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _allList,
            PanningMode = PanningMode.VerticalOnly,
        };

        _letterGrid = new UniformGrid
        {
            Columns = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _letterJump = new Grid
        {
            Background = StartStyle.Scrim,
            Visibility = Visibility.Collapsed,
            Children = { _letterGrid },
        };
        _letterJump.MouseLeftButtonUp += (_, _) => CloseLetterJump();

        return new Grid { Children = { _allScroll, _letterJump } };
    }

    private static string LetterOf(string name)
    {
        var first = name.TrimStart().FirstOrDefault();
        if (first == default) return OtherLetter;
        var letter = char.ToUpper(RemoveDiacritic(first), CultureInfo.CurrentCulture);
        return letter is >= 'A' and <= 'Z' ? letter.ToString() : OtherLetter;
    }

    private static char RemoveDiacritic(char c)
    {
        var decomposed = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return decomposed.Length > 0 ? decomposed[0] : c;
    }

    private void RebuildAllApps()
    {
        _allList.Children.Clear();
        _letterHeaders.Clear();

        var groups = _apps.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .GroupBy(a => LetterOf(a.DisplayName))
            .OrderBy(g => g.Key == OtherLetter ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var header = new PressableBorder(radius: 6)
            {
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 10, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = StartStyle.Label(group.Key, 15, StartStyle.Accent, FontWeights.SemiBold),
                ToolTip = "Jump to a letter",
            };
            header.Clicked += _ => OpenLetterJump();
            _letterHeaders[group.Key] = header;
            _allList.Children.Add(header);

            var rows = new UniformGrid { Columns = 3 };
            foreach (var app in group)
            {
                var row = new ResultRow(app.DisplayName, null, app);
                row.Clicked += _ => Launch(app);
                row.RightClicked += r => ShowItemMenu(app, r);
                rows.Children.Add(row);
            }
            _allList.Children.Add(rows);
        }
    }

    private void OpenLetterJump()
    {
        _letterGrid.Children.Clear();
        foreach (var letter in new[] { OtherLetter }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString())))
        {
            var present = _letterHeaders.ContainsKey(letter);
            var cell = new PressableBorder(radius: 8)
            {
                Width = 64,
                Height = 56,
                Margin = new Thickness(4),
                IsEnabled = present,
                Opacity = present ? 1 : 0.3,
                Child = new TextBlock
                {
                    Text = letter,
                    FontSize = 22,
                    FontFamily = StartStyle.Display,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StartStyle.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var target = letter;
            cell.Clicked += _ => JumpToLetter(target);
            _letterGrid.Children.Add(cell);
        }
        _letterJump.Visibility = Visibility.Visible;
    }

    private void CloseLetterJump()
    {
        if (_letterJump is not null) _letterJump.Visibility = Visibility.Collapsed;
    }

    private void JumpToLetter(string letter)
    {
        CloseLetterJump();
        if (!_letterHeaders.TryGetValue(letter, out var header)) return;
        var offset = header.TransformToAncestor(_allList).Transform(new Point(0, 0)).Y;
        _allScroll.ScrollToVerticalOffset(Math.Max(0, offset - 6));
    }

    // ---- Categories -------------------------------------------------------

    private ScrollViewer _categoryCards = null!;
    private WrapPanel _cardPanel = null!;
    private DockPanel _categoryDetail = null!;
    private TextBlock _categoryTitle = null!;
    private WrapPanel _categoryTiles = null!;
    private string? _openCategory;

    private FrameworkElement BuildCategoriesView()
    {
        _cardPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _categoryCards = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _cardPanel,
            PanningMode = PanningMode.VerticalOnly,
        };

        var back = new ChromeButton(StartStyle.Back, "Categories", "Back to categories");
        back.Clicked += _ => CloseCategory();
        _categoryTitle = StartStyle.Label(string.Empty, 20, weight: FontWeights.SemiBold);
        _categoryTitle.FontFamily = StartStyle.Display;
        _categoryTitle.Margin = new Thickness(12, 0, 0, 0);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { back, _categoryTitle } };
        _categoryTiles = new WrapPanel();
        var tilesScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _categoryTiles,
            PanningMode = PanningMode.VerticalOnly,
        };

        _categoryDetail = new DockPanel { Visibility = Visibility.Collapsed };
        DockPanel.SetDock(header, Dock.Top);
        _categoryDetail.Children.Add(header);
        _categoryDetail.Children.Add(tilesScroll);

        return new Grid { Children = { _categoryCards, _categoryDetail } };
    }

    private string CategoryOf(LauncherItem item) =>
        _layout.Categories.TryGetValue(item.Id, out var chosen) && AppCategorizer.All.Contains(chosen)
            ? chosen
            : AppCategorizer.Categorize(item);

    private void SetCategory(LauncherItem item, string category)
    {
        if (category == AppCategorizer.Categorize(item)) _layout.Categories.Remove(item.Id);
        else _layout.Categories[item.Id] = category;
        SaveLayout();
        RebuildCategories();
    }

    private void RebuildCategories()
    {
        var byCategory = _apps.Values
            .GroupBy(CategoryOf)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList());

        _cardPanel.Children.Clear();
        const double gap = 12;
        const int perRow = 4;
        var width = Math.Floor((DesignWidth - 48 - 12 - gap * perRow) / perRow);

        foreach (var category in AppCategorizer.All)
        {
            if (!byCategory.TryGetValue(category, out var apps) || apps.Count == 0) continue;
            _cardPanel.Children.Add(BuildCategoryCard(category, apps, width, gap));
        }

        if (_openCategory is { } open)
        {
            if (byCategory.TryGetValue(open, out var apps)) FillCategory(open, apps);
            else CloseCategory();
        }
    }

    /// <summary>
    /// A folder-style card: the category's first apps (pinned ones first),
    /// with a "+N" corner when there are more.
    /// </summary>
    private FrameworkElement BuildCategoryCard(string category, List<LauncherItem> apps, double width, double gap)
    {
        // Apps already on the desktop first: they are the ones this user uses.
        var desktop = Desktop;
        var shown = apps.OrderByDescending(a => desktop?.IsAppOnHome(a) == true).Take(apps.Count > 4 ? 3 : 4).ToList();
        var icons = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(6, 4, 6, 4) };
        const double iconSize = 50;

        foreach (var app in shown)
        {
            var image = new Image { Width = iconSize, Height = iconSize, Margin = new Thickness(4), ToolTip = app.DisplayName };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Loaded += async (_, _) =>
            {
                if (image.Source is not null) return;
                try { image.Source = await App.Icons.GetAsync(app, StartStyle.IconOptions(image, iconSize)).ConfigureAwait(true); }
                catch (Exception ex) { Log.Error($"category icon '{app.DisplayName}'", ex); }
            };
            icons.Children.Add(image);
        }

        if (apps.Count > 4)
        {
            icons.Children.Add(new Border
            {
                Width = iconSize * 0.84,
                Height = iconSize * 0.84,
                CornerRadius = new CornerRadius(iconSize),
                Background = StartStyle.Field,
                Child = new TextBlock
                {
                    Text = "+" + (apps.Count - 3),
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = StartStyle.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var title = StartStyle.Label(category, 14, weight: FontWeights.SemiBold);
        var count = StartStyle.Label($"{apps.Count} app{(apps.Count == 1 ? "" : "s")}", 11.5, StartStyle.Muted);
        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 10), Children = { title, count } };

        var card = new PressableBorder(StartStyle.Card, StartStyle.CardHover, radius: 12)
        {
            Width = width,
            Margin = new Thickness(0, 0, gap, gap),
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 8, 0, 0),
            Child = new StackPanel { Children = { icons, text } },
        };
        card.Clicked += _ => OpenCategory(category);
        return card;
    }

    private void OpenCategory(string category)
    {
        var apps = _apps.Values.Where(a => CategoryOf(a) == category)
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _openCategory = category;
        FillCategory(category, apps);
        _categoryCards.Visibility = Visibility.Collapsed;
        _categoryDetail.Visibility = Visibility.Visible;
    }

    private void FillCategory(string category, List<LauncherItem> apps)
    {
        _categoryTitle.Text = category;
        _categoryTiles.Children.Clear();
        foreach (var app in apps)
        {
            var tile = new StartTile(app, 48) { Width = 136, Height = 118, Margin = new Thickness(0, 0, 2, 4) };
            tile.Clicked += _ => Launch(app);
            tile.RightClicked += t => ShowItemMenu(app, t);
            _categoryTiles.Children.Add(tile);
        }
    }

    private void CloseCategory()
    {
        _openCategory = null;
        if (_categoryDetail is null) return;
        _categoryDetail.Visibility = Visibility.Collapsed;
        _categoryCards.Visibility = Visibility.Visible;
    }
}
