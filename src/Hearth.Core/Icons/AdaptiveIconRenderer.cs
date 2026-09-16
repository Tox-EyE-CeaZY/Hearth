using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Hearth.Core.Icons;

/// <summary>How one icon's artwork is placed into its tile.</summary>
public enum IconFit
{
    /// <summary>Decide from the artwork: fill for solid squares and plates, fit otherwise.</summary>
    Auto,

    /// <summary>Scale the artwork to cover the shape; whatever falls outside is cropped.</summary>
    Fill,

    /// <summary>Like Fill, but a further 25% in — for icons with a margin or ring of their own.</summary>
    FillZoomed,

    /// <summary>Shrink the artwork into the safe zone on a generated background.</summary>
    Fit,
}

public sealed record IconRenderOptions
{
    /// <summary>Per-icon placement; Auto unless the user chose otherwise.</summary>
    public IconFit Fit { get; init; } = IconFit.Auto;

    /// <summary>Silhouette every tile is masked into.</summary>
    public IconShapeKind Shape { get; init; } = IconShapeKind.Squircle;

    /// <summary>Tile edge length in device-independent pixels.</summary>
    public double Size { get; init; } = 64;

    /// <summary>Display scale, so a 200% monitor gets real pixels not an upscale.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>
    /// Bake the drop shadow into the bitmap. Costs one blur at cache time and
    /// saves a live DropShadowEffect per tile — which is the difference between
    /// a grid that scrolls at 120fps and one that stutters.
    /// </summary>
    public bool BakeShadow { get; init; } = true;

    /// <summary>
    /// Generate a background even for icons that already fill their square.
    /// Costs a little artwork size, buys a perfectly uniform grid.
    /// </summary>
    public bool ForceBackground { get; init; }

    /// <summary>Tunes generated backgrounds toward the surrounding UI.</summary>
    public bool DarkTheme { get; init; } = true;
}

/// <summary>
/// Composes a finished tile from raw icon artwork.
///
/// This is the heart of the look. Windows icons are not a design system — they
/// disagree on aspect ratio, padding, visual weight and whether they even have
/// a background. Android's icons look coherent because the platform forces
/// them into one silhouette with one safe zone, so that is what this does.
/// </summary>
public static class AdaptiveIconRenderer
{
    /// <summary>Shadow padding as a fraction of tile size, per side.</summary>
    private const double ShadowPadRatio = 0.10;

