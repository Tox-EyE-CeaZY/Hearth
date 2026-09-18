# Git Watch

A tracker for a specific Git repository, showing the current branch, commits ahead/behind, and uncommitted changes.

### Files
- `GitWatchWidget.cs`: UI logic and Settings window
- `GitWatchData.cs`: Data model storing the repository path
- `GitRunner.cs`: Background task that runs git status

### Data
Saves `git-watch.json` using `WidgetStore` to persist the selected Git repository path.

### Windows APIs
- Uses `System.Diagnostics.Process` in a background thread to run `git.exe`.

### Background work
- Periodically runs `git status -sb` in `Task.Run` every 2 minutes.
