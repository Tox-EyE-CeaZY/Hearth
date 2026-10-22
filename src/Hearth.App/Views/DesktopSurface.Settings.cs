using Hearth.App.Hosting;

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

    /// <summary>Turns Wallpaper Engine support on or off and applies it straight away.</summary>
    internal void SetWallpaperEngineSupport(bool enabled)
    {
        App.Settings.WallpaperEngineSupport = enabled;
        App.Settings.Save();
        DesktopHost.Current?.RefreshWallpaperEngineMode();
    }

    internal async Task SetIncludeInstalledAppsAsync(bool include)
    {
        App.Settings.IncludeInstalledApps = include;
        App.Settings.Save();
        await RefreshItemsAsync().ConfigureAwait(true);
    }
}
