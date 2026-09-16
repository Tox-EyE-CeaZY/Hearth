using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearth.Core.Layout;

/// <summary>Where one item sits on the home screen.</summary>
public sealed class GridPlacement
{
    /// <summary>Matches <see cref="Shell.LauncherItem.Id"/>.</summary>
    public required string ItemId { get; set; }

    public int Column { get; set; }
    public int Row { get; set; }

    /// <summary>Widgets span more than one cell; icons are always 1x1.</summary>
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    /// <summary>Set when this item lives inside a group rather than on the grid.</summary>
    public string? ParentGroupId { get; set; }

    /// <summary>Order within a group.</summary>
    public int IndexInGroup { get; set; }

    /// <summary>
    /// Free positioning for widgets. When set, the widget is drawn at
    /// <see cref="Free"/> rather than on the grid, may extend past the icon
    /// area, and Column/Row/ColumnSpan/RowSpan instead record which grid cells
    /// it happens to cover — which is what keeps icons out from underneath it.
    /// A span of zero means it covers no cells at all.
    /// </summary>
    public bool Unlocked { get; set; }

    /// <summary>Unlocked bounds, in physical pixels relative to the display's top-left.</summary>
    public FreeRect? Free { get; set; }
}

public sealed class FreeRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// A home-screen folder, in the Android/iOS sense: a tile on the grid that
/// opens into a panel of the items inside it. It has no shell identity and
/// nothing on disk moves — it is purely an arrangement.
/// </summary>
public sealed class GridGroup
{
    public required string Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Contents, in display order.</summary>
    public List<string> ItemIds { get; set; } = [];

    /// <summary>The id this folder's tile is placed under on the grid.</summary>
    [JsonIgnore]
    public string PlacementId => HomeLayout.GroupPrefix + Id;
}

/// <summary>
/// The home screen's arrangement for one monitor.
///
/// Keyed by the monitor's stable device path rather than its index, so
/// unplugging a display and plugging it back in does not reshuffle everything.
/// </summary>
public sealed class MonitorLayout
{
    public required string MonitorId { get; set; }

    public List<GridPlacement> Placements { get; set; } = [];
    public List<GridGroup> Groups { get; set; } = [];

    /// <summary>
    /// Grid dimensions this layout was authored against. When the resolution
    /// changes we reflow rather than clip, but we need the old size to know
    /// which items moved.
    /// </summary>
    public int Columns { get; set; }
    public int Rows { get; set; }

    public GridPlacement? Find(string itemId) =>
        Placements.FirstOrDefault(p => p.ItemId == itemId);

    public bool IsOccupied(int column, int row, string? ignoreItemId = null)
    {
        foreach (var p in Placements)
        {
            if (p.ParentGroupId is not null) continue;
            if (p.ItemId == ignoreItemId) continue;
            if (column >= p.Column && column < p.Column + p.ColumnSpan &&
                row >= p.Row && row < p.Row + p.RowSpan)
                return true;
        }
        return false;
    }

    /// <summary>
    /// First free cell in reading order. Returns null when the grid is full,
    /// which the caller surfaces rather than silently dropping the item.
    /// </summary>
    public (int Column, int Row)? FindFreeCell(int columns, int rows)
    {
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (!IsOccupied(column, row)) return (column, row);
            }
        }
        return null;
    }

    /// <summary>
    /// Places items that have no placement yet, and drops placements whose item
    /// has disappeared. Returns true when anything changed.
    /// </summary>
    public bool Reconcile(IReadOnlyCollection<string> liveItemIds, int columns, int rows)
    {
        var changed = false;

        var live = new HashSet<string>(liveItemIds, StringComparer.OrdinalIgnoreCase);
        var removed = Placements.RemoveAll(p => !live.Contains(p.ItemId));
        if (removed > 0) changed = true;

        var placed = new HashSet<string>(Placements.Select(p => p.ItemId), StringComparer.OrdinalIgnoreCase);
        foreach (var id in liveItemIds)
        {
            if (placed.Contains(id)) continue;

            var cell = FindFreeCell(columns, rows);
            if (cell is null) break; // grid full: leave the rest unplaced

            Placements.Add(new GridPlacement { ItemId = id, Column = cell.Value.Column, Row = cell.Value.Row });
            changed = true;
        }

        if (Columns != columns || Rows != rows)
        {
            Columns = columns;
            Rows = rows;
            changed = true;
        }

        return changed;
    }
}

