# Tasks (`tasks`)

A checklist. Click a task to tick it off, type at the bottom to add one, and
use the bin button to clear finished ones. Right-click a task for more.

## Files

| File | What it is |
| --- | --- |
| `TasksWidget.cs` | The widget and its view |
| `TaskList.cs` | The list (`TaskList`), its items and its store |

## Data

`%AppData%/Hearth/tasks.json`. Every Tasks widget shows the same list.

## Background

None. The view listens to `TaskList.Changed` while shown.

## Notes

- Unfinished tasks are listed first, then finished ones, each in the order
  they were added.
- Default size 3x3, smallest 2x2.
