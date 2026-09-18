using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearth.App.Views.Start;

/// <summary>
/// Pages side by side, for full-screen Start (tablet mode).
///
/// A tablet screen has room for more than one page, so rather than blowing
/// one page up, full-screen Start lays out a "spread": as many pages as fit
/// at a comfortable size, in columns (and in rows on a tall screen), each
/// keeping the floating menu's proportions. Turning the page moves a whole
/// spread. Each page's canvas keeps its own index in Tag, so dropping on,
/// or right-clicking, any page of the spread acts on that page.
/// </summary>
internal sealed partial class StartMenuWindow
{
    /// <summary>Below this a 7x4 page gets cramped (labels need two lines).</summary>
    private const double MinSpreadPageWidth = 580;
    private const double MaxSpreadPageWidth = 1100;
    private const double SpreadGap = 24;

    /// <summary>The floating page's shape (about 760 x 470 DIPs, measured); flatter clips two-line labels.</summary>
    private const double PageAspect = 760.0 / 470.0;

    private int _spreadColumns = 1;
    private int _spreadRows = 1;

    /// <summary>How many pages are shown at once: 1 except in full screen.</summary>
    private int Spread => _spreadColumns * _spreadRows;

    /// <summary>The first page of the spread holding <paramref name="index"/>.</summary>
    private int SpreadStart(int index) => index - index % Math.Max(1, Spread);

    private bool HasNextSpread => _pageIndex + Spread < PageCount;
    private bool HasPreviousSpread => _pageIndex > 0;

    /// <summary>Picks the arrangement that shows the most pages, then the largest.</summary>
    private void UpdateSpread()
    {
        _spreadColumns = 1;
        _spreadRows = 1;
        if (!_fullScreen) return;
        var width = _pageHost.ActualWidth;
        var height = _pageHost.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var best = (Columns: 1, Rows: 1, Width: 0.0);
        for (var columns = 1; columns <= 4; columns++)
        {
            for (var rows = 1; rows <= 3; rows++)
            {
                var count = columns * rows;
                if (count > 1 && count > PageCount) continue;
                var pageWidth = SpreadPageWidth(width, height, columns, rows);
                if (count > 1 && pageWidth < MinSpreadPageWidth) continue;
                var bestCount = best.Columns * best.Rows;
                if (count > bestCount || (count == bestCount && pageWidth > best.Width))
                    best = (columns, rows, pageWidth);
            }
        }
        _spreadColumns = best.Columns;
        _spreadRows = best.Rows;
    }

    private static double SpreadPageWidth(double width, double height, int columns, int rows) => Math.Min(
        MaxSpreadPageWidth,
        Math.Min(
            (width - SpreadGap * (columns - 1)) / columns,
            (height - SpreadGap * (rows - 1)) / rows * PageAspect));

    /// <summary>One page's size inside the spread.</summary>
    private (double Width, double Height) SpreadPageSize()
    {
        var width = SpreadPageWidth(Math.Max(1, _pageHost.ActualWidth), Math.Max(1, _pageHost.ActualHeight), _spreadColumns, _spreadRows);
        return (width, width / PageAspect);
    }

    /// <summary>The element that slides when the page turns: one page, or a whole spread.</summary>
    private FrameworkElement BuildPage(int index)
    {
        if (!_fullScreen) return BuildSinglePage(index);

        var (pageWidth, pageHeight) = SpreadPageSize();
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var c = 0; c < _spreadColumns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pageWidth + SpreadGap) });
        for (var r = 0; r < _spreadRows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(pageHeight + SpreadGap) });

        for (var k = 0; k < Spread; k++)
        {
            var pageIndex = index + k;
            if (pageIndex >= PageCount) break;
            var page = BuildSinglePage(pageIndex);
            page.Margin = new Thickness(SpreadGap / 2);
            Grid.SetColumn(page, k % _spreadColumns);
            Grid.SetRow(page, k / _spreadColumns);
            grid.Children.Add(page);
        }

        return new Border
        {
            Width = Math.Max(1, _pageHost.ActualWidth),
            Height = Math.Max(1, _pageHost.ActualHeight),
            Background = Brushes.Transparent,
            Child = grid,
            RenderTransform = new TranslateTransform(),
        };
    }

    /// <summary>The page a canvas shows (its Tag), or the first page of the spread.</summary>
    private static int PageIndexOf(object? element, int fallback) =>
        element is FrameworkElement { Tag: int index } ? index : fallback;
}