/// <summary>Every monitor's arrangement, and the disk format behind it.</summary>
public sealed class HomeLayout
{
    public const string GroupPrefix = "group:";

    public int Version { get; set; } = 1;
    public List<MonitorLayout> Monitors { get; set; } = [];

    /// <summary>
    /// Folders live at home level rather than per display, so dragging a folder
    /// to another display moves only its placement, never its contents.
    /// </summary>
    public List<GridGroup> Groups { get; set; } = [];

    /// <summary>
    /// Items the user has hidden from the home screen. Nothing happens to the
    /// file or app itself; it simply is not shown, on the grid or in a folder.
    /// </summary>
    public List<string> Hidden { get; set; } = [];

    public bool IsHidden(string itemId) => Hidden.Contains(itemId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Icon placement the user chose for individual icons. Missing means
    /// automatic. Icons differ too much for one rule to suit all of them.
    /// </summary>
    public Dictionary<string, Hearth.Core.Icons.IconFit> IconFits { get; set; } = [];

    public Hearth.Core.Icons.IconFit FitFor(string itemId) =>
        IconFits.TryGetValue(itemId, out var fit) ? fit : Hearth.Core.Icons.IconFit.Auto;

    public GridGroup? FindGroupByPlacement(string placementId) =>
        placementId.StartsWith(GroupPrefix, StringComparison.Ordinal)
            ? Groups.FirstOrDefault(g => g.Id == placementId[GroupPrefix.Length..])
            : null;

    public GridGroup? GroupContaining(string itemId) =>
        Groups.FirstOrDefault(g => g.ItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase));

