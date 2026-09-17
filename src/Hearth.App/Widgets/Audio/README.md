# Audio (`audio`)

The default output and input devices: pick another from a menu, drag the
volume, mute.

## Files

| File | What it is |
| --- | --- |
| `AudioWidget.cs` | The widget, one row per direction, and the volume bar |
| `AudioService.cs` | CoreAudio: lists devices, follows volume and device changes, switches the default device |
| `AudioInterop.cs` | The CoreAudio COM declarations, including `IPolicyConfig` |

## Data

None.

## Background

`AudioService.Current` is created the first time it's used and lives until
Hearth exits. Its device and volume callbacks arrive on CoreAudio threads,
and the view `Dispatcher.InvokeAsync`s them.

## Platform

Windows has no public API for changing the default device. `IPolicyConfig`
is the undocumented interface that EarTrumpet and SoundSwitch also use; a
Windows update could break it.

## Notes

- Also used by `QuickToggles/` (the mute pill). If Audio is removed,
  Quick Toggles has to go with it or bring its own mute.
- Built by Gemini, before `WidgetView` existed.
- Default size 3x2, smallest 2x2. On the Widgets board by default.
