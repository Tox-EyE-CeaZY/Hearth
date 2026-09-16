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
delete files, does not move anything on your desktop, and does not write to the
registry or change your shell. Quitting Hearth — or Hearth crashing, or being
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
dotnet run --project src/Hearth.App
```

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
| `src/Hearth.App/Widgets` | Widget contract and built-ins |

`Hearth.Core` knows nothing about hosting or app lifetime, so the catalogs and
the icon pipeline can be lifted into another WPF host as-is.

## Settings

Right-click the desktop background. Shape, icon size, home style, labels,
widgets and the installed-apps toggle all live there.

Stored as JSON you can edit directly:

- `%AppData%\Hearth\settings.json`
- `%AppData%\Hearth\layout.json`
- `%LocalAppData%\Hearth\icons\` — rendered tile cache, safe to delete
