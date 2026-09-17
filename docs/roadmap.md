# Roadmap

Ordered by what most changes whether Hearth is worth using, not by what is
easiest.

## Built

- Desktop-layer attachment (WorkerW), with Explorer-restart recovery
- Shell icon hide/restore, guarded on every exit path
- Per-monitor wallpaper rendering with scrim
- `shell:AppsFolder` enumeration (Win32 + MSIX) and desktop folder enumeration
- Icon extraction, analysis, adaptive background generation, shape masking,
  baked shadows, two-level cache
- Grid with free placement, drag to rearrange with swap, per-monitor layout
  persistence
- Context menus for items and background; settings surface
- Widget contract, registry and host; clock widget
- File drop from Explorer
- Top-level host pinned to the bottom of the Z-order; Win+D follows the desktop
- Multi-display layout: fill the primary, overflow to the rest; drag across displays
- A saved arrangement for each set of displays and grid size: unplugging a display
  merges its items in at their own size, and plugging it back in restores the
  old arrangement exactly
- Home-screen folders: drop an icon onto another to make one, onto a folder to
  add to it; 2x2 preview tile; open panel with rename; drag out to remove;
  one-item folders dissolve into their last item; ungroup
- Widgets can be dragged and removed from their own menu
- Widget resizing by grip: locked widgets snap to cells, unlocked widgets size
  and position freely (off-grid too); icons are moved out from under either
- Widgets: clock, now playing (Windows media session), calendar, system
  (CPU / memory / battery / disk), notes, weather (Open-Meteo; device location
  or city search, prompted when the widget is added)
- Hide items from the desktop (and unhide from the background menu); delete
  desktop files to the Recycle Bin; Uninstall for apps opens Installed apps
- Add apps drawer: searchable list of installed apps, one click to add or remove,
  new apps land where you right-clicked
- Notification badges on app tiles (from the shell's notification store)
- Taskbar-style jump lists in app right-click menus
- Start menu replacement: the Win key and Start button, a blurred backdrop, the
  Windows theme, its own pages (a 7x4 grid of apps, folders and widgets,
  copied once from the desktop), All apps, Categories, Widgets, and search
  over apps, settings, files and the web
- The real Windows context menu ("Show more options", or Shift+right-click)
  for items and for the desktop background
- Audio (output and input device switching, volume, mute) and Quick Toggles
  widgets
- A widget standard ([Widgets/README.md](../src/Hearth.App/Widgets/README.md)); every widget is a self-contained, auto-discovered folder; `WidgetView` lifetime, theme
  and thread handling, shared parts, saved data, background services
- Widgets: Tasks, Timer, Alarms (both ring through Windows' own scheduled
  alarm notifications, with Hearth's banner when notifications are off),
  Shelf (drop and drag-out, stash or real folder), Network (speeds, graph,
  ping), Clipboard (recent text and pins), Recent Files, Speed Dial (apps,
  websites, files, folders)
- `start-hearth.cmd` / `start-hearth.ps1`: build, clean quit (`--quit`),
  run from `artifacts\run`

## Next

### 1. Folder polish

The blurred backdrop behind an open folder (needs `Windows.UI.Composition`
interop; the panel currently sits on a plain dim scrim), reordering items
inside an open folder, and a smarter default name than "Folder".

### 2. Rename

Rename a desktop file from Hearth's own menu. The shell menu's Rename verb
expects an Explorer view to edit in and does nothing here.

### 3. Icon packs

See [icon-pipeline.md](icon-pipeline.md#what-is-not-built-yet). Highest
aesthetic return of anything on this list.

### 4. Multi-monitor item assignment

Items are all placed on the primary surface today. `MonitorLayout` is already
keyed per display; what is missing is deciding which display an item belongs to
and moving items between them by dragging across the seam.

### 5. More widgets



Media transport controls via
`GlobalSystemMediaTransportControlsSessionManager` is the highest-value one and
the reason the project targets a Windows 10 SDK TFM. Then calendar, weather,
system stats, folder preview.

Widget resize (drag corners, snap to cell spans) is needed before more than one
or two widgets is useful.

## Deliberately not doing yet

**Pages and an app drawer.** Explicitly out of scope — the desktop is one
surface, not a pageable home screen.

**Gestures.** Drag, click and right-click only. Nothing to learn.

**Shell replacement.** See [desktop-layer.md](desktop-layer.md). The overlay
approach gives most of the benefit for a fraction of the surface area and none
of the risk.

## The Start menu

Built (see above). Still open: dragging from All apps or search straight
onto a page, a live (rather than snapshot) blur, and Start's own widget
designs if the scaled desktop widgets turn out not to be enough.
