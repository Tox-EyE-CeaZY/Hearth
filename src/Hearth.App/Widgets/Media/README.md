# Now playing (`media`)

Whatever Windows' media overlay is showing (Spotify, a browser tab, VLC),
with artwork and play/pause/skip.

## Files

| File | What it is |
| --- | --- |
| `MediaWidget.cs` | The widget and its view |

## Data

None. Artwork is decoded when it changes and not saved.

## Background

While shown: the session manager's events, and a one-second tick for the
progress bar.

## Platform

`GlobalSystemMediaTransportControlsSessionManager` (WinRT), the same source
the volume flyout uses, so any app that reports media to Windows works.

## Notes

- Built before `WidgetView` existed.
- WinRT: the first use loads the projection assembly (about 2.6 s). See
  "Background work" in [../README.md](../README.md) before adding more
  WinRT calls here.
- Default size 4x2, smallest 3x1. On the Widgets board by default.
