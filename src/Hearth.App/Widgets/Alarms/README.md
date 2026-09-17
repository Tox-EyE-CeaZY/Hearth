# Alarms (`alarms`)

Alarms with a switch each. Click one to change it and + to add one; each
can repeat on chosen days or go off once.

## Files

| File | What it is |
| --- | --- |
| `AlarmWidget.cs` | The widget, its view, and `Edit` |
| `AlarmClock.cs` | The alarms, their store, scheduling and ringing |
| `AlarmEditWindow.cs` | The editor: time, name, days, delete. `ParseTime` accepts 7:30 AM, 19:30, 0730, 730, 7pm, 7.30 and 7h30. |

## Data

`%AppData%/Hearth/alarms.json`. A one-off alarm stores when it's due
(`OnceAt`) and switches itself off once it has gone off. Snoozes from the
banner are kept in memory only.

## Background

- `StartServices` calls `AlarmClock.Start`, and `StopServices` calls `Stop`.
- The next 7 days of each alarm are handed to Windows
  (`SystemNotifications`, group `alarm`) with Windows' Snooze and Dismiss.
  They are handed over again after every change and every ring, and when
  the clock changes.
- While Hearth runs, it checks every 5 s for anything due since the last
  check, which covers sleep. It shows `WidgetAlert` (Snooze 10 min, Dismiss)
  only if Windows notifications are off. Alarms missed while Hearth was
  closed aren't replayed; Windows covers those.

## Platform

Windows notifications and the Clock app (`ms-clock:`, from the header button).

## Notes

- Not yet heard ringing in either mode.
- Default size 3x2, smallest 2x2.
