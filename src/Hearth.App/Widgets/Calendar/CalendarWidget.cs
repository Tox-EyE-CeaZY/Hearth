using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Hearth.App.Widgets.Calendar;

/// <summary>
/// This month at a glance, with today marked.
///
/// The month is laid out at a fixed design size inside a Viewbox, so it scales
/// cleanly to whatever the widget is resized to — a calendar is one of the few
/// widgets where uniform scaling is exactly right.
/// </summary>
public sealed class CalendarWidget : IWidget
{
    public string Id => "calendar";
    public string Title => "Calendar";
    public (int Columns, int Rows) DefaultSpan => (3, 3);
    public (int Columns, int Rows) MinimumSpan => (2, 2);

    public FrameworkElement CreateView(WidgetContext context) => new CalendarView(context);

    public double BoardHeight(bool wide) => 280;
    public int Order => 30;

    public bool OnBoardByDefault => true;

    private sealed class CalendarView : ContentControl
    {
        private readonly WidgetContext _context;
        private readonly Grid _month = new();
        private readonly DispatcherTimer _midnight;
        private DateTime _shownDay;

        public CalendarView(WidgetContext context)
        {
            _context = context;

            var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = _month };
            Content = WidgetChrome.Card(context, viewbox);

            // Rebuild when the date rolls over; checking once a minute is plenty.
            _midnight = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _midnight.Tick += (_, _) => { if (DateTime.Today != _shownDay) Build(); };

            Loaded += (_, _) => { Build(); _midnight.Start(); };
            Unloaded += (_, _) => _midnight.Stop();
        }

        /// <summary>
        /// Built in design units (not scaled): the Viewbox does the scaling, so
        /// this only has to be proportioned well.
        /// </summary>
        private void Build()
        {
            using var theme = WidgetChrome.Scope(_context);
            var today = DateTime.Today;
            _shownDay = today;

            var culture = CultureInfo.CurrentCulture;
            var firstDayOfWeek = culture.DateTimeFormat.FirstDayOfWeek;
            var first = new DateTime(today.Year, today.Month, 1);
            var lead = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
            var start = first.AddDays(-lead);

            _month.Children.Clear();
            _month.RowDefinitions.Clear();
            _month.Width = 280;

            _month.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _month.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 12) };
            header.Children.Add(Label(culture.DateTimeFormat.GetMonthName(today.Month), 22, WidgetChrome.Primary, FontWeights.SemiBold));
            header.Children.Add(Label($"  {today.Year}", 22, WidgetChrome.Secondary, FontWeights.Normal));
            _month.Children.Add(header);

            var days = new UniformGrid { Columns = 7, Rows = 7 };
            Grid.SetRow(days, 1);
            _month.Children.Add(days);

            for (var i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)firstDayOfWeek + i) % 7);
                var name = culture.DateTimeFormat.GetShortestDayName(day);
                days.Children.Add(Cell(Label(name, 12, WidgetChrome.Faint, FontWeights.SemiBold), null));
            }

            for (var i = 0; i < 42; i++)
            {
                var date = start.AddDays(i);
                var inMonth = date.Month == today.Month;
                var isToday = date == today;

                var number = Label(date.Day.ToString(culture), 14,
                    isToday ? WidgetChrome.OnAccent : inMonth ? WidgetChrome.Primary : WidgetChrome.Faint,
                    isToday ? FontWeights.Bold : FontWeights.Normal);

                days.Children.Add(Cell(number, isToday ? WidgetChrome.Accent : null));
            }
        }

        private static TextBlock Label(string text, double size, Brush brush, FontWeight weight) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            FontFamily = WidgetChrome.Display,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static FrameworkElement Cell(UIElement content, Brush? highlight)
        {
            var cell = new Grid { Height = 36, Width = 40 };
            if (highlight is not null)
                cell.Children.Add(new Ellipse { Width = 30, Height = 30, Fill = highlight });
            cell.Children.Add(content);
            return cell;
        }
    }
}
