using System.Windows;
using System.Windows.Controls;
using Hearth.App.Widgets;

namespace Hearth.App.Views.Start;

/// <summary>
/// Widgets: a board of cards in two columns, like the Windows widgets board.
/// Each widget gets a height suited to it and a titled card drawn by the board
/// (so every card matches the menu's theme), with a menu to move it, make it
/// full width, or remove it. Cards go into whichever column is shorter, so the
/// board stays balanced.
///
/// Views are built when the tab is shown and dropped when it is left or the
/// menu hides, so a clock or a media poller never runs unseen.
/// </summary>
internal sealed partial class StartMenuWindow
{
    private const double BoardGap = 12;
    private const double CardHeaderHeight = 30;
    private const double CardPadding = 12;

    private StackPanel _widgetLeft = null!;
    private StackPanel _widgetRight = null!;
    private StackPanel _widgetWide = null!;
    private TextBlock _widgetsEmpty = null!;
    private ScrollViewer _widgetScroll = null!;

    private FrameworkElement BuildWidgetsView()
    {
        var add = new ChromeButton(StartStyle.Glyph(0xE710), "Add widgets")
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8),
        };
        add.Clicked += b => ShowWidgetPicker(b);

        _widgetsEmpty = StartStyle.Label("No widgets yet. Use Add widgets to pick some.", 13.5, StartStyle.Muted);
        _widgetsEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _widgetsEmpty.Margin = new Thickness(0, 40, 0, 0);

        _widgetWide = new StackPanel();
        _widgetLeft = new StackPanel();
        _widgetRight = new StackPanel();
        Grid.SetColumnSpan(_widgetWide, 3);
        Grid.SetRow(_widgetLeft, 1);
        Grid.SetRow(_widgetRight, 1);
        Grid.SetColumn(_widgetRight, 2);

        var board = new Grid
        {
            Margin = new Thickness(0, 0, 10, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = new GridLength(BoardGap) },
                new ColumnDefinition(),
            },
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            },
            Children = { _widgetWide, _widgetLeft, _widgetRight },
        };

        _widgetScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PanningMode = PanningMode.VerticalOnly,
            Content = new StackPanel { Children = { _widgetsEmpty, board } },
        };

        var dock = new DockPanel();
        DockPanel.SetDock(add, Dock.Top);
        dock.Children.Add(add);
        dock.Children.Add(_widgetScroll);
        return dock;
    }

    /// <summary>The user's choice, or the widgets that are on the board by default.</summary>
    private List<IWidget> ChosenWidgets()
    {
        var ids = _layout.Widgets ?? DefaultWidgetIds().ToList();
        return ids.Select(WidgetRegistry.Find).OfType<IWidget>().ToList();
    }

    private static IEnumerable<string> DefaultWidgetIds() =>
        WidgetRegistry.All
            .Where(w => w.OnBoardByDefault && w is not IConfigurableWidget { NeedsSetup: true })
            .Select(w => w.Id);

    /// <summary>How tall each widget's content is on the board: what it needs to read well.</summary>
    private static double BoardHeight(IWidget widget, bool wide) => widget.BoardHeight(wide);

    private bool IsWide(IWidget widget) =>
        _layout.WideWidgets.Contains(widget.Id, StringComparer.OrdinalIgnoreCase);

    private void BuildWidgets()
    {
        ClearWidgets();
        var chosen = ChosenWidgets();

        var contentWidth = (_widgetScroll.ActualWidth > 0 ? _widgetScroll.ActualWidth : DesignWidth - 48) - 12;
        var columnWidth = (contentWidth - BoardGap) / 2;
        double left = 0, right = 0;

        foreach (var widget in chosen)
        {
            var wide = IsWide(widget);
            var card = BuildWidgetCard(widget, wide ? contentWidth : columnWidth, BoardHeight(widget, wide), chosen);

            if (wide)
            {
                _widgetWide.Children.Add(card);
            }
            else if (left <= right)
            {
                _widgetLeft.Children.Add(card);
                left += card.Height + BoardGap;
            }
            else
            {
                _widgetRight.Children.Add(card);
                right += card.Height + BoardGap;
            }
        }

        _widgetsEmpty.Visibility = chosen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildWidgetCard(IWidget widget, double width, double contentHeight, List<IWidget> chosen)
    {
        var slot = new Size(width - CardPadding * 2, contentHeight);

        // Widgets are designed around a desktop cell of about 105 x 125.
        var columns = Math.Max(1, (int)Math.Round(slot.Width / 105));
        var rows = Math.Max(1, (int)Math.Round(slot.Height / 125));
        var view = StartWidgets.Create(widget, slot, columns, rows, bare: true);

        var title = StartStyle.Label(widget.Title, 12.5, StartStyle.Muted, FontWeights.SemiBold);
        var more = new ChromeButton(StartStyle.More, tooltip: "Widget options", glyphSize: 12)
        {
            Padding = new Thickness(6, 3, 6, 3),
        };
        more.Clicked += b => ShowBoardWidgetMenu(widget, chosen, b);

        var header = new DockPanel { Height = CardHeaderHeight, Margin = new Thickness(CardPadding, 4, CardPadding - 4, 0) };
        DockPanel.SetDock(more, Dock.Right);
        header.Children.Add(more);
        header.Children.Add(title);

        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(new Border { Padding = new Thickness(CardPadding, 0, CardPadding, CardPadding), Child = view });

        var card = new Border
        {
            Width = width,
            Height = contentHeight + CardHeaderHeight + 4 + CardPadding,
            Margin = new Thickness(0, 0, 0, BoardGap),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(14),
            Background = StartStyle.Card,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            Child = body,
        };
        card.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowBoardWidgetMenu(widget, chosen, card);
        };
        return card;
    }

    private void ShowBoardWidgetMenu(IWidget widget, List<IWidget> chosen, FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        var ids = chosen.Select(w => w.Id).ToList();
        var index = ids.FindIndex(id => id.Equals(widget.Id, StringComparison.OrdinalIgnoreCase));

        if (index > 0) menu.Items.Add(Item("Move up", () => MoveBoardWidget(ids, index, -1)));
        if (index >= 0 && index < ids.Count - 1) menu.Items.Add(Item("Move down", () => MoveBoardWidget(ids, index, 1)));

        var wide = new MenuItem { Header = "Full width", IsCheckable = true, IsChecked = IsWide(widget) };
        wide.Click += (_, _) =>
        {
            if (_layout.WideWidgets.RemoveAll(id => id.Equals(widget.Id, StringComparison.OrdinalIgnoreCase)) == 0)
                _layout.WideWidgets.Add(widget.Id);
            SaveLayout();
            BuildWidgets();
        };
        menu.Items.Add(wide);

        if (widget is IConfigurableWidget configurable)
        {
            menu.Items.Add(Item("Settings...", () =>
            {
                RunDialog(configurable.Configure);
                BuildWidgets();
            }));
        }

        if (index >= 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove", () =>
            {
                ids.RemoveAt(index);
                _layout.Widgets = ids;
                SaveLayout();
                BuildWidgets();
            }));
        }
        menu.IsOpen = true;
    }

    private void MoveBoardWidget(List<string> ids, int index, int step)
    {
        (ids[index], ids[index + step]) = (ids[index + step], ids[index]);
        _layout.Widgets = ids;
        SaveLayout();
        BuildWidgets();
    }

    /// <summary>
    /// Shows one of Hearth's dialogs over the menu: not topmost while it is up
    /// (or the dialog opens behind), and losing focus to it does not close the menu.
    /// </summary>
    private bool RunDialog(Func<bool> dialog)
    {
        _suppressHide = true;
        Topmost = false;
        try
        {
            return dialog();
        }
        finally
        {
            _suppressHide = false;
            Topmost = true;
            Activate();
        }
    }

    /// <summary>Removing the views unloads them, which is what stops their timers.</summary>
    private void ClearWidgets()
    {
        _widgetWide?.Children.Clear();
        _widgetLeft?.Children.Clear();
        _widgetRight?.Children.Clear();
    }

    private void ShowWidgetPicker(FrameworkElement anchor)
    {
        var chosen = ChosenWidgets().Select(w => w.Id).ToList();
        var menu = StartStyle.NewMenu(anchor);

        foreach (var widget in WidgetRegistry.All)
        {
            var on = chosen.Contains(widget.Id, StringComparer.OrdinalIgnoreCase);
            var entry = new MenuItem { Header = widget.Title, IsCheckable = true, IsChecked = on };
            entry.Click += (_, _) =>
            {
                if (on)
                {
                    chosen.RemoveAll(id => id.Equals(widget.Id, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    if (widget is IConfigurableWidget { NeedsSetup: true } setup && !RunDialog(setup.Configure)) return;
                    chosen.Add(widget.Id);
                }

                _layout.Widgets = chosen;
                SaveLayout();
                BuildWidgets();
            };
            menu.Items.Add(entry);
        }

        menu.IsOpen = true;
    }
}
