using System;

namespace Hearth.App.Widgets.QuickNote;

public sealed class QuickNoteData
{
    private static readonly WidgetStore<QuickNoteData> _store = new("quick-note.json");
    private static QuickNoteData State => _store.Load();
    
    public static event Action? Changed;

    public string? TargetFilePath { get; set; }

    public static string? Path => State.TargetFilePath;

    public static void SetPath(string path)
    {
        var state = State;
        state.TargetFilePath = path;
        _store.Save(state);
        Changed?.Invoke();
    }
}
