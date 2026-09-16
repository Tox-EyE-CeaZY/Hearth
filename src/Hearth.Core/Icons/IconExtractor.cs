using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearth.Core.Interop;

namespace Hearth.Core.Icons;

/// <summary>
/// A decoded icon in straight (non-premultiplied) BGRA.
///
/// Straight alpha is the right working format here: every analysis step
/// downstream wants the artwork's true colours, and premultiplied pixels drag
/// their own alpha into the channel values, which skews dominant-colour picking
/// toward black on anything with soft edges.
/// </summary>
public sealed class RawIcon
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Straight BGRA, top-down, stride = Width * 4.</summary>
    public required byte[] Pixels { get; init; }

    public int Stride => Width * 4;

    /// <summary>Re-premultiplies on the way out, because WPF wants Pbgra32.</summary>
    public BitmapSource ToBitmapSource()
    {
        var premultiplied = new byte[Pixels.Length];
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            var a = Pixels[i + 3];
            premultiplied[i + 0] = (byte)(Pixels[i + 0] * a / 255);
            premultiplied[i + 1] = (byte)(Pixels[i + 1] * a / 255);
            premultiplied[i + 2] = (byte)(Pixels[i + 2] * a / 255);
            premultiplied[i + 3] = a;
        }

        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, premultiplied, Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Pulls the largest available icon for a shell item.
///
/// IShellItemImageFactory is the only extraction API that reaches both classic
/// .ico resources and packaged apps' PNG assets, and it is the one that can
/// hand back genuine 256 px artwork instead of an upscaled 32 px one.
/// </summary>
public static class IconExtractor
{
    /// <summary>
    /// We always pull at this size regardless of display size. Caching is keyed
    /// on the item, not the size, so extracting once at full resolution and
    /// downscaling is both faster and sharper than re-extracting per size.
    /// </summary>
    public const int ExtractionSize = 256;

    public static RawIcon? Extract(string parsingName, int size = ExtractionSize)
    {
        object? raw = null;
        try
        {
            ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ShellNative.IID_IShellItem, out raw);
            if (raw is not IShellItemImageFactory factory) return null;

            // BiggerSizeOk: take a larger source over a blurry upscale.
            // IconOnly: never let a document thumbnail stand in for the icon.
            var flags = SIIGBF.BiggerSizeOk | SIIGBF.IconOnly;
            var hr = factory.GetImage(new Win32.SIZE(size, size), flags, out var hbitmap);

            if (hr != 0 || hbitmap == IntPtr.Zero)
            {
                // Some packaged apps refuse IconOnly; the plain request works.
                hr = factory.GetImage(new Win32.SIZE(size, size), SIIGBF.BiggerSizeOk, out hbitmap);
                if (hr != 0 || hbitmap == IntPtr.Zero) return null;
            }

            try { return FromHBitmap(hbitmap); }
            finally { Win32.DeleteObject(hbitmap); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException)
        {
            Debug.WriteLine($"[Hearth] icon extraction failed for '{parsingName}': {ex.Message}");
            return null;
        }
        finally
        {
            if (raw is not null && Marshal.IsComObject(raw)) Marshal.FinalReleaseComObject(raw);
        }
    }

    private static RawIcon? FromHBitmap(IntPtr hbitmap)
    {
        if (Win32.GetObject(hbitmap, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0) return null;
        if (bm.bmWidth <= 0 || bm.bmHeight <= 0 || bm.bmBitsPixel != 32) return null;

        var width = bm.bmWidth;
        var height = bm.bmHeight;
        var pixels = new byte[width * height * 4];

        if (bm.bmBits != IntPtr.Zero)
        {
            // GetImage hands back a DIB section, so the bits are already mapped
            // and we can copy them straight out — no GetDIBits round trip, and
            // no risk of the alpha channel being dropped in translation.
            //
            // Row order is NOT fixed. The shell returns bottom-up DIBs for some
            // icons and top-down for others, and BITMAP.bmHeight is positive
            // either way; only the section's header says which. Copying a
            // bottom-up DIB straight through renders the icon upside down —
            // invisible on symmetric artwork, obvious on everything else.
            var bottomUp = IsBottomUp(hbitmap);
            var srcStride = bm.bmWidthBytes;
            var dstStride = width * 4;
            for (var y = 0; y < height; y++)
            {
                var srcRow = bottomUp ? height - 1 - y : y;
                Marshal.Copy(bm.bmBits + srcRow * srcStride, pixels, y * dstStride, dstStride);
            }
        }
        else if (!CopyViaGetDIBits(hbitmap, width, height, pixels))
        {
            return null;
        }

        NormaliseAlpha(pixels);
        Unpremultiply(pixels);

        return new RawIcon { Width = width, Height = height, Pixels = pixels };
    }

    private static bool IsBottomUp(IntPtr hbitmap)
    {
        var size = Marshal.SizeOf<Win32.DIBSECTION>();
        if (Win32.GetObjectDibSection(hbitmap, size, out var section) != size)
            return false; // not a DIB section; the GetDIBits path handles order itself

        // Positive biHeight = bottom-up, negative = top-down.
        return section.dsBmih.biHeight > 0;
    }

    private static bool CopyViaGetDIBits(IntPtr hbitmap, int width, int height, byte[] pixels)
    {
        var hdc = Win32.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            var info = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative: top-down, matching our buffer
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                },
            };
            return Win32.GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref info, Win32.DIB_RGB_COLORS) != 0;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>
    /// Old 32bpp icon resources sometimes carry an all-zero alpha channel,
    /// which is indistinguishable from "fully transparent" and would render as
    /// nothing at all. Treat a uniformly-zero channel as opaque.
    /// </summary>
    private static void NormaliseAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) return;
        }
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
    }

    private static void Unpremultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0 || a == 255) continue;
            pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
    }
}
