# Widgets

Everything about Hearth's widgets lives in this folder: the contract, the
shared parts, and one folder per widget. **A widget is a folder.** Paste a
folder in and rebuild, and the widget appears in every add-widget menu.
Delete the folder and rebuild, and it's gone. Nothing outside `Widgets/` needs
to change either way.

```
Widgets/
  README.md          this file: the standard
  IWidget.cs         the contract, and the registry that finds widgets
  GEMINI.md          the rulebook for AI assistants (and a quick checklist for people)
  Framework/         parts every widget shares (see Framework/README.md)
  _Template/         a copyable starter widget (.txt files, so they aren't compiled)
  Clock/  Media/  Calendar/  SystemInfo/  Notes/  Weather/
  Audio/  QuickToggles/  Tasks/  Timers/  Alarms/  Shelf/
  Network/  Clipboard/  RecentFiles/  SpeedDial/
```

> **AI assistants and quick edits:** follow [GEMINI.md](GEMINI.md), the
> step-by-step rulebook. Start new widgets from [_Template/](_Template/), and
> finish with `tools/check-widgets.cmd`, which must report **PASSED**.

Each widget folder has its own `README.md`: what the widget does, its files,
where its data is kept, what runs in the background, and what to watch out
for. Read that first when changing a widget.

## What goes in a widget's folder

Everything the widget needs and nothing else uses:

| Kind | Example |
| --- | --- |
| The widget class, `<Name>Widget.cs` | `Tasks/TasksWidget.cs` |
| Its view (usually a nested class in the widget file) | `TasksWidget.TasksView` |
| Services, models, saved data | `Tasks/TaskList.cs`, `Network/NetworkMonitor.cs` |
| Dialogs | `Alarms/AlarmEditWindow.cs` |
| P/Invoke and COM declarations only it uses | `Audio/AudioInterop.cs`, `Shelf/ShellCopy.cs`, `QuickToggles/RecycleBin.cs` |
| Its settings (its own store, **not** `HearthSettings`) | `Weather/WeatherConfig.cs` |
| `README.md` | every folder |

What stays outside the folder:

- `Widgets/Framework/`: code that **two or more** widgets use. Something
  only one widget needs stays in that widget's folder, even if it looks
  general.
- The app itself (`Hosting/`, `Views/`, `Controls/`, `Services/`) and
  `Hearth.Core` (icons, shell, launching, shared Win32 declarations). A widget
  may use these, but must not add code to them. If a widget needs a new
  Win32 call, declare it in the widget's folder, as a private `DllImport`.

Rules for the folder:

- Namespace `Hearth.App.Widgets.<Folder>`.
- Don't name a folder after a type the widgets use: `System`, `Timer`,
  `Task`, `Path`, `Window` and so on. The namespace would hide the type in
  every widget file. That's why the folders are `SystemInfo` and `Timers`.
  (`Clipboard` already hides `System.Windows.Clipboard`: write it in full.)
- Don't give a class the folder's name (the reader in `RecentFiles/` is
  `RecentList`). Inside the namespace, the name would mean the namespace.
- Types are `internal` unless the widget class needs them `public`.
- Saved files are named after the widget (`tasks.json`, `alarms.json`).

## Checklist for a new widget

1. Make `Widgets/<Name>/` with `<Name>Widget.cs` and `README.md`. Copy the
   template below and the README outline at the end.
2. Pick a new, permanent `Id`.
3. Build the view on `WidgetView` from the parts in `Framework/`.
4. Keep state that must outlive the view in a static service or a
   `WidgetStore` inside the folder.
5. Build with no warnings, run `start-hearth.cmd`, and try the widget on the
   desktop, on a Start page and on the Widgets board, in dark and light themes.
6. Add a row to the table at the end of this file.

There is no registration step. When Hearth starts, `WidgetRegistry` reads the
app's metadata and registers every class that:

- is top-level (not nested inside another class),
- isn't abstract,
- implements `IWidget` itself (not only through a base class), and
- has a parameterless constructor.

Discovery reads metadata instead of calling `Assembly.GetTypes()`, which
loads every type and pulled in the WinRT projection: 2.6 s at start-up, now
10 ms. The log shows `widgets: N found in N ms`.

