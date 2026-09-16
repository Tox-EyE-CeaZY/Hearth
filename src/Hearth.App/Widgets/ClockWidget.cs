using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hearth.App.Widgets;

/// <summary>
/// Time and date, in the oversized light type an Android home screen opens
/// with. Deliberately the first widget: it is the one that makes a home screen
/// look like a home screen rather than a folder window.
/// </summary>
public sealed class ClockWidget : IWidget
{
    public string Id => "clock";
    public string Title => "Clock";
    public (int Columns, int Rows) DefaultSpan => (4, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 1);

    public FrameworkElement CreateView(WidgetContext context) => new ClockView(context);

    private sealed class ClockView : FrameworkElement
    {
        private readonly WidgetContext _context;
        private readonly DispatcherTimer _timer;
        private FormattedText? _time;
        private FormattedText? _date;

        public ClockView(WidgetContext context)
        {
            _context = context;

            // Ticking once a second would redraw 59 times for no visible
            // change. Align to the minute instead and the widget costs
            // essentially nothing to keep on screen.
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _timer.Tick += OnTick;

            Loaded += (_, _) => { ScheduleNextMinute(); Rebuild(); };

            // The desktop layer outlives any single widget, so a timer that is
            // not stopped here would keep the view alive forever.
            Unloaded += (_, _) => _timer.Stop();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            ScheduleNextMinute();
            Rebuild();
        }

        private void ScheduleNextMinute()
        {
            var now = DateTime.Now;
            var untilNextMinute = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond);
            if (untilNextMinute < TimeSpan.FromMilliseconds(250)) untilNextMinute = TimeSpan.FromSeconds(60);

            _timer.Stop();
            _timer.Interval = untilNextMinute;
            _timer.Start();
        }

        private void Rebuild()
        {
            _time = null;
            _date = null;
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            // Cached text is sized for the old bounds; a resize must rebuild it.
            Rebuild();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var foreground = _context.DarkTheme ? Brushes.White : Brushes.Black;
            var now = DateTime.Now;

            if (_time is null || _date is null)
            {
                var timeText = now.ToString("t", CultureInfo.CurrentCulture);
                var dateText = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);

                // Fit both ways. Sized from height alone, shrinking the widget
                // sideways left the text as big as before, spilling out of the
                // frame, so resizing smaller appeared to do nothing.
                const double reference = 100;
                var timeProbe = Build(timeText, reference, FontWeights.Thin, foreground, pixelsPerDip);
                var dateProbe = Build(dateText, reference, FontWeights.Medium, foreground, pixelsPerDip);

                var timeSize = Math.Min(height * 0.46, width * 0.98 / timeProbe.WidthIncludingTrailingWhitespace * reference);
                var dateSize = Math.Min(Math.Max(10, height * 0.15), width * 0.98 / dateProbe.WidthIncludingTrailingWhitespace * reference);

                // Keep the date visibly subordinate even when width is the limit.
                dateSize = Math.Min(dateSize, timeSize * 0.4);

                _time = Build(timeText, Math.Max(8, timeSize), FontWeights.Thin, foreground, pixelsPerDip);
                _date = Build(dateText, Math.Max(8, dateSize), FontWeights.Medium, foreground, pixelsPerDip);
            }

            var totalHeight = _time.Height + _date.Height * 1.2;
            var y = Math.Max(0, (height - totalHeight) / 2);

            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            dc.DrawText(_time, new Point(0, y));
            dc.DrawText(_date, new Point(2, y + _time.Height + _date.Height * 0.15));
            dc.Pop();
        }

        private static FormattedText Build(string text, double size, FontWeight weight, Brush brush, double pixelsPerDip)
        {
            var typeface = new Typeface(
                new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontStyles.Normal, weight, FontStretches.Normal);

            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, brush, pixelsPerDip)
            {
                // Same reasoning as the icon labels: a real shadow effect here
                // would force this widget out of the cached-glyph fast path.
                TextAlignment = TextAlignment.Left,
            };
        }
    }
}
