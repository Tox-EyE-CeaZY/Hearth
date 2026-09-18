namespace Hearth.App.Views;

/// <summary>The settings screen's way in to changes that need the surface's own state.</summary>
public partial class DesktopSurface
{
    internal void SetHideShellIcons(bool hide)
    {
        App.Settings.HideShellIcons = hide;
        if (hide) _layer.HideShellIcons();
        else _layer.RestoreShellIcons();
        App.Settings.Save();
    }

    internal async Task SetIncludeInstalledAppsAsync(bool include)
    {
        App.Settings.IncludeInstalledApps = include;
        App.Settings.Save();
        await RefreshItemsAsync().ConfigureAwait(true);
    }
}
