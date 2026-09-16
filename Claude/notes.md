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

Folders (drop icon onto icon → group). The model exists
(`GridGroup`, `GridPlacement.ParentGroupId`); the gesture, transition and group
preview tile do not. See [../docs/roadmap.md](../docs/roadmap.md).
