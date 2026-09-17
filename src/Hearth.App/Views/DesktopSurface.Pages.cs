using Hearth.App.Widgets;
using Hearth.Core.Layout;
using Hearth.Core.Notifications;
using Hearth.Core.Shell;

namespace Hearth.App.Views;

/// <summary>
/// One display's home screen as the Start menu draws it. Positions and sizes
/// are in grid cells (fractional for unlocked widgets), so the page can be
/// drawn at any scale with the same arrangement.
/// </summary>
internal sealed record HomePage(
    string Name,
    double CellWidth,
    double CellHeight,
    double Gap,
    double IconSize,
    double Scale,
    IReadOnlyList<HomeEntry> Entries);

/// <summary>An icon, a folder or a widget on a <see cref="HomePage"/>.</summary>
internal sealed record HomeEntry(double X, double Y, double Width, double Height)
{
    public LauncherItem? Item { get; init; }
    public GridGroup? Group { get; init; }
    public IWidget? Widget { get; init; }
}

public partial class DesktopSurface
{
    /// <summary>
    /// Every connected display's arrangement, primary first — the Start menu's
    /// Pages. Taken fresh each time, so it always matches the desktop.
    /// </summary>
    internal IReadOnlyList<HomePage> GetHomePages()
    {
        var pages = new List<HomePage>();
        for (var i = 0; i < _surfaces.Count; i++)
        {
            var surface = _surfaces[i];
            var layout = _layout.ForMonitor(surface.Info.DeviceId);
            var entries = new List<HomeEntry>();

            foreach (var placement in layout.Placements)
            {
                if (placement.ParentGroupId is not null) continue;

                if (WidgetRegistry.FromPlacementId(placement.ItemId) is { } widget)
                {
                    entries.Add(WidgetEntry(placement, surface) with { Widget = widget });
                }
                else if (_layout.FindGroupByPlacement(placement.ItemId) is { } group)
                {
                    entries.Add(new HomeEntry(placement.Column, placement.Row, 1, 1) { Group = group });
                }
                else if (_items.TryGetValue(placement.ItemId, out var item))
                {
                    entries.Add(new HomeEntry(placement.Column, placement.Row, 1, 1) { Item = item });
                }
            }

            pages.Add(new HomePage(
                i == 0 ? "Main display" : $"Display {i + 1}",
                surface.CellWidth,
                surface.CellHeight,
                App.Settings.GridGap * surface.Scale,
                App.Settings.IconSize * surface.Scale,
                surface.Scale,
                entries));
        }
        return pages;
    }

    /// <summary>Locked widgets use their cells; unlocked ones their free bounds, in cells.</summary>
    private static HomeEntry WidgetEntry(GridPlacement placement, MonitorSurface surface)
    {
        if (placement.Unlocked && placement.Free is { } free)
        {
            return new HomeEntry(
                (free.X - surface.PadX) / surface.CellWidth,
                (free.Y - surface.PadY) / surface.CellHeight,
                (free.Width + App.Settings.GridGap * surface.Scale) / surface.CellWidth,
                (free.Height + App.Settings.GridGap * surface.Scale) / surface.CellHeight);
        }

        return new HomeEntry(placement.Column, placement.Row,
            Math.Max(1, placement.ColumnSpan), Math.Max(1, placement.RowSpan));
    }

    internal IReadOnlyList<LauncherItem> ItemsIn(GridGroup group) =>
        group.ItemIds.Select(id => _items.GetValueOrDefault(id)).OfType<LauncherItem>().ToList();

    /// <summary>The badge a desktop item shows (desktop shortcuts resolve to their app).</summary>
    internal Badge BadgeForItem(string itemId) => BadgeFor(itemId);

    internal Badge BadgeForGroup(GridGroup group) => BadgeForPlacement(group.PlacementId);

    internal Hearth.Core.Icons.IconFit FitFor(string itemId) => _layout.FitFor(itemId);
}
