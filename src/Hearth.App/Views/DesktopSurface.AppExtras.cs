using System.Windows;
using System.Windows.Controls;
using Hearth.App.Controls;
using Hearth.Core.Diagnostics;
using Hearth.Core.Notifications;
using Hearth.Core.Shell;
using Hearth.Core.Threading;

namespace Hearth.App.Views;

/// <summary>
/// Per-app extras that depend on knowing which installed app a tile belongs
/// to: notification badges, and the app's jump list in its right-click menu.
/// </summary>
public partial class DesktopSurface
{
    /// <summary>Tile item id to AppUserModelID, rebuilt whenever the items are.</summary>
    private Dictionary<string, string> _appIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Works out app identities off the UI thread, then refreshes badges.</summary>
    private async Task ResolveAppIdsAsync()
    {
        var items = _items.Values.ToList();
        var installed = _installedApps ?? [];
        try
        {
            _appIds = await StaTask.Run(() => AppIdentity.Resolve(items, installed)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("resolve app ids", ex);
            return;
        }

        Log.Write($"app identities: {_appIds.Count} of {items.Count} item(s) matched to an installed app");
        ApplyBadges();
    }

    private void OnBadgesChanged() =>
        Dispatcher.InvokeAsync(ApplyBadges);

    private Badge BadgeFor(string itemId) =>
        _appIds.TryGetValue(itemId, out var appId) ? App.Badges.For(appId) : Badge.None;

    /// <summary>Folders show the total of what is inside them.</summary>
    private Badge BadgeForPlacement(string placementId)
    {
        if (_layout.FindGroupByPlacement(placementId) is not { } group) return BadgeFor(placementId);

        var total = 0;
        var dot = false;
        foreach (var id in group.ItemIds)
        {
            var badge = BadgeFor(id);
            total += badge.Count;
            dot |= badge.Dot;
        }
        return new Badge(total, dot && total == 0);
    }

    private void ApplyBadges()
    {
        foreach (var (id, tile) in _tiles) tile.Badge = BadgeForPlacement(id);
        foreach (var tile in FolderPanelTiles()) tile.Badge = tile.Item is { } item ? BadgeFor(item.Id) : Badge.None;
    }

    private IEnumerable<IconTile> FolderPanelTiles() =>
        _folderPanel is null ? [] : TilesUnder(_folderPanel);

    private static IEnumerable<IconTile> TilesUnder(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is IconTile tile) yield return tile;
            foreach (var nested in TilesUnder(child)) yield return nested;
        }
    }

    // ---- Jump lists -----------------------------------------------------

    /// <summary>Adds the tile's app jump list to the top of its menu, if it has one.</summary>
    private bool AddJumpList(ContextMenu menu, LauncherItem item) =>
        _appIds.TryGetValue(item.Id, out var appId) && JumpListMenu.Add(menu, appId);
}
