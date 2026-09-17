# Clock (`clock`)

Time and date in large, light type, the way an Android home screen opens.

## Files

| File | What it is |
| --- | --- |
| `ClockWidget.cs` | The widget, and a view that draws its own text |

## Data

None.

## Background

A timer runs while the widget is shown, set to fire on the minute.

## Notes

- Built before `WidgetView` existed: it is a plain `FrameworkElement` that
  renders cached glyph runs. A resize rebuilds the cache.
- Default size 4x2, smallest 2x1. On the Widgets board by default.
