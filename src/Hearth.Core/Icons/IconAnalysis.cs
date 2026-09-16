using System.Windows;
using System.Windows.Media;

namespace Hearth.Core.Icons;

/// <summary>What we learned about an icon's artwork by looking at its pixels.</summary>
public sealed class IconAnalysis
{
    /// <summary>Tight bounds of the non-transparent artwork, in pixels.</summary>
    public required Int32Rect ContentBounds { get; init; }

    /// <summary>How much of <see cref="ContentBounds"/> is actually opaque.</summary>
    public required double ContentFill { get; init; }

    /// <summary>Shorter side over longer side of the content bounds.</summary>
    public required double AspectRatio { get; init; }

    /// <summary>How much of the whole canvas the content bounds occupy.</summary>
    public required double CanvasFill { get; init; }

    /// <summary>Most characteristic colour of the artwork.</summary>
    public required Color DominantColor { get; init; }

    /// <summary>WCAG relative luminance of the artwork, weighted by opacity.</summary>
    public required double ForegroundLuminance { get; init; }

    /// <summary>True when the artwork has essentially no colour of its own.</summary>
    public required bool IsMonochrome { get; init; }

    /// <summary>
    /// The icon already fills a solid square — a tile in its own right. These
    /// get masked to the chosen shape and nothing more; generating a background
    /// behind an already-opaque icon would just shrink it for no reason.
    /// </summary>
    /// <remarks>
    /// Judged on the trimmed content alone. It used to also require the square
    /// to fill most of the canvas, which sent small square artwork (Steam's
    /// 32px icons) to a dot in the middle of a generated background; cropping
    /// it to the mask looks far better.
    /// </remarks>
    public bool IsFullBleed => ContentFill > 0.90 && AspectRatio > 0.85;

    /// <summary>
    /// The artwork carries its own solid backplate — a circle, rounded square
    /// or squircle — with the logo on top. These are zoomed until the plate
    /// covers the chosen mask instead of being shrunk onto a generated
    /// background, which would leave a shape inside a shape.
    /// </summary>
    public bool HasPlate { get; init; }

    /// <summary>
    /// For a plate: how far, as a fraction of the half-diagonal, the solid
    /// plate reaches from the centre toward each corner (top-left, top-right,
    /// bottom-right, bottom-left). A circle reaches about 0.71; a square 1.
    /// </summary>
    public double[] PlateReach { get; init; } = [];

