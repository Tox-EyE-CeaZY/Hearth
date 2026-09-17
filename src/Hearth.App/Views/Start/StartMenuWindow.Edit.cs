using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Hearth.App.Widgets;
using Hearth.Core.Diagnostics;
using Hearth.Core.Layout;
using Hearth.Core.Settings;
using Hearth.Core.Shell;

namespace Hearth.App.Views.Start;

/// <summary>
/// Edit mode for Pages, in the phone style: everything wiggles, every item
/// gets a remove button, widgets get a size button and can be dragged by
/// their whole body, empty cells are outlined, and a toolbar adds apps,
/// widgets and pages. Apps do not launch while editing; folders still open,
/// so their contents can be edited too.
/// </summary>
internal sealed partial class StartMenuWindow
{
    private bool _editing;
    private ChromeButton _editToggle = null!;
    private StackPanel _editBar = null!;
    private ChromeButton _deletePage = null!;
    private readonly Random _wiggle = new();

    /// <summary>The row under the page: edit tools on the left, dots in the middle, Edit/Done on the right.</summary>
    private FrameworkElement BuildPagesFooter()
    {
        _editToggle = new ChromeButton(StartStyle.Edit, "Edit", "Arrange, add and remove");
        _editToggle.Clicked += _ => SetEditing(!_editing);

        var addApps = new ChromeButton(StartStyle.Glyph(0xE710), "Apps", "Add apps to this page");
        addApps.Clicked += _ => OpenAppPicker();
        var addWidget = new ChromeButton(StartStyle.Glyph(0xE8A9), "Widget", "Add a widget to this page");
        addWidget.Clicked += b => ShowAddWidgetMenu(b);
        var addPage = new ChromeButton(StartStyle.Glyph(0xE8F4), "Page", "Add a page");
        addPage.Clicked += _ => AddPageAndShow();
        _deletePage = new ChromeButton(StartStyle.Glyph(0xE74D), tooltip: "Delete this page");
        _deletePage.Clicked += _ => DeleteCurrentPage();

        _editBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            Children = { addApps, addWidget, addPage, _deletePage },
        };

        _editToggle.HorizontalAlignment = HorizontalAlignment.Right;
        _dots.VerticalAlignment = VerticalAlignment.Center;
        _dots.Margin = new Thickness(0);

