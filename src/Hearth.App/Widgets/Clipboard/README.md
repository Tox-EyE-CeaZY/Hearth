# Clipboard (`clipboard`)

Recently copied text and pinned snippets. Click one to copy it again. Colour
codes like #8AB4F8 get a swatch.

## Files

| File | What it is |
| --- | --- |
| `ClipboardWidget.cs` | The widget and its view |
| `ClipboardHistory.cs` | The listener, recent entries, pins, and the clipboard calls |

## Data

- Recent entries (up to 30, each up to 10,000 characters) are kept **in
  memory only** and are gone when Hearth exits.
- Pins: `%AppData%/Hearth/clipboard-pins.json`.

## Background

The listener (a message-only window with `AddClipboardFormatListener`)
starts the first time a Clipboard widget is shown and runs until Hearth
exits (`StopServices`). Text copied before that isn't captured.

## Platform

Content that apps mark as private is skipped, the same way Windows'
clipboard history skips it: the `ExcludeClipboardContentFromMonitorProcessing`
and `Clipboard Viewer Ignore` formats, and `CanIncludeInClipboardHistory` = 0.
Password managers set these.

## Notes

- The namespace `Hearth.App.Widgets.Clipboard` hides `System.Windows.Clipboard`
  in every widget file. Write that one out in full.
- Only text is captured.
- Live capture hasn't been tried by hand yet.
- Default size 3x3, smallest 2x2.
