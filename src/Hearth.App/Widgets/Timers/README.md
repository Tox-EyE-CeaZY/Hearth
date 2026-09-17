# Timer (`timer`)

A countdown for focus sessions: 5, 15 and 25-minute presets, pause, reset and
+1 minute, around a progress ring. It rings through Windows.

## Files

| File | What it is |
| --- | --- |
| `TimerWidget.cs` | The widget, its view and the ring |
| `FocusTimer.cs` | The countdown: state, saving, and ringing |

## Data

`%AppData%/Hearth/timer.json`: the length, and the end time while it's
running or the time left while it's paused. A running timer survives
restarting Hearth.

## Background

- `StartServices` calls `FocusTimer.Restore`.
- When started, the end is scheduled with Windows
  (`SystemNotifications`, group `timer`) as an alarm notification, so it
  rings even if Hearth is closed.
- While running, Hearth checks every 30 s or less. At the end it shows
  `WidgetAlert` (with Restart) only if Windows notifications are off.

## Platform

Windows notifications and the Clock app (`ms-clock:`, from the header button).

## Notes

- The folder is `Timers`, not `Timer`: a `Timer` namespace would hide
  `System.Threading.Timer` in every widget file.
- Not yet heard ringing in either mode.
- Default size 3x2, smallest 2x2. The ring moves beside the controls when
  there's width for it.
