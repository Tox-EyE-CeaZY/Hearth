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

    public bool IsRegionFree(int column, int row, int columnSpan, int rowSpan)
    {
        for (var y = row; y < row + rowSpan; y++)
        {
            for (var x = column; x < column + columnSpan; x++)
            {
                if (IsOccupied(x, y)) return false;
            }
        }
        return true;
    }

    /// <summary>First run of free cells a widget of this size fits, in reading order.</summary>
    public (int Column, int Row)? FindFreeRegion(int columnSpan, int rowSpan, int columns, int rows)
    {
        for (var row = 0; row <= rows - rowSpan; row++)
        {
            for (var column = 0; column <= columns - columnSpan; column++)
            {
                if (IsRegionFree(column, row, columnSpan, rowSpan)) return (column, row);
            }
        }
        return null;
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

/// <summary>One connected display, as the layout sees it.</summary>
/// <param name="MonitorId">The shell's stable device path.</param>
/// <param name="Width">Physical pixels.</param>
/// <param name="Height">Physical pixels.</param>
/// <param name="Columns">Grid size, which already reflects icon size, gap, labels and style.</param>
/// <param name="Rows">See <paramref name="Columns"/>.</param>
public readonly record struct DisplayGrid(string MonitorId, int Width, int Height, int Columns, int Rows);

/// <summary>
/// The home screen as arranged for one particular set of displays.
///
/// Laptop on its own, laptop plus monitor, and monitor with the lid shut are
/// three different screens, and each keeps its own arrangement. Going back to
/// a set of displays restores exactly what was there, rather than whatever
/// was left after items were shuffled around to fit the one in between.
/// </summary>
public sealed class DisplayArrangement
{
    /// <summary>See <see cref="HomeLayout.KeyFor"/>.</summary>
    public required string Key { get; set; }

    /// <summary>One entry per display, primary first.</summary>
    public List<MonitorLayout> Monitors { get; set; } = [];

    public DateTime LastUsed { get; set; }
}

/// <summary>Every arrangement, and the disk format behind them.</summary>
public sealed class HomeLayout
{
    public const string GroupPrefix = "group:";

    /// <summary>
    /// Key for monitor layouts carried over from a version 1 file, or created
    /// before any display was known. Their order says nothing about which
    /// display was primary.
    /// </summary>
    public const string UnorderedKey = "unordered";

    /// <summary>Arrangements beyond this many, least recently used first, are forgotten.</summary>
    public const int MaxArrangements = 16;

    public int Version { get; set; } = 2;

    public List<DisplayArrangement> Arrangements { get; set; } = [];

    public string? ActiveArrangement { get; set; }

    /// <summary>The active arrangement's displays. Everything interactive works on these.</summary>
    [JsonIgnore]
    public List<MonitorLayout> Monitors => Current.Monitors;

    /// <summary>Version 1 wrote a single <c>Monitors</c> list. Read only.</summary>
    [JsonPropertyName("Monitors")]
    public List<MonitorLayout>? LegacyMonitors
    {
        get => null;
        set => _legacyMonitors = value;
    }

    private List<MonitorLayout>? _legacyMonitors;

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
    /// Installed apps the user added to the home screen one by one. Shown even
    /// when "Show installed apps" is off, which is what lets that setting stay
    /// off without the home screen being limited to desktop shortcuts.
    /// </summary>
    public List<string> Pinned { get; set; } = [];

    public bool IsPinned(string itemId) => Pinned.Contains(itemId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Icon placement the user chose for individual icons. Missing means
    /// automatic. Icons differ too much for one rule to suit all of them.
    /// </summary>
    public Dictionary<string, Hearth.Core.Icons.IconFit> IconFits { get; set; } = [];

    public Hearth.Core.Icons.IconFit FitFor(string itemId) =>
        IconFits.TryGetValue(itemId, out var fit) ? fit : Hearth.Core.Icons.IconFit.Auto;

    private DisplayArrangement Current
    {
        get
        {
            ActiveArrangement ??= UnorderedKey;
            var active = Find(ActiveArrangement);
            if (active is null)
            {
                active = new DisplayArrangement { Key = ActiveArrangement, LastUsed = DateTime.UtcNow };
                Arrangements.Add(active);
            }
            return active;
        }
    }

    private DisplayArrangement? Find(string key) =>
        Arrangements.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every placement in every arrangement, connected or not.</summary>
    [JsonIgnore]
    public IEnumerable<GridPlacement> AllPlacements =>
        Arrangements.SelectMany(a => a.Monitors).SelectMany(m => m.Placements);

    /// <summary>
    /// Identifies a set of displays: which displays, in which role, at what
    /// resolution and grid size. Changing icon size or style changes the grid,
    /// so it gets its own arrangement too, and changing it back restores the
    /// old one exactly.
    /// </summary>
    public static string KeyFor(IReadOnlyList<DisplayGrid> displays) =>
        string.Join("|", displays.Select(d =>
            $"{d.MonitorId.ToLowerInvariant()}@{d.Width}x{d.Height}/{d.Columns}x{d.Rows}"));

    /// <summary>
    /// Switches to the arrangement for these displays, creating it from the
    /// current one if this set of displays has not been seen before. Returns
    /// true when the active arrangement changed.
    /// </summary>
    /// <param name="displays">Connected displays, primary first.</param>
    public bool Activate(IReadOnlyList<DisplayGrid> displays)
    {
        var key = KeyFor(displays);
        if (string.Equals(ActiveArrangement, key, StringComparison.OrdinalIgnoreCase))
        {
            Current.LastUsed = DateTime.UtcNow;
            return false;
        }

        var target = Find(key);
        if (target is null)
        {
            target = new DisplayArrangement
            {
                Key = key,
                Monitors = Derive(Current, displays),
            };
            Arrangements.Add(target);
        }

        target.LastUsed = DateTime.UtcNow;
        ActiveArrangement = target.Key;

        // Forget the stalest. Transient display states during a plug or unplug
        // create arrangements nobody will return to.
        foreach (var stale in Arrangements
                     .Where(a => a != target)
                     .OrderByDescending(a => a.LastUsed)
                     .Skip(MaxArrangements - 1)
                     .ToList())
        {
            Arrangements.Remove(stale);
        }

        return true;
    }

    /// <summary>
    /// Builds a first arrangement for a new set of displays from the one being
    /// left. Nothing in <paramref name="from"/> is modified.
    ///
    /// A display keeps its own layout. The exception is a new primary taking
    /// over from a primary that has gone: the home screen follows the primary
    /// role, so the main layout arrives intact and whatever that display
    /// showed before is added around it. Layouts belonging to displays that
    /// are no longer connected are merged into the remaining ones, keeping each
    /// item's cell when it is free and each widget's size either way.
    /// </summary>
    private static List<MonitorLayout> Derive(DisplayArrangement from, IReadOnlyList<DisplayGrid> displays)
    {
        var previous = Clone(from.Monitors);
        var connectedIds = new HashSet<string>(displays.Select(d => d.MonitorId), StringComparer.OrdinalIgnoreCase);
        var unused = new List<MonitorLayout>(previous);
        var result = new List<MonitorLayout>();

        MonitorLayout? Take(MonitorLayout? layout)
        {
            if (layout is null || !unused.Remove(layout)) return null;
            return layout;
        }

        MonitorLayout? Own(string monitorId) => unused.FirstOrDefault(m =>
            string.Equals(m.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase));

        var ordered = !string.Equals(from.Key, UnorderedKey, StringComparison.OrdinalIgnoreCase);
        var oldPrimary = ordered ? previous.FirstOrDefault() : null;
        var primaryGone = oldPrimary is not null && !connectedIds.Contains(oldPrimary.MonitorId);

        for (var i = 0; i < displays.Count; i++)
        {
            var display = displays[i];
            var layout = (i == 0 && primaryGone ? Take(oldPrimary) : null)
                         ?? Take(Own(display.MonitorId))
                         ?? Take(unused.FirstOrDefault(m => !connectedIds.Contains(m.MonitorId)));

            layout ??= new MonitorLayout { MonitorId = display.MonitorId };
            layout.MonitorId = display.MonitorId;
            result.Add(layout);
        }

        var targets = result.Zip(displays, (layout, d) => (layout, d.Columns, d.Rows)).ToList();

        // Whatever is left belonged to displays that are gone, or was a new
        // primary's own layout displaced by the old primary's.
        foreach (var leftover in unused)
        {
            foreach (var placement in leftover.Placements
                         .Where(p => p.ParentGroupId is null)
                         .OrderBy(p => p.Row)
                         .ThenBy(p => p.Column))
            {
                PlaceCarried(placement, targets);
            }
        }

        return result;
    }

    /// <summary>
    /// Puts an existing placement onto the first display that can take it:
    /// at its own cells if they are free, otherwise at the first free run of
    /// cells its size fits. A widget that fits nowhere is not squeezed into a
    /// single cell; it is left off, and still exists in the other arrangements.
    /// </summary>
    private static bool PlaceCarried(
        GridPlacement template,
        IReadOnlyList<(MonitorLayout Layout, int Columns, int Rows)> targets)
    {
        var columnSpan = Math.Max(1, template.ColumnSpan);
        var rowSpan = Math.Max(1, template.RowSpan);

        // Unlocked widgets sitting in the margins cover no cells, so any
        // display will do. Pixel bounds are clamped to the display when drawn.
        if (template.Unlocked && (template.ColumnSpan == 0 || template.RowSpan == 0))
        {
            if (targets.Count == 0) return false;
            targets[0].Layout.Placements.Add(Copy(template));
            return true;
        }

        foreach (var (layout, columns, rows) in targets)
        {
            if (template.Column + columnSpan <= columns &&
                template.Row + rowSpan <= rows &&
                layout.IsRegionFree(template.Column, template.Row, columnSpan, rowSpan))
            {
                layout.Placements.Add(Copy(template));
                return true;
            }
        }

        foreach (var (layout, columns, rows) in targets)
        {
            if (layout.FindFreeRegion(columnSpan, rowSpan, columns, rows) is not { } cell) continue;

            var moved = Copy(template);
            moved.Column = cell.Column;
            moved.Row = cell.Row;
            moved.ColumnSpan = columnSpan;
            moved.RowSpan = rowSpan;

            // Its free pixel position belonged to the old spot. Snapping it to
            // the grid keeps its size without dropping it on top of something.
            moved.Unlocked = false;
            moved.Free = null;

            layout.Placements.Add(moved);
            return true;
        }

        return false;
    }

    private static GridPlacement Copy(GridPlacement p) => new()
    {
        ItemId = p.ItemId,
        Column = p.Column,
        Row = p.Row,
        ColumnSpan = p.ColumnSpan,
        RowSpan = p.RowSpan,
        Unlocked = p.Unlocked,
        Free = p.Free is { } f ? new FreeRect { X = f.X, Y = f.Y, Width = f.Width, Height = f.Height } : null,
    };

    private static List<MonitorLayout> Clone(List<MonitorLayout> monitors) =>
        JsonSerializer.Deserialize<List<MonitorLayout>>(JsonSerializer.Serialize(monitors, SerializerOptions), SerializerOptions) ?? [];

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

    /// <summary>Removes a placement from every arrangement, connected or not.</summary>
    public int RemoveEverywhere(string placementId) =>
        Arrangements.SelectMany(a => a.Monitors)
            .Sum(m => m.Placements.RemoveAll(p =>
                string.Equals(p.ItemId, placementId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Switches to the arrangement for <paramref name="displays"/>, then
    /// reconciles it: drops placements for items that no longer exist (in
    /// every arrangement), and places new items on the first display, in the
    /// order given, that still has room.
    ///
    /// Placement is a whole-home decision rather than a per-monitor one — done
    /// per monitor, the primary would claim every item and a second display
    /// could never receive anything but the overflow of a single full grid.
    /// </summary>
    /// <param name="displays">Connected displays, primary first.</param>
    /// <param name="unplaced">Items that fit nowhere.</param>
    public bool Reconcile(
        IReadOnlyCollection<string> liveItemIds,
        IReadOnlyList<DisplayGrid> displays,
        out int unplaced)
    {
        var changed = Activate(displays);
        var liveItems = new HashSet<string>(liveItemIds, StringComparer.OrdinalIgnoreCase);
        var allMonitors = Arrangements.SelectMany(a => a.Monitors).ToList();

        // Folders first. Deleted items leave them, and a folder left with one
        // item or none dissolves — its last item takes the folder's cell,
        // which is how Android behaves when you drag the second-last item out.
        // Every arrangement's copy of the tile is converted the same way.
        foreach (var group in Groups.ToList())
        {
            var before = group.ItemIds.Count;
            group.ItemIds = group.ItemIds
                .Where(liveItems.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (group.ItemIds.Count != before) changed = true;

            if (group.ItemIds.Count >= 2) continue;

            foreach (var monitor in allMonitors)
            {
                if (monitor.Find(group.PlacementId) is not { } tile) continue;
                if (group.ItemIds.Count == 1 && monitor.Find(group.ItemIds[0]) is null)
                    tile.ItemId = group.ItemIds[0];
                else
                    monitor.Placements.Remove(tile);
            }

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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var live = new HashSet<string>(liveOrdered, StringComparer.OrdinalIgnoreCase);

        // Uninstalled, deleted, hidden or grouped items leave every
        // arrangement, not only the one on screen.
        foreach (var monitor in allMonitors)
        {
            if (monitor.Placements.RemoveAll(p => !live.Contains(p.ItemId)) > 0) changed = true;
        }

        var current = Current;
        var connected = displays
            .Select(d => (Layout: ForMonitor(d.MonitorId), d.Columns, d.Rows))
            .ToList();

        // The active arrangement holds exactly the connected displays, primary
        // first. Anything else in it is left over from an older file.
        var connectedLayouts = connected.Select(c => c.Layout).ToList();
        var strays = current.Monitors.Where(m => !connectedLayouts.Contains(m)).ToList();
        if (strays.Count > 0 || !current.Monitors.SequenceEqual(connectedLayouts))
        {
            current.Monitors = connectedLayouts;
            changed = true;
        }

        // Placements that no longer fit, kept so they can be put back at their
        // own size rather than as a single cell.
        var carried = new Dictionary<string, GridPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var stray in strays)
        {
            foreach (var p in stray.Placements) carried.TryAdd(p.ItemId, p);
        }

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

            foreach (var outside in layout.Placements
                         .Where(p => p.ParentGroupId is null && !p.Unlocked &&
                                     (p.Column + p.ColumnSpan > columns || p.Row + p.RowSpan > rows))
                         .ToList())
            {
                layout.Placements.Remove(outside);
                carried[outside.ItemId] = outside;
                changed = true;
            }

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

        // Anything live but not on screen gets a spot. Its shape comes from
        // where it was last seen: a placement that fell off this grid, or the
        // most recent other arrangement holding it. A widget keeps its size.
        var elsewhere = Arrangements
            .Where(a => a != current)
            .OrderByDescending(a => a.LastUsed)
            .SelectMany(a => a.Monitors)
            .SelectMany(m => m.Placements)
            .Where(p => p.ParentGroupId is null);
        foreach (var p in elsewhere) carried.TryAdd(p.ItemId, p);

        unplaced = 0;
        foreach (var id in liveOrdered)
        {
            if (claimed.Contains(id)) continue;

            bool landed;
            if (carried.TryGetValue(id, out var template))
            {
                landed = PlaceCarried(template, connected);
            }
            else
            {
                landed = false;
                foreach (var (layout, columns, rows) in connected)
                {
                    if (layout.FindFreeCell(columns, rows) is not { } cell) continue;
                    layout.Placements.Add(new GridPlacement { ItemId = id, Column = cell.Column, Row = cell.Row });
                    landed = true;
                    break;
                }
            }

            if (landed)
            {
                claimed.Add(id);
                changed = true;
            }
            else
            {
                unplaced++;
            }
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
            var layout = JsonSerializer.Deserialize<HomeLayout>(File.ReadAllText(path), SerializerOptions) ?? new HomeLayout();

            // A version 1 file: one list of monitors, whichever were connected
            // when it was written. It becomes the arrangement every new set of
            // displays is derived from, once.
            if (layout._legacyMonitors is { Count: > 0 } legacy && layout.Arrangements.Count == 0)
            {
                layout.Arrangements.Add(new DisplayArrangement { Key = UnorderedKey, Monitors = legacy });
                layout.ActiveArrangement = UnorderedKey;
            }
            layout._legacyMonitors = null;
            layout.Version = 2;
            return layout;
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
