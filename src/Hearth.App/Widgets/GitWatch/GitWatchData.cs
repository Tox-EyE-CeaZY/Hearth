using System;

namespace Hearth.App.Widgets.GitWatch;

public sealed class GitWatchData
{
    private static readonly WidgetStore<GitWatchData> _store = new("git-watch.json");
    private static GitWatchData State => _store.Load();
    
    public static event Action? Changed;

    public string? RepoPath { get; set; }

    public static string? Path => State.RepoPath;

    public static void SetPath(string path)
    {
        var state = State;
        state.RepoPath = path;
        _store.Save(state);
        Changed?.Invoke();
    }
}
