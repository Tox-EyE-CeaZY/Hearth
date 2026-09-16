using System.IO;

namespace Hearth.Core.Shell;

/// <summary>
/// Reads what is actually sitting on the user's desktop — both the per-user
/// and the All Users desktop, which is where most installers drop shortcuts.
///
/// Hearth hides the real icon layer rather than deleting anything, so this
/// stays the source of truth: the files are untouched, and turning Hearth off
/// gives the user their normal desktop back unchanged.
/// </summary>
public sealed class DesktopItemCatalog
{
    public string UserDesktop { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string CommonDesktop { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public IReadOnlyList<LauncherItem> Enumerate()
    {
        var items = new List<LauncherItem>(64);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { UserDesktop, CommonDesktop })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            Collect(root, items, seen);
        }

        items.Sort(static (a, b) =>
        {
            // Folders first, then everything else, each alphabetical — the same
            // ordering Explorer uses, so nothing feels reshuffled.
            if (a.Kind != b.Kind)
            {
                if (a.Kind == LauncherItemKind.Folder) return -1;
                if (b.Kind == LauncherItemKind.Folder) return 1;
            }
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
        return items;
    }

    private static void Collect(string root, List<LauncherItem> items, HashSet<string> seen)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);

            // desktop.ini drives folder customisation and is never shown.
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            FileAttributes attrs;
            try { attrs = File.GetAttributes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) continue;
            if (!seen.Add(name)) continue;

            var isDirectory = attrs.HasFlag(FileAttributes.Directory);
            var ext = Path.GetExtension(path);
            var isShortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);

            items.Add(new LauncherItem
            {
                Id = path,
                // The shell hides a shortcut's extension, so we hide it too.
                DisplayName = isDirectory ? name : Path.GetFileNameWithoutExtension(path),
                Kind = isDirectory ? LauncherItemKind.Folder
                     : isShortcut ? LauncherItemKind.Shortcut
                     : LauncherItemKind.File,
                Target = path,
                FileSystemPath = path,
            });
        }
    }

    /// <summary>
    /// Watches both desktop roots and raises <paramref name="onChanged"/> —
    /// coalesced, because a single file copy fires several raw events.
    /// </summary>
    public IDisposable Watch(Action onChanged, TimeSpan? debounce = null)
    {
        var window = debounce ?? TimeSpan.FromMilliseconds(400);
        return new DesktopWatcher(new[] { UserDesktop, CommonDesktop }, onChanged, window);
    }

    private sealed class DesktopWatcher : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly Timer _debounceTimer;
        private readonly Action _onChanged;
        private readonly TimeSpan _window;

        public DesktopWatcher(IEnumerable<string> roots, Action onChanged, TimeSpan window)
        {
            _onChanged = onChanged;
            _window = window;
            _debounceTimer = new Timer(_ => _onChanged(), null, Timeout.Infinite, Timeout.Infinite);

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                var watcher = new FileSystemWatcher(root)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += Bump;
                watcher.Deleted += Bump;
                watcher.Renamed += Bump;
                watcher.Changed += Bump;
                _watchers.Add(watcher);
            }
        }

        private void Bump(object sender, FileSystemEventArgs e) =>
            _debounceTimer.Change(_window, Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            _debounceTimer.Dispose();
        }
    }
}