Removing a folder is safe as long as no other folder uses it. Quick Toggles
uses `Audio/AudioService`; nothing else crosses folders.

## Template

```csharp
using System.Windows;
using System.Windows.Controls;

namespace Hearth.App.Widgets.Example;

/// <summary>One or two sentences: what it shows and what you can do with it.</summary>
public sealed class ExampleWidget : IWidget
{
    public string Id => "example";          // stored in layouts: never change it
    public string Title => "Example";       // shown in the add-widget menus
    public (int Columns, int Rows) DefaultSpan => (3, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 2);
    public double BoardHeight(bool wide) => wide ? 150 : 190;
    public int Order => 1000;

    public FrameworkElement CreateView(WidgetContext context) => new ExampleView(context);

    private sealed class ExampleView : WidgetView
    {
        private readonly TextBlock _value;

        public ExampleView(WidgetContext context) : base(context)
        {
            var header = new WidgetHeader(context, "\uE71D", "Example");
            header.AddAction("\uE72C", "Refresh", Refresh);

            _value = WidgetChrome.Text(context, "…", 18, weight: FontWeights.SemiBold, display: true);

            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(_value);
            SetBody(layout);

            // Runs only while the view is on screen.
            While(() => ExampleData.Changed += OnChanged, () => ExampleData.Changed -= OnChanged);
            Every(TimeSpan.FromMinutes(1), Refresh);
        }

        protected override void OnShown() => Refresh();

        // Service events can arrive on any thread; Post brings them back.
        private void OnChanged() => Post(Refresh);

        private void Refresh() => _value.Text = ExampleData.Value;
    }
}
```

## The contract: `IWidget`

| Member | Rule |
| --- | --- |
| `Id` | Lower-case and stable. Layouts store it as `widget:<id>`, so changing it removes the widget from everyone's layout. Two widgets with the same id: the second is left out and logged. |
| `Title` | Short and in title case. It appears in menus as "Remove *Title* widget". |
| `DefaultSpan` | Grid cells when added. A desktop cell is roughly 105 × 125 px. |
| `MinimumSpan` | The smallest size the view still reads well at. Resizing stops there. |
| `BoardHeight(wide)` | Content height on the Start menu's Widgets board, for a half-width card or (`wide`) a full-width one. |
| `OnBoardByDefault` | `false` for new widgets, so an update doesn't fill people's Widgets board. |
| `Order` | Position in menus and on the default board. The built-ins use 10 to 160; the default is 1000. |
| `CreateView(context)` | Called for **every** placement and **every** relayout. It must be cheap and must not start work that outlives the view. |
| `StartServices()` / `StopServices()` | Called once as Hearth starts and exits, for work that must run whether or not the widget is shown (Alarms, Timer). Keep start-up fast: anything over 50 ms is logged. |

A widget with settings also implements `IConfigurableWidget`:

- `NeedsSetup` is true only if the widget can show nothing useful until it's
  configured (Weather has no location yet, for example). The hosts then ask
  for setup as the widget is added, and cancelling setup cancels the add.
- `Configure()` shows an ordinary dialog and returns false on cancel. The
  desktop and board menus offer it as "*Title* settings...". The settings
  apply to the widget type, not to one placement.

## Views: `WidgetView`

Build every view on `WidgetView` (`Framework/WidgetView.cs`). It handles the
parts that are easy to get wrong:

- **Lifetime.** Hosts throw views away on every relayout. `While(start, stop)`
  and `Every(interval, tick)` run only while the view is loaded, and repeated
  Loaded or Unloaded events are handled. Never subscribe to a static event
  without a matching unsubscribe.
- **Theme.** `WidgetChrome`'s brushes come from the theme that is current
  while code runs, and the host opens the widget's theme only during the
  constructor. Code that runs through `Post`, `Every`, `While` or `OnShown`
  gets the theme again automatically. Any other handler that builds UI
  (a click handler, an `await` continuation) must wrap it in
  `using var theme = Themed();`. Controls that change colour later, such as
  on hover, must keep the brushes they were built with, as `Pressable`,
  `Chip` and `WidgetSwitch` do.
- **Threads.** Service events may arrive on any thread. Handle them with
  `Post(...)`, which also drops the work if the view has gone.
