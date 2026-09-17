using System.IO;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Shelf;

internal enum ShelfMode
{
    /// <summary>A holding area Hearth keeps in %LocalAppData%\Hearth\Shelf.</summary>
    Stash,

    /// <summary>A real folder the user picked, such as Downloads.</summary>
    Folder,
}

internal sealed class ShelfConfig
{
    public ShelfMode Mode { get; set; } = ShelfMode.Stash;
    public string? FolderPath { get; set; }
}

/// <summary>
/// The folder behind the Shelf widget, and a watch on it while any Shelf is
/// showing. <see cref="Changed"/> can fire on any thread.
/// </summary>
internal static class ShelfFolder
{
    public static readonly string StashPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "Shelf");

    private static readonly WidgetStore<ShelfConfig> Store = new("shelf.json");
    private static readonly object Gate = new();
    private static ShelfConfig? _config;
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounce;
    private static int _watchers;

    public static event Action? Changed;

    public static ShelfConfig Config => _config ??= Store.Load();

    /// <summary>The folder mode's folder, if it still exists; the stash otherwise.</summary>
    public static string Location
    {
        get
        {
            if (Config.Mode == ShelfMode.Folder && Config.FolderPath is { } folder && Directory.Exists(folder)) return folder;
            Directory.CreateDirectory(StashPath);
            return StashPath;
        }
    }

    public static bool IsStash => Location == StashPath;

    public static string Name => IsStash ? "Shelf" : Path.GetFileName(Location.TrimEnd('\\')) is { Length: > 0 } name ? name : Location;

    public static void Configure(ShelfConfig config)
    {
        _config = config;
        Store.Save(config);
        lock (Gate)
        {
            if (_watchers > 0) Watch();
        }
        Changed?.Invoke();
    }

    /// <summary>What's on the shelf, newest first. Hidden and system files are left out.</summary>
    public static IReadOnlyList<string> Entries(int limit)
    {
        try
        {
            return new DirectoryInfo(Location).EnumerateFileSystemInfos()
                .Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(i => i.LastWriteTimeUtc)
                .Take(limit)
                .Select(i => i.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"shelf: couldn't list {Location}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Copies (or moves) dropped items onto the shelf, in the background.</summary>
    public static void Add(IReadOnlyList<string> paths, bool move, IntPtr owner)
    {
        var folder = Location;
        var incoming = paths
            .Where(p => !string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (incoming.Count == 0) return;

        // The shell's file operation wants an STA thread and blocks until done.
        var worker = new Thread(() =>
        {
            try
            {
                ShellCopy.Into(incoming, folder, move, owner);
            }
            catch (Exception ex)
            {
                Log.Error("shelf: add", ex);
            }
            Changed?.Invoke();
        })
        {
            IsBackground = true,
            Name = "Hearth.ShelfCopy",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static void BeginWatching()
    {
        lock (Gate)
        {
            if (_watchers++ == 0) Watch();
        }
    }

    public static void EndWatching()
    {
        lock (Gate)
        {
            if (--_watchers > 0) return;
            _watchers = 0;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private static void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(Location)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnDiskChange;
            _watcher.Deleted += OnDiskChange;
            _watcher.Renamed += OnDiskChange;
            _watcher.Changed += OnDiskChange;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Write($"shelf: can't watch {Location}: {ex.Message}");
        }
    }

    // A copy raises a burst of events; wait for it to settle.
    private static void OnDiskChange(object sender, FileSystemEventArgs e)
    {
        _debounce ??= new Timer(_ => Changed?.Invoke());
        _debounce.Change(400, Timeout.Infinite);
    }
}
