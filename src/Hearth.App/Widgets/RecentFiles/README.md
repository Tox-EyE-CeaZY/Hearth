# Recent Files (`recent`)

The files and folders you used last, from Windows' own Recent list. Filter
by all, files or folders. Click to open or drag out, and right-click for
Show in folder, Copy path, and Remove from Recent.

## Files

| File | What it is |
| --- | --- |
| `RecentFilesWidget.cs` | The widget and its view |
| `RecentList.cs` | Reads and resolves the shortcuts in `shell:recent` |

## Data

None of its own. It reads `%AppData%/Microsoft/Windows/Recent`.
"Remove from Recent" deletes the shortcut there, which also takes the item
off Windows' own Recent list.

## Background

While shown: a `FileSystemWatcher` on the Recent folder (debounced 1 s), a
read on a pool thread, and a one-minute tick for the "ago" times.

## Platform

`IShellLinkW` and `IPersistFile` from Core's shared interop. Network paths
aren't checked for existence, because they can take seconds to answer.

## Notes

- The reader is `RecentList`, not `RecentFiles`, because a class named after
  its namespace can't be referred to from inside that namespace.
- Shows up to 25 items; missing targets and duplicates are skipped.
- Default size 3x3, smallest 2x2. The filters hide on short placements.
