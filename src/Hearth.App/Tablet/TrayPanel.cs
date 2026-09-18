using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearth.App.Views.Start;

namespace Hearth.App.Tablet;

/// <summary>
/// The tray shade in Recent apps: every notification-area icon as a tile,
/// like the quick panel pulled down from the top of a phone. Tap a tile to
/// click the icon; press and hold (or right-click) for the app's own menu.
/// </summary>
internal sealed class TrayPanel : Border
{
    private const double TileWidth = 104;
    private const double HoldTime = 500;

    private readonly WrapPanel _tiles = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status;
    private readonly TranslateTransform _slide = new();

    /// <summary>An icon was chosen; true asks for its menu.</summary>
    public event Action<TrayIcon, bool>? Chosen;

    public TrayPanel()
    {
        Background = new SolidColorBrush(StartStyle.IsLight ? Color.FromRgb(0xF7, 0xF7, 0xF9) : Color.FromRgb(0x24, 0x24, 0x2A));
        BorderBrush = StartStyle.CardEdge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(18, 14, 18, 16);
        MaxWidth = 760;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = _slide;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35 };

        var title = StartStyle.Label("Running in the background", 15, weight: FontWeights.SemiBold);
        _status = StartStyle.Label("", 12.5, StartStyle.Muted);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 2, 0, 10);

        Child = new StackPanel
        {
            Children =
            {
                title,
                _status,
                new ScrollViewer
                {
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    PanningMode = PanningMode.VerticalOnly,
                    Content = _tiles,
                },
            },
        };
    }

    public void ShowLoading(IReadOnlyList<TrayIcon>? previous)
    {
        _status.Text = "Reading the tray...";
        if (previous is { Count: > 0 }) Fill(previous);
        else _tiles.Children.Clear();
        AnimateIn();
    }

    public void ShowIcons(IReadOnlyList<TrayIcon> icons)
    {
        _status.Text = icons.Count == 0
            ? "No apps have a tray icon right now."
            : "Tap to open. Press and hold, or right-click, for options.";
        Fill(icons);
    }

    public void ShowError() => _status.Text = "The tray could not be read.";

    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }

    private void Fill(IReadOnlyList<TrayIcon> icons)
    {
        _tiles.Children.Clear();
        foreach (var icon in icons) _tiles.Children.Add(Tile(icon));
    }

    private FrameworkElement Tile(TrayIcon icon)
    {
        FrameworkElement picture = icon.Image is { } image
            ? new Image { Source = image, Width = 32, Height = 32, Stretch = Stretch.Uniform }
            : StartStyle.GlyphText(StartStyle.Glyph(FallbackGlyph(icon.Title)), 26);
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);

        var label = StartStyle.Label(icon.Title, 12);
        label.TextAlignment = TextAlignment.Center;
        label.TextWrapping = TextWrapping.Wrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxHeight = 32;
        label.Margin = new Thickness(0, 8, 0, 0);

        var tile = new PressableBorder(radius: 10)
        {
            Width = TileWidth,
            Padding = new Thickness(6, 12, 6, 10),
            Margin = new Thickness(3),
            ToolTip = icon.Name,
            Child = new StackPanel { Children = { new Border { Height = 32, Child = picture }, label } },
        };

        // Press and hold for the menu, as on a phone; a plain tap clicks.
        DispatcherTimer? hold = null;
        var held = false;
        tile.PreviewMouseLeftButtonDown += (_, _) =>
        {
            held = false;
            hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldTime) };
            hold.Tick += (_, _) =>
            {
                hold.Stop();
                held = true;
                Chosen?.Invoke(icon, true);
            };
            hold.Start();
        };
        tile.PreviewMouseLeftButtonUp += (_, e) =>
        {
            hold?.Stop();
            if (!held) return;
            e.Handled = true; // the menu already opened; no click as well
        };
        tile.MouseLeave += (_, _) => hold?.Stop();
        tile.Clicked += _ =>
        {
            if (!held) Chosen?.Invoke(icon, false);
        };
        tile.RightClicked += _ => Chosen?.Invoke(icon, true);
        Stylus.SetIsPressAndHoldEnabled(tile, false);
        return tile;
    }

    /// <summary>Windows' own tray icons have no snapshot to borrow.</summary>
    private static int FallbackGlyph(string title)
    {
        if (title.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return 0xE702;
        if (title.Contains("Remove", StringComparison.OrdinalIgnoreCase) || title.Contains("Eject", StringComparison.OrdinalIgnoreCase)) return 0xE88E;
        return 0xE71D;
    }
}
