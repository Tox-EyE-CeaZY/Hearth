using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Hearth.App.Views.Start;

/// <summary>
/// A blurred picture of whatever is behind the Start menu, captured just
/// before it appears.
///
/// The system backdrops cannot be relied on for this: Mica only ever shows the
/// wallpaper, and both Mica and Acrylic turn into flat grey whenever Windows
/// switches transparency effects off — which Energy Saver does, and this
/// user's machine runs with Energy Saver on. A snapshot does not follow
/// changes behind the menu while it is open, which for a menu that is open
/// for a second or two is not noticeable.
/// </summary>
internal static class StartBackdropCapture
{
    /// <summary>
    /// Captures a screen rectangle (physical pixels) with a margin around it,
    /// so the blur has real pixels to sample at the edges instead of fading
    /// to black. Returns null if the capture fails.
    /// </summary>
    public static BitmapSource? Capture(Int32Rect area, int margin)
    {
        var x = area.X - margin;
        var y = area.Y - margin;
        var width = area.Width + margin * 2;
        var height = area.Height + margin * 2;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY | CAPTUREBLT)) return null;
            SelectObject(memory, previous);

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Shrunk before it is blurred: a quarter-size image blurred by a
            // quarter of the radius looks the same and costs a sixteenth.
            var small = new TransformedBitmap(source, new ScaleTransform(0.25, 0.25));
            small.Freeze();
            return small;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// The backdrop element: the capture, blurred, stretched past the window
    /// edges by the margin, under a theme tint.
    /// </summary>
    public static UIElement Build(BitmapSource capture, double marginDips, Brush tint)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = capture,
            Stretch = Stretch.Fill,
            Margin = new Thickness(-marginDips),
            Effect = new BlurEffect { Radius = 48, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            // Rendered once and kept, rather than re-blurred on every frame.
            CacheMode = new BitmapCache(),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);

        return new System.Windows.Controls.Grid
        {
            ClipToBounds = true,
            IsHitTestVisible = false,
            Children =
            {
                image,
                new System.Windows.Shapes.Rectangle { Fill = tint },
            },
        };
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