    /// <summary>
    /// Renders on the calling thread. DrawingVisual has thread affinity, so
    /// call this on whichever thread owns the work and rely on the returned
    /// bitmap being frozen before it crosses to the UI thread.
    /// </summary>
    public static BitmapSource Render(RawIcon icon, IconAnalysis analysis, IconRenderOptions options)
    {
        var size = options.Size;
        var pad = options.BakeShadow ? size * ShadowPadRatio : 0;
        var canvas = size + pad * 2;

        var artwork = CropToContent(icon, analysis);

        var visual = new DrawingVisual();

        // Small sources (32px Steam icons) get scaled up into the tile; the
        // default linear filter makes them visibly soft.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        using (var dc = visual.RenderOpen())
        {
            var shape = IconShape.Get(options.Shape, size);

            // Everything is drawn inside a translated, clipped group so the
            // shape mask applies to background and artwork in one pass.
            dc.PushTransform(new TranslateTransform(pad, pad));
            dc.PushClip(shape);

            var fillZoom = ResolveFill(analysis, options);

            if (fillZoom is { } zoom)
            {
                // Cover the mask and let it crop. For plate icons the zoom is
                // what makes the icon's own backplate reach past the mask, so
                // the outline the user sees is the chosen shape, not the icon's
                // shape sitting inside it.
                DrawCover(dc, artwork, size, zoom);
            }
            else
            {
                dc.DrawRectangle(BuildBackground(analysis, options), null, new Rect(0, 0, size, size));
                DrawInSafeZone(dc, artwork, size);
            }

            dc.Pop(); // clip
            dc.Pop(); // transform
        }

        if (options.BakeShadow)
        {
            visual.Effect = new DropShadowEffect
            {
                BlurRadius = size * 0.13,
                ShadowDepth = size * 0.035,
                Direction = 270,
                Opacity = options.DarkTheme ? 0.55 : 0.30,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Quality,
            };
        }

        var pixels = (int)Math.Ceiling(canvas * options.Scale);
        var target = new RenderTargetBitmap(pixels, pixels, 96 * options.Scale, 96 * options.Scale, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// Null means "fit on a generated background"; a number means "cover the
    /// mask at this zoom".
    /// </summary>
    private static double? ResolveFill(IconAnalysis analysis, IconRenderOptions options)
    {
        switch (options.Fit)
        {
            case IconFit.Fit:
                return null;
            case IconFit.Fill:
                return 1.0;
            case IconFit.FillZoomed:
                return 1.25;
        }

        // Automatic.
        if (options.ForceBackground) return null;
        if (analysis.IsFullBleed) return 1.0;
        return PlateZoom(analysis, options.Shape);
    }

    /// <summary>Scales the artwork to cover the whole tile, centred, then zooms.</summary>
    private static void DrawCover(DrawingContext dc, BitmapSource artwork, double size, double zoom)
    {
        var scale = Math.Max(size / artwork.PixelWidth, size / artwork.PixelHeight) * zoom;
        var width = artwork.PixelWidth * scale;
        var height = artwork.PixelHeight * scale;
        dc.DrawImage(artwork, new Rect((size - width) / 2, (size - height) / 2, width, height));
    }

    /// <summary>
    /// Zoom that makes a plate icon's backplate cover the mask, or null when
    /// the icon has no plate or would need so much zoom that its logo would be
    /// cut off — those fall back to a generated background.
    ///
    /// Worked per corner: the mask reaches some way toward each corner (a
    /// squircle about 85% of the half-diagonal, a circle 71%), the plate
    /// reaches some way (a circle 71%), and the plate must be scaled until it
    /// reaches at least as far as the mask on every corner.
    /// </summary>
    private static double? PlateZoom(IconAnalysis analysis, IconShapeKind shape)
    {
        const double MaxZoom = 1.45;
        const double Margin = 1.03; // hides the plate's antialiased rim

        if (!analysis.HasPlate || analysis.PlateReach.Length != 4) return null;

        var mask = MaskReach(shape);
        var zoom = 1.0;
        for (var corner = 0; corner < 4; corner++)
        {
            var plate = Math.Max(0.01, analysis.PlateReach[corner]);
            zoom = Math.Max(zoom, mask[corner] / plate);
        }

        zoom *= Margin;
        return zoom <= MaxZoom ? zoom : null;
    }

    private static readonly Dictionary<IconShapeKind, double[]> MaskReachCache = [];
    private static readonly object MaskReachLock = new();

    /// <summary>
    /// How far each mask shape reaches toward its corners (top-left, top-right,
    /// bottom-right, bottom-left), measured from the geometry itself so it
    /// stays right if a shape is ever changed.
    /// </summary>
    private static double[] MaskReach(IconShapeKind shape)
    {
        lock (MaskReachLock)
        {
            if (MaskReachCache.TryGetValue(shape, out var cached)) return cached;

            const double size = 200;
            var geometry = IconShape.Get(shape, size);
            var centre = size / 2;
            var directions = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
            var reach = new double[4];

            for (var i = 0; i < 4; i++)
            {
                var (dx, dy) = directions[i];
                var t = 1.0;
                while (t > 0 && !geometry.FillContains(new Point(centre + dx * centre * t, centre + dy * centre * t)))
                    t -= 0.005;
                reach[i] = t;
            }

            MaskReachCache[shape] = reach;
            return reach;
        }
    }

    /// <summary>
    /// Draws the artwork centred inside the safe zone, preserving aspect. The
    /// trim-then-refit is what rescues icons that ship with wildly different
    /// amounts of built-in padding.
    /// </summary>
    private static void DrawInSafeZone(DrawingContext dc, BitmapSource artwork, double size)
    {
        var safe = size * IconShape.SafeZoneScale;
        var aspect = (double)artwork.PixelWidth / artwork.PixelHeight;

        double w, h;
        if (aspect >= 1) { w = safe; h = safe / aspect; }
        else { h = safe; w = safe * aspect; }

        var x = (size - w) / 2;
        var y = (size - h) / 2;
        dc.DrawImage(artwork, new Rect(x, y, w, h));
    }

    private static BitmapSource CropToContent(RawIcon icon, IconAnalysis analysis)
    {
        var source = icon.ToBitmapSource();
        var bounds = analysis.ContentBounds;

        if (bounds.Width <= 0 || bounds.Height <= 0) return source;
        if (bounds.Width == icon.Width && bounds.Height == icon.Height) return source;

        // Guard against bounds that a resampled source would put out of range.
        if (bounds.X + bounds.Width > source.PixelWidth || bounds.Y + bounds.Height > source.PixelHeight)
            return source;

        var cropped = new CroppedBitmap(source, bounds);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>
    /// Derives a tonal background from the artwork's dominant colour.
    ///
    /// Deliberately not the average colour of the icon — averaging produces
    /// mud. Instead the dominant hue is kept and pushed to a fixed tonal step
    /// (the way Material You builds a palette), which is what makes a screen
    /// full of generated backgrounds look chosen rather than computed.
    /// </summary>
    private static Brush BuildBackground(IconAnalysis analysis, IconRenderOptions options)
    {
        var (h, s, _) = IconAnalysis.ToHsl(
            analysis.DominantColor.R, analysis.DominantColor.G, analysis.DominantColor.B);

        var artworkIsLight = analysis.ForegroundLuminance > 0.40;

        double targetL, targetS;
        if (analysis.IsMonochrome)
        {
            // No hue worth preserving: a neutral slate reads as intentional
            // where a desaturated tint would just look washed out.
            targetS = 0.06;
            targetL = artworkIsLight ? 0.18 : 0.90;
            h = options.DarkTheme ? 220 : 210;
        }
        else
        {
            targetS = artworkIsLight ? Math.Clamp(s, 0.35, 0.80) : Math.Clamp(s, 0.25, 0.62);
            targetL = artworkIsLight ? 0.22 : 0.88;
        }

        var baseColor = EnsureContrast(h, targetS, targetL, analysis.ForegroundLuminance, artworkIsLight);

        // A barely-there vertical gradient. Enough to stop 60 flat tiles
        // looking like printed stickers; not enough to notice as a gradient.
        var (bh, bs, bl) = IconAnalysis.ToHsl(baseColor.R, baseColor.G, baseColor.B);
        var top = IconAnalysis.FromHsl(bh, bs, Math.Clamp(bl + 0.045, 0, 1));
        var bottom = IconAnalysis.FromHsl(bh, bs, Math.Clamp(bl - 0.035, 0, 1));

        var brush = new LinearGradientBrush(top, bottom, new Point(0.5, 0), new Point(0.5, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Walks the background lightness away from the artwork until they are
    /// clearly separated. 2.0 is well below text contrast requirements on
    /// purpose: icon artwork only needs to sit on its background, not be read.
    /// </summary>
    private static Color EnsureContrast(double h, double s, double l, double foregroundLuminance, bool artworkIsLight)
    {
        const double MinimumRatio = 2.0;
        var step = artworkIsLight ? -0.04 : 0.04;

        for (var i = 0; i < 12; i++)
        {
            var candidate = IconAnalysis.FromHsl(h, s, l);
            var ratio = IconAnalysis.ContrastRatio(
                IconAnalysis.RelativeLuminance(candidate), foregroundLuminance);

            if (ratio >= MinimumRatio) return candidate;

            l = Math.Clamp(l + step, 0.04, 0.96);
        }

        return IconAnalysis.FromHsl(h, s, l);
    }
}
