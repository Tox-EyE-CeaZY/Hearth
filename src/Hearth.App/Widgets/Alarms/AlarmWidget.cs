using System.Windows;
using System.Windows.Controls;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.Alarms;

/// <summary>
/// Alarms with an on/off switch each. Click one to change it, + to add one.
/// They ring through Windows (see <see cref="AlarmClock"/>).
/// </summary>
public sealed class AlarmWidget : IWidget
{
    public string Id => "alarms";
    public string Title => "Alarms";
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => 200;
    public int Order => 110;

    public void StartServices() => AlarmClock.Start();
    public void StopServices() => AlarmClock.Stop();

    public FrameworkElement CreateView(WidgetContext context) => new AlarmView(context);

    /// <summary>Opens the editor; a null <paramref name="alarm"/> makes a new one.</summary>
    internal static void Edit(Alarm? alarm)
    {
        var window = new AlarmEditWindow(alarm);
        if (window.ShowDialog() != true) return;
        if (window.Deleted && alarm is not null) AlarmClock.Remove(alarm.Id);
        else if (window.Result is { } result) AlarmClock.Save(result);
    }

    private sealed class AlarmView : WidgetView
    {
        private readonly WidgetHeader _header;
        private readonly StackPanel _rows = new();
        private readonly FrameworkElement _empty;

        public AlarmView(WidgetContext context) : base(context)
        {
            _header = new WidgetHeader(context, "\uEA8F", "Alarms");
            _header.AddAction("\uE710", "Add an alarm", () => Edit(null));
            _header.AddAction("\uE8A7", "Open the Clock app", () => ShellLauncher.Open("ms-clock:"));

            _empty = WidgetLayout.Empty(context, "\uEA8F", "No alarms yet");

            var layout = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            layout.Children.Add(_header);
            layout.Children.Add(new Grid { Children = { WidgetLayout.Scroller(_rows), _empty } });
            SetBody(layout);

            While(() => AlarmClock.Changed += OnChanged, () => AlarmClock.Changed -= OnChanged);
            Every(TimeSpan.FromSeconds(30), UpdateDetail);
        }

        protected override void OnShown() => Rebuild();

        private void OnChanged() => Post(Rebuild);

        private void UpdateDetail() =>
            _header.Detail = AlarmClock.Soonest() is { } next ? WidgetFormat.Until(next - DateTime.Now) : string.Empty;

        private void Rebuild()
        {
            UpdateDetail();
            var alarms = AlarmClock.Alarms.OrderBy(a => a.Hour).ThenBy(a => a.Minute).ToList();
            _empty.Visibility = alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _rows.Children.Clear();
            foreach (var alarm in alarms) _rows.Children.Add(Row(alarm));
        }

        private FrameworkElement Row(Alarm alarm)
        {
            var dim = alarm.Enabled ? WidgetChrome.Primary : WidgetChrome.Faint;
            var time = WidgetChrome.Text(Context, alarm.TimeText, 20, dim, FontWeights.SemiBold, display: true);
            var about = WidgetChrome.Text(Context,
                alarm.Label.Length > 0 ? $"{alarm.Label} · {alarm.DaysText}" : alarm.DaysText, 11.5,
                alarm.Enabled ? WidgetChrome.Secondary : WidgetChrome.Faint);

            var toggle = new WidgetSwitch(Context, alarm.Enabled, on => AlarmClock.SetEnabled(alarm.Id, on))
            {
                Margin = new Thickness(8 * S, 0, 0, 0),
                ToolTip = alarm.Enabled ? "Turn off" : "Turn on",
            };

            var dock = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            dock.Children.Add(toggle);
            dock.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { time, about } });

            var row = new Pressable(() => Edit(alarm))
            {
                CornerRadius = new CornerRadius(8 * S),
                Padding = new Thickness(6 * S, 3 * S, 6 * S, 4 * S),
                Margin = new Thickness(-6 * S, 0, -6 * S, 2 * S),
                Child = dock,
                ToolTip = "Edit alarm",
            };
            row.ContextRequested = () => WidgetMenu.Show(row, menu =>
            {
                menu.Items.Add(WidgetMenu.Item("Edit...", () => Edit(alarm)));
                menu.Items.Add(WidgetMenu.Item(alarm.Enabled ? "Turn off" : "Turn on", () => AlarmClock.SetEnabled(alarm.Id, !alarm.Enabled)));
                menu.Items.Add(WidgetMenu.Item("Delete", () => AlarmClock.Remove(alarm.Id)));
            });
            return row;
        }
    }
}