        return new Grid
        {
            Margin = new Thickness(38, 6, 38, 0),
            Children = { _editBar, _dots, _editToggle },
        };
    }

    private void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        _editToggle.Child = EditToggleContent();
        _editBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (!editing) CloseAppPicker();
        RebuildPages();
    }

    private UIElement EditToggleContent()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(StartStyle.GlyphText(_editing ? StartStyle.Glyph(0xE73E) : StartStyle.Edit, 14));
        var label = StartStyle.Label(_editing ? "Done" : "Edit", 13, weight: _editing ? FontWeights.SemiBold : FontWeights.Normal);
        label.Margin = new Thickness(8, 0, 0, 1);
        row.Children.Add(label);
        return row;
    }

    /// <summary>Faint outlines for every cell while editing, so it is clear where things can go.</summary>
    private void AddEditCells(Canvas canvas, MonitorLayout? page, double cellWidth, double cellHeight)
    {
        for (var c = 0; c < StartLayout.Columns; c++)
        {
            for (var r = 0; r < StartLayout.Rows; r++)
            {
                if (page?.IsOccupied(c, r) == true) continue;
                var cell = new Rectangle
                {
                    Width = cellWidth - CellGap,
                    Height = cellHeight - CellGap,
                    RadiusX = 10,
                    RadiusY = 10,
                    Stroke = StartStyle.Faint,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 3],
                    Opacity = 0.7,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(cell, CellGap + c * cellWidth);
                Canvas.SetTop(cell, CellGap + r * cellHeight);
                canvas.Children.Add(cell);
            }
        }
    }

    /// <summary>
    /// An item as it looks while editing: wiggling, with a remove button (and
    /// a size button for widgets), dragged by its whole body.
    /// </summary>
    private FrameworkElement WrapForEditing(FrameworkElement content, GridPlacement placement, IWidget? widget)
    {
        // A widget's own buttons must not take the press: the whole widget is the handle.
        if (widget is not null) content.IsHitTestVisible = false;

        var remove = CornerButton(StartStyle.Glyph(0xE738), "Remove from Start");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(-6, -6, 0, 0);
        remove.Clicked += _ =>
        {
            _layout.Remove(placement.ItemId);
            SavePages();
        };

        var shell = new Grid
        {
            Background = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.SizeAll,
            Children = { content, remove },
        };

        if (widget is not null)
        {
            var resize = CornerButton(StartStyle.Glyph(0xE740), "Size and options");
            resize.HorizontalAlignment = HorizontalAlignment.Right;
            resize.VerticalAlignment = VerticalAlignment.Bottom;
            resize.Margin = new Thickness(0, 0, -6, -6);
            resize.Clicked += b => ShowWidgetMenu(widget, placement, b);
            shell.Children.Add(resize);
            shell.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowWidgetMenu(widget, placement, shell);
            };
        }

        MakeDraggable(shell, placement.ItemId);
        Wiggle(shell, widget is null ? 1.6 : 0.6);
        return shell;
    }

    private static PressableBorder CornerButton(string glyph, string tooltip)
    {
        var button = new PressableBorder(StartStyle.Panel, StartStyle.Panel, radius: 11)
        {
            Width = 22,
            Height = 22,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            ToolTip = tooltip,
            Child = StartStyle.GlyphText(glyph, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.25 },
        };
        Panel.SetZIndex(button, 1);
        return button;
    }

    /// <summary>The edit-mode wiggle, each item slightly out of step with the next.</summary>
    private void Wiggle(FrameworkElement element, double degrees)
    {
        var rotate = new RotateTransform();
        element.RenderTransform = rotate;
        var swing = new DoubleAnimation(-degrees, degrees, TimeSpan.FromMilliseconds(130 + _wiggle.Next(40)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(_wiggle.Next(120)),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, swing);
    }

    private void ShowAddWidgetMenu(FrameworkElement anchor)
    {
        var menu = StartStyle.NewMenu(anchor);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        foreach (var widget in WidgetRegistry.All)
        {
            var entry = new MenuItem { Header = widget.Title };
            entry.Click += (_, _) => AddWidgetToPage(widget);
            menu.Items.Add(entry);
        }
        menu.IsOpen = true;
    }

    private void DeleteCurrentPage()
    {
        if (Pages.Count <= 1 || _pageIndex >= Pages.Count) return;
        var page = Pages[_pageIndex];
        foreach (var placement in page.Placements)
        {
            if (_layout.FindGroup(placement.ItemId) is { } group) _layout.Groups.Remove(group);
        }
        Pages.Remove(page);
        _pageIndex = Math.Max(0, _pageIndex - 1);
        SavePages();
    }

    /// <summary>
    /// "--start selftest": runs the edit flows in place (new page, add apps
    /// from the picker, add a widget, delete the page) and logs any failure,
    /// so a crash can be reproduced without driving the mouse.
    /// </summary>
    public async Task RunSelfTestAsync()
    {
        async Task Step(string name, Action action)
        {
            try
            {
                action();
                await Task.Delay(400);
                Log.Write($"selftest: {name} ok (page {_pageIndex + 1} of {Pages.Count})");
            }
            catch (Exception ex)
            {
                Log.Error($"selftest: {name}", ex);
                throw;
            }
        }

        var before = System.Text.Json.JsonSerializer.Serialize(_layout.Pages);
        try
        {
            await Step("edit", () => { ShowTab(PagesTab); SetEditing(true); });
            await Step("new page", AddPageAndShow);
            await Step("open picker", OpenAppPicker);
            await Step("add first app", () => _pickerList.Children.OfType<ResultRow>().First().RaiseClick());
            await Step("add second app", () => _pickerList.Children.OfType<ResultRow>().Skip(1).First().RaiseClick());
            await Step("close picker", () => CloseAppPicker());
            await Step("add widget", () => AddWidgetToPage(WidgetRegistry.All.First(w => w is not IConfigurableWidget)));
            await Step("previous page", () => ChangePage(-1));
            await Step("next page", () => ChangePage(1));
            await Step("delete page", DeleteCurrentPage);
            await Step("done", () => SetEditing(false));
        }
        catch
        {
            // logged above
        }
        finally
        {
            // Leave the user's pages as they were.
            _layout.Pages = System.Text.Json.JsonSerializer.Deserialize<List<MonitorLayout>>(before);
            SaveLayout();
            RebuildPages();
            Log.Write("selftest: finished, pages restored");
        }
    }

    // ---- Add apps picker -------------------------------------------------------

    private Grid _appPicker = null!;
    private TextBox _pickerSearch = null!;
    private StackPanel _pickerList = null!;

    private FrameworkElement BuildAppPicker()
    {
        _pickerSearch = new TextBox
        {
            Background = StartStyle.Field,
            BorderBrush = StartStyle.CardEdge,
            Foreground = StartStyle.Text,
            CaretBrush = StartStyle.Text,
            FontSize = 13.5,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _pickerSearch.TextChanged += (_, _) => FillAppPicker();

        _pickerList = new StackPanel();
        var done = new ChromeButton(StartStyle.Glyph(0xE73E), "Done") { HorizontalAlignment = HorizontalAlignment.Right };
        done.Clicked += _ => CloseAppPicker();

        var title = StartStyle.Label("Add apps to this page", 16, weight: FontWeights.SemiBold);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        header.Children.Add(title);

        var card = new Border
        {
            Background = StartStyle.Panel,
            BorderBrush = StartStyle.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 14, 18, 14),
            Width = 460,
            Margin = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.3 },
            Child = new DockPanel
            {
                Children =
                {
                    Docked(header, Dock.Top),
                    Docked(_pickerSearch, Dock.Top),
                    new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        PanningMode = PanningMode.VerticalOnly,
                        Content = _pickerList,
                    },
                },
            },
        };
        card.MouseLeftButtonUp += (_, e) => e.Handled = true;

        _appPicker = new Grid
        {
            Background = StartStyle.Dim,
            Visibility = Visibility.Collapsed,
            Children = { card },
        };
        _appPicker.MouseLeftButtonUp += (_, _) => CloseAppPicker();
        return _appPicker;

        static UIElement Docked(UIElement element, Dock dock)
        {
            DockPanel.SetDock(element, dock);
            return element;
        }
    }

    private void OpenAppPicker()
    {
        _pickerSearch.Text = string.Empty;
        FillAppPicker();
        _appPicker.Visibility = Visibility.Visible;
        _pickerSearch.Focus();
    }

    private bool CloseAppPicker()
    {
        if (_appPicker is not { Visibility: Visibility.Visible }) return false;
        _appPicker.Visibility = Visibility.Collapsed;
        _pickerList.Children.Clear();
        _search.Focus();
        return true;
    }

    private void FillAppPicker()
    {
        var query = _pickerSearch.Text.Trim();
        var apps = query.Length == 0
            ? _apps.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            : AppSearch.Rank(_apps.Values, query);

        _pickerList.Children.Clear();
        foreach (var app in apps.Take(250)) _pickerList.Children.Add(PickerRow(app));
    }

    /// <summary>One app: click to add it to the current page, or to take it off Start.</summary>
    private ResultRow PickerRow(LauncherItem app)
    {
        var onStart = _layout.Contains(app.Id);
        var row = new ResultRow(app.DisplayName, onStart ? "On Start. Click to remove." : "Click to add to this page", app)
        {
            IsSelected = onStart,
        };
        row.Clicked += r =>
        {
            if (_layout.Contains(app.Id)) _layout.Remove(app.Id);
            else _layout.Place(app.Id, fromPage: _pageIndex);
            SaveLayout();
            RebuildPages();

            var index = _pickerList.Children.IndexOf(r);
            // Replaced by remove-then-insert: UIElementCollection's indexer
            // throws when the slot is still occupied.
            if (index < 0) return;
            _pickerList.Children.RemoveAt(index);
            _pickerList.Children.Insert(index, PickerRow(app));
        };
        return row;
    }
}
