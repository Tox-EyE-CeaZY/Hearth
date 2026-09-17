# System (`system`)

CPU, memory, battery and system-drive usage as bars.

## Files

| File | What it is |
| --- | --- |
| `SystemWidget.cs` | The widget, its view, and its kernel calls |

## Data

None.

## Background

A two-second sample while shown.

## Platform

`GetSystemTimes`, `GlobalMemoryStatusEx` and `GetSystemPowerStatus`,
declared in the widget itself. Performance counters would need an extra
package and are slow to give a first sample.

## Notes

- The folder is `SystemInfo`, not `System`: a `Hearth.App.Widgets.System`
  namespace would hide .NET's `System` in every widget file.
- Built before `WidgetView` existed.
- Default size 3x2, smallest 2x2. On the Widgets board by default.