    public static IconAnalysis Analyse(RawIcon icon)
    {
        var px = icon.Pixels;
        int w = icon.Width, h = icon.Height;

        const byte OpaqueThreshold = 24;

        // Windows pads small icons (Steam and other internet shortcuts ship
        // only 32px) out to the requested 256px and draws a thin square frame
        // around the edge. Left in, that frame makes the "content" span the
        // whole canvas: nothing gets trimmed and the real icon stays a dot in
        // the middle of its tile. So find the frame and analyse only inside it.
        var inset = FrameInset(icon, OpaqueThreshold);
        int left = inset, top = inset, right = w - inset, bottom = h - inset;

        int minX = w, minY = h, maxX = -1, maxY = -1;
        long opaqueCount = 0;

        // Weighted sums for luminance, and a coarse colour histogram. 5 bits per
        // channel (32 levels) is the sweet spot: fine enough to keep a brand
        // colour distinct, coarse enough that antialiased edges land in the same
        // bucket as the solid body they came from.
        var histogram = new Dictionary<int, (long Weight, long B, long G, long R, long Count)>(512);
        double luminanceSum = 0;
        double luminanceWeight = 0;
        double saturationSum = 0;

        for (var y = top; y < bottom; y++)
        {
            var row = y * icon.Stride;
            for (var x = left; x < right; x++)
            {
                var i = row + x * 4;
                var a = px[i + 3];
                if (a < OpaqueThreshold) continue;

                opaqueCount++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                var b = px[i + 0];
                var g = px[i + 1];
                var r = px[i + 2];

                var weight = a / 255.0;
                luminanceSum += RelativeLuminance(r, g, b) * weight;
                luminanceWeight += weight;

                var (_, sat, light) = ToHsl(r, g, b);
                saturationSum += sat * weight;

                // Near-white, near-black and unsaturated pixels are almost
                // always padding, outlines or shadow rather than the identity
                // of the icon, so they do not get a vote on dominant colour.
                if (sat < 0.20 || light < 0.10 || light > 0.93) continue;

                var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                var vote = (long)(sat * 255 * weight);
                if (histogram.TryGetValue(key, out var bucket))
                {
                    histogram[key] = (bucket.Weight + vote, bucket.B + b, bucket.G + g, bucket.R + r, bucket.Count + 1);
                }
                else
                {
                    histogram[key] = (vote, b, g, r, 1);
                }
            }
        }

        if (opaqueCount == 0)
        {
            // Fully transparent extraction — treat as an empty neutral tile
            // rather than dividing by zero downstream.
            return new IconAnalysis
            {
                ContentBounds = new Int32Rect(0, 0, w, h),
                ContentFill = 0,
                AspectRatio = 1,
                CanvasFill = 0,
                DominantColor = Color.FromRgb(0x60, 0x66, 0x70),
                ForegroundLuminance = 0.5,
                IsMonochrome = true,
            };
        }

        var boundsW = maxX - minX + 1;
        var boundsH = maxY - minY + 1;
        var bounds = new Int32Rect(minX, minY, boundsW, boundsH);
        var (hasPlate, plateReach) = DetectPlate(icon, bounds);

        var meanSaturation = saturationSum / luminanceWeight;
        var dominant = PickDominant(histogram, luminanceSum / luminanceWeight);

        return new IconAnalysis
        {
            ContentBounds = new Int32Rect(minX, minY, boundsW, boundsH),
            ContentFill = (double)opaqueCount / (boundsW * boundsH),
            AspectRatio = (double)Math.Min(boundsW, boundsH) / Math.Max(boundsW, boundsH),
            CanvasFill = (double)(boundsW * boundsH) / (w * h),
            DominantColor = dominant,
            ForegroundLuminance = luminanceSum / luminanceWeight,
            IsMonochrome = meanSaturation < 0.12,
            HasPlate = hasPlate,
            PlateReach = plateReach,
        };
    }

    /// <summary>
    /// Looks for a solid backplate filling the content bounds: solid in the
    /// middle, reaching all four edges, and reaching well out along all four
    /// diagonals. A transparent glyph fails at least one of those; a plate —
    /// even a circle, which reaches only 71% of the way to its corners — passes.
    /// </summary>
    private static (bool HasPlate, double[] Reach) DetectPlate(RawIcon icon, Int32Rect bounds)
    {
        const byte Solid = 200;
        if (bounds.Width < 16 || bounds.Height < 16) return (false, []);
        if ((double)Math.Min(bounds.Width, bounds.Height) / Math.Max(bounds.Width, bounds.Height) < 0.9) return (false, []);

        var cx = bounds.X + bounds.Width / 2.0;
        var cy = bounds.Y + bounds.Height / 2.0;
        var hw = bounds.Width / 2.0;
        var hh = bounds.Height / 2.0;

        bool IsSolid(double x, double y)
        {
            var ix = (int)x;
            var iy = (int)y;
            if (ix < 0 || iy < 0 || ix >= icon.Width || iy >= icon.Height) return false;
            return icon.Pixels[(iy * icon.Width + ix) * 4 + 3] >= Solid;
        }

        // How far along a ray from the centre the solid region extends (0..1).
        double Reach(double dx, double dy)
        {
            const int steps = 100;
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (double)steps;
                // Stop just short of the very edge pixel, which antialiasing softens.
                if (!IsSolid(cx + dx * hw * t * 0.985, cy + dy * hh * t * 0.985)) return t;
            }
            return 1.0;
        }

        // 1. Solid through the middle: a coarse grid inside 60% of the radius.
        int samples = 0, solid = 0;
        for (var gy = -6; gy <= 6; gy++)
        {
            for (var gx = -6; gx <= 6; gx++)
            {
                var fx = gx / 10.0;
                var fy = gy / 10.0;
                if (fx * fx + fy * fy > 0.36) continue;
                samples++;
                if (IsSolid(cx + fx * hw, cy + fy * hh)) solid++;
            }
        }
        // Logos drawn on the plate can be semi-transparent cut-outs, so allow some misses.
        if (solid < samples * 0.85) return (false, []);

