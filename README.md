# Hearth

An Android-style home screen that replaces the Windows desktop.

Hearth takes over the desktop's wallpaper layer and draws its own grid of apps,
folders and widgets there — adaptive icons in a consistent shape, free
placement, drag to rearrange. Explorer keeps running underneath, so the
taskbar, <kbd>Win</kbd>+<kbd>E</kbd>, <kbd>Win</kbd>+<kbd>D</kbd> and every
shell dialog behave exactly as they always did.

> **Status: early.** The desktop layer, icon pipeline, grid, drag-rearrange and
> widget host are built. Folders, icon packs and the Start-menu replacement are
> not. See [docs/roadmap.md](docs/roadmap.md).

## Why it looks different from a big-icons desktop

Android icons look coherent because the platform forces every one of them into
a single silhouette with a single safe zone. Windows icons are a free-for-all:
different aspect ratios, different padding, baked-in shadows, white logos on
transparency, and plenty that still ship nothing above 32px.

So Hearth does what Android does — it normalises them. Each icon is extracted at
256px, measured, and then either masked straight into your chosen shape (if it
already fills a square) or composited onto a generated background derived from
its own dominant colour. That one step is most of the difference between "an
Android home screen" and "Windows XP with the icon slider turned up".

Details in [docs/icon-pipeline.md](docs/icon-pipeline.md).

## Nothing is destructive

Hearth hides Explorer's icon layer by toggling window visibility. It does not
delete files, does not move anything on your desktop, and does not change your
shell. Its one registry write is its name for Windows notifications, under
`HKCU\Software\Classes\AppUserModelId\Hearth.Desktop`, which the Timer and
Alarms widgets use. Quitting Hearth — or Hearth crashing, or being
killed — puts the normal desktop straight back. Every exit path, including the
unhandled-exception handler, restores the icon layer.

If it ever does go wrong, restarting Explorer (Task Manager → Windows Explorer →
Restart) rebuilds the icon layer from scratch.

## Building

Requires the **.NET 8 SDK** (with the Windows Desktop workload) and Windows 10
1809 or newer.

```powershell
winget install Microsoft.DotNet.SDK.8
dotnet build Hearth.sln -c Release
```

## Running

Double-click **`start-hearth.cmd`**, or run `.\start-hearth.ps1` from
PowerShell. The script:

1. builds the solution (Release; `-Configuration Debug` for a debug build),
2. asks a running Hearth to quit cleanly (`Hearth.exe --quit`), which saves
   the layout and puts the Explorer icons back, and kills it only if it
   doesn't quit in time,
3. copies the build to `artifacts\run\` and starts Hearth from there.

Hearth runs from `artifacts\run\` so the running copy never locks the build
output, and a failed build leaves the running Hearth alone.

| Option | What it does |
| --- | --- |
| `-NoBuild` | Restart the last build without building |
| `-Stop` | Quit Hearth and don't start it again |
| `-Configuration Debug` | Build and run the Debug configuration |

If PowerShell refuses to run scripts on your machine, use the `.cmd` file,
which runs the script with `-ExecutionPolicy Bypass`.

`Hearth.exe --quit` and `Hearth.exe --start [pages|all|categories|widgets]`
also work on their own, sent to the running copy.

## Layout

| Path | What lives there |
| --- | --- |
| `src/Hearth.Core/Interop` | Win32 and Shell COM declarations |
| `src/Hearth.Core/Shell` | App and desktop enumeration, launching |
| `src/Hearth.Core/Icons` | Extraction, analysis, adaptive rendering, caching |
| `src/Hearth.Core/Layout` | Grid model and persistence |
| `src/Hearth.Core/Wallpaper` | Reading the user's wallpaper per monitor |
| `src/Hearth.App/Hosting` | The WorkerW desktop-layer attachment |
| `src/Hearth.App/Views` | The home screen window and its interactions |
| `src/Hearth.App/Widgets` | Everything widget-related: the contract, shared parts (`Framework/`), and one self-contained folder per widget. **[How to write a widget](src/Hearth.App/Widgets/README.md)** |

`Hearth.Core` knows nothing about hosting or app lifetime, so the catalogs and
the icon pipeline can be lifted into another WPF host as-is.

## Settings

Right-click the desktop background. Shape, icon size, home style, labels,
widgets and the installed-apps toggle all live there.

Stored as JSON you can edit directly:

- `%AppData%\Hearth\settings.json`
- `%AppData%\Hearth\layout.json`
- `%LocalAppData%\Hearth\icons\` — rendered tile cache, safe to delete
- `%AppData%\Hearth\*.json` — widget data (tasks, alarms, timer, shelf,
  Speed Dial, clipboard pins)
- `%LocalAppData%\Hearth\Shelf\` — the Shelf widget's holding area
- `%LocalAppData%\Hearth\hearth.log` — the log for the current run
