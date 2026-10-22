using System.Diagnostics;

namespace Hearth.App.Hosting;

/// <summary>Detects whether Wallpaper Engine is running and owns the shell's wallpaper layer.</summary>
internal static class WallpaperEngine
{
    public static bool IsRunning() => Has("wallpaper64") || Has("wallpaper32");

    private static bool Has(string name)
    {
        var list = Process.GetProcessesByName(name);
        try { return list.Length > 0; }
        finally { foreach (var p in list) p.Dispose(); }
    }
}
