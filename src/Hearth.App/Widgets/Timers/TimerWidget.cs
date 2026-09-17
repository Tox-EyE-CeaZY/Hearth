using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearth.App.Widgets.Timers;

/// <summary>
/// A countdown for focus sessions: 5/15/25-minute presets, pause, reset and
/// +1 minute, around a progress ring. The end rings through Windows (see
/// <see cref="FocusTimer"/>), and the Clock app is one click away.
/// </summary>
public sealed class TimerWidget : IWidget
{
    public string Id => "timer";
    public string Title => "Timer";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => wide ? 150 : 200;
    public int Order => 100;

    public void StartServices() => FocusTimer.Restore();

    public FrameworkElement CreateView(WidgetContext context) => new TimerView(context);

    private sealed class TimerView : WidgetView
    {
        private const string Play = "\uE768";
        private const string PauseGlyph = "\uE769";
        private static readonly int[] Presets = [5, 15, 25];

        private readonly Grid _body = new();
        private readonly Grid _dial = new();
        private readonly RingView _ring = new();
        private readonly TextBlock _time;
        private readonly TextBlock _state;
        private readonly StackPanel _controls;
        private readonly List<(Chip Chip, int Minutes)> _presets = [];
        private readonly GlyphButton _playPause;
        private readonly GlyphButton _reset;
        private readonly Chip _plusOne;

        public TimerView(WidgetContext context) : base(context)
        {
            var header = new WidgetHeader(context, "\uE916", "Timer");
            header.AddAction("\uE8A7", "Open the Clock app", FocusTimer.OpenClockApp);

            _time = WidgetChrome.Text(context, "25:00", 20, weight: FontWeights.SemiBold, display: true);
            _state = WidgetChrome.Text(context, "Ready", 10.5, WidgetChrome.Secondary);
            foreach (var t in new[] { _time, _state }) t.HorizontalAlignment = HorizontalAlignment.Center;
            _dial.Children.Add(_ring);
            _dial.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { _time, _state },
            });
            _dial.SizeChanged += (_, _) => _time.FontSize = Math.Max(10 * S, Math.Min(_dial.ActualWidth, _dial.ActualHeight) * 0.2);

            var presetRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var minutes in Presets)
            {
                var chip = new Chip(context, $"{minutes}m", () => FocusTimer.Start(TimeSpan.FromMinutes(minutes)))
                {
                    Margin = new Thickness(3 * S),
                    ToolTip = $"Start a {minutes}-minute timer",
                };
                _presets.Add((chip, minutes));
                presetRow.Children.Add(chip);
            }

            _playPause = new GlyphButton(context, Play, 38, OnPlayPause, filled: true);
            _reset = new GlyphButton(context, "\uE777", 38, FocusTimer.Reset) { ToolTip = "Reset" };
            _plusOne = new Chip(context, "+1m", FocusTimer.AddMinute) { Margin = new Thickness(6 * S, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Add a minute" };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6 * S, 0, 0),
                Children = { _playPause, new Border { Width = 6 * S }, _reset, _plusOne },
            };

            _controls = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { presetRow, buttons } };

            _body.Children.Add(_dial);
            _body.Children.Add(_controls);

            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(_body);
            SetBody(layout);

            _body.SizeChanged += (_, _) => Arrange();
            While(() => FocusTimer.Changed += OnChanged, () => FocusTimer.Changed -= OnChanged);
            Every(TimeSpan.FromMilliseconds(250), Refresh);
        }

        protected override void OnShown() => Refresh();

        private void OnChanged() => Post(Refresh);

        private static void OnPlayPause()
        {
            if (FocusTimer.IsRunning) FocusTimer.Pause();
            else if (FocusTimer.IsPaused) FocusTimer.Resume();
            else FocusTimer.Start(FocusTimer.Duration);
        }

        /// <summary>Dial beside the controls when there's width for it, above them otherwise.</summary>
        private void Arrange()
        {
            var (w, h) = (_body.ActualWidth, _body.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var side = w >= h * 1.5;

            _body.RowDefinitions.Clear();
            _body.ColumnDefinitions.Clear();
            if (side)
            {
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(h, w * 0.45)) });
                _body.ColumnDefinitions.Add(new ColumnDefinition());
            }
            else
            {
                _body.RowDefinitions.Add(new RowDefinition());
                _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            Grid.SetColumn(_controls, side ? 1 : 0);
            Grid.SetRow(_controls, side ? 0 : 1);
            _controls.Margin = side ? new Thickness(10 * S, 0, 0, 0) : new Thickness(0, 6 * S, 0, 0);

            // Presets only where they fit; the ring matters more.
            _controls.Children[0].Visibility = side || h >= 190 * S ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Refresh()
        {
            var running = FocusTimer.IsRunning;
            var paused = FocusTimer.IsPaused;
            var remaining = FocusTimer.Remaining;
            var duration = FocusTimer.Duration;

            _time.Text = WidgetFormat.Clock(remaining);
            _state.Text = running ? $"of {WidgetFormat.Clock(duration)}" : paused ? "Paused" : "Ready";
            _ring.Progress = running || paused ? remaining.TotalSeconds / Math.Max(1, duration.TotalSeconds) : 1;
            _ring.Active = running;

            _playPause.GlyphText = running ? PauseGlyph : Play;
            _playPause.ToolTip = running ? "Pause" : paused ? "Resume" : "Start";
            _reset.Visibility = running || paused ? Visibility.Visible : Visibility.Collapsed;
            _plusOne.Visibility = _reset.Visibility;
            foreach (var (chip, minutes) in _presets)
                chip.IsActive = (running || paused) && Math.Abs(duration.TotalMinutes - minutes) < 0.01;
        }
    }

    /// <summary>A circular progress track. Progress is the part still to go, 0..1.</summary>
    private sealed class RingView : FrameworkElement
    {
        private readonly Brush _track = WidgetChrome.Track;
        private readonly Pen _arc;
        private readonly Pen _idle;
        private double _progress = 1;
        private bool _active;

        public RingView()
        {
            _arc = WidgetChrome.Frozen(new Pen(WidgetChrome.Accent, 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            _idle = WidgetChrome.Frozen(new Pen(WidgetChrome.Faint, 1));
        }

        public double Progress
        {
            get => _progress;
            set
            {
                value = Math.Clamp(value, 0, 1);
                if (Math.Abs(value - _progress) < 0.0005) return;
                _progress = value;
                InvalidateVisual();
            }
        }

        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = Math.Min(RenderSize.Width, RenderSize.Height);
            if (size <= 0) return;

            var thickness = Math.Max(3, size * 0.06);
            var radius = (size - thickness) / 2;
            var center = new Point(RenderSize.Width / 2, RenderSize.Height / 2);
            dc.DrawEllipse(null, new Pen(_track, thickness), center, radius, radius);

            var pen = (_active || _progress < 1 ? _arc : _idle).CloneCurrentValue();
            pen.Thickness = thickness;
            if (_progress >= 0.9995)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
                return;
            }
            if (_progress <= 0) return;

            // Clockwise from twelve o'clock.
            var angle = _progress * 2 * Math.PI;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false, false);
                g.ArcTo(end, new Size(radius, radius), 0, _progress > 0.5, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
