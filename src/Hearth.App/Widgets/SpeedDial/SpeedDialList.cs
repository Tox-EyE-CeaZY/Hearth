using System.IO;
using Hearth.App.Views;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets.SpeedDial;

internal enum DialKind
{
    /// <summary>An installed app; the target is its AppUserModelID.</summary>
    App,

    /// <summary>A web address.</summary>
    Web,

    /// <summary>A file or folder.</summary>
    Path,
}

internal sealed class DialEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public DialKind Kind { get; set; }

    public LauncherItem ToItem() => Kind switch
    {
        DialKind.App => App.Apps.Find(Target) ?? new LauncherItem
        {
            Id = Target,
            DisplayName = Name,
            Kind = LauncherItemKind.App,
            Target = Target,
        },
        _ => WidgetFiles.ItemFor(Target),
    };

    public void Launch()
    {
        if (Kind == DialKind.App) ShellLauncher.Launch(ToItem());
        else ShellLauncher.Open(Target);
    }
}

internal sealed class SpeedDialData
{
    public List<DialEntry> Entries { get; set; } = [];
}

/// <summary>The shortcuts every Speed Dial widget shows. UI thread only.</summary>
internal static class SpeedDialList
{
    private static readonly WidgetStore<SpeedDialData> Store = new("speeddial.json");
    private static SpeedDialData? _data;

    public static event Action? Changed;

    private static SpeedDialData Data => _data ??= Store.Load();

    public static IReadOnlyList<DialEntry> Entries => Data.Entries;

    public static bool Contains(string target) =>
        Data.Entries.Exists(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));

    public static void Add(DialKind kind, string name, string target)
    {
        if (Contains(target)) return;
        Update(d => d.Entries.Add(new DialEntry { Kind = kind, Name = name, Target = target }));
    }

    public static void AddPath(string path) =>
        Add(DialKind.Path, Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path, path);

    public static void Remove(string id) => Update(d => d.Entries.RemoveAll(e => e.Id == id));

    public static void RemoveTarget(string target) =>
        Update(d => d.Entries.RemoveAll(e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase)));

    public static void Move(string id, int delta) => Update(d =>
    {
        var index = d.Entries.FindIndex(e => e.Id == id);
        var to = index + delta;
        if (index < 0 || to < 0 || to >= d.Entries.Count) return;
        var entry = d.Entries[index];
        d.Entries.RemoveAt(index);
        d.Entries.Insert(to, entry);
    });

    private static void Update(Action<SpeedDialData> change)
    {
        change(Data);
        Store.Save(Data);
        Changed?.Invoke();
    }

    /// <summary>Lets the app drawer add apps to Speed Dial instead of the home screen.</summary>
    public sealed class AppPicker : AddAppsWindow.IHome
    {
        public Task<IReadOnlyList<LauncherItem>> GetInstalledAppsAsync() => App.Apps.GetAsync();

        public bool IsOnHome(LauncherItem app) => Contains(app.Id);

        public void SetOnHome(LauncherItem app, bool onHome)
        {
            if (onHome) Add(DialKind.App, app.DisplayName, app.Id);
            else RemoveTarget(app.Id);
        }
    }
}
