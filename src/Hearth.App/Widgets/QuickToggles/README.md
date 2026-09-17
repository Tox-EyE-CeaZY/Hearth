# Quick Toggles (`toggles`)

Android-style pills: mute, Hearth's dark/light theme, screen snip, Night
Light settings, empty the Recycle Bin, and lock the PC.

## Files

| File | What it is |
| --- | --- |
| `QuickTogglesWidget.cs` | The widget and its pills |
| `RecycleBin.cs` | `SHQueryRecycleBin` and `SHEmptyRecycleBin` |

## Data

None. The theme pill changes `HearthSettings.DarkTheme`.

## Background

While shown: a five-second poll for the Recycle Bin count and theme, and
`AudioService` volume events.

## Platform

`ms-screenclip:`, `ms-settings:nightlight` (Windows has no public switch for
Night Light, so the pill opens its settings), and `LockWorkStation` (shared
in Core, which the Start menu also uses).

## Notes

- Depends on `Audio/AudioService`.
- The pills keep the brushes they were built with. They used to read the
  palette later, which made the Sound pill invisible in the light theme.
- Built by Gemini, before `WidgetView` existed.
- Default size 3x2, smallest 2x2. On the Widgets board by default.
