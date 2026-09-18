# Notes for AI assistants

Hearth is an Android-style home screen and Start menu that runs on top of
the Windows desktop (C#, .NET 8, WPF). If it crashes, the user's desktop
icons disappear, so be careful.

- **Widget work:** read [src/Hearth.App/Widgets/GEMINI.md](src/Hearth.App/Widgets/GEMINI.md)
  in full first, and follow it exactly. Only create or edit files inside one
  `src/Hearth.App/Widgets/<Name>/` folder, and finish with
  `tools/check-widgets.cmd` reporting **PASSED**.
- **Anything else:** read [Claude/notes.md](Claude/notes.md) (the project's
  running notes and known traps) and [README.md](README.md).
- **Run and restart Hearth:** `start-hearth.cmd`. Never kill `Hearth.exe`;
  quit it with `start-hearth.cmd -Stop` or `Hearth.exe --quit`.
- **Build:** `dotnet build Hearth.sln -c Release` must report 0 warnings and 0 errors.
- PowerShell scripts are blocked on the user's machine; use the `.cmd`
  files, or `powershell -ExecutionPolicy Bypass -File ...`.
- Write icon glyphs in C# as escapes, never as pasted characters.
- Don't commit, push, build installers, release, delete user data, or run
  anything as administrator unless the user asks.
