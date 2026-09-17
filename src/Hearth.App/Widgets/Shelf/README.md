# Shelf (`shelf`)

A place to drop files. Dropped files are copied (hold Shift to move them).
Drag them back out to wherever they're going, or click to open. It holds
either Hearth's own stash or a real folder such as Downloads.

## Files

| File | What it is |
| --- | --- |
| `ShelfWidget.cs` | The widget (`IConfigurableWidget`), its view, and drop handling |
| `ShelfFolder.cs` | Settings, listing, adding, and watching the folder |
| `ShelfSetupWindow.cs` | The settings dialog: stash or folder |
| `ShellCopy.cs` | `SHFileOperation` for copying and moving |

## Data

- Settings: `%AppData%/Hearth/shelf.json`.
- The stash: `%LocalAppData%/Hearth/Shelf/`.

## Background

While any Shelf is shown, a `FileSystemWatcher` on the folder (ref-counted,
debounced 400 ms). Copies run on their own STA thread.

## Platform

The shell's file operation, so drops get Explorer's progress window,
"Copy of ..." names on a clash, and undo. Remove and Delete use the Recycle
Bin (`ShellLauncher.Recycle`).

## Notes

- Files already in the shelf's folder aren't copied onto themselves.
- Shows up to 100 items, newest first. Hidden and system files are left out.
- Dragging in and out hasn't been tried by hand yet.
- Default size 3x2, smallest 2x2.
