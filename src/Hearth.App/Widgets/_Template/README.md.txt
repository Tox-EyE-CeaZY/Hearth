# Example (`example`)

What it shows and what you can do with it, in two or three sentences.

## Files

| File | What it is |
| --- | --- |
| `ExampleWidget.cs` | The widget and its view |
| `ExampleData.cs` | The saved items and their store |

## Data

`%AppData%/Hearth/example.json`. Every Example widget shows the same items.
Say here what is kept only in memory, if anything.

## Background

While shown: listens to `ExampleData.Changed`, and refreshes every minute.
Nothing runs when it isn't shown (no `StartServices`).

## Platform

Windows APIs or web services it uses, and any permissions they need. "None" is fine.

## Notes

- Traps, limits, and what hasn't been tried by hand.
- Default size 3x2, smallest 2x2.