- **Failures.** Exceptions in these callbacks are logged instead of
  reaching the dispatcher, where they would close Hearth. The hosts also
  catch exceptions from `CreateView` and leave a gap. Don't lean on either;
  they are a safety net.
- **Pixels.** The desktop is laid out in physical pixels. Multiply every
  size by `S` (the display scale). `WidgetChrome.Text` and `Glyph` already
  take sizes in points and scale them.
- **Size.** One widget can be 2×1 on the desktop and 4×3 somewhere else.
  React to `SizeChanged`: hide secondary parts and switch between stacked
  and side-by-side layouts. See `Timers/TimerWidget.Arrange` and
  `SpeedDial/SpeedDialWidget`.
- `SetBody(element)` wraps the content in the standard card. On the Widgets
  board (`context.Bare`) the card is left out, because the board draws its own.

## Shared parts

The full list, with what each part is for, is in
[Framework/README.md](Framework/README.md). Use these parts instead of
writing your own, so every widget looks and behaves the same.

## Input

- **Mouse.** Anything clickable must mark `MouseLeftButtonDown` as handled,
  or the press starts dragging the whole widget. `Pressable`, `GlyphButton`,
  `Chip`, `WidgetSwitch` and text boxes already do this. A right-click the
  widget doesn't handle opens the widget's own menu (size, lock, settings,
  remove), so handle right-clicks only on items that have a menu of their own.
- **Keyboard.** The desktop window never takes keyboard focus unless asked.
  Use `InlineInput` for short entries. For anything else, call
  `WidgetKeyboard.Begin(this)` when editing starts and `End(...)` when it
  stops; it does nothing in the Start menu, which already has focus. Don't
  call `DesktopHost.BeginKeyboardInput` directly.
- **Dialogs.** Longer forms go in an ordinary `Window` in the widget's
  folder, styled with `DialogChrome.Apply(this)`, set to `Topmost`, and shown
  with `ShowDialog()`. The app drawer (`Views/AddAppsWindow`) can be reused as
  an app picker through its `IHome` interface; see `SpeedDial/SpeedDialList.AppPicker`.
- **Launching.** Use `ShellLauncher.Launch` for apps and `ShellLauncher.Open`
  for paths, URLs and `ms-settings:` or `ms-clock:` links, never
  `Process.Start`.

## Data

- `new WidgetStore<T>("name.json")` saves to `%AppData%/Hearth/name.json`.
  Loading never throws, and saving writes a temporary file then moves it into place.
- Keep one static owner per data set (for example `TaskList` or `AlarmClock`)
  that loads lazily, saves on every change and raises `Changed`. Every
  placement of the widget then shows the same data and stays in sync.
- Only ever add properties to `T`. Old files must still load. If data moves,
  move it across once (see `Weather/WeatherConfig.cs`, which took its
  settings out of `settings.json`).
- Anything sensitive stays in memory unless the user chooses to keep it.
  Clipboard history is kept only in memory, only pins are saved, and content
  that apps mark as private is skipped.

## Background work

- Work that only matters while the widget is visible goes in `While` or
  `Every`. Ref-count shared samplers: `NetworkMonitor.Subscribe` and
  `Unsubscribe`, `ShelfFolder.BeginWatching` and `EndWatching`.
- Work that must run whether or not the widget is visible goes in
  `StartServices` and `StopServices`.
- **WinRT is expensive to load.** The first method that mentions a
  `Windows.*` type loads the projection assembly (2.6 s measured), and other
  assembly loads, including the UI thread's, wait for it. Touch WinRT only
  off the UI thread, and keep those types inside a class that only background
  code calls. `Framework/SystemNotifications.Platform` is the pattern.

## Alerts and notifications

For something that must get attention at a given time:

1. Schedule it with Windows through
   `SystemNotifications.ScheduleAlarm(group, id, at, title, body, snooze)`.
   Windows rings it with the system alarm sound and its own Snooze and
   Dismiss buttons, even if Hearth isn't running. Use `Cancel(group)` and
   schedule again whenever the times change.
2. While Hearth runs, also watch the time yourself. When it comes, show a
   `WidgetAlert` only if `SystemNotifications.Enabled` is false, which means
   the user has Windows notifications turned off.

`Timers/` and `Alarms/` both do this. Link to the Windows app that does the
same job where there is one (`ms-clock:`).

