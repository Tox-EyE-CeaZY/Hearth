# Framework

The parts every widget shares. Something goes here only when two or more
widgets use it; otherwise it belongs in that widget's folder. The rules for
using these parts are in [../README.md](../README.md).

## Files

| File | What it is |
| --- | --- |
| `WidgetView.cs` | Base class for views: lifetime (`While`, `Every`, `OnShown`, `OnHidden`), theme (`Themed`), `Post`, `SetBody`, and exceptions logged. |
| `WidgetChrome.cs` | The palette (`Primary`, `Secondary`, `Faint`, `Track`, `Accent`, `OnAccent`, `Hover`, `CardFill`, `CardEdge`), fonts, `Card`, `Text`, `Glyph`, `Frozen`, `Scope`. Also `GlyphButton` and `BarView`. |
| `WidgetParts.cs` | `Pressable` (clickable rows and tiles, right-click, drag out), `Chip`, `WidgetSwitch`, `WidgetHeader`, `InlineInput`, `WidgetKeyboard`, `WidgetMenu`, `WidgetFiles`, `WidgetLayout`, `WidgetFormat`. |
| `WidgetStore.cs` | `WidgetStore<T>`: a widget's JSON file in `%AppData%/Hearth`. Loading never throws; saving writes a temporary file then moves it. |
| `WidgetAlert.cs` | A corner banner with a chime (Windows' Alarm01.wav, looping for at most a minute). It stays on top without taking focus, and banners stack. |
| `SystemNotifications.cs` | Windows' own scheduled alarm notifications, run on a background queue that starts 8 s after launch. Its `Platform` class is the only code that touches WinRT types. |
| `WidgetServices.cs` | Calls every widget's `StartServices` and `StopServices`, and logs any that take over 50 ms. |

## Which part to use

| Need | Part |
| --- | --- |
| A title line with buttons | `WidgetHeader` (`Detail`, `AddAction`) |
| A clickable row or tile | `Pressable` (`ContextRequested`, `DragData`) |
| An icon button | `GlyphButton` |
| Presets or filters | `Chip` (`IsActive`) |
| On/off | `WidgetSwitch` |
| Progress | `BarView` |
| Typing a line | `InlineInput` (`Committed`) |
| Typing anything else | `WidgetKeyboard.Begin` and `End` |
| A menu | `WidgetMenu.Show(anchor, fill)` with `WidgetMenu.Item` |
| File and app icons, drag data, file menu items | `WidgetFiles` |
| A list that scrolls, or an empty state | `WidgetLayout.Scroller`, `WidgetLayout.Empty` |
| Buttons that show on hover | `WidgetLayout.RevealOnHover` |
| "5m ago", "in 2h", "09:59", "1.2 MB/s" | `WidgetFormat` |
| Saved data | `WidgetStore<T>` |
| Ringing at a given time | `SystemNotifications` plus `WidgetAlert` |

## Hosts

The desktop (`Views/DesktopSurface.xaml.cs`, `PlaceWidget`) and Start
(`Views/Start/StartParts.cs`, `StartWidgets.Create`) both build views inside
`WidgetChrome.Scope` and catch exceptions from `CreateView`. `App.xaml.cs`
calls `WidgetServices.Start` once the desktop is attached, and
`WidgetServices.Stop` in its teardown.
