using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hearth.App.Controls;

/// <summary>
/// The on-desktop container for a widget: the widget's own view, plus the
/// affordances for moving and resizing it.
///
/// The whole footprint is hit-testable (a transparent background), so a widget
/// can be grabbed anywhere its content does not handle the click itself —
/// buttons and text boxes inside a widget keep working as normal. On hover an
/// outline and a resize grip appear; the outline is dashed when the widget is
/// unlocked, which is the only visual difference between the two modes.
/// </summary>
public sealed class WidgetFrame : Grid
{
    private readonly Rectangle _outline;
    private bool _isResizing;

    public string PlacementId { get; }
    public FrameworkElement View { get; }
    public FrameworkElement Grip { get; }
    public bool IsUnlocked { get; }

    public WidgetFrame(string placementId, FrameworkElement view, double scale, bool unlocked)
    {
        PlacementId = placementId;
        View = view;
        IsUnlocked = unlocked;

        Background = Brushes.Transparent;
        ClipToBounds = false;

        Children.Add(view);

        _outline = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5 * scale,
            StrokeDashArray = unlocked ? new DoubleCollection { 4, 3 } : null,
            RadiusX = 18 * scale,
            RadiusY = 18 * scale,
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
            Margin = new Thickness(-3 * scale),
        };
        Children.Add(_outline);

        Grip = new Border
        {
            Width = 20 * scale,
            Height = 20 * scale,
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xF2, 0xF2, 0xF4)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -6 * scale, -6 * scale),
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Hidden,
            ToolTip = unlocked ? "Resize freely" : "Resize (snaps to the grid)",
            Child = new Path
            {
                Data = Geometry.Parse("M 9,2 L 2,9 M 9,5.5 L 5.5,9"),
                Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x20, 0x20, 0x24)),
                StrokeThickness = 1.3,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(5 * scale),
            },
        };
        Children.Add(Grip);

        MouseEnter += (_, _) => ShowChrome(true);
        MouseLeave += (_, _) => ShowChrome(_isResizing);
    }

    public bool IsResizing
    {
        get => _isResizing;
        set
        {
            _isResizing = value;
            ShowChrome(value || IsMouseOver);
        }
    }

    public bool IsOverGrip(DependencyObject? hit)
    {
        for (var node = hit; node is not null && !ReferenceEquals(node, this); node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, Grip)) return true;
        }
        return false;
    }

    private void ShowChrome(bool visible)
    {
        var state = visible ? Visibility.Visible : Visibility.Hidden;
        _outline.Visibility = state;
        Grip.Visibility = state;
    }
}
