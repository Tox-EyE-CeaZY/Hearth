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

## Build status

**Builds clean.** .NET SDK 8.0.425 installed 2026-09-15 via
`winget install Microsoft.DotNet.SDK.8`. Debug and Release both compile with
0 errors, 0 warnings.

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
