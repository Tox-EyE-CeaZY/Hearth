using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.Network;

/// <summary>
/// Live download and upload speeds with a one-minute graph, the connection's
/// name, and a ping to 1.1.1.1.
/// </summary>
public sealed class NetworkWidget : IWidget
{
    public string Id => "network";
    public string Title => "Network";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 1);
    public double BoardHeight(bool wide) => wide ? 150 : 180;
    public int Order => 130;

    public FrameworkElement CreateView(WidgetContext context) => new NetworkView(context);

    private sealed class NetworkView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly TextBlock _down;
        private readonly TextBlock _up;
        private readonly GraphView _graph = new();

        public NetworkView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uE774", "Network");
            _header.AddAction("\uE713", "Network settings", () => ShellLauncher.Open("ms-settings:network-status"));

            _down = WidgetChrome.Text(context, "0 B/s", 18, weight: FontWeights.SemiBold, display: true);
            _up = WidgetChrome.Text(context, "0 B/s", 18, weight: FontWeights.SemiBold, display: true);
            var rates = new Grid { Margin = new Thickness(0, 0, 0, 6 * S) };
            rates.ColumnDefinitions.Add(new ColumnDefinition());
            rates.ColumnDefinitions.Add(new ColumnDefinition());
            var down = Rate("\uE74B", WidgetChrome.Accent, _down, "Download");
            var up = Rate("\uE74A", WidgetChrome.Secondary, _up, "Upload");
            Grid.SetColumn(up, 1);
            rates.Children.Add(down);
            rates.Children.Add(up);

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(rates, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(rates);
            layout.Children.Add(_graph);
            SetBody(layout);

            layout.SizeChanged += (_, _) =>
            {
                _graph.Visibility = layout.ActualHeight >= 110 * S ? Visibility.Visible : Visibility.Collapsed;
                _down.FontSize = _up.FontSize = (layout.ActualWidth < 240 * S ? 13.5 : 18) * S;
            };

            While(() =>
            {
                NetworkMonitor.Current.Updated += OnUpdated;
                NetworkMonitor.Current.Subscribe();
            }, () =>
            {
                NetworkMonitor.Current.Updated -= OnUpdated;
                NetworkMonitor.Current.Unsubscribe();
            });
        }

        protected override void OnShown() => Show(NetworkMonitor.Current.Latest);

        private void OnUpdated(NetworkSnapshot snapshot) => Post(() => Show(snapshot));

        private FrameworkElement Rate(string glyph, Brush brush, TextBlock value, string label)
        {
            var arrow = WidgetChrome.Glyph(Context, glyph, 13, brush);
            arrow.Margin = new Thickness(0, 2 * S, 6 * S, 0);
            var caption = WidgetChrome.Text(Context, label, 10.5, WidgetChrome.Secondary);
            return new StackPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { arrow, value } },
                    caption,
                },
            };
        }

        private void Show(NetworkSnapshot snapshot)
        {
            _header.Glyph = snapshot.Kind switch
            {
                ConnectionKind.Wifi => "\uE701",
                ConnectionKind.Ethernet => "\uE839",
                _ => "\uE774",
            };
            _header.Title = snapshot.Name;
            _header.Detail = snapshot.PingMs is { } ms ? $"{ms} ms" : string.Empty;
            _down.Text = WidgetFormat.Rate(snapshot.DownBytesPerSecond);
            _up.Text = WidgetFormat.Rate(snapshot.UpBytesPerSecond);
            _graph.Samples = snapshot.History;
        }
    }

    /// <summary>Download as a filled area, upload as a line, scaled to the busiest second shown.</summary>
    private sealed class GraphView : FrameworkElement
    {
        private const double Floor = 64 * 1024;

        private readonly Pen _downLine;
        private readonly Brush _downFill;
        private readonly Pen _upLine;
        private readonly Pen _baseline;
        private IReadOnlyList<(double Down, double Up)> _samples = [];

        public GraphView()
        {
            var accent = ((SolidColorBrush)WidgetChrome.Accent).Color;
            _downLine = WidgetChrome.Frozen(new Pen(WidgetChrome.Accent, 1.5) { LineJoin = PenLineJoin.Round });
            _downFill = WidgetChrome.Frozen(new LinearGradientBrush(
                Color.FromArgb(0x66, accent.R, accent.G, accent.B),
                Color.FromArgb(0x08, accent.R, accent.G, accent.B), 90));
            _upLine = WidgetChrome.Frozen(new Pen(WidgetChrome.Secondary, 1.2) { LineJoin = PenLineJoin.Round });
            _baseline = WidgetChrome.Frozen(new Pen(WidgetChrome.Track, 1));
            ClipToBounds = true;
        }

        public IReadOnlyList<(double Down, double Up)> Samples
        {
            set
            {
                _samples = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var (w, h) = (RenderSize.Width, RenderSize.Height);
            if (w <= 0 || h <= 0) return;
            dc.DrawLine(_baseline, new Point(0, h - 0.5), new Point(w, h - 0.5));
            if (_samples.Count < 2) return;

            var peak = Math.Max(Floor, _samples.Max(s => Math.Max(s.Down, s.Up))) * 1.15;
            var step = w / (_samples.Count - 1);
            Point At(int i, double value) => new(i * step, h - value / peak * h);

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(0, h), true, true);
                for (var i = 0; i < _samples.Count; i++) g.LineTo(At(i, _samples[i].Down), true, true);
                g.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(_downFill, null, area);

            DrawLine(dc, _downLine, i => At(i, _samples[i].Down));
            DrawLine(dc, _upLine, i => At(i, _samples[i].Up));
        }

        private void DrawLine(DrawingContext dc, Pen pen, Func<int, Point> at)
        {
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(at(0), false, false);
                for (var i = 1; i < _samples.Count; i++) g.LineTo(at(i), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }
    }
}
