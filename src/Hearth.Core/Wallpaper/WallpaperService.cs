using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearth.Core.Interop;

namespace Hearth.Core.Wallpaper;

/// <summary>One physical display, as the shell identifies it.</summary>
public sealed record MonitorInfo(string DeviceId, Rect Bounds, string? WallpaperPath);

/// <summary>
/// Reads the user's wallpaper so Hearth can paint it itself.
///
/// Hearth's window lives inside WorkerW, which is the window that would
/// normally be painting the wallpaper — so anything we draw covers it. Rather
/// than fight for transparency (a layered window would drop us out of hardware
/// rendering), we take over the job: read the same images the shell would have
/// drawn and render them ourselves. That also buys the dim and blur passes that
/// make icons legible over a busy photo.
/// </summary>
public sealed class WallpaperService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        IDesktopWallpaper? wallpaper = null;

        try
        {
            wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            wallpaper.GetMonitorDevicePathCount(out var count);

            for (uint i = 0; i < count; i++)
            {
                wallpaper.GetMonitorDevicePathAt(i, out var deviceId);
                if (string.IsNullOrEmpty(deviceId)) continue;

                Rect bounds;
                try
                {
                    wallpaper.GetMonitorRECT(deviceId, out var rect);
                    bounds = new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
                }
                catch (COMException)
                {
                    // Device path is known but not currently attached.
                    continue;
                }

                string? path = null;
                try
                {
                    wallpaper.GetWallpaper(deviceId, out var w);
                    if (!string.IsNullOrEmpty(w) && File.Exists(w)) path = w;
                }
                catch (COMException)
                {
                    // Solid-colour background, or a slideshow mid-transition.
                }

                // Mid-unplug, a display can still be listed with no area.
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                monitors.Add(new MonitorInfo(deviceId, bounds, path));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            Debug.WriteLine($"[Hearth] IDesktopWallpaper unavailable: {ex.Message}");
        }
        finally
        {
            if (wallpaper is not null) Marshal.FinalReleaseComObject(wallpaper);
        }

        if (monitors.Count == 0) monitors.Add(FallbackMonitor());
        return monitors;
    }

    /// <summary>
    /// Used when IDesktopWallpaper is unavailable: one virtual-screen-sized
    /// surface, so Hearth still comes up rather than showing nothing.
    /// </summary>
    private static MonitorInfo FallbackMonitor()
    {
        var x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        var y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        var w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        var h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        return new MonitorInfo("fallback", new Rect(x, y, w, h), null);
    }

    public DesktopWallpaperPosition GetPosition()
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            wallpaper.GetPosition(out var position);
            return position;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return DesktopWallpaperPosition.Fill;
        }
        finally
        {
            if (wallpaper is not null) Marshal.FinalReleaseComObject(wallpaper);
        }
    }

    public Color GetBackgroundColor()
    {
        IDesktopWallpaper? wallpaper = null;
        try
        {
            wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            wallpaper.GetBackgroundColor(out var colorref);
            // COLORREF is 0x00BBGGRR.
            return Color.FromRgb(
                (byte)(colorref & 0xFF),
                (byte)((colorref >> 8) & 0xFF),
                (byte)((colorref >> 16) & 0xFF));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Colors.Black;
        }
        finally
        {
            if (wallpaper is not null) Marshal.FinalReleaseComObject(wallpaper);
        }
    }

    /// <summary>
    /// Builds the brush for one monitor, honouring the shell's fit mode so the
    /// wallpaper lands exactly where the user expects it.
    /// </summary>
    public Brush BuildBrush(MonitorInfo monitor, DesktopWallpaperPosition position, Color background)
    {
        if (monitor.WallpaperPath is null) return new SolidColorBrush(background);

        BitmapImage image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(monitor.WallpaperPath);

            // Decode straight to the size this monitor will actually show.
            //
            // Wallpapers are routinely far larger than the screen — a 3840x2401
            // photo decodes to ~35 MB, is held once per monitor, and then makes
            // HighQuality scaling build full-size resampling intermediates on
            // top. Letting the decoder do the scaling drops all of that, and
            // costs nothing in quality because the result is downscaled either
            // way.
            //
            // Tile and Center must decode at native size: their whole meaning
            // is the image's real pixel dimensions.
            if (position is not (DesktopWallpaperPosition.Tile or DesktopWallpaperPosition.Center))
            {
                var target = DecodeWidthFor(monitor.WallpaperPath, monitor.Bounds);
                if (target > 0) image.DecodePixelWidth = target;
            }

            image.EndInit();
            image.Freeze();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            Debug.WriteLine($"[Hearth] could not load wallpaper '{monitor.WallpaperPath}': {ex.Message}");
            return new SolidColorBrush(background);
        }

        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };

        switch (position)
        {
            case DesktopWallpaperPosition.Tile:
                brush.TileMode = TileMode.Tile;
                brush.Stretch = Stretch.None;
                brush.ViewportUnits = BrushMappingMode.Absolute;
                brush.Viewport = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
                break;

            case DesktopWallpaperPosition.Center:
                brush.Stretch = Stretch.None;
                break;

            case DesktopWallpaperPosition.Stretch:
                brush.Stretch = Stretch.Fill;
                break;

            case DesktopWallpaperPosition.Fit:
                brush.Stretch = Stretch.Uniform;
                break;

            // Fill and Span both crop to cover; Span's cross-monitor slicing is
            // handled by the caller positioning each surface in virtual-screen
            // space, so the brush itself is the same.
            default:
                brush.Stretch = Stretch.UniformToFill;
                break;
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Width to decode a wallpaper at so it still covers <paramref name="bounds"/>
    /// without carrying resolution nothing will ever display.
    ///
    /// Reads only the image header — BitmapFrame with DelayCreation does not
    /// decode pixels, so this costs a file-open, not a 35 MB allocation.
    /// Returns 0 when the source is already no larger than the target, leaving
    /// the decoder alone rather than upscaling.
    /// </summary>
    private static int DecodeWidthFor(string path, Rect bounds)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

            double sourceWidth = frame.PixelWidth;
            double sourceHeight = frame.PixelHeight;
            if (sourceWidth <= 0 || sourceHeight <= 0) return 0;

            // Cover, not fit: whichever axis needs more resolution wins, so a
            // UniformToFill crop never samples below one source pixel per
            // screen pixel.
            var scale = Math.Max(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
            if (scale >= 1.0) return 0; // already at or below display size

            return (int)Math.Ceiling(sourceWidth * scale);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            // Not worth failing the wallpaper over; fall back to a full decode.
            Debug.WriteLine($"[Hearth] could not read wallpaper header '{path}': {ex.Message}");
            return 0;
        }
    }
}
