using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearth.App.Controls;

namespace Hearth.App.Widgets.Alarms;

/// <summary>
/// Adds or changes an alarm: time, name and which days it repeats on.
/// An ordinary window, because typing needs the keyboard.
/// </summary>
internal sealed class AlarmEditWindow : Window
{
    private readonly Alarm? _existing;
    private readonly TextBox _time;
    private readonly TextBox _label;
    private readonly TextBlock _error;
    private readonly Dictionary<DayOfWeek, Button> _days = [];
    private int _dayMask;

    public Alarm? Result { get; private set; }
    public bool Deleted { get; private set; }

    public AlarmEditWindow(Alarm? existing)
    {
        _existing = existing;
        _dayMask = existing?.Days ?? 0;

        Title = (existing is null ? "New alarm" : "Edit alarm") + " — Hearth";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        DialogChrome.Apply(this);

        var heading = new TextBlock
        {
            Text = existing is null ? "New alarm" : "Edit alarm",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = WidgetChrome.Display,
            Margin = new Thickness(0, 0, 0, 14),
        };

        _time = new TextBox { Text = existing?.TimeText ?? DateTime.Today.AddHours(7).ToString("t", CultureInfo.CurrentCulture) };
        _label = new TextBox { Text = existing?.Label ?? string.Empty };
        _error = Muted(string.Empty);
        _error.Foreground = DialogChrome.Brush("#FFF28B82");
        _error.Visibility = Visibility.Collapsed;

        var dayRow = new UniformGrid7();
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + (int)format.FirstDayOfWeek) % 7);
            var button = new Button
            {
                Content = format.GetShortestDayName(day),
                Padding = new Thickness(0, 7, 0, 7),
                Margin = new Thickness(i == 0 ? 0 : 3, 0, i == 6 ? 0 : 3, 0),
                ToolTip = format.GetDayName(day),
            };
            button.Click += (_, _) =>
            {
                _dayMask ^= 1 << (int)day;
                PaintDays();
            };
            _days[day] = button;
            dayRow.Children.Add(button);
        }

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (name, mask) in new[] { ("Once", 0), ("Weekdays", 0b0111110), ("Weekends", 0b1000001), ("Every day", 0b1111111) })
        {
            var link = new Button { Content = name, Style = (Style)Resources["Link"], Margin = new Thickness(0, 0, 14, 0) };
            link.Click += (_, _) =>
            {
                _dayMask = mask;
                PaintDays();
            };
            quick.Children.Add(link);
        }

        var save = new Button { Content = "Save", MinWidth = 96, IsDefault = true, Style = (Style)Resources["Primary"], Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => OnSave();
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true };
        var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
        if (existing is not null)
        {
            var delete = new Button { Content = "Delete", MinWidth = 96 };
            delete.Click += (_, _) =>
            {
                Deleted = true;
                DialogResult = true;
            };
            DockPanel.SetDock(delete, Dock.Left);
            footer.Children.Add(delete);
        }
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(save);
        footer.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                heading,
                Label("Time"), _time, Hint("For example 7:30 AM, 19:30 or 0730."),
                Label("Name"), _label,
                Label("Repeat"), dayRow, quick,
                _error,
                footer,
            },
        };

        PaintDays();
        Loaded += (_, _) =>
        {
            _time.Focus();
            _time.SelectAll();
        };
    }

    private void PaintDays()
    {
        foreach (var (day, button) in _days)
        {
            var on = (_dayMask & (1 << (int)day)) != 0;
            button.Background = on ? DialogChrome.Brush("#FF8AB4F8") : DialogChrome.Brush("#FF2A2A31");
            button.Foreground = on ? DialogChrome.Brush("#FF10182A") : DialogChrome.Brush("#FFF2F2F4");
        }
    }

    private void OnSave()
    {
        if (ParseTime(_time.Text) is not { } time)
        {
            _error.Text = "That doesn't look like a time. Try 7:30 AM or 19:30.";
            _error.Visibility = Visibility.Visible;
            _time.Focus();
            return;
        }

        Result = new Alarm
        {
            Id = _existing?.Id ?? new Alarm().Id,
            Hour = time.Hours,
            Minute = time.Minutes,
            Label = _label.Text.Trim(),
            Days = _dayMask,
            Enabled = true,
        };
        DialogResult = true;
    }

    /// <summary>Accepts the culture's own formats, 24-hour times, "730", "0730", "7pm".</summary>
    internal static TimeSpan? ParseTime(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        if (text.All(char.IsDigit) && text.Length <= 4)
        {
            var value = int.Parse(text, CultureInfo.InvariantCulture);
            var (h, m) = text.Length <= 2 ? (value, 0) : (value / 100, value % 100);
            return h < 24 && m < 60 ? new TimeSpan(h, m, 0) : null;
        }

        // "7.30" and "7h30" are common ways to write 7:30.
        text = Regex.Replace(text, @"^(\d{1,2})[.h](\d{2})\b", "$1:$2");
        var lower = text.ToLowerInvariant().Replace(".", string.Empty);
        if (lower.EndsWith('a') || lower.EndsWith('p')) lower += "m";
        if (lower.EndsWith("am") || lower.EndsWith("pm"))
        {
            var digits = lower[..^2].Trim();
            if (digits.All(char.IsDigit) && digits.Length is > 0 and <= 2) lower = digits + ":00 " + lower[^2..];
        }

        string[] formats = ["h:mm tt", "h:mmtt", "hh:mm tt", "H:mm", "HH:mm", "h tt", "htt"];
        if (DateTime.TryParseExact(lower, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
            return exact.TimeOfDay;
        // Anything the culture reads as a date ("7/30") isn't a time.
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var local) &&
            local.Date == DateTime.MinValue.Date)
            return new TimeSpan(local.Hour, local.Minute, 0);
        return null;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 12, 0, 6),
    };

    private static TextBlock Hint(string text)
    {
        var hint = Muted(text);
        hint.Margin = new Thickness(0, 4, 0, 0);
        hint.FontSize = 12;
        return hint;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = DialogChrome.Brush("#99F2F2F4"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0),
    };

    /// <summary>Seven equal columns.</summary>
    private sealed class UniformGrid7 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid7()
        {
            Columns = 7;
            Rows = 1;
        }
    }
}
