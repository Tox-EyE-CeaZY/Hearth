using System.Windows;
using Hearth.Core.Diagnostics;
using Hearth.Core.Layout;
using Hearth.Core.Shell;

namespace Hearth.App.Views;

/// <summary>
/// Adding apps one at a time, rather than all 200-odd installed apps at once.
/// Apps chosen here are kept in <see cref="HomeLayout.Pinned"/>.
/// </summary>
public partial class DesktopSurface : AddAppsWindow.IHome
{
    private AddAppsWindow? _addAppsWindow;

    /// <summary>
    /// Where apps added from the drawer land: the cell that was right-clicked
    /// to open it. Each one takes the nearest free cell, so a run of additions
    /// fans out from that spot.
    /// </summary>
    private (string MonitorId, int Column, int Row)? _addAnchor;

    /// <summary>The last full AppsFolder read, reused by the drawer.</summary>
    private IReadOnlyList<LauncherItem>? _installedApps;

    private void OpenAddApps(Point? at)
    {
        _addAnchor = null;
        if (at is { } point && SurfaceAt(point) is { } surface)
        {
            var (column, row) = PointToCell(surface, point);
            _addAnchor = (surface.Info.DeviceId, column, row);
        }

        if (_addAppsWindow is not null)
        {
            _addAppsWindow.RefreshStates();
            _addAppsWindow.Activate();
            return;
        }

        _addAppsWindow = new AddAppsWindow(this);
        _addAppsWindow.Closed += (_, _) => _addAppsWindow = null;
        _addAppsWindow.Show();
        _addAppsWindow.Activate();
    }

    async Task<IReadOnlyList<LauncherItem>> AddAppsWindow.IHome.GetInstalledAppsAsync()
    {
        if (_installedApps is { Count: > 0 } cached) return cached;
        return _installedApps = await App.Apps.GetAsync().ConfigureAwait(true);
    }

    // ---- For the Start menu ---------------------------------------------

    internal bool IsAppOnHome(LauncherItem app) => HomeEntryFor(app) is not null;

    internal void SetAppOnHome(LauncherItem app, bool onHome)
    {
        // From Start there is no clicked cell to place it near.
        _addAnchor = null;
        if (onHome) AddApp(app);
        else RemoveFromHome(app);
    }

    bool AddAppsWindow.IHome.IsOnHome(LauncherItem app) => HomeEntryFor(app) is not null;

    void AddAppsWindow.IHome.SetOnHome(LauncherItem app, bool onHome)
    {
        if (onHome) AddApp(app);
        else RemoveFromHome(app);
    }

    /// <summary>
    /// The item on the home screen that stands for this app: the app itself,
    /// or a desktop shortcut with the same name (Windows puts a shortcut on the
    /// desktop for many installs, and a second tile would be a duplicate).
    /// </summary>
    private LauncherItem? HomeEntryFor(LauncherItem app)
    {
        if (_items.TryGetValue(app.Id, out var direct) && !_layout.IsHidden(direct.Id)) return direct;

        return _items.Values.FirstOrDefault(i =>
            i.Kind == LauncherItemKind.Shortcut &&
            !_layout.IsHidden(i.Id) &&
            string.Equals(i.DisplayName, app.DisplayName, StringComparison.CurrentCultureIgnoreCase));
    }

    private void AddApp(LauncherItem app)
    {
        if (!_layout.IsPinned(app.Id)) _layout.Pinned.Add(app.Id);
        _layout.Hidden.RemoveAll(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase));
        _items[app.Id] = app;

        // Placed here rather than left to Reconcile, which would use the first
        // free cell of the primary display instead of where the user asked.
        if (_addAnchor is { } anchor &&
            _layout.Locate(app.Id) is null &&
            _surfaces.FirstOrDefault(s => string.Equals(s.Info.DeviceId, anchor.MonitorId, StringComparison.OrdinalIgnoreCase)) is { } surface)
        {
            var layout = _layout.ForMonitor(surface.Info.DeviceId);
            if (NearestFreeCell(layout, surface, anchor.Column, anchor.Row) is { } cell)
                layout.Placements.Add(new GridPlacement { ItemId = app.Id, Column = cell.Column, Row = cell.Row });
        }

        Log.Write($"added app '{app.DisplayName}' ({app.Id})");
        TrySaveLayout();
        RelayoutTiles();
    }

    /// <summary>
    /// Takes an app off the home screen. An app that was added by hand is
    /// simply un-added; anything else (a desktop shortcut, or an app shown
    /// because "Show installed apps" is on) is hidden.
    /// </summary>
    private void RemoveFromHome(LauncherItem app)
    {
        if (_layout.Pinned.RemoveAll(id => string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase)) > 0 &&
            !App.Settings.IncludeInstalledApps)
        {
            _items.Remove(app.Id);
        }

        // Whatever still stands for the app goes too, or it would stay "Added".
        while (HomeEntryFor(app) is { } entry) _layout.Hidden.Add(entry.Id);

        Log.Write($"removed app '{app.DisplayName}' from home");
        CloseFolder();
        TrySaveLayout();
        RelayoutTiles();
    }

    /// <summary>True when "Remove from home" undoes an add rather than hiding.</summary>
    private bool IsRemovablePin(LauncherItem item) =>
        item.Kind == LauncherItemKind.App && _layout.IsPinned(item.Id) && !App.Settings.IncludeInstalledApps;
}
