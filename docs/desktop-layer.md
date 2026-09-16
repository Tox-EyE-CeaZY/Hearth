# The desktop layer

How Hearth becomes the desktop without replacing the shell — and why the
approach every guide recommends does not work on current Windows 11.

## What Hearth does

Hearth's UI is a **top-level window** covering the whole virtual screen, pinned
to the **bottom of the Z-order**, directly above the shell's own desktop window
(Progman). Explorer keeps running. The taskbar, <kbd>Win</kbd>+<kbd>E</kbd>, and
every shell dialog behave normally, nothing is written to the registry, and
quitting Hearth is instant and total.

Explorer's icon layer (`SHELLDLL_DefView`) is hidden while Hearth runs, by a
window-visibility toggle only. Files on the desktop are never touched.

Hearth paints the wallpaper itself (`WallpaperService`), so it does not need to
be transparent. A transparent WPF window would take the layered-window path and
lose hardware rendering across a surface the size of every display combined.

## Why not a child of the desktop window

The well-known technique — used by Wallpaper Engine and Lively, and described in
most guides — is to parent your window *into* the desktop: either into Progman
or into the `WorkerW` that Progman creates when sent the undocumented message
`0x052C`.

On Windows 11 build 26200 that fails, and it fails in the most misleading way
possible. Measured with a plain GDI window filled solid red, no WPF involved:

| Host | Input reaches the window | Pixels appear |
|---|---|---|
| Child of Progman | yes | **no** |
| Child of the wallpaper `WorkerW` | no — it is `WS_DISABLED` | no |
| Top-level at `HWND_BOTTOM` | yes | **yes** |

Explorer now draws the desktop through its own compositor and does not
composite foreign child windows. As a child of Progman, Hearth had correct size,
correct Z-order and working input — tiles could be clicked and apps launched —
while nothing was ever drawn. That symptom looks exactly like a WPF rendering
bug, and several genuine-but-irrelevant WPF fixes were made chasing it before
the GDI test isolated the real cause.

### The `WorkerW` hazard

The wallpaper `WorkerW` does not exist from boot. Progman paints the wallpaper
itself until something sends it `0x052C` — and Windows does that on its own
when animating a wallpaper change or slideshow step. The resulting `WorkerW`
lacks `WS_CLIPSIBLINGS`, so it paints over any sibling window it overlaps. For
a child-window design that means "renders for a moment, then vanishes."

A top-level Hearth is not a sibling of it, so none of this applies. **Hearth
never sends `0x052C`.**

## Staying at the bottom

A top-level window will be raised by activation and by the window manager
unless it is stopped. `DesktopHost` stops it three ways:

- `WS_EX_NOACTIVATE` — clicking Hearth does not activate it.
- `WM_MOUSEACTIVATE` → `MA_NOACTIVATE` — belt and braces for the same thing.
- `WM_WINDOWPOSCHANGING` — any Z-order change aimed at the window is rewritten
  to `HWND_BOTTOM` before it happens.

`WS_EX_TOOLWINDOW` keeps it out of Alt+Tab and the taskbar.

### Show desktop (Win+D)

Win+D does not minimise Hearth. Measured: Hearth stays visible and
un-minimised, while Windows raises Progman from the bottom of the stack to just
under the taskbar and makes it the foreground window, burying Hearth. So
`DesktopHost` watches foreground changes (`SetWinEventHook`,
`EVENT_SYSTEM_FOREGROUND`): when Progman or a WorkerW comes to the front,
Hearth lifts itself to sit directly above it, and as soon as anything else
becomes foreground it returns to the bottom.

`SetWindowPos(HWND_TOP)` does not work for this. Called from a background
process it reports success and changes nothing. Inserting after a *named*
window is not restricted, so Hearth inserts itself after whichever window is
currently directly above Progman. Explorer keeps adjusting the order for a
moment after the event, so the placement is re-applied a few times over the
next second.

### Text entry

Because the window is non-activating it never receives keystrokes. Renaming a
folder temporarily clears `WS_EX_NOACTIVATE`, calls `SetForegroundWindow`
(allowed, since the user has just clicked Hearth), and restores the style when
the edit ends.

Because the window never activates, a WPF context menu is never told when the
user clicks another application. `OpenMenu` watches the foreground window and
closes the menu when it changes. (Forcing the popup to the foreground instead
makes WPF dismiss the menu the instant it opens.)

## Coordinates

The window spans the virtual screen in physical pixels. Displays can run at
different DPI scales, so no single DIP-to-pixel ratio is correct everywhere.
`RootGrid.LayoutTransform` is set to `1 / dpiScale`, making one WPF unit equal
one physical pixel; all layout then works in the same virtual-screen coordinates
`IDesktopWallpaper` reports monitor rectangles in, and each display's own scale
is applied per surface for icon sizing.

`IDesktopWallpaper` returns displays in device order, not screen order. The
primary display — the one whose top-left is the virtual origin — is sorted
first.

## Confirming things from outside

Tools that turned out to lie, and what to use instead:

- **`SetParent`'s return value** cannot confirm an attach; it returns the
  previous parent, which is legitimately the desktop.
- **`GetParent`** returns the *owner* for a `WS_POPUP` window. Use
  `GetAncestor(hwnd, GA_PARENT)`.
- **`PrintWindow`** forces a separate software render of WPF content. It showed
  content that never reached the screen. Use `CopyFromScreen` and confirm with
  `WindowFromPoint` that the sampled pixels belong to Hearth.

## Safety

Hiding the icon layer is the one thing a user would notice if it went wrong:

- `DesktopLayer.Dispose()` restores it.
- `AppDomain.ProcessExit` and `UnhandledException` restore it.
- `Application.DispatcherUnhandledException` restores it, then shuts down
  rather than limping on.

A hard kill skips all of those; restarting Explorer, or showing
`SHELLDLL_DefView` again, brings the icons back. When Explorer restarts it
broadcasts `TaskbarCreated`; Hearth survives (it is not Explorer's child) and
re-hides the fresh icon layer.
