# Claude notes — Hearth

Running notes for anyone (including Claude) picking this up mid-stream. Update
this in the same pass as any investigation or change.

## What this is

Android-style home screen replacing the Windows desktop. Decided 2026-09-15.
Desktop-layer overlay, **not** a shell replacement — reasoning in
[../docs/desktop-layer.md](../docs/desktop-layer.md).

Name is easy to change; nothing outside the `Hearth.*` namespaces and the
solution file depends on it.

## Stack decision

C# / .NET 8 / WPF, zero external dependencies.

Chosen because ~80% of the work is Win32 and Shell COM interop (WorkerW
parenting, `IShellItemImageFactory`, `IDesktopWallpaper`, `IContextMenu` next),
and C# does that with the least friction. Tauri/web was considered — the user
works in JS — but the interop burden would just move to Rust, and the desktop
layer specifically (as opposed to an overlay launcher) is the variant where the
webview path is weakest.

TFM is `net8.0-windows10.0.19041.0` rather than plain `net8.0-windows`
specifically so WinRT projections are available for the media-controls widget
later.

`Hearth.Core` sets `UseWPF` and uses `BitmapSource`/`Geometry` directly. This is
deliberate — the icon pipeline's *output* is a WPF bitmap, and a parallel
imaging abstraction would cost a conversion per icon for nothing. Core still
knows nothing about hosting or app lifetime, so it stays embeddable (the user
mentioned it may fold into "Dungeon").

## Performance approach

"Fast and pretty" was the explicit brief. The strategy is **bake, don't
effect**:

- Icon drop shadows are rendered into the cached PNG, not applied as a live
  `DropShadowEffect`. A live effect per tile across ~200 tiles is the single
  most expensive thing a grid like this can do.
- `IconTile` is one `FrameworkElement` drawing in `OnRender`, not a
  Grid+Image+TextBlock composition. Saves several visual-tree nodes and a
  measure/arrange pass per tile.
- `FormattedText` for labels is cached, since `OnRender` runs on every hover.
- Shape geometries are frozen and cached per (shape, size).
- `AllowsTransparency` is **off** — it would force the window onto WPF's
  layered-window path and drop hardware rendering across the whole surface.
  This is why Hearth paints the wallpaper itself.

If any of these get "simplified" later, the grid will get visibly slower.

## Traps already hit

- **`System.Threading.Lock` is .NET 9.** Used `object` in `IconShape`.
- **Shell `HBITMAP`s are premultiplied.** Un-premultiply before analysis or
  dominant-colour picking skews to black on soft edges.
- **Some 32bpp icons have an all-zero alpha channel.** Treat uniformly-zero as
  opaque, or they render as nothing.
- **`DrawingVisual`/`RenderTargetBitmap` are `DispatcherObject`s** whose ctors
  create a Dispatcher for the current thread. Never render on the thread pool —
  it leaks a Dispatcher per worker. `IconService` uses one long-lived STA
  thread.
- **`SHELLDLL_DefView` does not always live under `Progman`.** Search for it;
  don't assume. This is the usual reason WorkerW code works on one machine only.
- **WPF projects exclude `System.IO` from implicit usings.** The WindowsDesktop
  SDK drops it (and `System.Net.Http`) because `Path` collides with
  `System.Windows.Shapes.Path`. Every file touching `File`/`Directory`/`Path`
  needs an explicit `using System.IO;` — and if you add one to a file that also
  imports `System.Windows.Shapes`, expect to have to qualify `Path`.
- **`Hearth.Core.Interop.Shell` vs the `Hearth.Core.Shell` namespace.** A class
  and a sibling namespace with the same name resolve to the namespace from
  inside it. Renamed the class to `ShellNative`; don't reintroduce the clash.
- **`Win32`/`ShellNative` are `internal`**, shared with Hearth.App via
  `InternalsVisibleTo` in Hearth.Core.csproj. That attribute names **`Hearth`**
  (the App's `AssemblyName`), not `Hearth.App`.
- **Restarting the wrong exe.** `dotnet build Hearth.sln` writes to
  `bin/x64/<Config>/`, but building the project alone writes to
  `bin/<Config>/`. Gemini kept relaunching the stale `bin/Release` copy, so
  "restart" never picked up changes. Use `start-hearth.cmd`, which always
  runs the solution build from `artifacts/run`.
- **A killed Hearth leaves the shell icons hidden** (TearDown never runs).
  `Hearth.exe --quit` exits cleanly; the start script uses it and only kills
  as a last resort, then shows `SHELLDLL_DefView` again itself.
- **PowerShell scripts are blocked on this machine** (execution policy).
  Run them with `powershell -ExecutionPolicy Bypass -File ...`; the `.cmd`
  wrapper does this.
- **The first WinRT type costs 2.6 s** (projection assembly load, measured in a
  fresh process), and other assembly loads, including the UI thread's, wait on
  it. Calling the toast API at start-up delayed the desktop by 2.7 s. Keep
  WinRT types inside a class only background code calls
  (`SystemNotifications.Platform`), and start that work after the desktop is up.
- **The tool pipeline rewrites escapes in written files:** `\uXXXX` becomes a
  literal private-use character, `\b` a backspace (0x08) and `\r` a carriage
  return, including inside Python heredocs. Build backslashes with `chr(92)`
  and scan afterwards (`src/Hearth.App/Widgets/README.md`, Glyphs section, has the check).
- **WPF's `Clipboard.SetDataObject` has no retry overload** (that one is
  WinForms). `WidgetFiles.SetClipboardText` retries by hand.
- **A namespace named `Clipboard`** (`Hearth.App.Widgets.Clipboard`) hides
  `System.Windows.Clipboard` everywhere under `Hearth.App.Widgets`; qualify it.
- **`string.GetHashCode` changes every run** in .NET, so it can't pick a
  stable colour. Speed Dial uses FNV-1a on the host name.
- **Windows notifications are switched off for all apps on this machine**
  (`ToastEnabled = 0`; the notifier reports `DisabledForUser`). Scheduling
  still succeeds; nothing is shown. Hearth falls back to its own banner.

## Verified on real hardware (2026-09-15 / 16)

Windows 11 Home, **build 26200** UBR 9445. Two 1920x1080 displays, vertically
offset (primary VSCD22B at 0,0; secondary AUOE0B2 panel at 1920,914). Virtual
screen (0,0)-(3840,1994). Intel integrated graphics.

**Working:** top-level host at the bottom of the Z-order, wallpaper painted by
Hearth, adaptive icons, labels, context menus, launching, drag including across
displays, installed-apps catalog (216 apps), widgets, layout persistence.

### The big one: child windows of the desktop never render here

See [../docs/desktop-layer.md](../docs/desktop-layer.md). Hearth was first built
as a child of the desktop (WorkerW, then Progman). Input worked and nothing was
ever drawn. Isolated with a plain GDI red window: child of Progman = input, no
pixels; child of WorkerW = neither; top-level at HWND_BOTTOM = both. Now a
top-level window pinned with WS_EX_NOACTIVATE + WM_WINDOWPOSCHANGING.

**Lesson for anyone debugging this again:** reach for a minimal non-WPF probe
early. Several real-but-irrelevant WPF fixes went in before the GDI test found
the actual cause.

### Other bugs found by running it

- **WorkerW is created by 0x052C**, not present from boot — and Windows sends
  0x052C itself on wallpaper transitions. It lacks WS_CLIPSIBLINGS. Hearth
  never sends 0x052C.
- **SetParent return / GetParent / PrintWindow all misreport** — see the doc.
  Verify with GetAncestor, CopyFromScreen + WindowFromPoint.
- **WPF implicit usings omit System.IO** (see traps above) — bit twice more.
- **The old DesktopWindow constructor set `Width = 1`** — a WPF Window sized 1x1 then resized via
  SetWindowPos lays out in 1x1. Moot now (HwndSource), but a real trap.
- **Shell COM on the thread pool** — AppsFolder enumeration must run STA
  (`StaTask`), and `GetDisplayName(FileSysPath)` on packaged apps throws
  `ArgumentException` (PreserveSig=false), not COMException.
- **IShellItemImageFactory returns bottom-up DIBs for some icons.** Row order
  comes from `DIBSECTION.dsBmih.biHeight` (BITMAP.bmHeight is always positive).
  CacheVersion bumped to 2 when this was fixed.
- **FormattedText with TextAlignment.Center + MaxTextWidth is already centred** —
  draw at x = 0.
- **Submenus need a MenuItem ControlTemplate**; a Background setter never reaches
  the submenu popup.
- **Context menus in a non-activating window** never hear about clicks in other
  apps; SetForegroundWindow on the popup makes WPF close it instantly. Watch
  GetForegroundWindow instead.
- **Drag ghosts orphaned on lost capture** looked like duplicated icons.
  Handled in OnLostMouseCapture; mouse-up must claim the drop *before*
  releasing capture, or the handler cancels every drop.
- **Layouts from older builds held items on both displays.** HomeLayout.Reconcile
  now dedupes, primary first.
- **Display order:** IDesktopWallpaper lists the secondary first here; surfaces
  are sorted primary-first.

### Added 2026-09-16

