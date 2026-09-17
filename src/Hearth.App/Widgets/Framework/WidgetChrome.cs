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
    /// <summary>One theme's colours for widgets.</summary>
    private sealed record Palette(Brush CardFill, Brush CardEdge, Brush Primary, Brush Secondary, Brush Faint,
        Brush Track, Brush Accent, Brush OnAccent, Brush Hover);

    private static Brush B(byte a, byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    private static readonly Palette Dark = new(
        CardFill: B(0xC4, 0x15, 0x15, 0x1A),
        CardEdge: B(0x24, 0xFF, 0xFF, 0xFF),
        Primary: B(0xFF, 0xF4, 0xF4, 0xF6),
        Secondary: B(0xA8, 0xF4, 0xF4, 0xF6),
        Faint: B(0x4C, 0xF4, 0xF4, 0xF6),
        Track: B(0x2E, 0xFF, 0xFF, 0xFF),
        Accent: B(0xFF, 0x8A, 0xB4, 0xF8),
        OnAccent: B(0xFF, 0x10, 0x18, 0x2A),
        Hover: B(0x26, 0xFF, 0xFF, 0xFF));

    private static readonly Palette Light = new(
        CardFill: B(0xE0, 0xFF, 0xFF, 0xFF),
        CardEdge: B(0x1A, 0x00, 0x00, 0x00),
        Primary: B(0xFF, 0x1B, 0x1B, 0x1F),
        Secondary: B(0xA8, 0x1B, 0x1B, 0x1F),
        Faint: B(0x5C, 0x1B, 0x1B, 0x1F),
        Track: B(0x1F, 0x00, 0x00, 0x00),
        Accent: B(0xFF, 0x1A, 0x6F, 0xD8),
        OnAccent: B(0xFF, 0xFF, 0xFF, 0xFF),
        Hover: B(0x14, 0x00, 0x00, 0x00));

    /// <summary>
    /// The palette for the widget being built. Widgets pick their colours up
    /// from these properties as they build, so a theme only has to be in
    /// scope while a view (or a later rebuild of part of it) is being made.
    /// Outside a scope the desktop's dark palette applies.
    /// </summary>
    [ThreadStatic] private static Palette? _current;

    private static Palette Current => _current ?? Dark;

    /// <summary>Makes <paramref name="context"/>'s theme current until disposed.</summary>
    public static IDisposable Scope(WidgetContext context)
    {
        var previous = _current;
        _current = context.DarkTheme ? Dark : Light;
        return new Restore(previous);
    }

    private sealed class Restore(Palette? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    public static Brush CardFill => Current.CardFill;
    public static Brush CardEdge => Current.CardEdge;
    public static Brush Primary => Current.Primary;
    public static Brush Secondary => Current.Secondary;
    public static Brush Faint => Current.Faint;
    public static Brush Track => Current.Track;
    public static Brush Accent => Current.Accent;
    public static Brush OnAccent => Current.OnAccent;
    public static Brush Hover => Current.Hover;

    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>
    /// The widget's card. A bare widget (the Start menu's Widgets board, which
    /// draws its own card) gets only the padding.
    /// </summary>
    public static Border Card(WidgetContext context, UIElement child) => new()
    {
        Background = context.Bare ? Brushes.Transparent : CardFill,
        BorderBrush = context.Bare ? Brushes.Transparent : CardEdge,
        BorderThickness = new Thickness(context.Bare ? 0 : 1),
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
        var hover = WidgetChrome.Hover;
        MouseEnter += (_, _) => { if (!filled) Background = hover; else Opacity = 0.9; };
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
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Captured when built, so the track matches the theme the bar was made in.
    private readonly Brush _track = WidgetChrome.Track;

    public BarView() => Fill = WidgetChrome.Accent;

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
        dc.DrawRoundedRectangle(_track, null, new Rect(0, 0, width, height), radius, radius);

        var filled = width * Math.Clamp(Value, 0, 1);
        if (filled > 0 && Fill is not null)
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(height, filled), height), radius, radius);
    }
}
