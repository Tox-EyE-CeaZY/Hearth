using System.Security;
using Hearth.Core.Diagnostics;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Hearth.App.Widgets;

/// <summary>
/// Windows' own notifications, for alarms and timers. They are handed to
/// Windows ahead of time (scheduled), so they go off with the system alarm
/// sound and Snooze/Dismiss buttons even if Hearth has been closed.
///
/// Hearth isn't a packaged app, so it registers an AppUserModelID under
/// HKCU\Software\Classes\AppUserModelId for Windows to show it by. When the
/// user has notifications turned off (<see cref="Enabled"/> is false),
/// callers show a <see cref="WidgetAlert"/> instead.
///
/// All of it runs on a background queue that starts a few seconds after
/// launch. The first method that mentions a WinRT type loads the whole
/// projection assembly: measured at 2.6 s in a fresh process, and while it
/// loads, other assembly loads (the UI thread's included) wait. So only
/// <see cref="Platform"/> mentions WinRT types, only queued work calls into
/// it, and the queue holds off until the desktop is up.
/// </summary>
internal static class SystemNotifications
{
    public const string AppId = "Hearth.Desktop";

    private static readonly object Gate = new();
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(8);
    private static Task _queue = Task.Delay(StartDelay);
    private static volatile bool _ready;

    /// <summary>
    /// Whether Windows will actually show Hearth's notifications right now.
    /// False until the platform has been reached (in the background, at
    /// start-up), so an alarm that early still gets a banner.
    /// </summary>
    public static bool Enabled => _ready && Platform.IsEnabled();

    /// <summary>Reaches the notification platform in the background, so <see cref="Enabled"/> is ready.</summary>
    public static void Warm() => Enqueue(() =>
    {
        Log.Write($"notifications: {Platform.Describe()}");
        _ready = true;
    });

    /// <summary>
    /// Schedules an alarm-style notification. <paramref name="id"/> must be at
    /// most 16 characters. Times in the past are ignored.
    /// </summary>
    public static void ScheduleAlarm(string group, string id, DateTime at, string title, string body, bool snooze)
    {
        var xml = AlarmXml(title, body, snooze);
        Enqueue(() =>
        {
            if (at > DateTime.Now.AddSeconds(1)) Platform.Schedule(group, id, at, xml);
        });
    }

    /// <summary>Takes back every scheduled notification in <paramref name="group"/>.</summary>
    public static void Cancel(string group) => Enqueue(() => Platform.Cancel(group));

    private static void Enqueue(Action work)
    {
        lock (Gate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Write($"notifications: {ex.Message}");
                }
            }, TaskScheduler.Default);
        }
    }

    private static string AlarmXml(string title, string body, bool snooze)
    {
        var snoozeChoice = snooze
            ? """
              <input id="snoozeTime" type="selection" defaultInput="10">
                <selection id="5" content="5 minutes"/>
                <selection id="10" content="10 minutes"/>
                <selection id="20" content="20 minutes"/>
                <selection id="30" content="30 minutes"/>
              </input>
              <action activationType="system" arguments="snooze" hint-inputId="snoozeTime" content=""/>
              """
            : string.Empty;

        return $"""
            <toast scenario="alarm">
              <visual>
                <binding template="ToastGeneric">
                  <text>{SecurityElement.Escape(title)}</text>
                  <text>{SecurityElement.Escape(body)}</text>
                </binding>
              </visual>
              <audio src="ms-winsoundevent:Notification.Looping.Alarm" loop="true"/>
              <actions>
                {snoozeChoice}
                <action activationType="system" arguments="dismiss" content=""/>
              </actions>
            </toast>
            """;
    }

    /// <summary>The only code that touches WinRT types.</summary>
    private static class Platform
    {
        private static ToastNotifier? _notifier;
        private static bool _tried;

        private static ToastNotifier? Notifier
        {
            get
            {
                if (_tried) return _notifier;
                _tried = true;
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
                    {
                        key.SetValue("DisplayName", "Hearth");
                    }
                    _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
                }
                catch (Exception ex)
                {
                    Log.Write($"notifications: unavailable: {ex.Message}");
                }
                return _notifier;
            }
        }

        public static string Describe() => Notifier?.Setting.ToString() ?? "unavailable";

        public static bool IsEnabled()
        {
            try
            {
                return Notifier?.Setting == NotificationSetting.Enabled;
            }
            catch (Exception ex)
            {
                Log.Write($"notifications: setting unreadable: {ex.Message}");
                return false;
            }
        }

        public static void Schedule(string group, string id, DateTime at, string xmlText)
        {
            if (Notifier is not { } notifier) return;
            try
            {
                var xml = new XmlDocument();
                xml.LoadXml(xmlText);
                notifier.AddToSchedule(new ScheduledToastNotification(xml, new DateTimeOffset(at))
                {
                    Id = id,
                    Tag = id,
                    Group = group,
                });
            }
            catch (Exception ex)
            {
                Log.Write($"notifications: couldn't schedule {group}/{id}: {ex.Message}");
            }
        }

        public static void Cancel(string group)
        {
            if (Notifier is not { } notifier) return;
            foreach (var toast in notifier.GetScheduledToastNotifications())
            {
                if (toast.Group == group) notifier.RemoveFromSchedule(toast);
            }
        }
    }
}
