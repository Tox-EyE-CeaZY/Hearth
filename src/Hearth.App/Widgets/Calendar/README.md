# Calendar (`calendar`)

This month at a glance, with today marked.

## Files

| File | What it is |
| --- | --- |
| `CalendarWidget.cs` | The widget and its view |

## Data

None.

## Background

While shown, a one-minute tick rebuilds the month when the date changes.

## Notes

- The month is laid out at a fixed size inside a `Viewbox`, so it scales
  evenly to any size.
- Built before `WidgetView` existed. `Build` opens the theme scope itself.
- Default size 3x3, smallest 2x2. On the Widgets board by default.
