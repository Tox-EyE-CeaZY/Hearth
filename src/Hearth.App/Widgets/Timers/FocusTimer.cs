using System.Windows.Threading;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.Timers;

internal sealed class TimerData
{
    public int DurationSeconds { get; set; } = 25 * 60;

    /// <summary>Set while running.</summary>
    public DateTime? EndsUtc { get; set; }

    /// <summary>Set while paused.</summary>
    public int? PausedSeconds { get; set; }
}

/// <summary>
/// The countdown behind every Timer widget. It lives outside the views, and
/// is saved, so a relayout or a Hearth restart doesn't lose it. The end is
/// scheduled with Windows as well, so it still goes off if Hearth is closed.
/// UI thread only.
/// </summary>
internal static class FocusTimer
{
    private const string Group = "timer";
    private static readonly WidgetStore<TimerData> Store = new("timer.json");
    private static TimerData? _data;
    private static DispatcherTimer? _due;

    public static event Action? Changed;

    private static TimerData Data => _data ??= Store.Load();

    public static TimeSpan Duration => TimeSpan.FromSeconds(Data.DurationSeconds);
    public static bool IsRunning => Data.EndsUtc is not null;
    public static bool IsPaused => Data.PausedSeconds is not null;

    public static TimeSpan Remaining =>
        Data.EndsUtc is { } ends ? ends - DateTime.UtcNow
        : Data.PausedSeconds is { } paused ? TimeSpan.FromSeconds(paused)
        : Duration;

    /// <summary>Called at start-up (TimerWidget.StartServices): picks a saved countdown back up.</summary>
    public static void Restore()
    {
        if (Data.EndsUtc is { } ends && ends <= DateTime.UtcNow)
        {
            // It ran out while Hearth was closed; Windows' notification covered it.
            Data.EndsUtc = null;
            Store.Save(Data);
        }
        Schedule();
        Arm();
    }

    public static void Start(TimeSpan duration)
    {
        Data.DurationSeconds = (int)Math.Clamp(duration.TotalSeconds, 1, 24 * 3600);
        Run(Duration);
    }

    public static void Pause()
    {
        if (Data.EndsUtc is null) return;
        Data.PausedSeconds = (int)Math.Ceiling(Math.Max(0, Remaining.TotalSeconds));
        Data.EndsUtc = null;
        Commit();
    }

    public static void Resume()
    {
        if (Data.PausedSeconds is not { } paused) return;
        Run(TimeSpan.FromSeconds(paused));
    }

    public static void Reset()
    {
        Data.EndsUtc = null;
        Data.PausedSeconds = null;
        Commit();
    }

    public static void AddMinute()
    {
        if (Data.EndsUtc is { } ends) Data.EndsUtc = ends.AddMinutes(1);
        else if (Data.PausedSeconds is { } paused) Data.PausedSeconds = paused + 60;
        else Data.DurationSeconds += 60;
        Commit();
    }

    public static void OpenClockApp() => ShellLauncher.Open("ms-clock:");

    private static void Run(TimeSpan length)
    {
        Data.PausedSeconds = null;
        Data.EndsUtc = DateTime.UtcNow + length;
        Commit();
    }

    private static void Commit()
    {
        Store.Save(Data);
        Schedule();
        Arm();
        Changed?.Invoke();
    }

    /// <summary>Lines Windows' notification up with the saved state.</summary>
    private static void Schedule()
    {
        SystemNotifications.Cancel(Group);
        if (Data.EndsUtc is { } ends)
        {
            SystemNotifications.ScheduleAlarm(Group, "timer", ends.ToLocalTime(), "Timer",
                $"Your {Describe(Duration)} timer is up", snooze: false);
        }
    }

    /// <summary>Lines the in-process end up with the saved state.</summary>
    private static void Arm()
    {
        _due?.Stop();
        if (Data.EndsUtc is not { } ends) return;

        _due ??= new DispatcherTimer(DispatcherPriority.Normal);
        _due.Tick -= OnDue;
        _due.Tick += OnDue;

        // Re-checked each tick, so sleep or a clock change can't skip it.
        var wait = ends - DateTime.UtcNow;
        _due.Interval = wait <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 30));
        _due.Start();
    }

    private static void OnDue(object? sender, EventArgs e)
    {
        if (Data.EndsUtc is not { } ends) return;
        if (ends > DateTime.UtcNow)
        {
            Arm();
            return;
        }

        _due?.Stop();
        Data.EndsUtc = null;
        Store.Save(Data);
        Changed?.Invoke();

        if (SystemNotifications.Enabled) return; // Windows is showing it.
        var length = Duration;
        WidgetAlert.Show("\uE916", "Timer", $"Your {Describe(length)} timer is up.",
            ("Restart", () => Start(length)), ("Dismiss", null));
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.Seconds == 0
            ? span.TotalMinutes == 1 ? "1 minute" : $"{(int)span.TotalMinutes} minute"
            : WidgetFormat.Clock(span);
}