## Glyphs

Icons are Segoe Fluent Icons code points. Write them in C# as `\u` escapes,
never as pasted characters. Some editing tools turn escapes into invisible
private-use characters, and a backslash followed by b or r into a control
character. After editing, this should print nothing:

```powershell
Get-ChildItem src -Recurse -Filter *.cs | Select-String '[\uE000-\uF8FF\x00-\x08]'
```

Check that a code point exists and looks right before using it: render it,
don't guess.

## Where widgets are shown

| Host | Notes |
| --- | --- |
| Desktop | Full interaction. Laid out in physical pixels. The widget can be resized, unlocked and dragged by any part that doesn't handle the press. |
| Start pages | Laid out at desktop density and scaled with a `Viewbox`. The widget's own controls don't respond there; the whole widget drags. |
| Start Widgets board | Interactive, `Bare` (no card of its own), `BoardHeight` tall, half or full width. |

## New widgets need a rebuild

Widgets are compiled into `Hearth.exe`, and `WidgetRegistry` looks for them
once, when Hearth starts. The desktop's **Refresh** re-reads desktop files
and apps only, so it won't pick up a new widget folder. Run
`start-hearth.cmd`, which builds and restarts Hearth. Loading widgets from
separate DLLs without a rebuild is planned (see `Claude/tablet-mode-plan.md`,
"Widget plugins").

## Checking your work

`tools/check-widgets.cmd` (or `tools/check-widgets.ps1`) must end with
**PASSED**. It fails on:

- changes outside `Widgets/`,
- invisible or mangled characters,
- a missing README or widget file,
- a folder or type named so that it hides another type,
- a namespace that doesn't match its folder,
- a widget Hearth wouldn't find, or a duplicate id,
- `Process.Start`, `Environment.Exit`, direct keyboard calls, or use of
  `HearthSettings`,
- glyphs that don't exist,
- any build warning or error.

It also warns about risky calls: blocking waits, `async void`, new
`HttpClient`s, deletions, the registry, WinRT, and raw timers in views.
`-All` scans every folder, and `-NoBuild` skips the build. A line that breaks
a rule on purpose ends with `// check-widgets: allow - <reason>`.

## Testing

- `dotnet build Hearth.sln -c Release` must report 0 warnings and 0 errors.
- `start-hearth.cmd` (or `start-hearth.ps1`) builds, quits the running
  Hearth cleanly, and starts the new build from `artifacts/run`.
  `-NoBuild` restarts without building, and `-Stop` only quits.
- `%LocalAppData%/Hearth/hearth.log` should show no errors and no slow
  widget services.
- Check both themes (the Quick Toggles Theme pill switches the desktop) and
  both the smallest and a large size.

## README outline for a widget folder

```markdown
# <Title> (`<id>`)

What it shows and what you can do with it, in two or three sentences.

## Files
| File | What it is |

## Data
Where it keeps things (file names), and what is kept only in memory.

## Background
What runs while it's shown, and what runs all the time (StartServices).

## Platform
Windows APIs it relies on, permissions, anything that behaves differently per machine.

## Notes
Traps, limits, and what hasn't been tested by hand.
```

## The widgets

| Folder | Id | Widget |
| --- | --- | --- |
| `Clock/` | `clock` | Time and date |
| `Media/` | `media` | Now playing (Windows media session) |
| `Calendar/` | `calendar` | Month view |
| `SystemInfo/` | `system` | CPU, memory, battery, disk |
| `Notes/` | `notes` | A sticky note |
| `Weather/` | `weather` | Open-Meteo forecast |
| `Audio/` | `audio` | Output and input devices, volume, mute |
| `QuickToggles/` | `toggles` | Mute, theme, snip, Night Light, Recycle Bin, lock |
| `Tasks/` | `tasks` | Checklist |
| `Timers/` | `timer` | Countdown with presets |
| `Alarms/` | `alarms` | Repeating alarms |
| `Shelf/` | `shelf` | File drop shelf, a stash or a real folder |
| `Network/` | `network` | Speeds, graph, connection, ping |
| `Clipboard/` | `clipboard` | Recent copied text and pins |
| `RecentFiles/` | `recent` | Windows' Recent list |
| `SpeedDial/` | `speeddial` | Apps, websites, files and folders |