    public GridGroup CreateGroup(string name, IEnumerable<string> itemIds)
    {
        var group = new GridGroup
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = name,
            ItemIds = itemIds.ToList(),
        };
        Groups.Add(group);
        return group;
    }

    /// <summary>Finds whichever display currently holds a placement.</summary>
    public (MonitorLayout Layout, GridPlacement Placement)? Locate(string placementId)
    {
        foreach (var monitor in Monitors)
        {
            var placement = monitor.Find(placementId);
            if (placement is not null) return (monitor, placement);
        }
        return null;
    }

    public MonitorLayout ForMonitor(string monitorId)
    {
        var existing = Monitors.FirstOrDefault(m =>
            string.Equals(m.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = new MonitorLayout { MonitorId = monitorId };
        Monitors.Add(created);
        return created;
    }

    /// <summary>
    /// Reconciles every connected display at once: drops placements for items
    /// that no longer exist, then places new items on the first display, in the
    /// order given, that still has a free cell.
    ///
    /// Placement is a whole-home decision rather than a per-monitor one — done
    /// per monitor, the primary would claim every item and a second display
    /// could never receive anything but the overflow of a single full grid.
    /// </summary>
    /// <param name="displays">Connected displays, primary first.</param>
    /// <param name="unplaced">Items that fit nowhere.</param>
    public bool Reconcile(
        IReadOnlyCollection<string> liveItemIds,
        IReadOnlyList<(string MonitorId, int Columns, int Rows)> displays,
        out int unplaced)
    {
        var changed = false;
        var liveItems = new HashSet<string>(liveItemIds, StringComparer.OrdinalIgnoreCase);

        // Folders first. Deleted items leave them, and a folder left with one
        // item or none dissolves — its last item takes the folder's cell,
        // which is how Android behaves when you drag the second-last item out.
        foreach (var group in Groups.ToList())
        {
            var before = group.ItemIds.Count;
            group.ItemIds = group.ItemIds
                .Where(liveItems.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (group.ItemIds.Count != before) changed = true;

            if (group.ItemIds.Count >= 2) continue;

            var located = Locate(group.PlacementId);
            if (group.ItemIds.Count == 1 && located is { } spot)
                spot.Placement.ItemId = group.ItemIds[0];
            else if (located is { } empty)
                empty.Layout.Placements.Remove(empty.Placement);

            Groups.Remove(group);
            changed = true;
        }

        var grouped = new HashSet<string>(Groups.SelectMany(g => g.ItemIds), StringComparer.OrdinalIgnoreCase);

        // A placement is live if its item exists and is not tucked in a folder,
        // or if it is a folder tile itself.
        // Ordered as well as a set: the order decides which free cell each new
        // item lands in, and a hash order would scatter them.
        var liveOrdered = liveItemIds.Where(id => !grouped.Contains(id))
            .Concat(Groups.Select(g => g.PlacementId))
            .ToList();
        var live = new HashSet<string>(liveOrdered, StringComparer.OrdinalIgnoreCase);

        // Uninstalled or deleted items leave every layout, connected or not.
        foreach (var monitor in Monitors)
        {
            if (monitor.Placements.RemoveAll(p => !live.Contains(p.ItemId)) > 0) changed = true;
        }

        var connected = displays
            .Select(d => (Layout: ForMonitor(d.MonitorId), d.Columns, d.Rows))
            .ToList();
        var connectedIds = new HashSet<string>(displays.Select(d => d.MonitorId), StringComparer.OrdinalIgnoreCase);

        foreach (var (layout, columns, rows) in connected)
        {
            // A resolution drop can strand placements off the edge of the grid.
            // Release them so they reflow instead of silently vanishing.
            // Unlocked widgets are exempt: they are not bound to the grid, and
            // their covered cells are simply clipped to whatever grid exists.
            foreach (var free in layout.Placements.Where(p => p.Unlocked))
            {
                free.Column = Math.Min(free.Column, columns);
                free.Row = Math.Min(free.Row, rows);
                free.ColumnSpan = Math.Max(0, Math.Min(free.ColumnSpan, columns - free.Column));
                free.RowSpan = Math.Max(0, Math.Min(free.RowSpan, rows - free.Row));
            }

            var outside = layout.Placements.RemoveAll(p =>
                p.ParentGroupId is null && !p.Unlocked &&
                (p.Column + p.ColumnSpan > columns || p.Row + p.RowSpan > rows));
            if (outside > 0) changed = true;

            if (layout.Columns != columns || layout.Rows != rows)
            {
                layout.Columns = columns;
                layout.Rows = rows;
                changed = true;
            }
        }

        // One item, one place. Layouts written by older builds (or by a
        // display order that has since changed) can hold the same item on two
        // displays at once; the earlier display in the list — the primary
        // first — keeps it.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (layout, _, _) in connected)
        {
            if (layout.Placements.RemoveAll(p => !claimed.Add(p.ItemId)) > 0) changed = true;
        }

        // Only a placement on a display that is actually attached counts as
        // "shown". Items parked on an unplugged monitor are brought over, and
        // removed from that monitor's layout so they cannot appear twice when
        // it comes back.
        var shown = new HashSet<string>(
            connected.SelectMany(c => c.Layout.Placements).Select(p => p.ItemId),
            StringComparer.OrdinalIgnoreCase);

        unplaced = 0;
        foreach (var id in liveOrdered)
        {
            if (shown.Contains(id)) continue;

            var landed = false;
            foreach (var (layout, columns, rows) in connected)
            {
                var cell = layout.FindFreeCell(columns, rows);
                if (cell is null) continue;

                foreach (var parked in Monitors.Where(m => !connectedIds.Contains(m.MonitorId)))
                    parked.Placements.RemoveAll(p => string.Equals(p.ItemId, id, StringComparison.OrdinalIgnoreCase));

                layout.Placements.Add(new GridPlacement { ItemId = id, Column = cell.Value.Column, Row = cell.Value.Row });
                shown.Add(id);
                changed = true;
                landed = true;
                break;
            }

            if (!landed) unplaced++;
        }

        return changed;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hearth", "layout.json");

    public static HomeLayout Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new HomeLayout();
            return JsonSerializer.Deserialize<HomeLayout>(File.ReadAllText(path), SerializerOptions) ?? new HomeLayout();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] layout load failed, starting fresh: {ex.Message}");
            return new HomeLayout();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temp, path, overwrite: true);
    }
}
