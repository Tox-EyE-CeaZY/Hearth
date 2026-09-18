# SteamLauncher

A quick launcher for your installed Steam games. It automatically detects installed games from your local Steam library.

### Files
- `SteamLauncherWidget.cs`: UI logic
- `SteamLibrary.cs`: Parser for Steam installation files

### Data
None, relies on Steam library paths on disk.

### Windows APIs
- `Registry.GetValue` to find the Steam installation path.

### Background work
- Reads `libraryfolders.vdf` and `appmanifest_*.acf` files in `Task.Run` when refreshed or first opened.