        // 2. Reaches all four edges.
        if (Reach(1, 0) < 0.93 || Reach(-1, 0) < 0.93 || Reach(0, 1) < 0.93 || Reach(0, -1) < 0.93)
            return (false, []);

        // 3. Reaches out along the diagonals at least as far as a circle does.
        var corners = new[] { Reach(-1, -1), Reach(1, -1), Reach(1, 1), Reach(-1, 1) };
        if (corners.Any(c => c < 0.62)) return (false, []);

        return (true, corners);
    }

    /// <summary>
    /// Width of a decorative frame around the canvas, or 0 if there is none.
    ///
    /// A frame is a ring of near-complete lines on all four sides within the
    /// outer few pixels, with almost nothing just inside it. That second
    /// condition is what separates it from full-bleed artwork, which is solid
    /// right through.
    /// </summary>
    private static int FrameInset(RawIcon icon, byte threshold)
    {
        const int maxFrame = 8;
        const int probe = 4;
        int w = icon.Width, h = icon.Height;
        if (w < 64 || h < 64) return 0;

        var rows = new int[h];
        var columns = new int[w];
        var px = icon.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = y * icon.Stride;
            for (var x = 0; x < w; x++)
            {
                if (px[row + x * 4 + 3] < threshold) continue;
                rows[y]++;
                columns[x]++;
            }
        }

        bool Line(int i) =>
            rows[i] >= w * 0.6 && rows[h - 1 - i] >= w * 0.6 &&
            columns[i] >= h * 0.6 && columns[w - 1 - i] >= h * 0.6;

        var lastLine = -1;
        for (var i = 0; i < maxFrame; i++)
        {
            if (Line(i)) lastLine = i;
        }
        if (lastLine < 0) return 0;

        // Just inside the frame, a real frame leaves the rows nearly empty
        // (only its own side edges cross them).
        for (var i = lastLine + 1; i <= lastLine + probe; i++)
        {
            if (rows[i] > w * 0.25 || rows[h - 1 - i] > w * 0.25) return 0;
            if (columns[i] > h * 0.25 || columns[w - 1 - i] > h * 0.25) return 0;
        }

        return lastLine + 1;
    }

    private static Color PickDominant(
        Dictionary<int, (long Weight, long B, long G, long R, long Count)> histogram,
        double fallbackLuminance)
    {
        if (histogram.Count == 0)
        {
            // A greyscale icon. Pick a neutral that will still read as a
            // deliberate choice rather than a muddy average.
            return fallbackLuminance > 0.5
                ? Color.FromRgb(0x3A, 0x3F, 0x4A)
                : Color.FromRgb(0xE8, 0xEA, 0xEE);
        }

        var best = histogram.First();
        foreach (var entry in histogram)
        {
            if (entry.Value.Weight > best.Value.Weight) best = entry;
        }

        // Average the true colours inside the winning bucket so we return the
        // artwork's actual colour, not the bucket's quantised centre.
        var n = best.Value.Count;
        return Color.FromRgb(
            (byte)(best.Value.R / n),
            (byte)(best.Value.G / n),
            (byte)(best.Value.B / n));
    }

    // ---- Colour maths --------------------------------------------------

    /// <summary>WCAG 2.1 relative luminance, on linearised channels.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Linearise(byte channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linearise(r) + 0.7152 * Linearise(g) + 0.0722 * Linearise(b);
    }

    public static double RelativeLuminance(Color c) => RelativeLuminance(c.R, c.G, c.B);

    public static double ContrastRatio(double l1, double l2)
    {
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static (double H, double S, double L) ToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var delta = max - min;

        if (delta < 1e-6) return (0, 0, l);

        var s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

        double h;
        if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;

        return (h * 60.0, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s < 1e-6)
        {
            var grey = (byte)Math.Round(l * 255);
            return Color.FromRgb(grey, grey, grey);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToChannel(p, q, h / 360.0 + 1.0 / 3.0) * 255),
            (byte)Math.Round(HueToChannel(p, q, h / 360.0) * 255),
            (byte)Math.Round(HueToChannel(p, q, h / 360.0 - 1.0 / 3.0) * 255));

        static double HueToChannel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }
    }
}
