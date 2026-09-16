using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hearth.App.Widgets;

/// <summary>
/// Shared look for card-style widgets, so they read as one family: the same
/// translucent card, the same type scale, the same accent.
///
/// Everything is sized in physical pixels (the desktop surface is laid out in
/// them), so every measurement here is multiplied by the display scale.
/// </summary>
internal static class WidgetChrome
{
    public static readonly Brush CardFill = Frozen(new SolidColorBrush(Color.FromArgb(0xC4, 0x15, 0x15, 0x1A)));
    public static readonly Brush CardEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Primary = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)));
    public static readonly Brush Secondary = Frozen(new SolidColorBrush(Color.FromArgb(0xA8, 0xF4, 0xF4, 0xF6)));
    public static readonly Brush Faint = Frozen(new SolidColorBrush(Color.FromArgb(0x4C, 0xF4, 0xF4, 0xF6)));
    public static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8)));
    public static readonly Brush OnAccent = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x2A)));
    public static readonly Brush Hover = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public static Border Card(WidgetContext context, UIElement child) => new()
    {
        Background = CardFill,
        BorderBrush = CardEdge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(22 * context.Scale),
        Padding = new Thickness(16 * context.Scale),
        Child = child,
    };

    public static TextBlock Text(WidgetContext context, string text, double size,
        Brush? brush = null, FontWeight? weight = null, bool display = false) => new()
    {
        Text = text,
        FontSize = size * context.Scale,
        FontFamily = display ? Display : Body,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Primary,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Glyph(WidgetContext context, string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Glyphs,
        FontSize = size * context.Scale,
        Foreground = brush ?? Primary,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// A round, borderless icon button.
///
/// It marks the press as handled, which is what stops the desktop treating a
/// click on it as the start of dragging the whole widget.
/// </summary>
internal sealed class GlyphButton : Border
{
    private readonly TextBlock _glyph;
    private readonly Action _onClick;
    private bool _pressed;

    public GlyphButton(WidgetContext context, string glyph, double size, Action onClick, bool filled = false)
    {
        _onClick = onClick;
        Width = size * context.Scale;
        Height = size * context.Scale;
        CornerRadius = new CornerRadius(size * context.Scale / 2);
        Background = filled ? WidgetChrome.Primary : Brushes.Transparent;
        Cursor = Cursors.Hand;

        _glyph = WidgetChrome.Glyph(context, glyph, size * 0.42, filled ? WidgetChrome.OnAccent : WidgetChrome.Primary);
        Child = _glyph;

        var idle = Background;
        MouseEnter += (_, _) => { if (!filled) Background = WidgetChrome.Hover; else Opacity = 0.9; };
        MouseLeave += (_, _) => { Background = idle; Opacity = 1; _pressed = false; };
    }

    public string GlyphText
    {
        get => _glyph.Text;
        set => _glyph.Text = value;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed && IsEnabled) _onClick();
        _pressed = false;
        e.Handled = true;
    }
}

/// <summary>A thin rounded progress bar. Value is 0..1.</summary>
internal sealed class BarView : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(BarView),
        new FrameworkPropertyMetadata(WidgetChrome.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        dc.DrawRoundedRectangle(WidgetChrome.Track, null, new Rect(0, 0, width, height), radius, radius);

        var filled = width * Math.Clamp(Value, 0, 1);
        if (filled > 0)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(height, filled), height), radius, radius);
    }
}
