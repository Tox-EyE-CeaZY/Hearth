# Notes (`notes`)

A sticky note. Click it to type.

## Files

| File | What it is |
| --- | --- |
| `NotesWidget.cs` | The widget and its editor |

## Data

`%AppData%/Hearth/notes.txt`, a plain text file. It's saved 0.7 s after you
stop typing, and again when you click away.

## Background

None.

## Notes

- Takes the keyboard through `WidgetKeyboard`, so typing in it inside the
  Start menu doesn't pull focus to the desktop.
- Built before `WidgetView` existed.
- Default size 3x3, smallest 2x2. Not on the Widgets board by default.
