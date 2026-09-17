using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Controls;
using Hearth.Core.Diagnostics;
using Hearth.Core.Interop;

namespace Hearth.App.Widgets;

/// <summary>
/// A banner in the corner of the screen, with a chime, for widgets that need
/// attention now (a timer ran out, an alarm went off). It stays on top without
/// taking focus, so it never eats the keystrokes of whatever you're typing in.
/// Banners stack upwards; the chime loops while any is open, for a minute at most.
/// </summary>
internal sealed class WidgetAlert : Window
{
    private static readonly List<WidgetAlert> Open = [];
    private static readonly TimeSpan ChimeLimit = TimeSpan.FromMinutes(1);
    private static SoundPlayer? _chime;
    private static DispatcherTimer? _chimeLimit;

    private const double Gap = 12;

    private WidgetAlert(string glyph, string title, string message, IReadOnlyList<(string Label, Action? Action)> buttons)
    {
        Title = $"{title} — Hearth";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        FontFamily = WidgetChrome.Body;
        FontSize = 14;
        Foreground = DialogChrome.Brush("#FFF2F2F4");
        Resources = DialogChrome.CreateResources();

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = WidgetChrome.Glyphs,
            FontSize = 22,
            Foreground = DialogChrome.Brush("#FF8AB4F8"),
            Margin = new Thickness(0, 2, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = WidgetChrome.Display };
        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = DialogChrome.Brush("#B3F2F2F4"),
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, action) = buttons[i];
            var button = new Button { Content = label, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
            if (i == buttons.Count - 1) button.Style = (Style)Resources["Primary"];
            button.Click += (_, _) =>
            {
                Close();
                WidgetMenu.Run(action);
            };
            row.Children.Add(button);
        }

        var text = new StackPanel { Children = { heading, body } };
        var top = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        top.Children.Add(icon);
        top.Children.Add(text);

        Content = new Border
        {
            Background = DialogChrome.Brush("#F21C1C21"),
            BorderBrush = DialogChrome.Brush("#33FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 16, 16, 14),
            Child = new StackPanel { Children = { top, row } },
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE,
                new IntPtr(style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        };
        SizeChanged += (_, _) => Arrange();
        Closed += (_, _) =>
        {
            Open.Remove(this);
            Arrange();
            if (Open.Count == 0) StopChime();
        };
    }

    /// <summary>
    /// Shows a banner. Each button closes it, then runs its action (null to
    /// just close). The last button is drawn as the main one.
    /// </summary>
    public static void Show(string glyph, string title, string message, params (string Label, Action? Action)[] buttons)
    {
        if (buttons.Length == 0) buttons = [("Dismiss", null)];
        var alert = new WidgetAlert(glyph, title, message, buttons);
        Open.Add(alert);
        alert.Show();
        Arrange();
        StartChime();
        Log.Write($"alert: {title}");
    }

    /// <summary>Stacks open banners up from the bottom-right of the work area.</summary>
    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - Gap;
        foreach (var alert in Open)
        {
            var height = alert.ActualHeight > 0 ? alert.ActualHeight : 140;
            alert.Left = area.Right - alert.Width - Gap;
            alert.Top = bottom - height;
            bottom -= height + Gap;
        }
    }

    private static void StartChime()
    {
        try
        {
            if (_chime is null)
            {
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
                if (!File.Exists(file))
                {
                    SystemSounds.Exclamation.Play();
                    return;
                }
                _chime = new SoundPlayer(file);
                _chime.PlayLooping();
            }

            _chimeLimit ??= new DispatcherTimer { Interval = ChimeLimit };
            _chimeLimit.Tick -= OnChimeLimit;
            _chimeLimit.Tick += OnChimeLimit;
            _chimeLimit.Stop();
            _chimeLimit.Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Log.Write($"alert chime failed: {ex.Message}");
        }
    }

    private static void OnChimeLimit(object? sender, EventArgs e) => StopChime();

    private static void StopChime()
    {
        _chimeLimit?.Stop();
        _chime?.Stop();
        _chime?.Dispose();
        _chime = null;
    }
}
