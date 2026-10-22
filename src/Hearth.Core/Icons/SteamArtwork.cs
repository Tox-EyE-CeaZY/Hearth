using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Hearth.Core.Icons;

/// <summary>
/// Builds a real icon for a Steam game out of Steam's own artwork.
///
/// A Steam desktop shortcut is a .url whose IconFile is a 32 px .ico, and no
/// amount of scaling makes 32 px into a 256 px tile — this is the single
/// biggest source of soft icons on a machine with a games library on the
/// desktop. But Steam has the artwork: every game in the library cache has a
/// transparent <c>logo.png</c> (usually around a thousand pixels wide) and a
/// <c>library_hero</c> behind it, which is exactly the logo-on-a-background
/// pair an adaptive icon wants.
///
/// So rather than upscaling a 32 px icon, the icon is composed: the hero,
/// cropped square and dimmed, with the logo laid over it at a size that leaves
/// the safe margin an icon mask needs. Failing a logo, the portrait capsule is
/// cropped square, which still beats 32 px by a factor of eight.
///
/// The result is written once to <c>%LocalAppData%\Hearth\steam-icons</c> and
/// handed to the ordinary pipeline as an icon override, so shapes, masking,
/// shadows and caching all behave exactly as they do for anything else.
/// </summary>
public static class SteamArtwork
{
    private const int Size = 256;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "steam-icons");

    private static string? _steamPath;
    private static bool _looked;

    /// <summary>The app id in a <c>steam://rungameid/NNN</c> target, or null.</summary>
    public static string? AppIdOf(string? target)
    {
        if (target is null) return null;
        const string prefix = "steam://rungameid/";
        var index = target.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var id = target[(index + prefix.Length)..].Trim();
        // Non-Steam shortcuts use a 64-bit id; both are digits.
        var end = 0;
        while (end < id.Length && char.IsDigit(id[end])) end++;
        return end > 0 ? id[..end] : null;
    }

    /// <summary>Reads the URL out of a .url shortcut, or null if it is not one.</summary>
    public static string? UrlOf(string path)
    {
        try
        {
            if (!path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] could not read '{path}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The composed icon for an app id, building it if it is not there yet.
    /// Returns null when Steam has no artwork for it.
    /// </summary>
    public static string? IconFor(string appId)
    {
        try
        {
            var target = Path.Combine(CacheDirectory, appId + ".png");
            if (File.Exists(target)) return target;

            var art = Find(appId);
            if (art is null) return null;

            var bitmap = Compose(art.Value.Logo, art.Value.Background);
            if (bitmap is null) return null;

            Directory.CreateDirectory(CacheDirectory);
            var temp = target + ".tmp";
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] steam icon for {appId} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Forgets the composed icons, so they are made again.</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.png")) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] steam icon cache clear failed: {ex.Message}");
        }
    }

    /// <summary>Where Steam is installed, from its own registry key.</summary>
    private static string? SteamPath()
    {
        if (_looked) return _steamPath;
        _looked = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") as string is { Length: > 0 } path && Directory.Exists(path))
                _steamPath = path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[Hearth] Steam path lookup failed: {ex.Message}");
        }
        return _steamPath;
    }

    /// <summary>
    /// The files Steam keeps for one game.
    ///
    /// Recent Steam versions put each game in its own folder under
    /// librarycache, with the logo at the top and the rest inside hashed
    /// subfolders; older ones wrote flat names like <c>&lt;id&gt;_logo.png</c>
    /// straight into librarycache. Both are looked for, newest layout first.
    /// </summary>
    private static (string? Logo, string? Background)? Find(string appId)
    {
        var steam = SteamPath();
        if (steam is null) return null;

        var cache = Path.Combine(steam, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;

        string? logo = null, background = null;
        var folder = Path.Combine(cache, appId);
        if (Directory.Exists(folder))
        {
            logo = First(folder, "logo.png");
            background = First(folder, "library_hero.jpg")
                         ?? First(folder, "library_capsule.jpg")
                         ?? First(folder, "header.jpg");
        }

        // The older flat layout.
        logo ??= Existing(Path.Combine(cache, appId + "_logo.png"));
        background ??= Existing(Path.Combine(cache, appId + "_library_hero.jpg"))
                       ?? Existing(Path.Combine(cache, appId + "_library_600x900.jpg"))
                       ?? Existing(Path.Combine(cache, appId + "_header.jpg"));

        return logo is null && background is null ? null : (logo, background);

        static string? Existing(string path) => File.Exists(path) ? path : null;

        // One level of hashed subfolders is all Steam uses.
        static string? First(string root, string name)
        {
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) return direct;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root))
                {
                    var candidate = Path.Combine(sub, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing there.
            }
            return null;
        }
    }

    /// <summary>
    /// The logo over a square crop of the hero.
    ///
    /// The hero is centre-cropped and darkened so a logo of any colour reads
    /// against it, and the logo is fitted into 78% of the tile — an icon mask
    /// cuts the corners off, so artwork that runs to the edge loses its ends.
    /// </summary>
    private static BitmapSource? Compose(string? logoPath, string? backgroundPath)
    {
        var logo = Load(logoPath);
        var background = Load(backgroundPath);
        if (logo is null && background is null) return null;

        var visual = new DrawingVisual();

        // The logo is ~1000 px wide and the hero ~1900, drawn into 256. WPF's
        // default filter (bilinear) skips source pixels on a reduction that
        // large, which is what makes edges jagged. HighQuality averages them.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var context = visual.RenderOpen())
        {
            var full = new Rect(0, 0, Size, Size);

            if (background is not null)
            {
                var scale = Math.Max(Size / (double)background.PixelWidth, Size / (double)background.PixelHeight);
                var width = background.PixelWidth * scale;
                var height = background.PixelHeight * scale;
                context.PushClip(new RectangleGeometry(full));
                context.DrawImage(background, new Rect((Size - width) / 2, (Size - height) / 2, width, height));
                // Enough to keep a white logo and a dark one both legible,
                // and no more: the key art is the part worth seeing.
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x3D, 0, 0, 0)), null, full);
                context.Pop();
            }
            else
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x21)), null, full);
            }

            if (logo is not null)
            {
                const double safe = Size * 0.82;
                var scale = Math.Min(safe / logo.PixelWidth, safe / logo.PixelHeight);
                var width = logo.PixelWidth * scale;
                var height = logo.PixelHeight * scale;
                context.DrawImage(logo, new Rect((Size - width) / 2, (Size - height) / 2, width, height));
            }
        }

        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource? Load(string? path)
    {
        if (path is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image.PixelWidth > 0 ? image : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}
