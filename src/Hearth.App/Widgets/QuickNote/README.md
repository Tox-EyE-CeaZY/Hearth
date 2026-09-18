# Quick Note

A rapid-entry text widget that appends to a local Markdown file. Ideal for capturing fleeting thoughts into Obsidian or similar tools.

### Files
- `QuickNoteWidget.cs`: UI logic and Settings window
- `QuickNoteData.cs`: Data model storing the target file path

### Data
Saves `quick-note.json` using `WidgetStore` to persist the selected Markdown file path.

### Windows APIs
None.

### Background work
- Appends text to the target file in `Task.Run` when the user submits a note.