- **Folders** — `HomeLayout.Groups` (home level, so a folder can move between
  displays); items inside a folder have no placement; the folder's tile is
  placed as `group:{id}`. `HomeLayout.Reconcile` dissolves folders with fewer
  than two items. A throwaway harness exercised merge, dissolve,
  deleted-item, dedupe, overflow and save/load: all passed. The drag gestures
  themselves have NOT been exercised by automation (that would mean moving
  the user's mouse); they need a hands-on test.
- **Widget drag** — widgets are wrapped in a transparent Border so the whole
  footprint is grabbable; `_widgetViews` maps the wrapper back to its
  placement id. A widget drop only happens if its whole span is free.
- **Win+D** — verified with a synthetic Win+D: Hearth lands directly above
  Progman and owns the screen, then returns to the bottom when Win+D is
  pressed again. `HWND_TOP` silently fails from a background process; insert
  after the window above Progman instead.
- **Probing** — a reusable window probe lives in the session scratchpad
  (`probe.cs`); PowerShell `Add-Type` types don't survive between tool calls.

### Widgets, 2026-09-16 (later)

- **Folder merge never happened:** `ReferenceEquals(FindGroupByPlacement(x),
  drag.FromFolder)` is `ReferenceEquals(null, null)` == true for a plain
  icon-on-icon drop. Guard with `drag.FromFolder is not null`. Confirmed fixed
  by the user making a folder.
- **Unlocked widgets:** `GridPlacement.Unlocked` + `Free` (physical px relative
  to the display). Column/Row/Span then mean "cells covered", recomputed every
  relayout (`UpdateCoveredCells`); a span of 0 covers nothing. Reconcile never
  removes unlocked placements for being off-grid. `EvictFromUnder` moves icons
  to the nearest free cell after any widget move/resize/unlock.
- `DragSession.IsWidget` is an explicit flag now — an unlocked widget can have
  a 1x1 or 0x0 span, so span can't identify widgets.
- **Widget buttons** (`GlyphButton`) mark MouseLeftButtonDown handled, which
  is what stops a click on them starting a widget drag. Text boxes do this
  natively.
- **Media widget** uses `GlobalSystemMediaTransportControlsSessionManager`;
  its events arrive on background threads.
- **Tooling trap:** the tool pipeline turns backslash-u escapes in written
  files and heredocs into the literal characters. Glyph constants ended up as
  invisible private-use characters. Fixed by building the backslash with
  chr(92) in the fixing script. Check with `cat -A` after editing glyphs.

### Hide / delete / shell menu / weather, 2026-09-16

- **Hidden items** live in `HomeLayout.Hidden`; they are filtered out of the
  live set, so Reconcile removes them from the grid and folders.
- **Delete** uses `SHFileOperation` with `FOF_ALLOWUNDO` (Recycle Bin, and the
  user's own confirmation setting). Only items directly in a desktop folder
  are deletable; apps get "Uninstall..." (`ms-settings:appsfeatures`).
- **Shell context menu** (`Hosting/ShellContextMenu.cs`): items via
  `BindToHandler(BHID_SFUIObject, IID_IContextMenu)`; background via
  `SHGetDesktopFolder` + `CreateViewObject`. `TrackPopupMenuEx` needs its
  owner in the foreground, so it borrows `BeginKeyboardInput`.
  `WM_INITMENUPOPUP` / `DRAWITEM` / `MEASUREITEM` / `MENUCHAR` are forwarded
  from `DesktopHost.WndProc` to IContextMenu2/3 so "Open with" and "Send to"
  fill in. NOT exercised by automation — it blocks in a modal menu loop.
- **Clock resize:** text was sized from height only (so shrinking sideways
  looked like nothing happened) and the formatted text was cached across
  resizes. Now it fits width and height, rebuilds on resize, and clips.
- **Weather:** Open-Meteo forecast and geocoding (both URL formats checked
  against the live API). Device location via `Geolocator.RequestAccessAsync`;
  a null result sends the user to `ms-settings:privacy-location`. Forecasts
  are cached for 15 minutes, because widget views are rebuilt on every
  relayout. The widget was rendered with a temporary London config, which was
  then removed so the user still gets the setup prompt.
- **More pipeline mangling:** a single-quoted backslash-zero char literal
  written by a script became real NUL bytes (grep said "Binary file
  matches"). Use `new string((char)0, 2)`, and prefer scripts saved as files
  over inline heredocs.

### Padded small icons (Steam), 2026-09-16

Steam shortcuts are .url files whose IconFile is a 32px .ico. When asked for
256px, IShellItemImageFactory returns that icon centred on a 256px canvas
**inside a drawn square frame**: an outer ring at alpha ~38 and a more opaque
ring two pixels in. The frame made the content bounds the whole canvas, so no
trimming happened and the icon stayed a dot. A higher alpha threshold does not
help because of the inner ring. `IconAnalysis.FrameInset` detects the frame
(near-full lines on all four sides within 8px, with nearly empty rows just
inside) and analysis runs inside it. Full-bleed icons with a painted border
(Lethal Company) are solid inside and are not affected. Checked with a
throwaway harness (scratchpad `icondiag`) on Hollow Knight, Don't Starve,
Spore, Terraria, Call of Duty and Lethal Company. CacheVersion is now 3.

There is no larger square icon for these games on disk: Steam's
librarycache holds a 32px icon plus logo, header and capsule art only.

### Per-icon styles, 2026-09-16

The user's point: icons fail in different ways, so there has to be a per-icon
choice, and cropping beats a small square in the dead centre.

- `IconFit` { Auto, Fill, FillZoomed (x1.25), Fit }, stored per item in
  `HomeLayout.IconFits` and offered as right-click -> Icon style. It is part
  of the icon cache key. CacheVersion is now 4.
- `IsFullBleed` no longer requires the square to fill most of the canvas, so
  small solid-square artwork is cropped to the mask instead of floated.
- **Backplate detection** (`IconAnalysis.DetectPlate`): solid through the
  middle, reaching all four edges, and reaching at least 0.62 along each
  diagonal. Auto zooms plate icons until the plate covers the mask:
  zoom = max over corners of (mask reach / plate reach) x 1.03, capped at
  1.45; anything above the cap falls back to Fit. Mask reach is measured from
  the real geometry.
- Measured on the user's icons: circle-plate icons (Battle.net, OBS, YouTube
  Music, Don't Starve, Starbound) report reach ~0.72; Spore 0.67; solid
  squares 1.0. Glyphs without a plate (Hollow Knight, Terraria, DELTARUNE)
  stay Fit, and those are the ones the per-icon override exists for.
- The rendered style sheet was NOT seen: the conversation reached its image
  limit. The decisions above come from the measurements only.

### Arrangements per display set, 2026-09-16

**Bug the user hit:** with the laptop panel and the monitor both at
1920x1080, disconnecting one display moved every item from the missing
display onto the remaining one as a 1x1 placement. `HomeLayout.Reconcile`
created `new GridPlacement { ItemId = id }` for anything "not shown", widgets
included, so every widget was squashed into one cell. It also deleted the
items from the missing display's layout ("so they cannot appear twice"), so
reconnecting it restored nothing. The saved layout afterwards had all 13
placements on VSCD22B and none on AUOE0B2.

**Fix (layout file version 2):**
- `HomeLayout.Arrangements`: one `DisplayArrangement` for each set of connected
  displays, keyed by `KeyFor` = device id + pixel size + grid columns x rows for
  each display, primary first. Icon size, gap, labels and style change the grid, so
  each of those settings gets its own arrangement, and changing a setting back
  restores the exact layout. `Monitors` is now the active arrangement's list
  (`[JsonIgnore]`), and everything interactive still goes through it.
- `Activate` switches arrangements. An unseen set is derived from the current one
  (`Derive`, which works on a clone): each display keeps its own layout, **except**
  that a new primary takes the old primary's layout when the old primary has
  gone (the user: "move the primary's over"). Leftover layouts are merged in with
  `PlaceCarried`, which keeps the item's own cell if it is free, otherwise the
  first free run of its **span**. An unlocked widget that has to move is snapped
  to the grid (`Unlocked=false`, `Free=null`, same as re-locking). A widget
  that fits nowhere is left off rather than squashed.
- Reconcile: deleted, hidden and grouped items, and folder dissolves, now apply to
  **every** arrangement. Items that are live but not shown are placed from a
  template: a placement that fell off the grid, or the most recent other
  arrangement holding it, so widgets keep their size there too.
- The App lists widget ids from `AllPlacements`, and `RemoveWidget` uses
  `RemoveEverywhere`. The App calls `Activate` **before** the covered-cells
  pass and saves when the arrangement switched (Reconcile's own Activate call
  then returns false).
- At most 16 arrangements are kept (least recently used dropped), because
  transient states during a plug event create junk ones.
- `WM_DISPLAYCHANGE` is debounced (600 ms) in `DesktopHost`, and
  `WallpaperService` skips monitors reporting a zero-size rect.
- A version 1 `Monitors` list loads as the `unordered` arrangement. Its order
  says nothing about which display is primary, so `Derive` skips the
  primary-follows rule for it.
- Harness: scratchpad `arrtest` runs the user's real layout through lid shut,
  external unplugged (secondary becomes primary), reconnects, a grid-size
  change and back, delete/add while on one display, widget removal and a
  save/load round trip: 22/22 pass. Backup of the pre-change file:
  `%APPDATA%/Hearth/layout.before-arrangements.json`. The rebuilt Release
  build migrated the live file correctly.
- **Not yet tested by physically unplugging a display**; that needs the user.
- Trap again: the tool pipeline turned a double backslash in a heredoc'd C#
  verbatim string into a single one. Build the device-path prefix at runtime with `new string((char)92, 2)`.

### Add apps drawer, 2026-09-16

The user asked to make adding apps easier. Before this the only choices were
"Show installed apps" (all 216 at once), or desktop shortcuts and files dragged
in from Explorer.

- **`HomeLayout.Pinned`**: AUMIDs added one by one. `RefreshItemsAsync` reads
  AppsFolder when `IncludeInstalledApps` is on **or** Pinned is non-empty, and
  adds only the pinned apps when the setting is off. The full catalog is cached
  in `_installedApps` for the drawer.
- **`AddAppsWindow`** (Views): an ordinary window, because typing needs focus,
  like the weather setup. It talks to the surface through the nested
  `IHome` interface, which `DesktopSurface.AddApps.cs` implements explicitly. Features:
  - Search ranks a name prefix above a word prefix or initials ("ym" finds
    YouTube Music), and those above a substring match.
  - Enter or Space toggles the selected row; Up and Down from the search box
    move the selection.
  - Rows are virtualized and load their icon on `Loaded`. Icons use
    `ToRenderOptions(1.0)` so they share cache entries with the grid.
- **Background menu -> "Add apps..."** (first item) opens the drawer. The
  right-clicked cell is `_addAnchor`, and each added app takes
  `NearestFreeCell` from it, placed before Reconcile runs.
- **"On home"** means the app id is in `_items` and not hidden, **or** there is
  a visible desktop Shortcut with the same display name (Windows often puts one
  there). Removing un-pins, then hides whatever still stands for the app.
- The item menu shows "Remove from home" instead of "Hide from desktop" for
  pinned apps while "Show installed apps" is off.
- **Caret/placeholder misalignment (user report):** the TextBox template bound
  `Padding` as the ScrollViewer's margin, but TextBox already applies Padding
  to its text view, so the padding counted twice and the caret sat about 18px in
  while the hint sat at 10px. Now the template has no margin binding, padding is
  10,8, and the hint is at 13px, 14pt. Measured with `GetRectFromCharacterIndex(0)`:
  the caret and the hint are both at (13, 9). This fixes the weather search box too.
- `Controls/DialogChrome.cs` now holds the shared dialog styles (moved from
  `WeatherSetupWindow`), plus a thin dark scrollbar.
- Verified:
  - A harness (scratchpad `drawertest`) compiles the window source with a
    stub `App` and a fake `IHome`, and renders it with the real catalog and
    icon pipeline. The list, search, initials match and Enter toggle all work.
  - The live app, with Calculator written into `Pinned`, logged "1 added by
    hand" and placed a 9th tile. The entry was removed again afterwards.
- **Not exercised by automation:** the menu click, clicking rows in the live
  drawer, and placement at the anchor (these need the mouse).
- The log from the earlier session shows the user did an unplug test at 12:39:
  Hearth switched to the AUOE0B2-only arrangement and back to the dual one.

### Auto-hide taskbar would not come up, 2026-09-16

The user had to press Win to reach an auto-hidden taskbar.

- **Cause:** with the desktop in front, `SHQueryUserNotificationState` returned
  **QUNS_BUSY (2)**, "a full-screen app is running". To the shell, a borderless
  window covering every display looks like a game.
- **Fix:** the shell's own opt-out, the **`NonRudeHWND`** window property.
  `DesktopHost` creates the window hidden, sets the property, then calls
  `ShowWindow(SW_SHOWNA)`.
- **Timing matters:** measured with a probe (scratchpad `quns`), setting the
  property on a window that is already visible changes nothing until the
  window is hidden and shown again.
- **Result:** after a clean start, the state reads 5 with the desktop in
  front, and the user confirmed the taskbar now comes up on hover.
- **Testing:** synthetic hover tests were unreliable; trust the state value.

### Notification badges, 2026-09-16

- **Reader:** `Core/Notifications/BadgeService` reads
  `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db` read-only,
  through System32's `winsqlite3.dll` (`Interop/WinSqlite.cs`), so there is
  still no NuGet dependency. `UserNotificationListener` would need package
  identity.
- **What counts:** the app's own `<badge value=.../>` if it set one (a number,
  a glyph shown as a dot, or "none"); otherwise the count of its toasts.
- **Refresh:** a FileSystemWatcher on `wpndatabase.db*` (debounced) plus a
  30-second poll.
- **Drawing:** `IconTile.Badge` draws a red pill (99+) or a dot. Folders show
  the sum of their contents.
- **Tile to app:** `AppIdentity.Resolve` (STA). An app is its own AUMID. A
  .lnk uses its `System.AppUserModel.ID` property, or else the installed app
  with the same display name. 41 of 42 items matched.
- **Untested:** this user has notifications off
  (`HKCU\...\PushNotifications\ToastEnabled = 0`), so no toasts are stored
  and toast counting could not be tested. Only app-set badges show
  (Phone Link: 227).

### Jump lists, 2026-09-16

- **Where tasks come from:**
  `%APPDATA%\Microsoft\Windows\Recent\CustomDestinations\{hash}.customDestinations-ms`.
  **The hash is CRC-64 (Jones polynomial, reflected, 0x92C64265D32139A4,
  initial value all ones, no final xor) of the upper-cased AUMID as
  UTF-16LE.** Found by brute force: it names 19 of the files on this machine.
- **File format:**
  - Header: version 2, then the category count.
  - Category types: 0 custom (name plus links), 1 known (1 frequent, 2
    recent), 2 tasks.
  - Each entry is the ShellLink CLSID followed by a persisted link. **A link's
    length is the stream position after `IPersistStream.Load`.** The
    0xBABFFBAB footer is optional.
- **Recent and frequent** come from `IApplicationDocumentLists`. Entries that
  are bare URIs are skipped.
- **Packaged apps:**
  - The title is AppUserModel pid 27, and the arguments are pid 20.
  - They launch through `IApplicationActivationManager`.
  - Their category names (`@{...?ms-resource:///...}`) do not resolve; the
    fallback turns the key into a name ("HistoryGroupTitle" becomes
    "History").
- **Launching ordinary entries:** the link is written to
  `%LOCALAPPDATA%\Hearth\jumplist\{sha1}.lnk` and ShellExecuted. Verified:
  Vivaldi's "New private window" link came back with `--incognito`.
- **Shared code:** `Controls/JumpListMenu` serves both the desktop and
  Start. Clicking an entry has not been tested by automation.

### Start menu, 2026-09-16

The user's decisions:
- It opens with the Win key and the Start button.
- Badges go on app icons (not the tray).
- App menus show taskbar-style jump lists.
- The layout is 4:3 landscape with Pages, All apps, Categories, Widgets and
  search.
- Later feedback, now applied:
  - A blurred backdrop that shows what is behind the menu.
  - The Windows theme.
  - A gap above the taskbar.
  - The taskbar held up while open.
  - The real account picture.
  - Click outside to close.
  - Pages that are Start's own (not a mirror of the desktop).
  - Widgets that scale properly.

**Trigger (`Hosting/StartTrigger`)**
- WH_KEYBOARD_LL and WH_MOUSE_LL run on their own thread; a slow hook gets
  silently removed. The hooks are always installed. `Intercept` (the setting)
  decides whether Win and Start are taken over, and `ReportPresses` reports
  mouse presses while the menu is open.
- **Win key:** the physical Win-up is swallowed and
  `vkE8 down, vkE8 up, Win up` is injected instead (AutoHotkey's mask-key
  trick). Injected input is ignored. Any key, click or wheel event while Win
  is held counts as a chord.
- **Start button:** Win11 keeps a hidden legacy `Start` child window in each
  Shell_TrayWnd and Shell_SecondaryTrayWnd whose rect equals the XAML
  button's (454..485 x 1078..1110 here), so clicks are compared against it.
  **PowerShell trap:** in PowerShell P/Invoke, `$null` for a string argument
  becomes "", so `FindWindowEx(..., "Start", $null)` falsely returned 0. It is
  a direct child (checked from C#).
- **Click outside:** Hearth's desktop never takes focus, so clicking it does
  not deactivate the menu. Presses outside the menu, other than on Hearth's
  own popups, hide it (`OnMousePressedAt`).
- **`Hearth.exe --start [pages|all|categories|widgets]`** toggles the running
  copy (named event plus the `start-request` file). This is how the menu is
  tested.

**Window (`Views/Start/*`)**
- **Size and placement:** 880x660 DIPs, scaled down as a whole to fit. It is
  placed with SetWindowPos in physical pixels **before** Show. The bottom sits
  12 px above the taskbar: an auto-hidden taskbar reserves no work area, so
  its height comes from its window rect. `Window.Opacity` does nothing
  without AllowsTransparency.
- **Holding an auto-hidden taskbar up:** the menu is made an *owned* window
  of that monitor's taskbar (`GWLP_HWNDPARENT`), then `SetForegroundWindow`
  goes to the taskbar and immediately back to the menu. Probes in scratchpad
  `barprobe`:
  - Ownership alone, or ABM_ACTIVATE: nothing.
  - Foregrounding the taskbar without ownership: it hides within a second.
  - Both together, even with no delay: it stays up.
  - **Release ownership before Hide**, or Windows activates the taskbar and
    it stays up. `_suppressHide` covers the Deactivated event from the
    hand-off.
- **Backdrop:** `StartBackdrop.Blur` is the default. Mica only ever shows the
  wallpaper, and Mica and Acrylic both go flat grey when transparency effects
  are off (**Energy Saver does that; this user has it on**). Blur BitBlts the
  screen area plus a 60 DIP margin *before* showing, at quarter size, then
  applies BlurEffect 48 with BitmapCache and the theme tint on top. The
  system options remain in "..." -> Background.
- **Theme:** `StartStyle` palettes follow `SystemUsesLightTheme` (this user is
  on light) and the UISettings accent. The controller rebuilds the window
  when the theme changed. Context menus use `StartStyle.NewMenu`, which
  carries themed styles, because App.xaml's menu styles are dark-only and
  StaticResource-bound. **`IconTile` looks up LabelBrush through
  Application.Current**, so window resources never reached it; it now has
  `LabelBrush`, `FolderPadBrush` and `FolderPadEdge` properties.
- **Account picture:** the newest `*-Image192.*` in
  `C:\Users\Public\AccountPictures\{SID}`.
- **Pages (`StartLayout.Pages`, `%APPDATA%\Hearth\start.json`):**
  - A list of `MonitorLayout` on a fixed 7x4 grid, plus Start's own
    `Groups`.
  - **Seeded once from the desktop** (`DesktopSurface.GetHomePages`): one
    page per display, in reading order, widget spans clamped to 2..4 x 2..3,
    folders copied. After that the pages are independent.
  - A first attempt that mirrored the desktop scaled into the page was
    illegible (a 17-column desktop at a scale of about 0.4). Do not go back
    to that.
  - Each page is a rounded card with a faint dot at each cell corner.
  - Drag (WPF DragDrop, format `HearthStartPlacement`) moves items to free
    cells. Dropping an app on an app makes a folder; dropping on a folder
    adds to it. Hovering an arrow turns the page, and past the last page it
    creates one.
  - Widgets have a hover handle (drag, menu: size presets, settings,
    remove).
  - The empty-page menu offers "Add widget", "New page" and "Delete page".
    The "+" after the dots adds a page. Trailing empty pages are dropped.
  - The folder overlay is a near-opaque panel with a renamable title.
  - Item menus have "Add to Start" / "Remove from Start" / "Remove from
    folder", plus "Add to desktop".
- **Two-finger touchpad swipe:** precision touchpads send `WM_MOUSEHWHEEL`,
  which WPF does not surface, so an HwndSource hook
  (`OnHorizontalScroll`) handles it. The page follows the fingers at
  0.6 px per scroll unit. 140 ms after the last message it turns or snaps
  back, using the same 70 px threshold as touch. Inertia is ignored for
  450 ms after a turn. The direction is unverified: positive delta is taken
  as "scroll right", meaning the next page. The user needs to try it.
- **Page turning is a carousel.** While a swipe is in progress (touch,
  mouse or touchpad), the neighbouring page is built and slides alongside
  (`_neighbour`, 24 px apart). A turn carries on from where the fingers left
  both pages, with a duration that depends on the distance left.
  - The user reported the touchpad version was slow: the next page came
    from a page-width away, and only after the touchpad's momentum ended.
  - Now a touchpad swipe commits at a third of the page width and swallows
    the rest of that gesture. The gesture ends after a 90 ms pause in
    messages.
- **WPF trap (bit twice):** a Timeline's `Completed` handler must be
  attached **before** `BeginAnimation`, because the handlers are copied into
  the clock when it starts.
  - Attached afterwards, the folder close handler never ran. The faded
    panel stayed Visible and caught clicks: the user found the folder
    "still there", and only the bottom of the folder tile was clickable,
    because the shrunken invisible card sat over it.
  - Old pages were also never removed after turns.
  - Fixed: `Slide(..., completed)` and the folder animation attach first.
    The folder panel is not hit-testable while closing.
- **Crash: adding an app from the edit picker.**
  `UIElementCollection[index] = newRow` throws "Specified index is already
  in use". Fixed with RemoveAt plus Insert.
  - The crash details were lost because `Log.Start` truncated the log on
    the next launch. The previous run is now kept as
    `hearth.previous.log`, and `Log.Error` writes `ex.ToString()`.
  - **`Hearth.exe --start selftest`** runs the edit flows in place (edit,
    new page, picker add x2, add widget, turn pages, delete page), logs each
    step, then restores the pages. It reproduced the crash, and now passes
    every step. Use it before asking the user to click through edit flows.
- **Opening speed in real use:** 0.5 to 0.7 s, against about 0.15 s in tests.
  The taskbar hand-off (`SetForegroundWindow` to a hidden taskbar) waits on
  Explorer. The menu is now shown and focused first, and the hand-off runs
  at ContextIdle, under `_suppressHide`.
- **Folder open animation:** the panel scales and moves from the folder
  tile's rect, the background dim fades in, and the tile hides while the
  folder is open. Closing reverses it. A generation counter guards
  overlapping animations, and a folder refreshed while open does not replay
  the animation.
- **Edit mode (`StartMenuWindow.Edit.cs`):** Edit/Done is in the page footer
  (or "Edit pages" in the page menu, or `--start edit`).
  - Items wiggle (a RotateTransform with a random phase). Each gets a remove
    button; widgets also get a size button, and a widget's own controls are
    not hit-testable, so the whole widget drags.
  - Only empty cells are outlined, dashed.
  - The toolbar has Apps (a searchable picker overlay that adds to the
    current page or removes from Start), Widget, Page and Delete page.
  - Apps do not launch while editing; folders still open.
  - `OnWindowText` no longer steals typing from other TextBoxes (the folder
    name, the picker). Left/Right only turn pages when no other TextBox has
    focus.
- **Dropping (the user's call):** dropping an item on a same-sized item
  **swaps** them; the other item goes to where the dragged one was, on its
  page. While hovering, the displaced tile slides to that spot as a preview
  (or fades, if it would go to another page), and the hint is dashed.
  **Folders are made from the right-click menu** ("Move to folder" -> "New
  folder" or an existing one), not by dropping, because on a full page
  dropping made folders by accident. A new folder can start with one item.
- **Widget themes:** `WidgetChrome` brushes now come from a palette set by
  `WidgetChrome.Scope(context)` (a [ThreadStatic] value; dark outside a
  scope, so the desktop is unchanged).
  - Code that builds later has to capture the palette or open a scope
    itself: `CalendarView.Build`, `WeatherView.RefreshDayCount`,
    `GlyphButton`'s hover, `BarView`'s track and fill, and `SystemWidget.Row`'s
    accent.
  - `WidgetContext.Bare` leaves out the widget's own card.
- **Widgets tab (`StartMenuWindow.Widgets.cs`):** the user said the loose
  flow looked wonky. It is now a board of titled cards in two columns: each
  card goes into the shorter column, and "full width" widgets sit on top.
  Each widget has its own height (`BoardHeight`). The "..." menu offers move
  up/down, full width (`StartLayout.WideWidgets`), settings and remove.
  `RunDialog` handles topmost and suppressing hide around dialogs.
- **Speed (the user said it took about a second to appear):** timings are
  logged per open (`start menu: shown ... ms: ...` and `first frame idle`).
  - Before: the cold first open settled at about 670 ms plus window
    creation; a warm open at about 140 ms.
  - The controller now prewarms 3 s after start-up: it builds the window,
    loads apps, and shows it once at (-32000,-32000), not activated, with
    every view visible for one layout pass. That takes about 340 ms.
  - The first real open then settled at 231 ms. The open animation is now
    150 ms with a 10 px slide.
- **Widgets in Start (`StartWidgets.Create`):** the widget is laid out at
  desktop density (about 105x125 per cell) on a canvas with the slot's
  proportions, then scaled in a Viewbox. This was the user's suggestion; it
  handles any difference in size.
- **Other views:**
  - All apps: A-Z sections with a letter-jump overlay.
  - Categories: `AppCategorizer` guesses, plus the user's own choices in
    `StartLayout.Categories`.
  - Search: apps (`AppSearch.Rank`, shared with the drawer), Settings pages,
    files, and web search (`WebSearchUrl`).
  - **File search uses the Windows Search index through late-bound ADO**
    (`Search.CollatorDSO`); enumerating a `search-ms:` folder returned 0
    items.
- `DesktopHost.OnForegroundChanged` ignores Hearth's own windows.
  `Services/InstalledApps` (`App.Apps`) is the shared AppsFolder cache.
- **Verified by `--start` plus screenshots:**
  - The views render.
  - Light theme, blur and the account picture.
  - The 12 px gap: menu bottom 1036, taskbar top 1048.
  - The taskbar held up while open.
  - The log shows the menu stays open (`start menu: shown ... active=True`).
  - The user confirmed blur, theme and picture in their own screenshots.
- **NOT verified by automation:** the physical Win key and Start button,
  touch, drag and drop, the folder overlay, power actions, the search view,
  and clicking a jump-list entry.
- **Never inject keystrokes to test UI on the user's machine.** A SendKeys
  test typed "maven" into the user's message box, because the menu had
  already closed. Keep `--start` tests (which take focus) to a minimum.

### Widget suite and widget standard, 2026-09-16 (evening)

The user asked Gemini for a set of widgets (its plan: Audio and Quick Toggles
first, then Tasks, Timer/Alarm, Drop Shelf, Network, Clipboard, Recent Files
and Speed Dial). Gemini finished the first two but couldn't restart Hearth.
Claude then fixed the restart, built the rest, wrote a widget standard and a
start script. The user asked that the timer and alarms use Windows' built-in
features.

- **Start script:** `start-hearth.cmd` / `start-hearth.ps1` (README, Running).
  `Hearth.exe --quit` is new (`StartMenuController.QuitRequest`, sent over
  the same request event as `--start`). If nothing is running, `--quit` starts
  nothing. Verified: the old build (without `--quit`) was killed and its icons
  restored; later builds quit cleanly.
- **Standard:** now [../src/Hearth.App/Widgets/README.md](../src/Hearth.App/Widgets/README.md) (see the next section for the folder layout). Framework in
  `Widgets/Framework/`:
  - `WidgetView`: lifetime (`While`, `Every`), theme (`Themed`, and callbacks
    re-enter the theme), `Post`, and exceptions logged.
  - `WidgetParts`: `Pressable`, `Chip`, `WidgetSwitch`, `WidgetHeader`
    (`TitleRow` shortens the title before the detail), `InlineInput`,
    `WidgetKeyboard`, `WidgetMenu`, `WidgetFiles`, `WidgetLayout`,
    `WidgetFormat`.
  - `WidgetStore<T>`, `WidgetAlert`, and `WidgetServices` (start/stop,
    logs any service over 50 ms).
- **IWidget** gained `BoardHeight(wide)` and `OnBoardByDefault` (the Start
  board's switch and its hard-coded default list are gone). Existing widgets
  keep their heights; Notes stays off by default, and every new widget is off.
- **Desktop host** now builds widgets inside `WidgetChrome.Scope` (so the
  Quick Toggles Theme pill really re-themes desktop widgets; the default is
  still dark) and catches `CreateView` exceptions.
- **Fixed in Gemini's work:** Quick Toggles pills read theme brushes after the
  scope had closed (light theme showed an invisible Sound pill); Snip used
  the Unpin glyph (now E924). Notes called `BeginKeyboardInput` even inside
  the Start menu, which pulled focus to the desktop; it uses `WidgetKeyboard` now.
- **Timer and Alarms (`Widgets/Timers/`)** use Windows' scheduled alarm
  notifications (`Services/SystemNotifications`: scenario alarm, looping
  alarm sound, system Snooze/Dismiss). They are registered under the
  AppUserModelID `Hearth.Desktop` in HKCU. Alarms hand Windows the next 7
  days of occurrences and re-sync on every ring and change. While Hearth
  runs it also watches the times itself (every 5 s, and after sleep or a
  clock change), and shows `WidgetAlert` (Alarm01.wav, looping for at most a
  minute) only when notifications are off. Both widgets link to the Clock
  app (`ms-clock:`). The timer is saved (`timer.json`) and survives restarts.
- **Shelf:** a stash in `%LocalAppData%/Hearth/Shelf` or a chosen folder
  (`shelf.json`). Drops are copied, or moved with Shift, through
  `ShellLauncher.CopyInto` (SHFileOperation on an STA thread: Explorer's
  progress window, renamed on a clash, undo). Items drag out as a FileDrop.
  Remove and Delete go to the Recycle Bin.
- **Network:** `Core/Network/NetworkMonitor` sums the interface counters once
  a second (ref-counted), keeps 60 samples of history, names the connection
  from the WinRT profile (no location permission needed), and pings 1.1.1.1
  every 5 s.
- **Clipboard:** a message-only `HwndSource` with
  `AddClipboardFormatListener`, started the first time the widget is shown.
  Recent text is kept in memory only (30 entries); pins are saved to
  `clipboard-pins.json`. Content marked private is skipped (the
  ExcludeClipboardContentFromMonitorProcessing,
  CanIncludeInClipboardHistory = 0 and Clipboard Viewer Ignore formats).
- **Recent Files:** `Core/Shell/RecentFiles` resolves `shell:recent` .lnk
  files. Filters for All, Files and Folders; "Remove from Recent" deletes the .lnk.
- **Speed Dial:** apps (through the `AddAppsWindow` drawer, which now takes a
  title and intro), websites (a lettered tile clipped to the user's icon
  shape), files and folders. Short placements show one row that scrolls
  sideways.
- **Verified:** Release and Debug build with 0 warnings. An offscreen render
  harness (scratchpad; reflection, in-memory sample data, nothing saved)
  rendered every new widget in both themes and at several sizes, plus the
  dialogs and the banner. Problems it found and that are now fixed: toggles
  in the light theme, cramped Network and Speed Dial at 2x1, unstable
  monogram colours, and `ParseTime` reading "7.30" as a date. Start-up is
  back to about 30 ms from the start trigger to the surface.
- **NOT verified by hand:** a timer or alarm actually ringing (either the
  Windows notification or the banner and its sound), Snooze, drag and drop
  in and out of the Shelf, clipboard capture in the live app, the add
  dialogs, and the Speed Dial app picker.

### Widgets as self-contained folders, 2026-09-16 (night)

The user asked for the standard to put everything a widget needs in one
folder under `Widgets/`, with the information about each widget right there,
so a widget can be cut and pasted as a single folder. The core app stays
where it is.

- The standard moved to `src/Hearth.App/Widgets/README.md`;
  `docs/widgets.md` now points there. Every folder has a `README.md`
  (files, data, background work, platform, notes); `Framework/README.md`
  lists the shared parts.
- Folders: Clock, Media, Calendar, SystemInfo (not `System`: that would
  hide .NET's `System`), Notes, Weather, Audio, QuickToggles, Tasks, Timers,
  Alarms, Shelf, Network, Clipboard, RecentFiles, SpeedDial, plus Framework.
  Namespaces are `Hearth.App.Widgets.<Folder>`.
- Moved into the widget folders:
  - Audio: `AudioService` and `AudioInterop`, from `Hearth.Core`.
  - Network: `NetworkMonitor`, from Core.
  - RecentFiles: Core's `Shell/RecentFiles`, now `RecentList`, because a
    class named after its namespace can't be referred to inside it.
  - Clipboard: the listener P/Invokes, from Core's `Win32`.
  - QuickToggles: the Recycle Bin P/Invokes (`RecycleBin.cs`), from Core's
    `Win32`.
  - Shelf: `ShellCopy`, from `ShellLauncher.CopyInto`.
  - Framework: `SystemNotifications`, from `App/Services`.
  - `WidgetChrome.cs` moved to `Framework/`.
  - `LockWorkStation` stays in Core, because the Start menu's power menu uses it.
- **Discovery:** `WidgetRegistry` no longer has a list. It reads the
  assembly's metadata (`System.Reflection.Metadata`) for top-level classes
  that implement `IWidget` directly, then loads only those. Calling
  `Assembly.GetTypes()` first took **2557 ms** (it loaded the WinRT
  projection); metadata takes 10 ms. `IWidget.Order` keeps the old menu
  order (10 to 160; the default is 1000).
- **Service hooks:** `IWidget.StartServices()` and `StopServices()`
  replaced the hard-coded list in `WidgetServices` (Timer, Alarms, Clipboard).
- **Weather settings:** these moved out of `settings.json` into
  `weather.json` (`Weather/WeatherConfig.cs`, `WeatherSettings`).
  `HearthSettings.Weather` is now `LegacyWeather`, a `JsonElement` under the
  same JSON name, and it's moved across once. Verified on the user's machine
  (log: `weather: settings moved to weather.json`). The user's old
  settings.json was backed up to the session scratchpad first.
- **Verified:** Debug and Release build with 0 warnings. With `Tasks/` and
  `SpeedDial/` removed, the solution still builds with 0 errors. The harness
  found all 16 widgets in order and rendered 15 of them in both themes
  (Weather was left out, so the harness couldn't touch its settings).
  Hearth restarted cleanly, with 16 ms from the start trigger to the
  surface. The only cross-folder dependency is Quick Toggles on
  `Audio/AudioService`.

### Installer, 2026-09-16 (night)

- **Inno Setup 6.7.3** was installed per-user with winget
  (`%LocalAppData%/Programs/Inno Setup 6/ISCC.exe`). No other installer tool
  was on the machine.
- `build-installer.ps1` / `.cmd`: `dotnet publish` self-contained win-x64
  with ReadyToRun (**57.3 MB** setup, 471 files), then ISCC with the
  version, publish dir and icon passed as defines.
- `installer/Hearth.iss`:
  - Installs per-user, with no admin prompt, to `{autopf}/Hearth` (which is
    `%LocalAppData%/Programs/Hearth`). The folder picker is hidden because
    install and uninstall wipe the folder.
  - Optional HKCU Run entry ("Start Hearth when I sign in").
  - Start menu entries for Hearth, Quit Hearth (`--quit`) and Uninstall.
  - The uninstaller removes the `AppUserModelId/Hearth.Desktop` key, and
    asks (unless silent) whether to delete `%AppData%/Hearth` and
    `%LocalAppData%/Hearth`.
- `installer/quit-hearth.ps1` is shared by the installer (run from `{tmp}`
  before copying files), the uninstaller (from `{app}/tools`) and
  `start-hearth.ps1`. It sends `--quit`, waits 10 s, and only then kills
  Hearth and shows the desktop icons again.
- **Icon:** `assets/Hearth.ico` (orange gradient tile, Segoe Fluent flame
  ECAD), drawn by `installer/make-icon.ps1`, set as the exe's
  `ApplicationIcon` and the setup icon.
- **Version** bumped to 0.2.0. The build script stamps
  `InformationalVersion` as 0.2.0.yyyymmddhhmm; the file version is 0.2.0.0.
- **Verified on this machine:** a silent install (`/TASKS=""`, so no Run
  entry) quit the dev Hearth first, installed 471 files and the three
  shortcuts, and the installed Hearth started (cold first start: discovery
  259 ms). A silent uninstall quit it and removed the folder, shortcuts,
  AUMID key and uninstall entry, and kept user data. The dev Hearth was then
  restarted from `artifacts/run`.
- **Trap:** in PowerShell `Add-Type` P/Invokes, passing `$null` to a
  `string` parameter sends `""`, so `FindWindowEx(..., $null)` matches only
  untitled windows. Pass null from inside the C# instead, as
  `quit-hearth.ps1` does.
- **Not tried:** the interactive wizard pages, the "delete my data" prompt,
  upgrading over an older install, and the sign-in startup entry.

### AI rulebook, widget checker, template; tablet-mode plan, 2026-09-16 (late)

The user wants a less-capable AI (Gemini) to be able to add widgets without
breaking anything, and asked for a full tablet-mode plan saved in the notes.

- **`src/Hearth.App/Widgets/GEMINI.md`**: the rulebook for AI assistants
  writing widgets. One-folder-only rule, step-by-step workflow, ~33 hard
  rules, exact framework signatures, a known-good glyph table, a build-error
  table, and a done checklist. Root `GEMINI.md` and `AGENTS.md` point to it
  (and to notes.md for non-widget work); `Widgets/README.md` links it.
- **`src/Hearth.App/Widgets/_Template/`**: a copyable starter
  (`ExampleWidget.cs.txt`, `ExampleData.cs.txt`, `README.md.txt`; `.txt` so
  they don't compile). Verified: copied to `Example/`, it builds and passes
  the checker, then removed.
- **`tools/check-widgets.ps1` / `.cmd`**: validates widget work. ERRORs
  (fail): changes outside `Widgets/`, invisible/mangled/lone-CR characters,
  missing README or *Widget.cs, folder or type named to shadow a type,
  namespace/folder mismatch, an undiscoverable widget, duplicate/bad Id,
  `Process.Start`/`Environment.Exit`/keyboard/`HearthSettings` calls,
  missing glyphs, any build warning/error. WARNINGs (advisory): Framework
  or IWidget.cs edits, blocking waits, `async void`, new `HttpClient`, deletes,
  registry, WinRT, raw timers in views. `// check-widgets: allow - <reason>`
  exempts a line. `-All`, `-NoBuild`, `-AllowOutside`.
  - Running it flagged real issues in the existing widgets, now fixed:
    QuickToggles/Weather used `Process.Start` (-> `ShellLauncher.Open`);
    the rest were marked `allow` where deliberate (Weather one-time settings
    move, RecentFiles .lnk delete, Media/Weather WinRT usings).
- **Answer to "will Refresh notice new widgets?": no.** Widgets are compiled
  in and discovered once at start-up (metadata scan). The desktop's Refresh
  re-reads files and apps only. A rebuild + restart (`start-hearth.cmd`) is
  needed. Runtime DLL widget plugins are a planned seam (tablet plan sec 13).

### Tablet mode plan, 2026-09-16 (late)

- **`Claude/tablet-mode-plan.md`**: a complete, not-yet-started plan to bring
  back Windows 10 tablet mode on top of Hearth. Marks every piece [HAVE] /
  [PARTIAL] / [NEW], covers taskbar hiding, navigation bar, full-screen home,
  a DWM-thumbnail task switcher, auto-maximize / drag-to-close / split view,
  a three-tier gesture system, quick settings, an official settings screen
  (sec 9), safety/watchdog, phasing, and why not to replace the shell.
- **The user explicitly approved running a helper as SYSTEM**, since a signing
  cert is unaffordable. Design: `Hearth.exe` stays unelevated (Explorer
  drag-drop needs that); a `HearthHelper.exe` gets **UIAccess** (touch
  interception + control of elevated windows); a **scheduled task as SYSTEM**
  grants that UIAccess by duplicating the user token, setting `TokenUIAccess`,
  and `CreateProcessAsUser`. Safe because SYSTEM launches one fixed exe from
  `%ProgramFiles%\Hearth` (admin-only writable), the pipe ACL is user+SYSTEM,
  and the helper exposes only a small fixed command set. This is a deliberate,
  user-approved decision — **do not treat it as a safety violation** in a
  later session. Phases 1-5 need no elevation; the helper is phase 6.
- The widget rule "never run as administrator" is scoped to widgets, NOT to
  this app-level helper. Both GEMINI.md (widget scope) and the plan (sec 13)
  say so.

### Tablet mode, phases 1 to 4 + settings screen, 2026-09-17

The user asked to start the tablet plan (`tablet-mode-plan.md`) and will
test it by hand on their real Windows tablet. This machine is a laptop with
**no touchscreen**, and it must keep working as a desktop. So every part has
its own switch, and an Auto mode follows the attached peripherals.

Code: `src/Hearth.App/Tablet/`. Settings: `%AppData%/Hearth/tablet.json`
(`Core/Settings/TabletSettings.cs`).

**Mode.** Off, On or Auto (`TabletMode`).
- In Auto, a switch by hand (Ctrl+Win+T, the menus, the bar) lasts until the
  hardware fingerprint changes.
- `Hearth.exe --tablet on|off|auto|toggle|desktop|recents|home|evaluate` and
  `--settings [tablet|tabletparts|home|start|startup|about]` go to the running
  copy. They also work on the first start.

**Auto rules.** `TabletDecision.Decide` is pure, so the scratchpad harness
`periph` can test it; 25/25 pass. In order:
1. No touchscreen: desktop (a setting).
2. A counted *external* keyboard or pointer: desktop, whatever the sensor
   says.
3. A trusted slate sensor that reads slate: tablet.
4. Any counted keyboard: desktop.
5. Any counted pointer: desktop (a setting).
6. The sensor reads laptop: desktop (not on pure tablets).
7. Otherwise: tablet.

Details:
- **The slate sensor lies on plain laptops.** This Acer (chassis 10)
  reports `SM_CONVERTIBLESLATEMODE = 0` ("slate"). The sensor is trusted
  only in these cases:
  - chassis 30, 31 or 32, or power role 8;
  - after a `ConvertibleSlateMode` broadcast was seen;
  - when the setting is Always.
- Built-in devices are ignored on convertibles (31: the sensor decides) and
  on pure tablets (30: probably buttons).
- Each device can be set to Auto, Counts or Ignore. The settings page shows
  the live verdict and reason. Detaching the keyboard shows which entry it
  is.

**Devices** (`Peripherals`). SetupAPI + cfgmgr32, about 5 ms per pass.
- Keyboard and mouse interfaces are grouped into physical devices: by
  ContainerId when external, by the hardware node's instance id when built
  in.
- Virtual devices are dropped: ROOT, SWD, VHF, TERMINPUT and VMBUS nodes.
  **Only check up to the device's own hardware node.** Every tree passes
  through `ROOT\ACPI_HAL`, so the first version dropped everything.
- A mouse collection whose HID siblings are a touch screen or pen
  (UP:000D U:0004/0002/0001) is dropped. A U:0005 sibling makes it a
  touchpad.
- Built-in keyboards under button drivers or names are dropped
  (hidinterrupt, HidEventFilter, SurfaceButton, "button", "airplane"...).
- **Gaming mice declare boot-keyboard interfaces.** The Razer here does, on
  MI_01 and MI_02. A USB device whose **MI_00** is a boot mouse (Prot_02)
  counts only as a mouse.
- This machine lists four: Apple Keyboard and Razer mouse (external), the
  built-in keyboard (ACPI\1025171E) and the built-in touchpad (ELAN0524).
- The chassis type comes from raw SMBIOS (`GetSystemFirmwareTable` RSMB,
  type 3).

**Watcher** (`PeripheralWatcher`).
- A hidden top-level HwndSource. Message-only windows get no
  WM_SETTINGCHANGE.
- Listens for device notifications (keyboard, mouse and HID interface
  classes), WM_DISPLAYCHANGE and the hotkey (RegisterHotKey). A 10 s poll is
  the fallback.
- Auto waits `SwitchDelayMs` (1.2 s) for the hardware to settle. It can ask
  first (`SwitchPrompt`, no chime).

**Taskbar** (`TaskbarControl`).
- Before changing anything, it records the old auto-hide state in
  `%LocalAppData%/Hearth/shell-state.json`. Then it turns auto-hide on and
  hides Shell_TrayWnd and the secondary bars with `SW_HIDE`. A 1.5 s timer
  hides them again.
- **ABM_SETSTATE blocks for about 2 s** while Explorer reflows windows. Hide
  and restore therefore run on a background queue, in order, and exit
  flushes the queue.
- Tested both ways: auto-hide already on (left on) and off (turned off
  again).
- `StartTrigger` ignores a hidden taskbar's Start button, whose rect stays
  valid.

**Crash safety.**
- `Hearth.exe --watchdog <pid>` runs while the taskbar is hidden. If the
  parent dies with the state still saying hidden, it restores the taskbar
  and shows DefView.
- **Tested by force-killing Hearth in tablet mode:** the taskbar and icons
  came back, and the watchdog exited.
- `--restore-shell` does the same by hand, and `quit-hearth.ps1` runs it
  after a kill. The next start also undoes a leftover state.
- These helper runs happen before the single-instance mutex. `Log.Write`
  does nothing in them.

**Navigation bar** (`NavigationBar`). A registered app bar that never takes
focus.
- Buttons: Start and keyboard; Back, Home and Recents; quick settings
  (Win+A), notifications (Win+N) and the clock.
- Each button can be switched off, and the edge and size are settings. The
  bar's right-click menu has settings, leave tablet mode, and hide the bar.
- **ABM_SETPOS and ABM_REMOVE also block for 1 to 2 s.** The bar is placed
  at once and claims or releases its strip from Task.Run.
- ABN_POSCHANGED echoes: six docks in a row were seen. Docking is debounced
  (250 ms) and skipped when the rect is unchanged.
- Back sends Alt+Left, or a per-program key. Arrow keys need
  KEYEVENTF_EXTENDEDKEY.
- Home sends Win+D. When the desktop is already in front, it toggles Start.

**Recents** (`TaskSwitcher`).
- Covers the work area over a blurred capture, with DWM thumbnails. DWM
  draws above WPF, so the title row sits above the preview.
- Window list: Alt+Tab-like rules (`WindowTools.IsSwitchable`).
- Tap a card to switch to it. Swipe it up or press X to close it
  (WM_SYSCOMMAND SC_CLOSE). Close all closes every card.
- Rendering was checked with 4 real windows. **Swiping and closing were not
  exercised**: no input injection on the user's session.

**Auto-maximize** (`WindowManager`).
- EVENT_OBJECT_SHOW and FOREGROUND hooks; acts 120 ms later with
  SC_MAXIMIZE, once per window per session.
- Skips owned, tool, topmost, dialog and non-resizable windows, and the
  exception list.
- When tablet mode ends, windows Hearth maximized are restored (a setting).
  A kill leaves them maximized.

**Edge gestures** (`EdgeGestures`).
- Layered, alpha-1 strips cover the middle 80% of each edge that has an
  action, except the bar's edge.
- `MouseEventArgs.StylusDevice` tells touch and pen from the mouse.
- Mouse hover makes a strip WS_EX_TRANSPARENT until the pointer is 40 px
  away. A tap that is not a swipe is replayed as a click underneath.
- The top edge is drag to close (`DragToClose`): the foreground window's
  thumbnail follows the finger. Let go in the bottom 20% to close it, or at
  a side to snap it (`SplitView.Place` allows for the invisible frame).
- **Not tested:** there is no touchscreen here, and no input injection. It
  is on the hands-on list.

**Full-screen Start and the spread.** The user asked mid-session to "put
pages side by side".
- In tablet mode, Start covers the work area. Its content fills that area at
  UI scale min(w/1440, h/900), clamped to 1..1.4.
- The search box (760 wide) and the tabs are centred. Other tabs are capped
  at 1100 wide.
- The Pages view shows a spread (`StartMenuWindow.Spread.cs`): the most
  pages that fit at 580 DIPs wide or more, in columns and rows. Pages keep
  the floating page's shape, **760:470** (760:420 clipped two-line labels).
- Paging moves a whole spread (`SpreadStart`, `HasNextSpread`). Drops and
  the page menu use the canvas `Tag` page index. The pages are rebuilt when
  the presentation flips.
- Verified at 1920x1040: the user's 2 pages sit side by side.

**Settings screen** (`Views/SettingsScreen`).
- The namespace is named so that `Settings` identifiers in `Views` don't
  bind to it.
- Pages: Tablet mode (live status, reason, devices), Tablet features, Home,
  Start, Startup and About. Startup uses the HKCU Run value "Hearth", the
  same one the installer writes.
- Opened from the desktop menu (Settings..., plus a Tablet mode submenu),
  Start's "..." and the navigation bar.
- `DesktopSurface.Settings.cs` exposes icon hiding and the installed-apps
  switch. `StartMenuController.InvalidateWindow` rebuilds Start after a
  backdrop change.

**Start-up speed: a regression, found and fixed.**
- `StartStyle`'s static palette read the accent colour through WinRT.
  Warming WinRT on a background thread still delayed the surface by 2.7 s,
  because the loader waits.
- `SystemTheme.Accent` now reads
  `HKCU\...\Explorer\Accent\AccentPalette` (RGBA entries: [1] AccentLight2,
  [4] AccentDark1, checked equal to UISettings here). WinRT is only the
  fallback.
- The first tablet decision waits for ApplicationIdle. The surface is up
  0.28 s after the hooks, as before.

**Glyph trap.** 0xE782 (`StartStyle.Windows`, unused) is in neither Segoe
Fluent Icons nor MDL2. Start uses 0xF0E2 instead. Check new glyphs against
the typeface's `CharacterToGlyphMap` (scratchpad `glyphs.ps1`).

**Hands-on tests left for the user, on the tablet:**
- Auto switching as the keyboard or cover comes and goes, and which device
  entries appear.
- Whether the slate sensor is used.
- Edge swipes and drag to close, and the mouse passing through at the
  edges.
- Swiping cards away in Recents.
- Back in real apps.
- The keyboard button (ITipInvocation, falling back to TabTip.exe).
- Win+A and Win+N from the bar.
- The spread in portrait (it should stack pages).
- 150-200% scaling.

**Not started:**
- The UIAccess/SYSTEM helper (plan section 2, phase 6).
- Raw touch (6.2).
- Turning off Windows' edge swipes (6.4).
- A notification panel.
- Rotation lock.
- A Quick Toggles pill.
- Split view beyond snapping from drag to close.

### Launching single-instance apps, Home, bar gap, 2026-09-17

User reports (the third came while testing on their Surface Pro 7+, a 3:2 screen at 200%):

1. "Some apps refuse to launch from Hearth", e.g. Antigravity IDE.
2. Home in tablet mode should go to the desktop, not to the app menu.
3. There is a gap between the bottom of the screen and the navigation bar.

**Launching (`Services/AppLauncher`).**
- Antigravity is in Start as the app id `Google.AntigravityIDE`.
  `ShellExecuteEx("shell:AppsFolder\Google.AntigravityIDE")` does start a
  new "Antigravity IDE" process (seen 140 ms after the launch, by polling
  process ids).
- It is single-instance (Electron): the new process hands over to the
  running copy and exits. The running copy is not allowed to take the
  foreground, because Hearth, which launched it, is a background window, so
  nothing appears.
- `AllowSetForegroundWindow(ASFW_ANY)` with the injected-key trick did
  **not** help (tested).
- Fix: `AppLauncher.Launch` records the process ids, launches, then polls
  for new processes for 1.8 s (`QueryFullProcessImageName`; helpers such as
  conhost and explorer are skipped).
  - If a window of the new program comes to the front, it is done.
  - Otherwise, the program's newest existing window is activated with
    `WindowTools.Activate`.
  - Browsers and other multi-window apps show their new window, so the
    fallback does nothing for them.
- Verified: with Hearth settings in front, `Hearth.exe --launch
  Google.AntigravityIDE` brought the IDE forward. The log says "handed over
  to a running copy".
- Launches from the desktop (tiles, folders, the Open menu item) and from
  Start go through it. The Speed Dial widget still calls `ShellLauncher`
  directly (a widget folder).
- New request: `--launch <app id or path>`.

**Home (`TabletMode.GoHome`).**
- Home always shows the desktop. It closes Recents and Start first, then
  sends Win+D only when the desktop is not already in front.
- The check is `DesktopHost.IsDesktopShown` (the Win+D raised state) or a
  Progman/WorkerW foreground. After Win+D the apps are not minimized, so
  "any app visible" would have toggled them back.
- Verified: from an app, pressed a second time, and with Start open, the
  foreground is Progman every time.

**Bar gap.**
- Not reproduced on this laptop. The bar was flush even with auto-hide off,
  and its first strip query already returned the full screen.
- Two changes:
  - With the taskbar hidden by Hearth, the bar is placed flush with the
    screen edge. Windows' suggested rect is ignored; ABM_SETPOS is still
    sent to reserve the space.
  - The bar docks again once the background taskbar hide finishes
    (`HideTaskbarThenRedock`). A re-dock requested mid-dock is queued
    (`_dockAgain`), not dropped.
- The dock log line now includes the screen rect and scale. **Ask the user
  for those lines** if the gap persists: they would show whether the rect or
  the WPF window size is wrong on a 3:2 screen at 200%.

### Tray icons in Recent apps, 2026-09-17

User: "you also need to be able to get to the tray apps inside of the recent
menu, maybe through a drop down or some androidesq menu."

**Where the tray can be read** (`Tablet/TrayIcons.cs`):
- The managed `System.Windows.Automation` client sees nothing inside the
  Windows 11 taskbar (XAML). **Native UI Automation** (CUIAutomation8) does.
  - It is called through vtable slots (no interop assembly, no NuGet),
    numbered from `UIAutomationClient.h` in SDK 10.0.26100:
    - IUIAutomation: ElementFromHandle is slot 6, CreateTrueCondition 21.
    - IUIAutomationElement: FindAll 6, GetCurrentPattern 16,
      CurrentName 23, CurrentAutomationId 29.
    - IUIAutomationElement3: ShowContextMenu is slot 3+82+6.
    - IUIAutomationInvokePattern: Invoke is slot 3.
- In Shell_TrayWnd, visible tray icons are `AutomationId=NotifyItemIcon`
  (class SystemTray.AccentButton). The system icons (network, volume,
  clock) are `SystemTrayIcon`.
- The hidden-icons chevron is the `SystemTrayIcon` just before the first
  NotifyItemIcon. Its name ("Show Hidden Icons") is localized, so it is
  found by position.
- Hidden icons exist only while the flyout `TopLevelWindowForOverflowXamlIsland`
  is open. The chevron toggles it; posting Escape also closes it.
- **Nothing is exposed while the taskbar is SW_HIDE'd or slid away**
  (auto-hide, y=1078 here). Hearth shows it and gives it focus (injected
  no-op key, then SetForegroundWindow), as Start's taskbar hold does.
  - In tablet mode the navigation bar covers where the taskbar rises.
  - `TaskbarControl.PauseEnforcing` stops the re-hide timer meanwhile.
- **Invoke** is a left click. **ShowContextMenu** opens the app's own
  menu. Both were tested on a visible icon (Tailscale) and on hidden ones
  (Windows Security, Bluetooth). A menu for a hidden icon opens with the
  flyout left on screen; the flyout closes with the menu.
- When only reading, the flyout is moved to -32000 so it never flashes.
  For a click it stays in place, because apps position their popups
  relative to the icon.
- A read takes about 0.9 s (raise 0.3, images, taskbar, flyout). The first
  version hit 5.3 s while an earlier flyout was still open.
- **Images:** `HKCU\Control Panel\NotifyIconSettings\*\IconSnapshot` holds a
  PNG per icon, with ExecutablePath (known-folder GUID prefixes, resolved
  with SHGetKnownFolderPath) and InitialTooltip. An icon is matched to an
  entry whose program is running:
  - first by tooltip;
  - else by words from the exe name, tooltip, ProductName and
    FileDescription (4+ letters, generic words skipped).
  - Result: 13 of 15 icons here. Explorer's own (Safely Remove, Bluetooth)
    get glyphs.
- Icons are found again by exact name, then by the same position with the
  same first word, then by the longest shared prefix. Tooltips change;
  Task Manager's shows live CPU use.
- **Test trap:** matching "Task Manager" by name alone hit its *taskbar
  button* and opened its jump list, which then held the foreground.
  Posting Escape didn't close it, and neither did SetForegroundWindow from
  a script; only Hearth taking the foreground (`--settings`) did. Matching
  is now NotifyItemIcon only.

**UI** (`TrayPanel`, `TaskSwitcher`):
- Recents has a header: "Recent apps" on the left and a **Tray** pill on the
  right that toggles a shade. The shade is a card of icon tiles over a dim;
  a tap on the dim or Esc closes it.
- DWM previews are hidden while the shade is open, because they draw above
  WPF.
- Reading raises the taskbar, which takes focus. `_trayBusy` stops that
  from closing Recents, and Recents takes the foreground back afterwards.
- The previous list shows while a fresh one loads. The shade reopens if it
  was left open.
- Tap a tile to click the icon. Press and hold (500 ms) or right-click for
  the menu. Recents hides first.
- Verified by screenshot (`--tablet recents-tray`): 15 icons, focus back on
  Recents.

**Test hooks:** `Hearth.exe --tablet recents-tray`, `--tablet tray` (lists
icons in the log), and `--tablet trayclick:<name>` / `traymenu:<name>`,
which use the same code as the tiles.

**Not verified:** a real finger's press and hold on a tile, and on the
Surface. Localized Windows builds are also untested; there the chevron is
found by position, but the AutomationIds could differ.

**Launch follow-up, reworked.** The first version followed any process that
started during the watch window, and once picked a test script's
`sleep.exe`. `ShellLauncher.ProgramPathOf` now resolves the program up
front:
- an .exe itself;
- otherwise `System.Link.TargetParsingPath` of the shortcut or of the
  `shell:AppsFolder\<id>` entry (Antigravity resolves this way).

Only that program's windows count. Store apps and documents are not
followed. Re-verified: with the settings window in front, the IDE came
forward.

### Memory

~450-550 MB working set, stable. Mostly WPF's D3D9 stack plus the Intel driver
and surfaces for a 3840x1994 window. Wallpaper is decoded at display size.

### Live wallpaper, menus that would not close, Start placement and density, the bar, 2026-09-28

The user's reports: changing the background needed a restart; some system
menus would not close when clicking Hearth; the Start menu should be able to
sit anywhere on screen and have a bigger middle-of-the-screen size; Start
should open faster (an outline appeared first, then the content); and Hearth
should grow an optional bar of its own. They also asked for the hidden-icon
tray flyout without the Windows taskbar getting in the way.

**Wallpaper changes now repaint (`Core/Wallpaper/WallpaperWatcher.cs`).**

Hearth paints the wallpaper itself, so a change reached it through nothing at
all — Explorer redraws a desktop Hearth is covering. There is no single
reliable signal, so three are used and the result is debounced 350 ms:

- WM_SETTINGCHANGE / SPI_SETDESKWALLPAPER (0x0014), fed in from
  `DesktopHost.WndProc`, which is what the Settings app broadcasts;
- `RegNotifyChangeKeyValue` on `HKCU\Control Panel\Desktop` and on
  `...\Explorer\Wallpapers` (`Core/Wallpaper/RegistryWatch.cs`). **A slideshow
  stepping to the next picture broadcasts nothing**, so the registry watch is
  the only signal for it. The notify has to be re-armed inside its own
  callback; it fires once.
- a FileSystemWatcher on `%AppData%\Microsoft\Windows\Themes` and its
  `CachedFiles`, where the transcoded copy lives.

All three are noisy, so `WallpaperService.Signature()` (position, background
colour, and per monitor the device id, bounds, path, file write time and
length) decides whether anything really changed. `DesktopSurface.RefreshWallpaper`
then swaps only the plate brushes — tiles, widgets and an open folder are left
alone — and falls back to a full rebuild when the displays changed too.
`BuildSurfaces` was split so `PaintWallpaper` can be called on its own.

Verified live, both directions: SPI_SETDESKWALLPAPER to another picture logged
`wallpaper: repainted` within the debounce, and setting the user's own
wallpaper back logged it again. Their original wallpaper was restored.

**Another program's menus now close (`Hosting/ForeignPopups.cs`).**

Cause: a menu closes because its owner stops being the foreground window.
Hearth's desktop is WS_EX_NOACTIVATE and never takes the foreground, so
clicking it left tray menus and shell flyouts exactly where they were — the
user had to click an empty strip of taskbar. `ForeignPopups.DismissForClick`
is called from **WM_MOUSEACTIVATE**, which is the one message every click on
the desktop produces (Hearth is never the active window):

- a classic menu (anything on TrackPopupMenu: tray applications, Explorer's
  own) is found with `GetGUIThreadInfo` (GUI_INMENUMODE / GUI_POPUPMENU) and
  cancelled with `WM_CANCELMODE` posted to `hwndMenuOwner`. **`EndMenu` only
  works on the calling thread**, so it is no use across processes;
- a XAML flyout (Start, quick settings, the calendar, the tray overflow) is
  sent Escape, the same way `TrayIcons` already closes the overflow flyout.

Escape only ever goes to a foreground window that is not ours and is **not
`WindowTools.IsSwitchable`** — that is, not something Alt+Tab would list — so
an ordinary application never sees it. The bar and the Halo ring call the same
helper from their own WM_MOUSEACTIVATE.

**Start menu placement (`HearthSettings.StartPlacement`).**

`AboveTaskbar` (the default) is the old behaviour. The other ten detach the
menu from the taskbar and pin it to the display's **work area** — corners,
edge centres, the middle, and `CentreLarge`, which is the middle at 1.25x.
`StartScale` (0.7–1.6) multiplies on top and `StartMargin` is the gap from the
edges. `PlaceDetached` holds no taskbar up: a detached menu is not standing on
one. `Fit()` now also allows growth, so a size above 1 really enlarges rather
than being clamped to 1; the 4:3 box is still scaled as a whole, so nothing
reflows. Verified by screenshot at CentreLarge on 1920x1080.

**Start menu density (`Views/Start/StartMetrics.cs`).**

The user, seeing the centred menu: give the pages more room and make the
furniture smaller. `StartDensity` is Auto / Standard / Compact; **Auto is
Compact for the two Centre placements** and Standard elsewhere. Compact
tightens the search box, tabs, content margin and footer and hands about 50 of
the 660 design DIPs to the views — roughly a tenth more height for the Pages
card, which fills `_pageHost` and so grows with it. The window is built once
and kept, so the effective density is recorded on it as `StartMenuWindow.Density`
and `StartMenuController.EnsureWindow` rebuilds when it (or the theme) changed;
changing the placement or the density in settings calls `InvalidateWindow`.

**Start menu speed.** The user saw "an outline, and then it pops up".

Three causes, all fixed:

- **The window was presented before WPF had composed anything.** DWM draws the
  glass frame (`GlassFrameThickness(-1)`) as soon as the HWND is visible.
  `PresentAsync` now shows it at (-32000,-32000), waits for one composed frame
  (`ComposedAsync`: the *second* `CompositionTarget.Rendering` tick, since that
  fires before each frame; 120 ms timeout so a display asleep cannot hang it),
  and only then moves it into place.
- **The foreground faded in from 0**, so the first frame was the blank frame
  again. It starts at 0.35 now, and `AnimateOpen` runs *before* the move, so
  the fade is already under way when anything becomes visible.
- **The backdrop was the most expensive thing in the first frame.** The live
  `BlurEffect` + `BitmapCache` is gone: `StartBackdropCapture.Blurred` renders
  the blur into a frozen `RenderTargetBitmap` before the window is shown
  (radius `48 * Scale`, because the capture is at a quarter size). The capture
  itself is now a **StretchBlt straight into a quarter-size bitmap** instead of
  a full-size BitBlt plus a `TransformedBitmap`, which took a ~4.6 MB copy into
  managed memory only to throw fifteen sixteenths of it away. `SetStretchBltMode`
  is COLORONCOLOR, the fastest, because the result is blurred anyway.
- **Every open rebuilt every view.** `if (apps.Count != _apps.Count)` compared
  the source list against a dictionary keyed by id, so any two apps sharing an
  id made the counts differ for ever. It is a reference comparison against
  `_appsSource` now; a list that really changed arrives through
  `InstalledApps.Changed`.

Measured on this machine (log line `start menu: shown ... ms:`):

| | before | after |
|---|---|---|
| warm open, done | ~380 ms | ~195 ms |
| warm open, first frame idle | ~1220 ms | ~230 ms |
| cold first open, done | ~440 ms | ~430 ms |

The cold first open is unchanged; the prewarm covers the build but the first
real Show still pays for something not yet identified (the backdrop path was
warmed in `Prewarm` and did not help). A screenshot taken at the earliest
catchable frame shows the menu complete, mid-slide — no outline.

### The Windows taskbar, hidden without being disabled (`Bar/ShellTaskbar.cs`)

The user asked to try moving the taskbar off screen so the hidden-icon flyout
could be used without the bar in the way. **Moving it does not work**:
`SetWindowPos` on `Shell_TrayWnd` from another process is simply ignored —
measured, the rect was unchanged afterwards. Explorer pins its own position.

**An empty window region does work, and is better.**
`SetWindowRgn(bar, CreateRectRgn(0,0,0,0), true)` leaves the taskbar visible,
positioned, focusable and fully populated, with no area: nothing of it is
painted and `WindowFromPoint` over its strip returns whatever is behind it, so
clicks land on Hearth. Auto-hide is turned on alongside it only so Windows
stops reserving the strip; the bar never actually slides, because nothing can
reach an edge it no longer occupies.

Measured, the same `--tablet tray` read:

| | SW_HIDE (tablet mode) | empty region |
|---|---|---|
| total | 7379 ms | 883 ms |
| of which raising the bar | 6774 ms | 92 ms |
| flash on screen | yes | none |

All 14 tray icons still read, and the hidden-icon flyout still opens, because
nothing about the window changed except its area — which is exactly what
`SW_HIDE` broke (the notes above: "nothing is exposed while the taskbar is
SW_HIDE'd or slid away").

- **Explorer drops the region** whenever it rebuilds the bar, so `Apply()` runs
  on a 2 s timer and after an Explorer restart.
- **Leftover popups.** Reading the tray makes Explorer raise the chevron's own
  tooltip ("Show hidden icons", class `Xaml_WindowedPopupClass`, title
  `PopupHost`). Normally it goes when the pointer leaves the taskbar; with no
  area the pointer never was on it, so it hung over the wallpaper.
  `DismissOrphanedPopups` hides `Xaml_WindowedPopupClass` windows overlapping
  the taskbar's own rect (+80 px), after every tray read and on the timer.
- The change is written to the same `shell-state.json` tablet mode uses, so the
  watchdog, `--restore-shell` and the next start all put it back. Verified:
  `--bar off` restored the rect, the hit-testing and the auto-hide setting.

### Hearth's optional bar (`src/Hearth.App/Bar/`, `Core/Settings/BarSettings.cs`)

Off by default (`bar.json`, `Enabled = false`); nothing starts and the shell is
untouched until it is switched on. Five styles, meant as five different ideas
about what a bar is rather than one bar with five skins:

| style | what it is |
|---|---|
| `Taskbar` | Start, one labelled button per window (or per app), tray, clock |
| `Panel` | Linux panel: an Applications button, a window list, **Hearth widgets as applets**, tray, clock |
| `MenuBarAndDock` | two windows — a 32 px menu bar that reserves its strip, and a floating, self-sizing dock that magnifies under the pointer |
| `Docklike` | one square per application, grouped, with a dot per window |
| `Halo` | see below |

Shared parts:

- **`WindowList`** — one WinEvent hook set (FOREGROUND, MINIMIZE, SHOW/HIDE,
  DESTROY, NAMECHANGE, `WINEVENT_SKIPOWNPROCESS`) plus a 3 s poll, coalesced
  120 ms, raising `Changed` only when a signature of the list actually differs.
  Window icons are cached per HWND because `WindowTools.Icon` sends WM_GETICON,
  which blocks on the owning program.
- **`BarWindow`** — the docked strip or floating capsule. Same app-bar
  mechanics as `NavigationBar`: ABM_NEW/ABM_SETPOS off the UI thread (they
  block 1–2 s on Explorer), ABN_POSCHANGED debounced, ABN_FULLSCREENAPP drops
  it to HWND_BOTTOM. WS_EX_NOACTIVATE + MA_NOACTIVATE, because a bar that
  activated would itself be the window in front.
- **`BarTray`** — the real notification area, read through the existing
  `Tablet/TrayIcons` UIA code and redrawn at Hearth's size; clicks and
  right-clicks go back to the real icon. This is only pleasant because of the
  region hide above.
- **Trap:** `Refresh()` runs on every window change, and the first version
  built a new `BarTray` each time, so the tray was re-read several times a
  second. `BarPresentation.Tray`/`Clock` keep one instance and `Detach` it
  from the old tree into the new one.
- **Trap:** `tile.Clicked += _ => _ = TrayIcons.ActivateAsync(...)` does not
  compile — `_` is the lambda's own `PressableBorder` parameter, so the
  discard assigns a `Task<bool>` to it.

**Halo.** The novel one the user asked for, deliberately unlike the other four.

The screen's border *is* the bar. The perimeter is treated as one line of
length 2(w+h) running clockwise from the top-left, and every running
application owns a contiguous arc of it, coloured with its **own icon's
dominant colour** and as long as its share of the open windows. It reserves no
work area and moves no window: it is drawn over the pixels outside every
maximized window. An arc that crosses a corner is split and drawn on both
edges, so the ring reads as continuous.

- Four layered `HaloEdge` windows, `WS_EX_TRANSPARENT` so they are invisible
  to the mouse while still visible to the eye.
- **The dwell is what makes it safe.** The ring only leaves the click-through
  state after the pointer has stayed within `HaloActiveThickness + 3` px for
  180 ms, so a maximized window's close button — which is inside that band —
  keeps working for anyone moving normally. Which edges carry the ring is a
  setting; leaving the top off avoids the question entirely.
- Pointer position is **polled** (100 ms idle, 16 ms at an edge), not hooked: a
  low-level mouse hook that runs on every movement is the one thing the shell
  silently removes for being slow.
- Resting on an arc blooms it and opens `HaloCapsule` with the icon, the name
  and the window titles. Click raises or cycles; right-click is the app menu.
- `HaloCorner` is the one concession: a small capsule at the bottom right with
  the ember (Start), the tray and the clock, because a clock cannot be a
  coloured length. A corner, not an edge, so it never sits over a window's own
  controls.
- Colours: `BarService.Dominant` reads a 16x16 copy of the icon and weights
  each pixel by **saturation cubed** — a plain average gives mud, because most
  icons are mostly one flat background. `Lift` then clamps saturation and
  lightness into a range a 5 px line is actually visible at. Apps with no icon
  get FNV-1a of the path (not `string.GetHashCode`, which changes every run).

**Test hook:** `Hearth.exe --bar on|off|toggle|settings|taskbar|panel|menubar|docklike|halo`.

**Verified by screenshot:** all five styles render and dock correctly on
1920x1080 at 100%, with the Windows taskbar invisible and click-through; the
tray shows 14 real icons in each; `--bar off` restores everything.
**NOT verified** (would mean moving the user's mouse on their own session):
hovering and clicking a Halo arc, the dwell, dock magnification, pinning from
the button menu, the Panel applets with a real widget chosen, and any of this
on a second display.

### Launching: a longer watch (`Services/AppLauncher.cs`)

The user again: "the start menu and desktop still have some problems starting
some apps... Antigravity IDE". The hand-over fix from 2026-09-17 is right but
its watch window was **1.8 s**, which covers an app handing over to a running
copy (measured 140 ms) and not one starting cold. `FollowUpAsync` now settles
for 1.2 s, then watches up to 12 s, activating the program's window as soon as
one exists — and stops the moment the foreground becomes something that is
neither what it was launched from nor one of Hearth's own windows, so nothing
is ever taken from the user. A failure now logs `no window of '<name>'
appeared within 12 s`, so the next report is diagnosable.

### Bar and Start follow-ups, 2026-09-28 (later)

The user, using the bar for real: the hidden tray icons were being opened
periodically and pulling them out of whatever they were doing; the Start menu
was not accounting for Hearth's own bar; Compact should fit more on a page;
labels were clipping; the bar's Start button did not close the menu; and the
icons looked grainy.

**The tray was stealing the foreground every 20 seconds. The worst bug here.**

`BarTray` refreshed on a timer with `includeHidden: true`. Hidden tray icons
**only exist while Explorer's overflow flyout is open**, so every refresh
invoked the real chevron, which activates the flyout — mid-keystroke, every 20
seconds, for as long as the bar was running.

- The timer now reads shown icons only (`includeHidden: false`), which never
  touches the chevron: measured ~130 ms with no `flyout`/`closed` marks in the
  log at all, against ~700 ms and a focus change before. The interval went to
  45 s as well.
- The hidden icons are read **once, when the user opens them** in Hearth's own
  tray, and kept. The chevron is offered until a full read proves there is
  nothing behind it, because finding that out is the very thing that costs.
- `ForegroundGuard` (in `TrayIcons.cs`) wraps every read: it records the
  foreground window and puts it back **only if the thing that took it was one
  of the shell's own windows**, so a user who clicked elsewhere meanwhile is
  left where they went.
- `RaisedTaskbar.Raise` now returns immediately when `ShellTaskbar.IsHiddenByUs`.
  With the region hide the bar is already up, populated and readable; the
  `SetForegroundWindow` dance it used to do was pure interruption.

**Start now stands on Hearth's bar, not on an invisible Windows one.**
`PlaceOn` asked `TaskbarOn(monitor)` for a taskbar rect even when that taskbar
was the one Hearth had taken off the screen, so `barAtTop` came from a bar
nobody could see and the menu went to the wrong edge. Order now:
`BarService.EdgeFor(monitor)` (only bars that reserve a strip — Halo and the
floating dock reserve nothing, so nothing needs to avoid them), then the
Windows taskbar only if it is not hidden by us, then the plain work area.

**The desktop grid ignored every docked bar.** `ComputeGrid` laid out in
`LocalBounds`, the whole display, so the bottom row of icons sat underneath
whatever was docked there — which nobody noticed against a 32 px taskbar and
was obvious against a 48 px Hearth bar. `MonitorSurface` gained `WorkBounds`;
the wallpaper is still painted over the whole display, the grid is laid out in
the work area, and `PadX`/`PadY` are measured from the work area's corner.
`SPI_SETWORKAREA` (WM_SETTINGCHANGE) rebuilds the grid, debounced 500 ms
because claiming a strip broadcasts several times over a second or two.

**Compact's grid: 8x4, not 9x5.** 9x5 was tried first and was worse. The cells
lost a quarter of their height, which took the page icons to about 28 px and
clipped every two-line label ("Games on Mah Phone", "Antigravity IDE"). The
width is where the compact chrome actually gives something back, so that is
where the extra cell goes, and the row height — and with it the 48 px icon —
is untouched.

- `PageIconFor(cellHeight)` replaced the fixed `PageIcon = 48`: the label's
  height is reserved first (it is the part that cannot shrink and stay
  readable) and the icon takes what is left, clamped to 26..48. So no grid,
  and no small screen, can clip a label again.
- Verified against the user's saved `start.json`: every placement is inside
  7x4, so nothing moved through either grid change. Growing a grid never moves
  anything; only shrinking does, and `StartLayout.UseGrid` re-places strays
  into the first free run of their own size, their own page first.

**All apps and Categories were laid out at a fixed width** — `UniformGrid
{ Columns = 3 }` and a `WrapPanel` of 136 px tiles — and left most of a
column's width unused on a wide menu. Both now take their column count from
`ColumnsFor(minimum)`, which measures `_content`, and `_content.SizeChanged`
rebuilds them when the count would really change. The minimums are part of
`StartMetrics`, so Compact fits more of both.

**The bar's Start button now toggles.** `StartMenuController.Toggle(string?
tab)`: open, or close when the menu is already showing what was asked for; a
different tab switches instead of closing. The bar needs this rather than
`Open` because the bar is WS_EX_NOACTIVATE — a click on it never reaches the
menu as a deactivation, so the menu never hid itself and the second click just
re-opened it.

**Grainy icons: the menu's own scale was missing from the render size.**
Icons are extracted at 256 px and rasterised at `Size * Scale`, where `Scale`
was the display DPI only. But the whole menu is scaled bodily to fit — and
*up* by 1.25 for the larger centred placement — so a 48 px bitmap was being
drawn at 60. `StartStyle.RenderScale` is set from the placement's `fit` (and
from the full-screen `ui`), multiplies into `IconOptions`, and a change of more
than 2% triggers `RerenderIcons()`. Nothing else was wrong with the pipeline:
the extraction size, the cache key and the handling of new apps were all
already right.

Settings -> Home now has **"Rebuild every icon"**, which clears the disk cache
and re-renders, for an icon cached before a setting changed. Worth being clear
about what this is and is not: there is no upscaler. An app that only ships a
32 px icon (Steam shortcuts, see the 2026-09-16 entry) cannot be improved by
resampling it, and the answer for those is better artwork on disk, not a better
filter.

**Not verified by automation:** clicking the bar's Start button twice, the
tray chevron in the bar, and the page layout at 8x4 — screenshotting Start
takes the focus, and the user was typing. Left for them.

### Data loss: a relayout before the catalogs were read, 2026-09-28

**What happened.** The work-area rebuild added earlier that day
(`SPI_SETWORKAREA` -> `RebuildEverything`) fired during start-up, between the
window being created and the first catalog read. `RelayoutTiles` handed
`GridLayout.Reconcile` an empty live set, and Reconcile did what it is written
to do with one: filtered every folder's `ItemIds` down to nothing, dissolved
all five folders, and swept every placement off every arrangement. The user's
home screen came back as 47 loose icons where it had been 14 tiles.

The log says it plainly, and is the thing to look for if it ever recurs:

```
13:05:54.896  desktop catalog: 42 item(s)
13:05:55.992  relayout: 0 tile(s) across 1 display(s)   <-- nothing live yet
13:05:56.071  apps catalog: 224 app(s), 8 added by hand
13:05:56.338  relayout: 47 tile(s)
```

**Guards, both of them needed.**

- `GridLayout.Reconcile` returns immediately when the live set is empty and
  anything is placed. No real state has zero live items and a populated grid;
  it means the catalogs have not been read. This is the one that makes the
  data safe whatever calls it, and it should never be removed.
- `DesktopSurface` keeps `_itemsLoaded`, set after the first catalog read that
  produced anything, and `RelayoutTiles` returns before that.

**Recovery.** `HomeLayout.Groups` was gone from `layout.json` and there was no
recent backup — the `layout.before-*.json` files are from 2026-09-16. What
saved it: **the Start menu keeps its own copy of the same folders**
(`start.json`, seeded from the desktop and independent since), and it was
untouched. All five (Video, Games on Mah Phone, Game Store, AI, Equalizer, 33
items) were rebuilt from there, each folder taking the first cell its contents
had occupied, applied to every arrangement. Restored file backed up first as
`layout.json.before-folder-restore`.

**Worth doing before the next risky layout change:** copy `layout.json` aside.
Reconcile is destructive by design and there is no undo.

### Bar and Start, third round, 2026-09-28

- **Hearth's own windows are switchable now.** `WindowTools.IsSwitchable`
  dropped its `IsOwnProcess` test, so the settings window appears on the bar
  and in Recents as the user expected. Everything else Hearth shows is a tool
  window or non-activating, so the ordinary rules still leave the desktop host,
  Start, the bars and the halo out. `WindowList` also stopped passing
  `WINEVENT_SKIPOWNPROCESS`, or the 3 s poll would have been the only thing to
  notice the settings window opening.
- **Running indicators follow the bar's edge.** They were hard-coded to the
  bottom (or the left when vertical). They now sit against whichever side of
  the button faces the screen edge, flush to it, with the button's padding
  shifted to that side so the label keeps clear. The Docklike dots too.
- **Docklike centres like Windows 11.** The Start button joined the squares
  rather than hugging the corner, and `Layout(..., centre: true)` spans the
  cluster across the whole bar centred, with the ends drawn over it — centring
  it only in the gap between the ends does not line up with the middle of the
  screen.
- **Blank icons after "Rebuild every icon".** `IShellItemImageFactory` fails
  transiently, and most when every icon is extracted at once, which is exactly
  what that button asks for. A failure was never cached but was never retried
  either, so the tile stayed empty until something else rebuilt it.
  `ProduceAsync` now retries once after 180 ms, and `ClearDiskCache` clears
  `_inFlight` as well as the memory cache.
- **The Start menu's grid does not change the desktop's.** Worth stating
  because it caused confusion: `StartLayout.Columns/Rows` is Start's page grid;
  the desktop's comes from `ComputeGrid` and the work area.

### Bars, theme, control centre and Steam artwork, 2026-09-28 (evening)

A long round of the user's own notes from living with the bar. Grouped by the
thing rather than the message.

**Theme (`Hosting/HearthTheming.cs`, `HearthSettings.Theme`).** Auto, Light or
Dark. `StartStyle.Resolve()` is the single answer and `UseSystemTheme` applies
it; Auto follows `Themes\Personalize` through the same `RegistryWatch` the
wallpaper uses, and only rebuilds when the resolved value really changed — that
key is written for plenty Hearth does not care about. The palette is now
settled at start-up (`HearthTheming.Start`) rather than when the Start menu is
first opened, because the bar reads it too. Icon rendering's own `DarkTheme` is
left alone deliberately: it is part of the icon cache key, and re-rendering two
hundred icons for a UI colour change is not worth it.

**Start placement follows the bar.** `StartPlacement.Auto` (the new default)
resolves against `BarService.EdgeFor(monitor)`: the menu opens from the edge the
Hearth bar is on, centre-screen for Halo (no edge to come from), and falls back
to the Windows taskbar when no bar is running.

**Ordering, and the separator.** `PinnedAndRunning` returned the pinned apps
then everything else in *Z-order*, so a button moved every time you switched
windows — the thing you were about to click was never where you looked.
`BarService.ItemsFor` returns pinned (in pin order) and running (in first-seen
order, kept in `_firstSeen` until the program closes), and every style draws a
`BarSeparator` between them.

**Sections and bar widgets (`Bar/BarWidgets.cs`).** `BarSection` is Start,
Middle or End, and `BarPresentation.AddSection` fills one — which is what lets a
stack or a spacer be dropped into any style without the style knowing about it.
`BarWidget` (in Core, because it is saved) covers Stack, DesktopStack,
Shortcut, Spacer and Widget. A `StackButton` draws the folder's first three
items as a pile, and `StackFlyout` opens it as a **Grid**, a **Fan** (an arc of
separate cards, the Mac gesture) or a **List**.

**Control centre (`Bar/ControlCentre.cs`).** One panel: Quick Toggles, Audio
and Media — Hearth's own widgets, not a second implementation of each control —
plus a theme switch and shortcuts into the real Windows panels. On the menu bar
by default, and on Halo's pill.

- **Crash, found by the user:** clicking a shortcut called `Dismiss()`, whose
  `Close()` deactivated the window, whose `Deactivated` handler called
  `Dismiss()` again — and WPF throws `InvalidOperationException` on a close
  during a close, which the dispatcher handler turns into a shutdown. Both
  `ControlCentre.Dismiss` and `StackFlyout.CloseStack` now return early when the
  close is already under way. **Any window here with a Deactivated-closes
  handler needs that guard.**

**Blur (`Bar/BarGlass.cs`).** `SetWindowCompositionAttribute` with
`ACCENT_ENABLE_ACRYLICBLURBEHIND`, which is what Explorer's own bars use.
Chosen over DWM's backdrops for the same reason the Start menu's blur was:
Mica only shows the wallpaper, and both Mica and Acrylic go flat grey when
transparency effects are off, which Energy Saver does and this machine runs
with. The tint rides in the accent colour as ABGR so the blur and the wash are
one pass; `Settings.Opacity` is how strong the wash is. The surface brush goes
transparent when blur is on, or it would cover the blur entirely.

**Auto-hide.** Per bar: the menu bar and the dock have their own switches,
because wanting the menu bar always there and the dock out of the way is the
usual pairing. **The strip stays claimed while hidden** — a bar that gave its
space back would resize every window twice a second, which is why Windows'
own auto-hide is unpleasant. Only the window moves. It stays out while
anything of the bar's is open (`BarService.HasOpenFlyout`).

**The ring's pill.** Full, Shrink (a dot until the pointer comes near — the
ring's claim is that it costs nothing, so the one piece that does should be
able to get out of the way) or Hidden; any corner; its own scale and margin.

**Per-style character.**
- *Panel* is now a rail rather than a taskbar: a named "Applications" button,
  square corners (radius 3), pinned launchers separated from the task list, the
  task list given titles and a minimum width, and every applet in its own
  compartment with a rule before it.
- *Docklike* centres its cluster across the whole bar with Start inside it, the
  way Windows 11's centred taskbar does — centring only in the gap between the
  ends does not line up with the middle of the screen.

**Indicators.** They were hard-coded to the bottom and grew with the window
count, which made them the loudest thing on the button. Now: a 4 px dot for
something running, a 14 px bar for whatever is in front, against whichever side
of the button faces the screen edge, flush to it, with the padding shifted to
that side. The count lives in the tooltip.

**Window names and icons.** `WindowTools.FriendlyName` reads the file's own
description, so the menu bar says "Vivaldi" rather than "vivaldi.exe" (cached —
reading version info opens the file, and a bar asks on every rebuild). A window
with no icon of its own — installed web apps are the common case, and Hearth's
settings window was another — falls back to the program file's icon rather than
a blank square.

### Steam artwork (`Core/Icons/SteamArtwork.cs`)

The user: a large chunk of the blurry icons are Steam games. They are, and the
2026-09-16 note said there was no larger square icon — which was right about
*square* and wrong about what could be built.

A Steam desktop shortcut is a `.url` whose IconFile is a 32 px `.ico`. But the
library cache has the parts: per app id, a transparent `logo.png` (usually near
a thousand pixels wide) and a `library_hero` / `library_capsule` / `header`
behind it. That is exactly the logo-on-a-background pair an adaptive icon
wants, so the icon is **composed** rather than upscaled: the hero centre-cropped
square and dimmed (0x3D — enough for a white logo and a dark one both, no
more), with the logo at 82% of the tile because a mask cuts the corners off.

- Layout, current Steam: `appcache/librarycache/<appid>/logo.png` and
  `<appid>/<hash>/{library_hero,library_capsule,header}.jpg`. The old flat
  `<appid>_logo.png` names are looked for too.
- Steam's own install path comes from `HKCU\Software\Valve\Steam\SteamPath`.
- `LauncherItem.SteamAppId` carries the id; the picture is composed **on the
  icon service's render thread**, never in the catalog, because
  `DrawingVisual`/`RenderTargetBitmap` create a Dispatcher on whatever thread
  touches them first and the catalog runs on the pool (the trap from
  2026-09-15). Results are cached to `%LocalAppData%\Hearth\steam-icons`.
- Verified: 25 games composed at 256 px on this machine. Hollow Knight and
  Terraria checked by eye and are a different thing entirely from the 32 px
  original.

**On AI upscaling.** The user asked for on-device upscaling in the style of
Upscayl. Not done, and worth writing down why rather than leaving it looking
forgotten: Hearth's stated rule is zero external dependencies, and a real
Real-ESRGAN-class upscaler means shipping either an ONNX model plus a runtime
or an ncnn/Vulkan binary. The in-box route that would respect the rule is
`Windows.AI.MachineLearning`, which can run an ONNX model with no extra
runtime — but still needs a model file shipped and licensed. The honest order
of value is also worth keeping in mind: better *source* artwork beats any
upscaler, which is why Steam went first, and a resampler cannot invent what a
32 px icon never had.

### The taskbar's reserved strip: what actually frees it, 2026-09-28 (late)

The user: "system still seems to think the taskbar is up there" — an invisible
Windows taskbar still holding 32 px, so every window stopped short of an edge
with nothing on it. Three separate causes, and one of them overturns what the
earlier entry claimed.

**1. A leaked app bar registration (fixed).** `BarWindow.CloseForExit` sent
`ABM_REMOVE` from `Task.Run` and then closed the window immediately. The
release raced the destroy, Explorer had no handle left to match, and the strip
stayed reserved for ever — so every change of bar style leaked another one. It
is sent synchronously now, before `Close()`. It blocks for a moment; that is
the price of the shell letting go.

**2. The tray was re-read on every bar rebuild (fixed).** `BarTray` started its
read from `Loaded`, which fires again every time the bar rebuilds and the
cached panel is moved into the new tree — which is whenever any window
anywhere changes. Dozens of reads a minute. It reads once now (`_read`), and
then only on its own 45 s timer.

**3. The empty region does NOT free the strip.** This corrects the earlier
entry, which said auto-hide was turned on only "so Windows stops reserving the
strip" and implied the region did the rest. Measured, with Halo running (which
reserves nothing of its own): region applied, taskbar invisible, work area
still `Y=32`. Explorer keeps reserving for a taskbar with no area.

So auto-hide is the only thing that frees it — and on this build **auto-hide
cannot be set programmatically at all**:

| route | result on build 26200 |
|---|---|
| `ABM_SETSTATE` with `ABS_AUTOHIDE` | returns 1, `ABM_GETSTATE` still reads 0. **Verified from PowerShell too**, so it is the OS, not Hearth |
| `SPI_SETWORKAREA` with a null rect | returns false |
| `SPI_SETWORKAREA` with an explicit monitor rect | works, and Explorer overrides it again within seconds |
| `StuckRects3` byte 8, bit 0 | takes effect **only when Explorer next loads** |

Also worth knowing: **a window region set on Explorer's taskbar survives
Hearth exiting.** A run that ended badly leaves the next one facing a taskbar
that already has no area, which is its own trap; `Hide()` now clears the region
before doing anything else.

**What Hearth does about it.** It writes the StuckRects3 bit (and restores the
previous value on `Restore`), keeps the region for invisibility and
click-through, and then **says so**: `ShellTaskbar.StripStillReserved` is true
while the region is on and auto-hide is not in force, and the bar settings page
grows a card explaining that only Windows can give the strip back, with
"Restart Explorer now" and "Open Windows taskbar settings". Hearth never
restarts Explorer on its own — open File Explorer windows close, and that is
the user's call. Hearth itself survives one: it is not Explorer's child and
already re-hides the new bars on `TaskbarCreated`.

**If this is revisited:** do not add another retry loop around `ABM_SETSTATE`.
It was tried at two seconds a tick and produced nothing but a log full of the
same line.

### Flyouts, the dock's last pieces, and small icons, 2026-09-28 (night)

**Flyouts opened in the wrong place and would not close (`Bar/BarFlyout.cs`).**
Two faults, both shared by the control centre and the stacks, now both handled
in one place:

- **Placement.** They were positioned from `DesiredSize` *before* `Show()`, and
  a window that has never been shown has no size — so a zero height put them
  against the bottom of the screen with a sliver visible, which is what the
  ring's control centre looked like. `BarFlyout.Present` shows the window off
  screen, gives it a layout pass, then moves it into place: the same order the
  Start menu uses for the same reason.
- **Dismissal.** They are opened from bars that never take focus, so the
  foreground never moved to them, `Deactivated` never fired, and clicking away
  left them up. `Present` claims the foreground deliberately once the window is
  shown (`StartTrigger.ClaimForegroundRight` then `SetForegroundWindow`), and
  `StackFlyout` had to stop being `ShowActivated = false` for the same reason.

Also clamped to the work area of the display they were opened from, so a panel
opened from a corner cannot hang off the screen.

**The control centre is Windows' own now.** Hearth had one for about an hour
and the user's verdict was "abysmal", which was fair: Windows' quick settings
already has brightness and volume, the real network and Bluetooth flyouts,
Cast, Night light, accessibility and battery, all live and all wired to the
system, and a reimplementation is a worse copy of every one. The button on the
menu bar and on the ring's pill sends Win+A. `Bar/ControlCentre.cs` is deleted
rather than left unused.

**The Shelf was not using its width.** A `WrapPanel` of fixed 78 px tiles left
most of another tile's width empty on every row. It is a `UniformGrid` whose
column count comes from the width it is actually given, with the tiles
stretching — the same treatment the Start menu's lists got.

**Maximized windows short of the bar.** Windows broadcasts a work-area change
and most programs re-layout, but some keep the size they were maximized to and
leave a strip of wallpaper between themselves and the bar — Chromium windows
and anything else drawing its own frame. `BarWindow.NudgeMaximizedWindows`
sends `SWP_FRAMECHANGED` (no move, no size) to every maximized switchable
window after a dock, which makes them recalculate and costs nothing for the
ones that were already right.

**The dock's last pieces.** A recycle bin (`Bar/BarTrash.cs`) that shows
whether it is empty, takes a drop (`SHFileOperation` with `FOF_ALLOWUNDO`, so
the shell's own confirmation and undo apply), and offers Empty; and minimised
windows given their own run at the end of the dock, after a separator.

**Trap, again:** writing `'\\0'` into a source file through the tool pipeline
produces **real NUL bytes** — the file becomes binary and the build chokes. The
2026-09-16 note says this and it still caught me. Build it at run time:
`var nul = ((char)0).ToString();`.

### Small icons: what was done instead of a neural upscaler

`Core/Icons/IconUpscaler.cs`, on by default (`SharpenSmallIcons`), applied only
when the artwork is smaller than the tile it has to fill:

- **Edge-directed doubling.** The new pixel at the centre of each 2x2 block is
  taken along whichever diagonal's ends are more alike, rather than averaged
  across both. That is what keeps a diagonal edge a line instead of a staircase
  of blended pixels, and it suits icons specifically — they are flat colour and
  hard edges, not photographs.
- **A restrained unsharp pass on luminance only**, clamped, to put back the
  local contrast any enlargement costs. On colour it fringes.

It is part of the cache key, so turning it off re-renders rather than serving
stale bitmaps.

**Why not Real-ESRGAN.** It means shipping a model plus a runtime (ONNX or
ncnn), against the project's zero-dependency rule. The route that would respect
the rule is `Windows.AI.MachineLearning`, in-box since 1809, which runs an ONNX
graph with nothing else installed — but a model still has to be shipped and
licensed, and that is a decision for the user, not a thing to slip in. Worth
keeping in proportion either way: **better source artwork beats any upscaler**,
which is why Steam's library art went first, and no filter can invent what a
32 px icon never had.

### Blur that works, intellihide, dock shape, and Hearth's icons on the bars, 2026-09-28 (late)

**The bars' blur was blurring nothing (`Bar/BarGlass.cs`, rewritten).**
The first version used `SetWindowCompositionAttribute` with
ACCENT_ENABLE_ACRYLICBLURBEHIND — what Explorer's own bars use — and it did
nothing here. The reason is one this project already knew and had already
solved once: **Windows' blur effects are switched off on this machine** (Energy
Saver does it), and every system backdrop degrades silently to a flat wash when
they are. That is exactly why the Start menu does not use them and instead
grabs the pixels behind itself and blurs them; the user's verdict on that was
"works 110%". So the bars do the same thing now, through the same
`StartBackdropCapture`.

- The capture is taken with the bar moved off screen (`WithoutSelf`) or it
  photographs itself. Two `SetWindowPos` calls, no repaint between.
- Retaken after every placement, on `Dispatcher.BeginInvoke(..., Loaded)`.
- A bar is a better case for a snapshot than the Start menu: a thin strip that
  does not move. It cannot follow a window sliding underneath, which is the
  same trade the Start menu makes.
- The bar's own `Surface` goes transparent while blur is on, with the tint
  painted into the captured backdrop, or an opaque surface would cover it.

**Auto-hide is animated, and knows about intellihide.** It teleported before,
which the user called abysmal and which reads as a glitch rather than as
something getting out of the way. `BarWindow.SlideTo` eases over
`HideAnimationMs` (default 180), a call part way through only changes where it
is heading, and `PlaceAt` is the single place that positions the window along
its travel. `HideStyle.WhenCovered` is the one people mean by intellihide: the
bar stays put on an empty desktop and ducks only when a switchable,
non-minimised window overlaps its strip.

**Dock shape.** `DockShape` — Pill (the default, and what a dock has looked
like since 2001), Rounded, Subtle, Square — with `BarSettings.DockCorners`
deriving the radius from the thickness, and `CornerRadius` still overriding it
if set by hand.

**The bars use Hearth's icon system (`Bar/BarIcons.cs`).** They were showing the
raw window icon — whatever the program handed the shell, usually 16 or 32 px in
its own shape and padding, which next to the home screen looked like a
different program's bar. Now every button asks the same pipeline the desktop
does: same shape, size, adaptive background and cache. The installed-app entry
is preferred over the exe (a packaged app's icon lives with the package, not
with whatever exe hosts its window); the window icon shows immediately and is
replaced when the rendered one arrives, because the pipeline goes to disk.

**Flyouts that would not close, part two.** Escape alone does not shut the
shell's XAML flyouts — quick settings, the calendar, the notification centre.
They close on losing activation and nothing else, and a click on one of
Hearth's non-activating surfaces never takes it from them.
`ForeignPopups.StealActivation` now takes the foreground with a **one-pixel,
off-screen, tool-window of Hearth's own**, which makes the flyout deactivate,
and then hands the foreground straight to the top switchable window that is not
ours. The thief window is kept rather than created per click.

Hearth's own stacks had the mirror of the problem: they hold focus, and
clicking the bar behind them (WS_EX_NOACTIVATE) did not deactivate them.
`BarWindow`'s WM_MOUSEACTIVATE now calls `CloseStacksExcept(StackUnderPointer())`
— everything except the stack whose own button was clicked, which is about to
toggle itself and would otherwise have nothing left to open.

**Folders belong to the dock.** `BarPresentation.Accepts` returns false for
stacks by default; `DockPresentation` and `MenuBarPresentation` override it to
true. On a taskbar or a panel a stack read as a stray button nobody asked for.

**The control centre is gone for good.** Replaced by Windows' own (Win+A) at
the user's request — theirs has brightness, volume, the real network and
Bluetooth flyouts, Cast, Night light, accessibility and battery, all live.

**Not chased, worth knowing:** a bar was seen sitting over a borderless
full-screen game. `ABN_FULLSCREENAPP` drops it to HWND_BOTTOM, but Explorer
only sends that for apps it recognises as full-screen, and a borderless window
is often not one of them. If it comes up, `SHQueryUserNotificationState` is the
other signal — and note that Hearth's own desktop had to set `NonRudeHWND`
precisely because the shell mistakes a borderless full-screen window for a
game.

### The reserved strip, finally solved, 2026-09-28 (night)

Two separate things were stopping applications using the space, and only one of
them was Windows' fault.

**1. An auto-hiding bar was still reserving its strip. Hearth's own mistake.**
`BarWindow` kept its app-bar claim while hidden, on the reasoning that giving
the space back would make every window resize twice a second. That reasoning is
wrong for a bar that hides: the whole point of hiding — and the entire point of
intellihide, which hides *because* a window wants the space — is that the space
becomes usable. Reserving it means the bar disappears and nothing can move into
where it was, which is exactly what the user saw.

A bar with auto-hide on now registers no app bar at all (`reserveSpace:
!autoHide` in `BarService.Build`), the way every other auto-hiding bar in
existence behaves. It simply appears on top when reached for.

**2. `Hide()` had to clear the region before asking for auto-hide, and now
does — and it works.** The earlier entry recorded that ABM_SETSTATE is ignored
on build 26200. That was measured against a taskbar that **already had an empty
region from a previous run**, which is the state Explorer refuses. With the
region cleared first, auto-hide takes:

```
19:56:05  bar: Windows taskbar hidden by region (auto-hide was off, now on)
          ABM_GETSTATE = 1
          work area = 0,0 1920x1080   (the whole screen)
```

So the rule is the one already written down, and it is the *order* that matters:
**clear the region, set auto-hide, then take the area away again.** Do not
conclude from a single failed ABM_SETSTATE that the API is dead; check whether
a region is on the bar first.

**3. `ShellTaskbar.ReclaimStrips` as the backstop.** When the setting cannot be
made to stick, the work area is set directly instead, on the same two-second
timer that keeps the region applied: full monitor, minus the strips Hearth's own
docked bars are really claiming (`BarService.ReservedStrips`, which excludes
auto-hiding ones). It only ever *hands space back* — if something else has
legitimately claimed more than Hearth knows about, it is left alone rather than
fought over. This is what makes the space usable without waiting for Explorer to
reload.

The user also moved their taskbar to the bottom edge in the middle of this, which
is worth knowing when reading the timestamps: the taskbar changing edge
re-registers it, which is part of why the auto-hide finally took.

### Start above the Start button, and three smaller fixes, 2026-09-28 (night)

**The Start menu was opening in the middle of the screen.** Two faults stacked.

- `StartPlacement.Auto` mapped the bar's edge onto the *centred detached*
  placements (`TopCentre`, `BottomCentre`, …), which put the menu in the middle
  of that edge whatever the bar actually looked like. Auto now resolves to
  `AboveTaskbar` and lets that branch do the work — it is the one that knows
  about bars. The ring is the exception: no edge to come from, so the middle is
  the honest answer.
- `EdgeFor` had started excluding auto-hiding bars (correctly — it is about the
  work area), which meant Auto stopped following the bar at all once auto-hide
  was on. `BarEdgeFor` is the other question: which edge the bar *lives* on,
  hidden or not, because that is where the Start button is.

**"Above the taskbar" now means above the Start button.** It was always
horizontally centred, which is right only by accident. Windows 11 centres its
taskbar by default and can be set left; Windows 10 was always left; Hearth's own
bars put Start at the left (Taskbar, Panel), inside a centred cluster
(Docklike), or in a corner (the ring). The anchor is
`BarService.StartButtonRect` for Hearth's own bar — the presentation stores the
button it built on `BarWindow.StartButton` — falling back to
`StartTrigger.StartButtonRect`, which reads the hidden legacy "Start" child of
`Shell_TrayWnd` (the same one the click handling uses, and accurate even when
the taskbar is hidden).

The contingencies it now covers: a bar down the left or right edge puts the menu
*beside* it and level with the button rather than above anything; the result is
clamped to the display's work area, so an anchor near a corner cannot push the
menu off screen; and with no Start button anywhere it falls back to centred.

**The blur squared off the dock's rounded ends.** `ClipToBounds` on a `Border`
clips to its rectangle, not to its `CornerRadius` — so the blurred backdrop
filled the corners the dock's shape had cut away. A `RectangleGeometry` clip
with the same radius, resized with the window, is the only thing that follows
the shape.

**The ring's pill now unfolds.** Swapping dot and pill in one frame read as the
dot being replaced rather than as the same thing opening. It scales and fades
from the end nearest its corner — so it grows out of where the dot was and
towards the middle of the screen — over 170 ms out and 130 ms back.

### Steam art in Start, shell panels, and the dock's slide, 2026-09-28 (late night)

**Start's Games category showed the 32 px icons.** The Steam artwork was wired
into `DesktopItemCatalog` only, so the games on the desktop were sharp and the
same games in Start were not. They reach the apps folder as `.url` shortcuts in
the Start menu folder, exactly like the desktop ones, so
`AppsFolderCatalog.Describe` sets `SteamAppId` the same way — off `FileSystemPath`
when it ends in `.url`.

**Windows' own panels opened in the wrong corner (`Bar/ShellFlyouts.cs`).**
Win+A anchors quick settings to the *Windows* taskbar's tray. With Hearth's menu
bar along the top and the Windows taskbar along the bottom, clicking the control
centre button opened the panel in the opposite corner of the screen.

There is no supported way to say where it should go, so it is moved afterwards:
the visible shell windows are noted, the chord is sent, and the one that appears
and is flyout-shaped gets moved to the button. **Found by process, not by class
name** — these panels' classes have changed more than once across Windows 11
builds and that is the sort of thing that breaks silently on an update, whereas
`explorer` / `ShellExperienceHost` / `ShellHost` have not. If nothing turns up
within 900 ms it is left where Windows put it, which is no worse than before.

**The floating dock did not slide fully off screen.** `PlaceAt` moved a hiding
bar by its own depth, which is right for a strip flush against the edge and
wrong for a dock that floats a gap clear of it — the gap's worth stayed
visible. The travel is measured to the screen's edge now
(`screen.Bottom - r.Top` and so on), so any gap is included.

The same gap broke reaching for it: the pointer had to land in a band ten pixels
up from the bottom, which nobody aims for. `Peek` extends the hit rect to the
screen edge on the bar's own side.

**Settings layout:** the "Stacks, shortcuts and spacers" buttons had no width
bound, so they took the whole row and squeezed the title column to one
character per line. `MaxWidth` on the wrap panel.

### A rebuild storm, and the tray going empty, 2026-09-28 (night)

**Turning on the Shelf applet locked Hearth into a rebuild loop**, and the user
could not turn it off again because the settings window was being torn down
underneath them roughly once a second. The log signature:

```
20:29:31.830  bar: work area reset on 1 display(s)
20:29:31.855  bar: Top strip at (0,0)-(1920,36)
20:29:31.939  tray: 0 shown and 0 hidden icons
20:29:32.816  bar: work area reset          <-- again, and again
```

`work area reset` + `strip at` + a fresh tray read, repeating, is
`BarService.SaveAndApply` being re-entered: it tears every bar down, resets the
work area and builds new ones, and each new bar makes a new `BarTray`, which
reads the real notification area.

**Starting with the applet already on does not loop** (measured: one reset, one
dock, one tray read over 20 s), so the applet is not the thing that recurses —
*(this conclusion was too broad: the applet was not recursing through
`SaveAndApply`, but it was spinning WPF layout, which logs nothing. See "The
Shelf applet really was the thing, 2026-09-29" below.)* —
the loop came from `SaveAndApply` being reached again while it was still
running. Rather than chase the one path that did it, the method is now
guarded: a call that arrives mid-rebuild is coalesced into a single follow-up
250 ms later. Whatever re-enters it costs one extra rebuild instead of a storm.

**This is the rule for anything that rebuilds the bars:** it must be safe to
call from inside its own consequences. Tear-and-build touches app bars, the
work area and the tray, and all three of those broadcast.

**The tray had gone empty (`tray: 0 shown and 0 hidden`).** A regression from
auto-hide finally working. `RaisedTaskbar.Raise` was returning early whenever
`ShellTaskbar.IsHiddenByUs`, written when the belief was that the region alone
freed the strip and auto-hide was never really on — so there was nothing to
raise. Now that auto-hide does take, the taskbar genuinely slides away, and
**a slid-away taskbar exposes nothing to UI Automation**; the region only takes
away its pixels. It is raised again for reads, which is invisible precisely
because the region is on, and `ForegroundGuard` still puts the user's window
back. Measured after the fix: 2 icons, 316 ms to raise.

### The Shelf applet really was the thing, 2026-09-29

The previous entry cleared the Shelf applet ("**Starting with the applet
already on does not loop**") and pinned the trouble on `SaveAndApply`
re-entry. **That was wrong, or rather it measured the wrong loop.** The
`SaveAndApply` storm was real and the guard was right, but a second and worse
loop was left in place, and it is the one that made Hearth unusable: with
`Applets: ["shelf"]` on the Panel, the process sat at **~100% of a core
continuously and 8.2 GB of private bytes, still climbing**, the Start menu
took **28.8 s** to show, and `Hearth.exe --quit` could not be answered at all.

It logs nothing, which is why the log-signature hunt missed it. It is a **WPF
layout feedback loop**, and it lives entirely between two pieces of code that
are each reasonable on their own:

- `ShelfWidget` picks its column count from the width it is given:
  `Columns(w) = max(1, floor(w / 68))`, and re-runs `Rebuild` from
  `_tiles.SizeChanged` whenever that count would change.
- `BarService.BuildApplet` put the widget in a `Viewbox`, **which measures its
  child at infinite width**.

At infinity nothing bounds the widget, so the two feed each other with a gain
well over 1. One column of tiles measures ~258 px wide (a file name at an
unconstrained width does not wrap, so a long one sets the cell width) ->
`Columns(258)` is 3 -> three columns measure ~774 px -> `Columns(774)` is 11
-> ~2838 px -> 41 -> ... It never reaches a fixed point, so layout never
settles, and each pass rebuilds all 67 tiles. On the desktop grid and the
Start board the same widget is fine, because there something else decides its
width.

**The fix, in three parts:**

1. `BuildApplet` now **imposes** the size it already declared
   (`built.Width/Height = AppletSize`) instead of only putting it in the
   context. The `Viewbox` then scales a real 210x105 widget down to the bar,
   which is what the comment there always claimed was happening. This is the
   one that stops the loop, and it protects every other widget from the same
   class of bug — the Shelf is only the widget that happened to read its own
   width back.
2. Applets now **survive a bar refresh**, as `BarPresentation.Applets`,
   exactly like `Tray` and `Clock` and for the same reason: `Refresh` runs on
   every window title change anywhere on the desktop, and an applet is a whole
   widget, so a fresh one re-runs the widget's whole start-up. Cached per
   presentation and keyed on bar thickness and theme; a change to the applet
   list goes through `SaveAndApply`, which replaces the presentation and the
   cache with it. `BarService.BuildApplets` is gone — `BuildApplet` is the one
   path now.
3. `ShelfView.Rebuild` **returns early when neither the file list nor the
   column count moved.** A host that rebuilds its tree hands the same view
   back unloaded and then loaded again, and WPF is not dependable about
   whether it raises that pair on a detach-and-reattach, so the widget does
   not rely on it: a tile per file is the whole cost of the Shelf and it is
   only paid when something changed.

**Measured, same machine, same `bar.json` (Panel, `Applets: ["shelf"]`, 67
files on the shelf):**

| | before | after |
| --- | --- | --- |
| private bytes | 8.2 GB, +8 MB/s | 257 MB, flat |
| CPU | 19.1 s per 20 s wall (~1 core) | 1.2 s per 25 s (~5%) |
| Start menu shown | 28 800 ms | 770 ms (prewarm) |
| clean `--quit` | never answered | normal |

**Rules this leaves behind:**

- **A widget must never be measured at an unbounded width.** Any host that
  shrinks a widget — the bar today, anything else later — gives it a real
  size first. A `Viewbox` is not a size.
- **A widget that reads `ActualWidth` back into its own layout has to
  converge.** `SizeChanged -> change the layout -> change the desired size` is
  only safe where the width is imposed from outside. Widgets that merely hide
  optional parts on `SizeChanged` (Audio, Media, Weather, QuickToggles and the
  rest) are fine; the Shelf was the only one steering a `UniformGrid`'s column
  count with it.
- **`Refresh` on a bar is a hot path.** It runs on every window title change
  on the desktop, coalesced to 90 ms. Anything in it that costs more than
  moving an element must be built once and `Detach`ed into the new tree.
- **A loop that leaves no log line is still a loop.** Ruling something out
  because the log looks calm only rules out the loops that log. Sampling
  `CPU` and `PrivateMemorySize64` over 20 s separates "busy" from "spinning"
  in a way the log cannot.

### Intellihide losing z-order fights, and the bar catching the cursor over fullscreen games, 2026-09-29

The user reported the Start menu and the bar's own right-click menu ending up
**under** the auto-hiding bar instead of clearing it — "much more like how the
native windows systems would treat that logic" — and separately, that moving
the mouse fast in a game could get it "caught on the bar."

**The menu-under-the-bar bug** was a z-order fight the bar was winning by
accident. `BarWindow.PlaceAt` moved the window with
`SetWindowPos(_hwnd, HWND_TOPMOST, ...)` on every slide frame. `HWND_TOPMOST`
as the insert-after handle does not mean "stay above ordinary windows" — the
window already gets that from WPF's `Topmost = true` (`WS_EX_TOPMOST`), set
once at construction. It means "jump to the very front of the topmost band,"
ahead of every other topmost window, including the Start menu and any
`ContextMenu`, each of which only asserts `Topmost` once, when it opens.

`SlideTo`'s re-entry guard made this worse than "just" every slide frame: it
only skipped a repeated call while a previous animation was still enabled, not
while the bar was already sitting still at the requested target. `Peek()`
asks for the same target every 90 ms as long as nothing changes, so a
concealed-and-staying-concealed bar was restarting a zero-length "animation"
and re-issuing `SetWindowPos(HWND_TOPMOST, ...)` roughly eleven times a
second, forever. Against that, a Start menu or a context menu that asserted
`Topmost` once had no chance — the bar reclaimed the front of the topmost
band well within a second of the menu opening, and everything drawn where the
two overlapped showed the bar.

Fix: `PlaceAt` now moves with `SWP_NOZORDER` (`SetWindowPos(_hwnd, IntPtr.Zero,
..., SWP_NOZORDER | ...)`), which repositions without touching z-order at all.
The window stays in the topmost band on its own (`WS_EX_TOPMOST` is a window
style, not something `SetWindowPos` has to keep re-asserting), so it still
sits above every ordinary window, and now leaves the Start menu and context
menus wherever their own one-time `Topmost` put them. `ABN_FULLSCREENAPP`'s
explicit `HWND_BOTTOM`/`HWND_TOPMOST` toggle is untouched — that one really is
a deliberate, occasional z-order change in response to a real state change
(a fullscreen app arriving or leaving), which is exactly the kind of call
`HWND_TOPMOST` is for. `SlideTo` also now no-ops when the bar is already
sitting at the requested position and nothing is animating, which stops the
90 ms re-trigger at the source as well as the z-order churn it caused.

**The "caught on the bar" bug in games** was `Peek()`'s hover check having no
exception for something covering the whole screen. `Peek`'s reach for the
edge runs all the way to the screen boundary — deliberate, so a floating dock
a gap clear of the edge is still reachable — which means any fast mouse pass
across the top of the screen (aiming, panning, whatever a game does with the
cursor) satisfied `over` and pulled the bar into view over the game, every
time. The existing fullscreen handling only answers `ABN_FULLSCREENAPP`, the
shell's own exclusive-fullscreen broadcast, which a borderless or
windowed-fullscreen game — the common case now — never sends: to the shell
that is just an ordinary top-level window sized to the monitor.

Fix: a new `CoversFullScreen`, checked only when `over` would otherwise force
a reveal (so it costs nothing the rest of the time), looks for any window
whose rect matches the display's bounds exactly rather than merely reaching
under the bar's strip (`ShouldHide`'s check, which is deliberately looser —
intellihide should duck for an ordinary maximized window too). When one is
found, hovering the edge no longer counts, the same exception the real
taskbar makes for exclusive fullscreen, extended to the borderless case by
rect instead of by shell notification.

**Also added, same investigation:** the bar is now click-through
(`WS_EX_TRANSPARENT`) for the whole time it is concealed, toggled in
`SyncClickThrough` off the same `_hidden` flag `SlideTo` already tracks —
interactive the instant it starts coming back, click-through the instant it
starts leaving, not once either trip finishes. `Peek` never needed hit-testing
for this: it polls `GetCursorPos` on a timer, not WPF mouse events. All the
concealed sliver's hit-testable pixels were doing was giving Windows
something to catch the cursor on. This is a second, independent guard against
the same "caught on the bar" complaint — `CoversFullScreen` stops the bar from
*appearing* uninvited during a fullscreen game; this stops the OS from ever
routing input to the sliver in the first place, for any window at all, not
only ones judged fullscreen.

**Verified after the fix**, bar enabled (Panel, `Hiding: WhenCovered`,
`AutoHide: true`, 240 ms slide): starts clean, one `work area reset` and one
`bar: started` line, no repeats. CPU settles to ~0.5–0.6 s per 7–8 s wall
(~7%) with nothing open, working set flat at ~370 MB, handle count flat. No
errors in the log. `--quit` answered cleanly on the next restart (previous
sessions sometimes needed a kill; not conclusive on its own, but consistent
with less contention on the UI thread from a quieter Peek loop).

**Not verified by hand yet** — needs the user at the keyboard: opening Start
and the bar's right-click menu while auto-hidden, to confirm they now draw
above the bar instead of under it; and the actual fullscreen/borderless-game
case, since that needs a real game to flick the mouse in.

### Three more from the same conversation: the sliver still caught the cursor, the hover zone ate window chrome, and the home screen had no margin for an unreserved bar, 2026-09-29 (later)

Follow-up to the z-order and fullscreen entry above, same session, after the
user kept finding more of the same family of bug.

**The click-through fix was not enough.** The user plays Body Cam, which uses
the mouse for an in-game tablet, and reported the real Windows cursor
becoming visible over the game — something the game itself never draws —
right at the top of the screen. `WS_EX_TRANSPARENT` only changes which window
answers a click; it does nothing to which window's cursor policy applies to a
pixel while the mouse is merely sitting over it, and **that is decided by
z-order, not by any extended style.** The concealed sliver is still a real,
topmost window sitting at the screen's edge, and Windows resolves cursor
ownership for a pixel to whatever is topmost there — click-through or not.

Fixed by extending the existing `ABN_FULLSCREENAPP` treatment (send the window
to `HWND_BOTTOM` while covered, back to `HWND_TOPMOST` once it is not) to the
borderless case `CoversFullScreen` already finds by rect. Both paths now go
through one gate, `SyncFullScreenYield`, keyed on a single `_belowFullscreen`
bool so the shell's own exclusive-fullscreen notification and Peek's own
rect check cannot re-assert opposite z-orders against each other. Once the
bar is actually behind the window that covers the screen, every pixel the
cursor can be over resolves to that window, because that window is what is
actually in front there — not a style flag, an actual z-order fact.

**Bars were revealing far too easily.** The user: "bars come up when you
cover any part of where they would be with the mouse... I cannot close
windows without the menu bar coming down to get in the way." The hover check
(`Peek`) used the bar's own shown rect as one bound and the screen's edge as
the other, for every style alike. For a floating dock that is correct — the
gap between the edge and the dock is dead space nothing else lives in, and
has to be crossed to reach it. For a strip flush with the edge it made the
entire bar's own thickness, across the full width of the screen, into the
hot zone — for a 34 px menu bar, the top 34-40 px of the *whole screen*,
which is exactly where a maximized window's own close button sits. Closing a
window meant the cursor crossed the reveal zone first and pulled the bar down
on top of the click.

Fixed by splitting the two cases: a floating dock keeps reaching across its
gap as before; a strip now answers only to the last couple of pixels at the
true screen edge (`Math.Round(2 * scale)`, the same idea as the concealed
`Sliver`), still along its full length so the edge stays easy to find, but no
longer as tall as the bar itself. This is the same fix in spirit as
Windows' own taskbar, which wants the cursor at the literal edge, not
anywhere within the height it would occupy once shown.

**The home screen had no margin for an unreserved bar at all.** New ask,
same conversation: an intellihide bar should not be able to cover a home-screen
icon or widget when it reveals. It turns out it always could —
`BarService.Build()` passes `reserveSpace: !Settings.AutoHide`, so any
auto-hiding bar (which is the normal way to run one) never registers an app
bar strip, and `DesktopSurface.WorkAreaFor` lays the grid out using the
**raw OS work area**, which excludes nothing that was never reserved. That is
correct for *other* windows — an auto-hiding bar deliberately does not want
to push them around every time it hides, which is the whole point of not
reserving — but the home screen is Hearth's own layer, not "another window",
and the bar is a real strip of pixels the instant it reveals.

Fixed with a new, narrower concept that only the home screen consults:
`BarService.UnreservedMarginFor(monitor)`, returning the edge and thickness of
whichever bar on that display does not reserve space (floating docks
excluded — they already sit clear of the edge on purpose, the same as over
any other window). `WorkAreaFor` subtracts that margin from the grid's own
usable area, in physical pixels via the monitor's own scale, on top of the
real OS work area rather than instead of it. Nothing about app-bar
registration changes; other windows still are not pushed around by an
auto-hiding bar. Only Hearth's own tile grid now leaves the room.

**Verified after all three:** clean build, clean restart (`MenuBarAndDock`,
2 bar windows on screen), no errors, CPU settles to the same ~10% idle range
as before, `relayout` succeeded with one fewer grid row than an earlier
Panel-style run at similar dimensions — consistent with a margin now being
subtracted, though that comparison has too many other variables changed
(different style, different total bar footage) to call it proof on its own.

**Still needs the user at the keyboard** for the part none of this can be
checked from here: whether Body Cam's cursor now stays hidden, whether a
maximized window's close button is comfortably clickable again, and whether a
home-screen icon or widget at the bar's edge visibly stays clear when the bar
reveals.

### Folder double-click, drag-to-reorder, opening animation, and 0.3.0, 2026-10-22

- **Double click with "Open with a single click" off** never opened anything
  inside a folder, and a folder tile opened and closed in the same press:
  `OnMouseDoubleClick` fires during the second press, which then reached
  `OnFolderPress` as a click outside the new panel. Double clicks are now
  counted from `ClickCount` in the press/release path (`_pressIsDoubleClick`)
  and `OnMouseDoubleClick` is gone. Confirmed by the user.
- **Dragging inside an open folder** reorders it, Android-style: the panel
  stays open while the pointer is over it (`UpdateFolderDrag`), tiles slide
  to their new slots, and letting go saves `GridGroup.ItemIds`
  (`CommitFolderOrder`). Leaving the panel closes it and the drag continues
  through `CompleteDrop` as before. Confirmed by the user.
- **Opening animation** grows the panel from the folder tile (scale plus
  translate about the top-left corner), 200 ms. New setting
  `CenterOpenFolders` (off by default; Settings -> Layout and behaviour):
  off, the panel opens over its tile, clamped to the display; on, centred.
  Closing is still instant.
- **Version** bumped to 0.3.0.
- The user builds and restarts Hearth themselves; do not run the build or
  `start-hearth.cmd` unless asked.

## Build status

**Builds clean.** .NET SDK 8.0.425 installed 2026-09-15 via
`winget install Microsoft.DotNet.SDK.8`. Debug and Release both compile with
0 errors. Three warnings are outstanding in the uncommitted bar work, all in
`Bar/`: `BarTrash.OnDrop` hides `UIElement.OnDrop` (CS0114),
`StackFlyout.Owner` hides `Window.Owner` (CS0108), and an unawaited call at
`BarWindow.cs:303` (CS4014).

Runs on the machine above. Still unverified:

1. Other Windows builds — Win10 and earlier Win11 may still composite child
   windows; the top-level approach should work there too, but it is untested
2. The `RootScale` physical-pixel scheme on a genuinely mixed-DPI setup (both
   displays here are 100%)
3. Folder and widget drag gestures, by hand (logic is tested; gestures are not)
4. `GetDIBits` fallback path in `IconExtractor` — the DIB-section fast path is
   the one that will normally run, so the fallback is effectively untested
5. Whether `IEnumShellItems` marshalling on `AppsFolderCatalog` needs
   `Marshal.Release` tuning under repeated enumeration

## Next thing to build

See [../docs/roadmap.md](../docs/roadmap.md). New widgets follow
[../src/Hearth.App/Widgets/README.md](../src/Hearth.App/Widgets/README.md). First, have the user test by hand
what automation could not: the new widgets' "NOT verified" list above, and the Win key and Start button, drag and drop on
pages, folders, touch swiping, the power menu, jump-list clicks, and badges
once notifications are on.
