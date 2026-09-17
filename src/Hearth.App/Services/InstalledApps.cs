using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;
using Hearth.Core.Threading;

namespace Hearth.App.Services;

/// <summary>
/// The installed-apps list, shared by the desktop, the Add apps drawer and the
/// Start menu, so AppsFolder is read once rather than by each of them.
/// </summary>
internal sealed class InstalledApps
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly AppsFolderCatalog _catalog = new();
    private IReadOnlyList<LauncherItem> _apps = [];
    private DateTime _readAt = DateTime.MinValue;
    private Task<IReadOnlyList<LauncherItem>>? _reading;

    /// <summary>Raised on the UI thread when a refresh changed the list.</summary>
    public event Action? Changed;

    public IReadOnlyList<LauncherItem> Current => _apps;

    public bool IsStale => DateTime.UtcNow - _readAt > MaxAge;

    /// <summary>
    /// The list, reading it first if it has never been read. A stale list is
    /// returned immediately and refreshed in the background. UI thread only.
    /// </summary>
    public async Task<IReadOnlyList<LauncherItem>> GetAsync()
    {
        if (_readAt == DateTime.MinValue) return await RefreshAsync().ConfigureAwait(true);
        if (IsStale) _ = RefreshAsync();
        return _apps;
    }

    public Task<IReadOnlyList<LauncherItem>> RefreshAsync()
    {
        return _reading ??= ReadAsync();

        async Task<IReadOnlyList<LauncherItem>> ReadAsync()
        {
            try
            {
                var fresh = await StaTask.Run(() => _catalog.Enumerate()).ConfigureAwait(true);
                Update(fresh);
                return fresh;
            }
            catch (Exception ex)
            {
                Log.Error("installed apps", ex);
                return _apps;
            }
            finally
            {
                _reading = null;
            }
        }
    }

    /// <summary>Takes a list read elsewhere (the desktop reads it too).</summary>
    public void Update(IReadOnlyList<LauncherItem> fresh)
    {
        var changed = fresh.Count != _apps.Count ||
                      !fresh.Select(a => (a.Id, a.DisplayName)).SequenceEqual(_apps.Select(a => (a.Id, a.DisplayName)));
        _apps = fresh;
        _readAt = DateTime.UtcNow;
        if (changed) Changed?.Invoke();
    }

    public LauncherItem? Find(string id) =>
        _apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
