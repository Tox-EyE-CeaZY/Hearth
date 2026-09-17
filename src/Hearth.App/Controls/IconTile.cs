using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Hearth.Core.Icons;
using Hearth.Core.Notifications;
using Hearth.Core.Shell;

namespace Hearth.App.Controls;

/// <summary>
/// One icon and its label.
///
/// Drawn directly in OnRender rather than composed from a Grid + Image +
/// TextBlock. A home screen can easily carry 200 of these, and the nested-panel
/// version costs several visual-tree nodes and a measure/arrange pass each;
/// this is one element that draws two things. The shadow is already baked into
/// the icon bitmap, so rendering is a textured quad plus cached glyphs.
/// </summary>
public sealed class IconTile : FrameworkElement
{
    private FormattedText? _label;
    private double _labelPixelsPerDip;
    private bool _isHovered;

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(LauncherItem), typeof(IconTile),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemChanged));

    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.Register(
        nameof(IconSource), typeof(ImageSource), typeof(IconTile),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconTile),
        new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel), typeof(bool), typeof(IconTile),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(IconTile),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviewsProperty = DependencyProperty.Register(
        nameof(Previews), typeof(IReadOnlyList<ImageSource>), typeof(IconTile),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(IconShapeKind), typeof(IconTile),
        new FrameworkPropertyMetadata(IconShapeKind.Squircle, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDropTargetProperty = DependencyProperty.Register(
        nameof(IsDropTarget), typeof(bool), typeof(IconTile),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(Badge), typeof(IconTile),
        new FrameworkPropertyMetadata(Badge.None, FrameworkPropertyMetadataOptions.AffectsRender, OnBadgeChanged));

    /// <summary>Unread notifications for this app, drawn on the icon's corner.</summary>
    public Badge Badge
    {
        get => (Badge)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    private FormattedText? _badgeText;

    private static void OnBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((IconTile)d)._badgeText = null;

    /// <summary>Label colour; when unset, the "LabelBrush" resource (white, for the desktop).</summary>
    public Brush? LabelBrush { get; set; }

    /// <summary>Folder pad fill and edge; when unset, the desktop's frosted white.</summary>
    public Brush? FolderPadBrush { get; set; }
    public Pen? FolderPadEdge { get; set; }

    /// <summary>For folder tiles: icons of the first few items inside.</summary>
    public IReadOnlyList<ImageSource>? Previews
    {
        get => (IReadOnlyList<ImageSource>?)GetValue(PreviewsProperty);
        set => SetValue(PreviewsProperty, value);
    }

    /// <summary>Silhouette for the folder pad, matching the icon setting.</summary>
    public IconShapeKind Shape
    {
        get => (IconShapeKind)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    /// <summary>Something being dragged would land in this tile if dropped now.</summary>
    public bool IsDropTarget
    {
        get => (bool)GetValue(IsDropTargetProperty);
        set => SetValue(IsDropTargetProperty, value);
    }

    public bool IsFolder => Item?.Kind == LauncherItemKind.Group;

    public LauncherItem? Item
    {
        get => (LauncherItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Space reserved under the icon for up to two lines of label.</summary>
    private const double LabelHeight = 34;
    private const double LabelGap = 6;

    /// <summary>
    /// Extra room around the icon so the baked shadow is not clipped. Must
    /// match AdaptiveIconRenderer's padding ratio.
    /// </summary>
    private const double ShadowPadRatio = 0.10;

    public IconTile()
    {
        // The tile is drawn edge to edge; without this, gaps between glyphs and
        // the icon would not raise mouse events and hover would flicker.
        Focusable = true;
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tile = (IconTile)d;
        tile._label = null;
        tile.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var iconBox = IconSize * (1 + ShadowPadRatio * 2);
        var height = iconBox + (ShowLabel ? LabelGap + LabelHeight : 0);
        // Labels are routinely wider than the icon; the cell, not the glyph
        // run, decides the tile's footprint so the grid stays regular.
        var width = Math.Max(iconBox, IconSize * 1.6);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // RenderSize, not ActualWidth: this runs in the render pass, and
        // ActualWidth can still read zero here even though the element has been
        // arranged. Bailing out on that leaves WPF caching an empty drawing,
        // and nothing re-renders until something invalidates the visual.
        var width = RenderSize.Width;
        var height = RenderSize.Height;


        if (width <= 0 || height <= 0) return;

        // Hit-test backing. Without a filled rect the element only responds
        // where something was drawn, which makes hover feel broken.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        if (_isHovered || IsSelected)
        {
            var brushKey = IsSelected ? "TileSelectedBrush" : "TileHoverBrush";
            if (Application.Current.TryFindResource(brushKey) is Brush backplate)
            {
                var radius = Math.Min(width, height) * 0.16;
                dc.DrawRoundedRectangle(backplate, null, new Rect(0, 0, width, height), radius, radius);
            }
        }

        var iconBox = IconSize * (1 + ShadowPadRatio * 2);
        var boxX = (width - iconBox) / 2;

        if (IsDropTarget && Application.Current.TryFindResource("DropTargetBrush") is Brush ring)
        {
            var radius = iconBox * 0.3;
            dc.DrawRoundedRectangle(ring, null, new Rect(boxX, 0, iconBox, iconBox), radius, radius);
        }

        if (IsFolder)
        {
            DrawFolder(dc, boxX + IconSize * ShadowPadRatio, IconSize * ShadowPadRatio);
        }
        else if (IconSource is { } icon)
        {
            dc.DrawImage(icon, new Rect(boxX, 0, iconBox, iconBox));
        }

        if (Badge.IsVisible) DrawBadge(dc, boxX + IconSize * ShadowPadRatio, IconSize * ShadowPadRatio);

        if (!ShowLabel) return;

        var label = GetLabel(width);
        if (label is null) return;

        // Drawn at x = 0: the FormattedText is already centred within
        // MaxTextWidth (= the tile width) by TextAlignment.Center. Offsetting
        // by (width - label.Width) / 2 as well centres it twice and pushes every
        // label to the right of its icon.
        dc.DrawText(label, new Point(0, iconBox + LabelGap));
    }

    private static readonly Brush FolderFill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen FolderEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>
    /// A frosted pad in the user's icon shape with a 2x2 preview of the
    /// contents — the folder look both Android and iOS converged on, because it
    /// reads as "a group of these" at a glance.
    /// </summary>
    private void DrawFolder(DrawingContext dc, double x, double y)
    {
        var size = IconSize;
        var shape = IconShape.Get(Shape, size);

        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(FolderPadBrush ?? FolderFill, FolderPadEdge ?? FolderEdge, shape);

        var previews = Previews;
        if (previews is { Count: > 0 })
        {
            var margin = size * 0.15;
            var gap = size * 0.06;
            var cell = (size - margin * 2 - gap) / 2;

            // Preview bitmaps carry the same baked shadow padding as full
            // tiles, so each is drawn slightly larger than its cell.
            var pad = cell * ShadowPadRatio;

            for (var i = 0; i < Math.Min(4, previews.Count); i++)
            {
                var cx = margin + (i % 2) * (cell + gap);
                var cy = margin + (i / 2) * (cell + gap);
                dc.DrawImage(previews[i], new Rect(cx - pad, cy - pad, cell + pad * 2, cell + pad * 2));
            }
        }

        dc.Pop();
    }

    private static readonly Brush BadgeFill = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Pen BadgeEdge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), 1));

    /// <summary>
    /// A red count (or dot) straddling the icon's top-right corner, sized from
    /// the icon so it scales with the grid. Counts above 99 read "99+".
    /// </summary>
    private void DrawBadge(DrawingContext dc, double iconX, double iconY)
    {
        var badge = Badge;
        var height = Math.Max(10, IconSize * (badge.Count > 0 ? 0.34 : 0.22));
        var right = iconX + IconSize + height * 0.2;
        var top = iconY - height * 0.2;

        if (badge.Count <= 0)
        {
            var dot = new Rect(right - height, top, height, height);
            dc.DrawEllipse(BadgeFill, BadgeEdge, new Point(dot.X + height / 2, dot.Y + height / 2), height / 2, height / 2);
            return;
        }

        _badgeText ??= new FormattedText(
            badge.Count > 99 ? "99+" : badge.Count.ToString(CultureInfo.CurrentCulture),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            height * 0.62,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var width = Math.Max(height, _badgeText.Width + height * 0.6);
        var pill = new Rect(right - width, top, width, height);
        dc.DrawRoundedRectangle(BadgeFill, BadgeEdge, pill, height / 2, height / 2);
        dc.DrawText(_badgeText, new Point(pill.X + (width - _badgeText.Width) / 2, pill.Y + (height - _badgeText.Height) / 2));
    }

    private FormattedText? GetLabel(double maxWidth)
    {
        var text = Item?.DisplayName;
        if (string.IsNullOrEmpty(text)) return null;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Rebuilding a FormattedText is not cheap and OnRender runs on every
        // hover, so it is cached until something that affects it changes.
        if (_label is not null && Math.Abs(_labelPixelsPerDip - pixelsPerDip) < 0.001)
            return _label;

        var foreground = LabelBrush ?? Application.Current.TryFindResource("LabelBrush") as Brush ?? Brushes.White;

        _labelPixelsPerDip = pixelsPerDip;
        _label = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
            12,
            foreground,
            pixelsPerDip)
        {
            MaxLineCount = 2,
            MaxTextWidth = Math.Max(24, maxWidth),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        // Baked into the glyphs rather than applied as an effect: a live
        // DropShadowEffect on every label is the single most expensive thing
        // a grid like this can do.
        _label.SetForegroundBrush(foreground);
        return _label;
    }

    /// <summary>Drops the cached label, e.g. after a folder is renamed.</summary>
    public void InvalidateLabel()
    {
        _label = null;
        InvalidateVisual();
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        InvalidateVisual();
    }
}

