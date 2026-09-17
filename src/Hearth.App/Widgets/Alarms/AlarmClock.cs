using System.Globalization;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;
using Microsoft.Win32;

namespace Hearth.App.Widgets.Alarms;

internal sealed class Alarm
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    public string Label { get; set; } = string.Empty;

    /// <summary>One bit per <see cref="DayOfWeek"/>. None means it goes off once.</summary>
    public int Days { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>For a one-off alarm: when it was set to go off (local time).</summary>
    public DateTime? OnceAt { get; set; }

    public bool Repeats => Days != 0;

    public bool OnDay(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    public string TimeText => DateTime.Today.AddHours(Hour).AddMinutes(Minute).ToString("t", CultureInfo.CurrentCulture);

    public string DaysText => Days switch
    {
        0 => "Once",
        0b1111111 => "Every day",
        0b0111110 => "Weekdays",
        0b1000001 => "Weekends",
        _ => string.Join(" ", Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)((i + (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek) % 7))
            .Where(OnDay)
            .Select(d => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d))),
    };
}

internal sealed class AlarmData
{
    public List<Alarm> Alarms { get; set; } = [];
}

/// <summary>
/// The alarms behind every Alarm widget.
///
/// Each enabled alarm's next week of go-off times is handed to Windows as
/// scheduled notifications, so they ring (with Windows' snooze) even while
/// Hearth is closed. While Hearth runs it also watches the times itself: if
/// Windows notifications are off, it shows its own banner instead.
/// UI thread only.
/// </summary>
internal static class AlarmClock
{
    private const string Group = "alarm";
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(7);
    private static readonly TimeSpan SnoozeLength = TimeSpan.FromMinutes(10);

    private static readonly WidgetStore<AlarmData> Store = new("alarms.json");
    private static readonly List<(DateTime At, Alarm Alarm)> Snoozed = [];
    private static AlarmData? _data;
    private static DispatcherTimer? _watch;
    private static DateTime _checkedUpTo;

    public static event Action? Changed;

    private static AlarmData Data => _data ??= Store.Load();

    public static IReadOnlyList<Alarm> Alarms => Data.Alarms;

    /// <summary>Called at start-up (AlarmWidget.StartServices). Missed alarms from while Hearth was closed are not replayed.</summary>
    public static void Start()
    {
        if (_watch is not null) return;
        _checkedUpTo = DateTime.Now;
        _watch = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
        _watch.Tick += (_, _) => Check();
        _watch.Start();
        SystemEvents.TimeChanged += OnTimeChanged;
        Sync();
    }

    public static void Stop()
    {
        _watch?.Stop();
        SystemEvents.TimeChanged -= OnTimeChanged;
    }

    public static Alarm? Find(string id) => Data.Alarms.Find(a => a.Id == id);

    /// <summary>Adds the alarm, or replaces the one with the same id.</summary>
    public static void Save(Alarm alarm)
    {
        var index = Data.Alarms.FindIndex(a => a.Id == alarm.Id);
        if (index >= 0) Data.Alarms[index] = alarm;
        else Data.Alarms.Add(alarm);
        Arm(alarm);
        Commit();
    }

    public static void Remove(string id)
    {
        Data.Alarms.RemoveAll(a => a.Id == id);
        Snoozed.RemoveAll(s => s.Alarm.Id == id);
        Commit();
    }

    public static void SetEnabled(string id, bool enabled)
    {
        if (Find(id) is not { } alarm) return;
        alarm.Enabled = enabled;
        if (enabled) Arm(alarm);
        else Snoozed.RemoveAll(s => s.Alarm.Id == id);
        Commit();
    }

    /// <summary>When the alarm next goes off, or null if it won't.</summary>
    public static DateTime? NextAt(Alarm alarm, DateTime after)
    {
        if (!alarm.Enabled) return null;
        if (!alarm.Repeats) return alarm.OnceAt is { } once && once > after ? once : null;

        for (var day = 0; day <= 7; day++)
        {
            var at = after.Date.AddDays(day).AddHours(alarm.Hour).AddMinutes(alarm.Minute);
            if (at > after && alarm.OnDay(at.DayOfWeek)) return at;
        }
        return null;
    }

    /// <summary>The soonest alarm, for the widget's header.</summary>
    public static DateTime? Soonest()
    {
        var now = DateTime.Now;
        return Data.Alarms.Select(a => NextAt(a, now))
            .Concat(Snoozed.Select(s => (DateTime?)s.At))
            .Where(t => t is not null)
            .Min();
    }

    /// <summary>A one-off alarm goes off at the next time its hour and minute come round.</summary>
    private static void Arm(Alarm alarm)
    {
        if (alarm.Repeats || !alarm.Enabled) return;
        var now = DateTime.Now;
        var at = now.Date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
        alarm.OnceAt = at > now ? at : at.AddDays(1);
    }

    private static void Commit()
    {
        Store.Save(Data);
        Sync();
        Changed?.Invoke();
    }

    /// <summary>Re-hands Windows the next week of alarms.</summary>
    private static void Sync()
    {
        SystemNotifications.Cancel(Group);
        var now = DateTime.Now;
        foreach (var alarm in Data.Alarms)
        {
            var n = 0;
            for (var at = NextAt(alarm, now); at is { } due && due - now <= Horizon; at = NextAt(alarm, due))
                SystemNotifications.ScheduleAlarm(Group, $"{alarm.Id}{n++}", due, Title(alarm), Body(alarm), snooze: true);
        }
        foreach (var (at, alarm) in Snoozed)
            SystemNotifications.ScheduleAlarm(Group, $"{alarm.Id}z", at, Title(alarm), Body(alarm), snooze: true);
    }

    private static void OnTimeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _checkedUpTo = DateTime.Now;
            Sync();
            Changed?.Invoke();
        });

    /// <summary>Rings anything due since the last check. Runs every few seconds.</summary>
    private static void Check()
    {
        var now = DateTime.Now;
        var from = _checkedUpTo;
        _checkedUpTo = now;
        if (now < from) return;

        var ringing = new List<Alarm>();
        foreach (var alarm in Data.Alarms)
        {
            if (NextAt(alarm, from) is { } at && at <= now) ringing.Add(alarm);
        }
        foreach (var snoozed in Snoozed.Where(s => s.At > from && s.At <= now).ToList())
        {
            Snoozed.Remove(snoozed);
            ringing.Add(snoozed.Alarm);
        }
        if (ringing.Count == 0) return;

        foreach (var alarm in ringing.Distinct())
        {
            Log.Write($"alarm: {alarm.TimeText} {alarm.Label}");
            if (!alarm.Repeats) alarm.Enabled = false;
            if (!SystemNotifications.Enabled) Ring(alarm);
        }

        // Also moves Windows' week of alarms along.
        Store.Save(Data);
        Sync();
        Changed?.Invoke();
    }

    /// <summary>Hearth's own banner, for when Windows notifications are off.</summary>
    private static void Ring(Alarm alarm)
    {
        WidgetAlert.Show("\uEA8F", Title(alarm), Body(alarm),
            ($"Snooze {SnoozeLength.TotalMinutes:0} min", () =>
            {
                Snoozed.Add((DateTime.Now + SnoozeLength, alarm));
                Sync();
                Changed?.Invoke();
            }),
            ("Dismiss", null));
    }

    private static string Title(Alarm alarm) => alarm.Label.Length > 0 ? alarm.Label : "Alarm";

    private static string Body(Alarm alarm) => alarm.TimeText;
}
