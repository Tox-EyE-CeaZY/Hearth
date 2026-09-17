# Speed Dial (`speeddial`)

A compact launcher for apps, websites, files and folders, in the home
screen's icon style. Right-click a tile to move or remove it.

## Files

| File | What it is |
| --- | --- |
| `SpeedDialWidget.cs` | The widget, its view, lettered tiles, and the add menu |
| `SpeedDialList.cs` | The entries, their store, launching, and the app picker |
| `WebsiteWindow.cs` | The "Add a website" dialog |

## Data

`%AppData%/Hearth/speeddial.json`. Every Speed Dial widget shows the same
entries.

## Background

None.

## Platform

- Apps are picked in Hearth's app drawer (`Views/AddAppsWindow`), through
  `SpeedDialList.AppPicker`, and launched with `ShellLauncher.Launch`.
- Files and folders use the Windows file and folder pickers.
- Websites open in the default browser. Nothing is fetched from them.

## Notes

- A website tile shows a letter on a colour picked from the host name with
  FNV-1a (`string.GetHashCode` changes every run), clipped to the user's
  icon shape.
- Short placements drop the header and show one row that scrolls sideways
  (with the mouse wheel too).
- Default size 4x1, smallest 2x1.
