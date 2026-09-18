# Rules for AI assistants writing Hearth widgets

Read this whole file before changing anything. It is written for AI coding
assistants (Gemini, Copilot, Claude, others) and for people working quickly.
Following it keeps a new widget from breaking Hearth. The full design
reasoning is in [README.md](README.md); this file is the practical rulebook.

Hearth is a desktop replacement. **If Hearth crashes, the user's desktop
icons disappear.** That's why these rules are strict.

## 1. What you may touch

For a widget task, you may **only** create or edit files inside **one**
folder: `src/Hearth.App/Widgets/<YourWidget>/`.

You must **not** edit, move or delete, unless the user explicitly asks:

- anything in `Widgets/Framework/` or `Widgets/IWidget.cs` (every widget uses them),
- another widget's folder,
- anything outside `src/Hearth.App/Widgets/`: `Hosting/`, `Views/`,
  `Controls/`, `Services/`, `App.xaml*`, `Hearth.Core/`, the `.csproj` or
  `.sln` files, `Directory.Build.props`, the installer, and the scripts,
- `Widgets/_Template/` (copy it; don't change it).

You don't need to touch anything outside your folder: widgets are found
automatically, and there is no list to add yours to.

Never, unless the user asks: commit, push, create releases or installers,
delete user data in `%AppData%/Hearth`, install software, or run anything as
administrator.

## 2. Workflow, step by step

1. Pick a folder name: PascalCase, letters and digits, and **not** the name
   of a common type (`System`, `Timer`, `Task`, `Path`, `File`, `Window`,
   `Image`, `Color`, `Button`, `App`, `Log`). Good: `StockTicker`,
   `BatteryInfo`, `Pomodoro`.
2. Copy `Widgets/_Template/` to `Widgets/<Name>/`, then rename:
   - `ExampleWidget.cs.txt` to `<Name>Widget.cs`
   - `ExampleData.cs.txt` to `<Name>Data.cs` (delete it if the widget saves nothing)
   - `README.md.txt` to `README.md`
3. In every file, replace `Example` with `<Name>` and `example` with your
   lower-case id. The namespace must be exactly
   `namespace Hearth.App.Widgets.<Name>;`.
4. Choose a new `Id`: lower-case, for example `stock-ticker`. Check that no
   other widget uses it (search for `Id => "`).
5. Write the widget, following section 3.
6. Fill in `README.md` with what it does, its files, its data, its
   background work, the Windows APIs it uses, and notes.
7. Run the checker from the repository root:
   ```
   tools\check-widgets.cmd
   ```
   It must end with **PASSED**. Fix every ERROR, read every WARNING, and
   run it again.
8. Try it in the running app:
   ```
   start-hearth.cmd
   ```
   Then right-click the desktop, choose Widgets, and pick your widget.
   Check it works when added, resized small and large, and in the Start
   menu's Widgets tab. If Hearth closes or misbehaves, look at
   `%LocalAppData%\Hearth\hearth.log`.
9. Report to the user what you built, what you checked, and what you
   couldn't check.

**A new or changed widget only appears after a rebuild and restart.**
Widgets are compiled into Hearth.exe and found once at start-up. The
desktop's "Refresh" doesn't load new widgets; `start-hearth.cmd` does.

## 3. Hard rules

### The widget class

1. It is declared as `public sealed class <Name>Widget : IWidget`, is not
   nested in another class, and has no constructor parameters. Otherwise
   Hearth won't find it.
2. `Id` never changes once the widget has been used: layouts store it.
3. Set `Title`, `DefaultSpan`, `MinimumSpan`, `BoardHeight(bool wide)` and
   `Order` (use a number above 160).
4. `CreateView` does no slow work: no file reads over a few KB, no network,
   no waiting. It runs every time the desktop lays out.
5. `StartServices` and `StopServices` are only for work that must run while
   the widget is **not** shown (an alarm, for example). They must return
   within a few milliseconds.

### The view

6. The view class inherits from `WidgetView`.
7. Build the whole UI in the constructor, and call `SetBody(layout)` once.
8. Subscribe to events with `While(subscribe, unsubscribe)`. Never use a
   bare `+=` on a static event.
9. Use `Every(interval, action)` for timers. Never create a `DispatcherTimer`
   or `System.Threading.Timer` in a view.
10. Handle events from services and background threads with
    `Post(() => ...)`.
11. Load the initial data in `protected override void OnShown()`.
12. Code that builds UI outside the constructor, `Post`, `Every`, `While`
    and `OnShown` (for example a click handler that creates new rows) must
    start with `using var theme = Themed();`.
13. Multiply every pixel size you write by `S` (`new Thickness(8 * S)`,
    `Width = 40 * S`). Sizes passed to `WidgetChrome.Text` and
    `WidgetChrome.Glyph` are points: don't multiply those.
14. Colours come only from `WidgetChrome` (`Primary`, `Secondary`, `Faint`,
    `Track`, `Accent`, `OnAccent`, `Hover`). Never hard-code colours in a
    widget; it has to work in both dark and light themes. A control that
    changes colour later (on hover or when toggled) must store the brushes
    in fields when it's built.
15. Use the shared parts (section 4) instead of plain WPF `Button`,
    `CheckBox`, `ListBox` or `ToggleButton` controls.
16. Anything clickable must be a `Pressable`, `GlyphButton`, `Chip` or
    `WidgetSwitch`. A plain `Border` with a `MouseDown` handler starts
    dragging the whole widget instead.
17. Work at small sizes: react to `SizeChanged` by hiding optional parts.
    Never let content spill outside the widget.

### Data, input and the system

18. Saved data goes in a `WidgetStore<T>` in your own folder. Never use
    `App.Settings`, `HearthSettings`, the registry, or files outside
    `%AppData%/Hearth/<yourid>.json`.
19. Data classes have `{ get; set; }` properties with defaults. Later
    versions may only **add** properties.
20. Keep one static owner of the data (like `<Name>Data`) with a `Changed`
    event. Every placement of the widget shows the same data.
21. Text entry: `InlineInput`, or `WidgetKeyboard.Begin` and `End`. Never
    call `DesktopHost.BeginKeyboardInput`.
22. Longer forms go in their own `Window` class in your folder. Call
    `DialogChrome.Apply(this)`, set `Topmost = true`, and open it with
    `ShowDialog()`.
23. Open files, folders, websites and settings pages with
    `ShellLauncher.Open(target)`, and apps with `ShellLauncher.Launch(item)`.
    Never use `Process.Start`.
24. Never call `Environment.Exit`, `Application.Current.Shutdown`, or
    anything else that closes Hearth.
25. Never block the UI thread: no `Thread.Sleep`, `.Result`, `.Wait()` or
    synchronous network calls. Use `async`/`await`, and wrap the whole body
    of an `async void` handler in `try`/`catch`.
26. For network data, use one `private static readonly HttpClient`, a
    timeout, a `CancellationTokenSource` cancelled in `OnHidden`, and a
    cache kept in the data class (views are rebuilt often). Send only what
    the feature needs, and say in the README what is sent where.
27. Don't use `Windows.*` (WinRT) APIs on the UI thread or at start-up.
    The first use takes 2.6 seconds. If you must use them, call them from
    `Task.Run` in a separate class.
28. Deleting user files goes through `ShellLauncher.Recycle(path, WidgetFiles.OwnerOf(this))`.
29. Anything that might throw (files, network, COM) is wrapped in `try`/`catch`
    and logged with `Log.Write("<id>: ...")` (`using Hearth.Core.Diagnostics;`).
30. Declare Windows API calls (`DllImport`) as `private` members inside your
    own folder.

### Text in source files

31. Write icon glyphs as escapes: `"\uE710"`. Never paste the character
    itself; it's invisible and the checker rejects it.
32. Only use glyphs from the table in section 5, or ones you have checked
    exist. The checker fails on missing glyphs.
33. If a rule has to be broken for a good reason, end that line with
    `// check-widgets: allow - <reason>`, and tell the user.

## 4. The parts, with exact signatures

All of these are in `Widgets/Framework/` and can be used from any widget
folder without a `using`.

```csharp
// Base class
abstract class WidgetView : ContentControl
    WidgetView(WidgetContext context)
    WidgetContext Context            // .PixelSize, .Scale, .DarkTheme, .Bare
    double S                         // display scale: multiply pixel sizes by it
    bool IsShown
    void SetBody(UIElement body)     // wraps in the standard card
    IDisposable Themed()             // using var theme = Themed();
    void While(Action start, Action stop)
    DispatcherTimer Every(TimeSpan interval, Action tick)
    void Post(Action action)         // any thread -> view thread, dropped if the view is gone
    virtual void OnShown()
    virtual void OnHidden()

// Look
static class WidgetChrome
    Brush Primary, Secondary, Faint, Track, Accent, OnAccent, Hover, CardFill, CardEdge
    FontFamily Body, Display, Glyphs
    TextBlock Text(WidgetContext c, string text, double size, Brush? brush = null, FontWeight? weight = null, bool display = false)
    TextBlock Glyph(WidgetContext c, string glyph, double size, Brush? brush = null)
    T Frozen<T>(T freezable)

// Controls
GlyphButton(WidgetContext c, string glyph, double size, Action onClick, bool filled = false)   // .GlyphText, .ToolTip
Pressable(Action? onClick)       // .Child, .ContextRequested (Action), .DragData (Func<DataObject?>), .IdleBrush, .HoverBrush
Chip(WidgetContext c, string text, Action onClick, string? glyph = null, double size = 12)     // .Text, .IsActive
WidgetSwitch(WidgetContext c, bool isOn, Action<bool> changed)                                 // .IsOn
BarView()                        // .Value (0..1), .Fill
WidgetHeader(WidgetContext c, string glyph, string title)   // .Glyph, .Title, .Detail, AddAction(glyph, tip, onClick) -> GlyphButton
InlineInput(WidgetContext c, string placeholder, double size = 13)   // event Committed(string), .Text, BeginEditing()

// Helpers
WidgetKeyboard.Begin(Visual element) -> bool;  WidgetKeyboard.End(bool began)
WidgetMenu.Show(FrameworkElement anchor, Action<ContextMenu> fill)
WidgetMenu.Item(string header, Action action, bool? isChecked = null) -> MenuItem
WidgetFiles.ItemFor(string path) -> LauncherItem
WidgetFiles.Icon(WidgetContext c, LauncherItem item, double pixels) -> Image
WidgetFiles.DragData(string path) -> DataObject?
WidgetFiles.AddMenuItems(ContextMenu menu, string path)      // Open, Show in folder, Copy path
WidgetFiles.SetClipboardText(string text) -> bool
WidgetFiles.OwnerOf(Visual visual) -> IntPtr
WidgetLayout.Scroller(UIElement content) -> ScrollViewer
WidgetLayout.Empty(WidgetContext c, string glyph, string text) -> FrameworkElement
WidgetLayout.RevealOnHover(UIElement row, UIElement actions)
WidgetFormat.Ago(DateTime utc)   // "5m ago"
WidgetFormat.Until(TimeSpan t)   // "in 2h 5m"
WidgetFormat.Clock(TimeSpan t)   // "09:59"
WidgetFormat.Rate(double bytesPerSecond), WidgetFormat.Bytes(double bytes)

// Data
new WidgetStore<T>("<id>.json")  // .Load() -> T (never throws), .Save(T), .FilePath

// Attention at a time (see Timers/ and Alarms/ before using)
SystemNotifications.ScheduleAlarm(string group, string id16, DateTime at, string title, string body, bool snooze)
SystemNotifications.Cancel(string group);  SystemNotifications.Enabled
WidgetAlert.Show(string glyph, string title, string message, params (string Label, Action? Action)[] buttons)

// From the app (add the using)
Hearth.Core.Shell.ShellLauncher.Open(string target) / .Launch(LauncherItem item) / .Recycle(string path, IntPtr owner)
Hearth.Core.Diagnostics.Log.Write(string message) / .Error(string context, Exception ex)
Hearth.App.Controls.DialogChrome.Apply(Window window)       // dialog styling; Resources["Primary"] is the main-button style
```

WidgetContext.PixelSize is the size the host gave the widget when it was
built. Use `ActualWidth` and `ActualHeight` in `SizeChanged` for the current size.

## 5. Glyphs known to exist

Write each one as `"\uXXXX"`.

| Code | Meaning | Code | Meaning |
| --- | --- | --- | --- |
| E71D | list | E710 | add (+) |
| E711 | close (x) | E74D | delete (bin) |
| E713 | settings (gear) | E712 | more (...) |
| E72C | refresh | E777 | reset |
| E768 | play | E769 | pause |
| E916 | stopwatch | EA8F | bell |
| E823 | clock | E787 | calendar |
| E8B7 | folder | E838 | open folder |
| E8A5 | document | E7C3 | page |
| E7B8 | box | E896 | download |
| E898 | upload | E74A | arrow up |
| E74B | arrow down | E701 | Wi-Fi |
| E839 | ethernet | E774 | globe |
| E77F | clipboard | E8C8 | copy |
| E718 | pin | E77A | unpin |
| E840 | pinned | E81C | history |
| E734 | star | E8A7 | open in new window |
| EA3A | empty circle | EC61 | filled check circle |
| E73E | check mark | E80F | home |
| E924 | screen snip | E9D9 | activity |
| ECAD | flame | E767 | volume |
| E74F | mute | E720 | microphone |
| E72E | lock | E706 | brightness |
| E708 | moon | E8F4 | new folder |

Anything else: check it first. The checker reports glyphs that don't exist.

## 6. Build errors and what they mean

| Error | Fix |
| --- | --- |
| `The type or namespace name 'X' could not be found` | Add the `using` (`System.IO`, `System.Windows.Controls`, `Hearth.Core.Shell`, `Hearth.Core.Diagnostics`). WPF projects don't include `System.IO` automatically. |
| `'Path' is an ambiguous reference` | Write `System.IO.Path`. |
| `'Clipboard' is a namespace but is used like a type` | Write `System.Windows.Clipboard`, or better, `WidgetFiles.SetClipboardText`. |
| `'Timer' is ambiguous` | Use `Every(...)` in views, or `System.Threading.Timer` in full in services. |
| `does not implement interface member IWidget...` | Add the missing member exactly as in the template. |
| `'X' is inaccessible due to its protection level` | Framework types are `internal`; make your own types `internal` too, and keep the widget class `public`. |
| `An object reference is required` for `S` or `Context` | Those only exist inside the view class, not in the widget class or helpers. Pass the context in. |
| Hearth starts, but the widget isn't in the menu | The class isn't `public sealed class <Name>Widget : IWidget`, it's nested, it has constructor parameters, or its `Id` is taken (see the log). |
| Hearth closes when the widget is added | Look at `%LocalAppData%\Hearth\hearth.log`. Usually an exception in the constructor or in a click handler that isn't wrapped. |

## 7. Patterns to copy

| You're building... | Look at |
| --- | --- |
| A list with add, remove and check-off | `Tasks/` |
| Live numbers with a graph | `Network/` (a ref-counted sampler) |
| Numbers that refresh on a timer | `SystemInfo/` |
| Something from the internet | `Weather/` (static HttpClient, cache, setup dialog) |
| Settings for the widget | `Shelf/` (`IConfigurableWidget` and a dialog), `Weather/` |
| Files and icons, drag out | `Shelf/`, `RecentFiles/` |
| Launching apps and websites | `SpeedDial/` |
| Ringing at a time | `Alarms/`, `Timers/` |
| Start-up and shutdown work | `Alarms/AlarmWidget.StartServices` |
| Adapting to size | `Timers/TimerWidget.Arrange`, `SpeedDial/SpeedDialWidget` |

## 8. Before you say you're done

- [ ] Only files inside `Widgets/<Name>/` were created or changed.
- [ ] `tools\check-widgets.cmd` ends with **PASSED**.
- [ ] `README.md` in the folder is filled in.
- [ ] The widget was added on the desktop, resized to its smallest and a
      large size, and seen in the Start menu's Widgets tab (or you told the
      user you couldn't do this).
- [ ] Nothing was committed or pushed, unless the user asked.
