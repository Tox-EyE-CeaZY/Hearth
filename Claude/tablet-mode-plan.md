# Plan: tablet mode, settings screen, SYSTEM helper

Written 2026-09-16 at the user's request, to be built later. It brings
Windows 10's tablet mode back, and goes further, on top of Hearth.

**Progress (2026-09-17).** Phases 1 to 4 and the settings screen are built;
details and test results are in notes.md ("Tablet mode, phases 1 to 4").
Done: 3.1 (plus Auto by peripherals, which the user asked for), 3.2, 3.3,
3.4 (full screen, pages side by side), 4, 5.1, 5.2, snapping from 5.2
(not yet a divider for 5.3), 6.1, the 8.4 keyboard button, 9, and 10
(watchdog, restore command, recovery on the next start). Still to do:
phase 6 (the helper, 6.2, 6.3, 6.4), 8.1 as a Hearth panel (the bar opens
Windows' own quick settings for now), 8.2, 8.3, the split-view divider, and
a Quick Toggles pill. Everything touch-related still needs testing on the
real tablet.

The user's decisions (explicit, not to be second-guessed by a later session):

- **Make absolutely everything work that can be made to work.**
- **No code-signing certificate** (too expensive). Privileged work runs
  **as SYSTEM**, on purpose and with the user's clear approval: a scheduled
  task starts a helper that has UIAccess. This is the user's own app on the
  user's own machine, recreating a real Windows feature. It is legitimate.
  See section 2.3 for how it stays safe, and section 13 for why this does
  **not** contradict the widget rule "never run as administrator" (that rule
  is about widgets Gemini writes, not about this app-level helper).
- Stay on top of Explorer rather than replacing the shell (section 12
  explains why, and what would change that).
- An **official settings screen** replaces the settings scattered through
  right-click menus.

Status marks used below:

- **[HAVE]** already in Hearth and working
- **[PARTIAL]** a piece exists and needs extending
- **[NEW]** not started

Sizes are rough: **S** is under a day, **M** a few days, **L** a week or more.

---

## 0. What Windows 10's tablet mode did (the target)

| Windows 10 behaviour | Hearth plan |
| --- | --- |
| Full-screen Start | Full-screen home and app drawer (section 3.4) |
| Apps opened maximized; Store apps lost their title bar | Auto-maximize with rules, optional immersive mode (section 5) |
| Taskbar without app icons, with a back button | Hide the taskbar and add a navigation bar (sections 3.2, 3.3) |
| Swipe from the left: Task View | Gesture map (section 6), task switcher (section 4) |
| Swipe from the right: Action Center | Quick settings and notification panel (section 8) |
| Drag down from the top: close; drag to a side: snap | Drag to close, split view (section 5) |
| Switched on when the keyboard was detached | Posture detection (section 3.1) |
| The touch keyboard appeared by itself | Keyboard handling (section 8.4) |
| Desktop icons hidden | **[HAVE]** Hearth hides them |

---

## 1. What Hearth already has

| Piece | Status | Where |
| --- | --- | --- |
| Desktop layer above the wallpaper, below apps, following Win+D | [HAVE] | `Hosting/DesktopHost`, `DesktopLayer` |
| Hiding and restoring Explorer's desktop icons on every exit path | [HAVE] | `DesktopLayer`, `App.TearDown`, `installer/quit-hearth.ps1` |
| Recovery when Explorer restarts (`TaskbarCreated`) | [HAVE] | `DesktopHost` |
| Clean quit (`Hearth.exe --quit`) and a single instance | [HAVE] | `App`, `StartMenuController` |
| Taking over the Win key and Start button (low-level hooks) | [HAVE] | `Hosting/StartTrigger` |
| Full Start menu: pages, All apps, categories, widgets, search | [HAVE] | `Views/Start/*` |
| Swiping between Start pages with touch | [PARTIAL] | `StartMenuWindow.Pages` (untested on real touch) |
| Installed apps catalog, icons, adaptive tiles, jump lists | [HAVE] | `Services/InstalledApps`, `Core/Icons`, `Core/Shell/JumpLists` |
| Matching windows and tiles to apps (AppUserModelID) | [PARTIAL] | `DesktopSurface.AppExtras` matches tiles; windows are new |
| Notification badges from the shell's notification store | [HAVE] | `Core/Notifications/BadgeService`, `Core/Interop/WinSqlite` |
| Widgets, including audio and quick toggles | [HAVE] | `Widgets/*` |
| Taskbar geometry and auto-hide state reads | [PARTIAL] | `StartMenuWindow` (`SHAppBarMessage` ABM_GETSTATE) |
| Dialog styling, backdrops (Mica, Acrylic, blur) | [HAVE] | `Controls/DialogChrome`, `Views/Start/StartBackdrop` |
| Keyboard focus on demand for a non-activating window | [HAVE] | `DesktopHost.BeginKeyboardInput` |
| Windows notifications (scheduled) | [HAVE] | `Widgets/Framework/SystemNotifications` |
| Installer (per-user), start script, clean quit | [HAVE] | `installer/`, `build-installer.*`, `start-hearth.*` |
| Settings | [PARTIAL] | `HearthSettings` plus right-click menus; no settings screen |
| Tablet mode, navigation bar, task switcher, edge gestures | [HAVE] (2026-09-17) | `Tablet/*` |
| Settings screen | [HAVE] (2026-09-17) | `Views/SettingsScreen/*` |
| UIAccess helper | [NEW] | |

---

## 2. Architecture

### 2.1 Processes

```
Hearth.exe            normal user, not elevated (drag and drop from Explorer needs this)
  |-- everything visible: desktop, Start, widgets, tablet UI, settings
  |-- works fully on its own; the helper only adds abilities
  |
  |   named pipe  \\.\pipe\Hearth.Helper.<session id>   (ACL: this user and SYSTEM only)
  |
HearthHelper.exe      same user, but with UIAccess (started by the SYSTEM task)
  |-- owns touch interception (RegisterPointerInputTarget) and hands gestures to Hearth
  |-- moves, maximizes and closes elevated windows (UIAccess passes UIPI)
  |-- touch injection (InitializeTouchInjection), for gestures that pass touches on to apps
  |-- watchdog: if Hearth.exe dies, shows the taskbar and desktop icons again
  |
HearthLauncher (scheduled task "Hearth Helper", runs as SYSTEM, at sign-in and on demand)
  |-- runs HearthHelper.exe --launch, which as SYSTEM:
  |     WTSQueryUserToken(session) -> DuplicateTokenEx
  |     SetTokenInformation(TokenUIAccess = 1)       (needs SeTcbPrivilege, which SYSTEM has)
  |     CreateProcessAsUser(...) -> HearthHelper.exe runs in the user's session WITH UIAccess
  |-- this is the free alternative to a signed, Program-Files-installed UIAccess app
```

Three levels, each optional above the one below:

1. **Hearth.exe alone.** Everything in sections 3-5 that doesn't need to
   intercept touch or touch elevated windows: hide the taskbar, navigation
   bar, full-screen home, task switcher, auto-maximize, drag-to-close, split
   view, edge-strip gestures. This is most of tablet mode, and needs no
   elevation at all.
2. **+ HearthHelper.exe (UIAccess).** System-wide multi-finger gesture
   interception, and control of elevated windows. This is the part that
   makes gestures feel native.
3. **The SYSTEM launcher** exists only to grant the helper UIAccess without a
   certificate. It does nothing visible itself.

### 2.2 The SYSTEM launcher, concretely

- Installed by the installer **in admin mode** (a second, optional installer,
  or an "Enable advanced tablet features" step that asks for elevation once).
  Files go to `%ProgramFiles%\Hearth\` (a folder only admins can write), which
  is what makes SYSTEM launching them safe.
- Registers a **scheduled task** "Hearth Helper", principal `SYSTEM`, run
  level highest, triggers: at log on of the user, and on a custom event
  Hearth raises when it wants the helper back. A service is the alternative;
  a task is simpler and needs no service host.
- The task action is `HearthHelper.exe --launch`. As SYSTEM it duplicates the
  user's token, sets `TokenUIAccess`, and `CreateProcessAsUser` into the
  user's session. The launched helper is the same exe run as the user, but
  with UIAccess.
- **Alternative if scheduled tasks are unavailable:** a real Windows service
  (`HearthHelperService`) doing the same token work. More moving parts;
  keep as plan B.

### 2.3 Why this is safe (write this down so it isn't re-litigated)

- SYSTEM only ever launches **one fixed exe** (`%ProgramFiles%\Hearth\HearthHelper.exe`),
  from a directory non-admins can't write to. That is the rule that stops
  another program from substituting its own exe and inheriting privilege.
- The helper takes commands only over a named pipe whose ACL is **this user
  and SYSTEM only**, and only a small, fixed set of commands (move/maximize/
  close a window handle; start/stop gesture capture). It is not a general
  "run anything elevated" surface.
- The helper is **least-privilege for the job**: UIAccess (an integrity
  nudge from medium to just above), not full admin. It cannot install
  software or write protected files.
- Everything the user sees still runs unelevated, so Explorer drag-and-drop
  keeps working.
- All of this is opt-in: sections 1-level-1 work without it, and the
  installer step is clearly labelled and asks for elevation once.

---

## 3. Tablet mode core

### 3.1 Posture and mode switching — [NEW], M

- A `TabletMode` controller (`Hosting/TabletMode.cs`) with an explicit state:
  Off / On, plus "Auto".
- Auto reads convertible posture: `GetSystemMetrics(SM_CONVERTIBLESLATEMODE)`
  and `SM_TABLETPC`, plus the `WM_SETTINGCHANGE` "ConvertibleSlateMode" event,
  and the registry `...\ImmersiveShell` "TabletMode" value that Windows itself
  writes. Also expose a manual toggle (a quick-toggle pill and a settings
  switch) for desktops with a touchscreen.
- Entering tablet mode: hide the taskbar (3.2), show the navigation bar (3.3),
  bring home forward, turn on gestures (6). Leaving reverses each step. Every
  step is individually reversible and logged, the way icon hiding already is.

### 3.2 Hide the taskbar — [PARTIAL -> extend], S/M

- Hearth already reads taskbar geometry and auto-hide state
  (`SHAppBarMessage`). Extend to hide it:
  - Preferred: set the taskbar's own auto-hide (`ABM_SETSTATE` with
    `ABS_AUTOHIDE`), so Windows keeps managing it and restores cleanly.
  - Or hard-hide `Shell_TrayWnd` and the secondary `Shell_SecondaryTrayWnd`
    windows with `ShowWindow(SW_HIDE)`, remembering to restore them on exit
    and after an Explorer restart (`TaskbarCreated`, already handled).
- Reclaim the space: register Hearth's navigation bar as an **appbar**
  (`ABM_NEW` / `ABM_SETPOS`) so maximized apps stop above it, exactly as the
  real taskbar reserves its strip.
- Must survive Explorer restart and monitor changes. Reuse `DesktopHost`'s
  existing recovery.

### 3.3 Navigation bar — [NEW], M

- A thin always-on-top appbar window (like the Start window, non-activating),
  docked to the bottom (configurable edge), reserving its strip.
- Buttons: **Back**, **Home**, **Recents** (opens the task switcher, 4),
  optionally **Keyboard** (toggle the touch keyboard) and a clock/battery.
- **Back**: there is no universal back for desktop apps (true on Win10 too).
  Send `Alt+Left` (browsers, Explorer, Office, most document apps), fall back
  to `Esc`, with a small per-app override table in settings. Foreground app
  via `GetForegroundWindow`, its AUMID via the existing matcher.
- **Home**: bring Hearth's desktop forward (it already follows Win+D;
  factor out that raise).
- Buttons are big touch targets, themed with `DialogChrome`/`WidgetChrome`.

### 3.4 Full-screen home and app drawer — [PARTIAL], S

- The Start menu already is a full app drawer (pages, All apps, categories,
  search). In tablet mode, open it full-screen and borderless instead of the
  floating panel, and let Home/Recents/gestures drive it.
- Reuse `StartMenuWindow`; add a full-screen presentation mode. The page
  swipe (`StartMenuWindow.Pages`, [PARTIAL]) becomes primary here — test it
  on real touch (it never has been).

---

## 4. Task switcher — [NEW], M/L

The best part of the experience, and fully doable unsigned.

- A full-screen overlay of live window thumbnails.
- **Live previews** via DWM thumbnails (`DwmRegisterThumbnail` /
  `DwmUpdateThumbnailProperties`): the same real-time mechanism Alt+Tab and
  Task View use. Cheap; no bitmap copying.
- Window list: `EnumWindows` filtered to top-level, visible, non-tool,
  non-cloaked windows (`DWMWA_CLOAKED` to skip virtual-desktop-hidden and
  suspended UWP), titled. Icon and name from the existing AUMID matcher.
- Interactions: tap a card to foreground it; **swipe a card up to close**
  (`WM_CLOSE`); a persistent "close all". Cards in a scrollable/grid layout,
  touch-first.
- Reads only. Foregrounding another window from a background app can hit the
  `SetForegroundWindow` restriction; use the known `AllowSetForegroundWindow`
  / attach-input workaround, or route the actual foreground call through the
  helper when present.

---

## 5. Window management — [NEW], M each

### 5.1 Auto-maximize

- Watch for new top-level windows: a `WinEventHook` for
  `EVENT_OBJECT_SHOW` / `EVENT_SYSTEM_FOREGROUND` (Hearth already hooks
  foreground for Win+D).
- Maximize real app windows; **skip** tool windows, owned/dialog windows,
  fixed-size windows (no `WS_THICKFRAME`/`WS_MAXIMIZEBOX`), and a per-app
  exception list (some apps misbehave maximized). `ShowWindow(SW_MAXIMIZE)`.
- Elevated target windows can't be touched by an unelevated Hearth -> route
  through the helper when present, otherwise skip.

### 5.2 Drag down from the top to close

- With the gesture layer (6): a drag starting at the top edge over a
  maximized window, past a threshold, closes it (`WM_CLOSE`), with a shrink
  animation of a DWM thumbnail for feedback.

### 5.3 Split view

- Hearth positions two windows (left/right halves of the work area) and draws
  a draggable divider (a thin always-on-top window); dragging it resizes both
  via `SetWindowPos`. This is Hearth doing Snap itself, so it works the tablet
  way without FancyZones-style dependencies.
- Elevated windows via the helper.

---

## 6. Gestures — [NEW], L (the hard part)

Three capture methods, combined; each is independently useful.

### 6.1 Edge strips — no elevation, do first

- Thin (a few px) always-on-top, non-activating, transparent windows along
  each screen edge. A touch that **starts** in a strip lands on Hearth.
- Covers: swipe up from the bottom (Home / app drawer / switcher), swipe in
  from left (task switcher), swipe in from right (quick settings, 8), swipe
  down from top (drag-to-close hand-off).
- WPF `Manipulation`/`Touch` events for tracking. The page-swipe code is a
  starting point.
- Limitation: only gestures that begin at an edge; can't see multi-finger
  gestures out in the middle of an app.

### 6.2 Raw touch (background) — no elevation

- Read the touchscreen as a raw HID / `WM_POINTER` source even when Hearth is
  not foreground, to recognise multi-finger gestures (three-finger swipe,
  four-finger tap) anywhere.
- `RegisterPointerInputTarget` needs UIAccess (6.3); without it, use raw
  input (`RegisterRawInputDevices` for the touch usage page) which delivers
  copies of contacts **without** consuming them.
- Limitation: sees the touches but can't stop them reaching the app beneath,
  so use it for gestures that don't need to swallow input (recognise, then
  act), or pair with 6.3.

### 6.3 UIAccess interception — via the SYSTEM helper

- The helper (UIAccess) calls `RegisterPointerInputTarget` to **redirect**
  touch on chosen regions to itself, recognises the gesture, and either acts
  or replays the touch to the app with `InjectTouchInput`. This is how the
  real shell does edge swipes.
- Also lets us **turn off Windows' own edge gestures** that would compete
  (EdgeUI), via the policy in 6.4.
- The helper streams recognised gestures to Hearth over the pipe; Hearth
  decides what each does.

### 6.4 Turning off Windows' competing gestures

- Windows 11's own edge swipes fight ours. Disable via policy
  (`...\Policies\Microsoft\Windows\EdgeUI` AllowEdgeSwipe = 0) — needs admin,
  so it's an installer/settings option done through the elevated step, and
  reversible.

---

## 7. (folded into 6)

---

## 8. Quick settings, notifications, touch keyboard

### 8.1 Quick settings panel — [PARTIAL], M

- A slide-in panel (swipe from the right, 6). Hearth already has audio,
  brightness-ish and toggle widgets; reuse `Widgets/QuickToggles` and
  `Widgets/Audio` content in a panel form. Add Wi-Fi/Bluetooth/rotation-lock
  quick actions (mostly `ms-settings:` deep links, some via WinRT radios).

### 8.2 Notifications — [PARTIAL], L

- Hearth reads the shell notification store for badges already
  (`BadgeService`, `WinSqlite`). A full notification panel is a bigger job
  (listening to `UserNotificationListener`, which needs capability consent).
  Mark as later; badges plus "open Action Center" is enough to start.

### 8.3 Rotation — [NEW], S

- Handle display rotation cleanly (Hearth spans the virtual screen; it
  already rebuilds on display changes). Add a rotation-lock quick action.

### 8.4 Touch keyboard — [PARTIAL], S/M

- Windows 11 still auto-shows the touch keyboard in many apps. For Hearth's
  own fields, force it: the documented `FrameworkInputPane` / the
  `TabTip.exe` invoke, or the shell's input-pane COM. Tie into the existing
  `WidgetKeyboard`/`BeginKeyboardInput` opt-in.

---

## 9. Official settings screen — [NEW], M

Replaces settings scattered in right-click menus with one window.

- A proper `SettingsWindow` (`Views/Settings/`), `DialogChrome`-styled, touch-
  friendly, opened from the desktop menu, the Start options button, and the
  nav bar. Left-hand categories, right-hand content.
- **Migrate what already exists** (`HearthSettings`, today set through menus):
  - **Home**: icon shape, size, grid gap, labels, home style, single-click,
    uniform backgrounds, baked shadows, wallpaper dim, hide Windows desktop
    icons, show installed apps.
  - **Start**: use Hearth's Start, backdrop (Mica/Acrylic/blur), web search URL.
  - **Theme**: dark/light (today the Quick Toggles pill), accent later.
  - **Widgets**: which are on the board, board order.
- **New sections** for this plan:
  - **Tablet**: mode Off/On/Auto, which edge the nav bar docks to, nav-bar
    buttons, auto-maximize on/off + exceptions, gestures on/off + per-gesture
    map, disable Windows edge swipes (elevated).
  - **Advanced tablet features (helper)**: status (installed / running /
    not installed), an "Enable" button that runs the elevated installer step,
    and a plain-language explanation of what SYSTEM + UIAccess means and why.
  - **Startup**: start Hearth at sign-in (the installer's Run entry), start in
    tablet mode.
- `HearthSettings` grows the new fields; keep back-compat (only add
  properties, as with the widgets). Consider splitting tablet settings into
  their own file the way weather moved out.
- Keep the right-click menu items working (or have them open the matching
  settings section), so nothing is lost.

---

## 10. Safety and recovery — thread through everything

- Every takeover (taskbar hidden, gestures captured, windows maximized) is
  individually reversible and logged, like icon hiding today.
- **Watchdog**: if Hearth.exe crashes, the helper (or, without it, the
  existing exit paths and `quit-hearth.ps1`) restores the taskbar, desktop
  icons and Windows gestures. Add taskbar-restore to `App.TearDown` and the
  unhandled-exception path.
- A global "panic" (a hotkey and a tray fallback) that drops tablet mode and
  restores the normal shell instantly.
- Test the ugly cases by hand: crash, kill, Explorer restart, monitor
  plug/unplug, rotation, RDP session, fast user switch, lock screen.

---

## 11. Suggested phases

1. **Tablet mode switch + taskbar + nav bar + full-screen home** (3). Biggest
   visible change, no elevation, all reversible. Ship behind a settings toggle.
2. **Task switcher** (4). High value, self-contained, unsigned.
3. **Window management** (5): auto-maximize, drag-to-close, split view.
4. **Edge-strip gestures** (6.1) + quick settings panel (8.1).
5. **Settings screen** (9) — can slot in earlier; it's independent.
6. **The helper** (2.2, 6.3, 6.4) + raw touch (6.2): native-feeling gestures
   and elevated-window control. This is the elevation step.
7. **Polish**: posture auto-switch, touch keyboard, rotation, notifications.

Phases 1-5 deliver a real tablet experience with zero elevation. Phase 6 is
the upgrade the SYSTEM helper unlocks.

---

## 12. Why not replace the shell

Setting Hearth as the shell (the `Shell` registry value instead of
`explorer.exe`) would force us to rebuild the notification-area tray, the
quick-settings and notification flyouts (Explorer-owned on Win11), Win+X, and
a long tail of launch plumbing. ManagedShell (Cairo) shows it's possible, but
for a tablet layer it mostly adds breakage. Stay on top of Explorer. Only
revisit if hiding the taskbar proves unreliable across updates — and even
then, hiding is a smaller problem than owning the whole tray.

---

## 13. Related: widget plugins (separate track) — [NEW], M

Referenced from `Widgets/README.md`. Today widgets are compiled into
Hearth.exe and found at start-up (metadata scan, `WidgetRegistry`), so a new
widget needs a rebuild. To load widgets from separate DLLs at runtime
(and make "Refresh" notice new ones), we'd add an `AssemblyLoadContext` that
loads `%AppData%\Hearth\Widgets\*.dll`, feed those assemblies into the same
discovery, and refresh the registry on demand. Out of scope for tablet mode,
but the discovery seam is already there.

**Note on scope vs. the widget rulebook:** `Widgets/GEMINI.md` tells widget
authors "never run as administrator." That is correct for widgets — a clock
widget must never elevate. It does **not** apply to this app-level tablet
helper, which is a deliberate, user-approved architectural decision (section
2). Different scope. A future session should not treat the helper as a
violation of the widget rule.

---

## Open questions for the user (later)

- Touch hardware to test on? (The notes say touch is untested on the current
  two-monitor desktop; gestures need a real tablet.)
- One installer with an optional elevated "advanced features" step, or a
  separate helper installer?
- How aggressive should auto-maximize be by default?
- Auto-enter tablet mode on posture change, or manual only?
