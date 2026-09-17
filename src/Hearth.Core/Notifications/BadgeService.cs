using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Hearth.Core.Interop;

namespace Hearth.Core.Notifications;

/// <summary>
/// What a tile should show in its corner. <see cref="Count"/> of zero with
/// <see cref="Dot"/> set means "something new" without a number.
/// </summary>
public readonly record struct Badge(int Count, bool Dot)
{
    public static readonly Badge None = new(0, false);
    public bool IsVisible => Count > 0 || Dot;
}

/// <summary>
/// Unread-notification badges, per AppUserModelID.
///
/// Read from the shell's own notification store,
/// %LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db. The
/// supported API for this (UserNotificationListener) needs package identity,
/// which Hearth does not have. The store is SQLite in WAL mode and is opened
/// read-only, so Hearth never writes to it or holds a lock beyond a read.
///
/// An app's badge is the badge it set itself (the count on its taskbar
/// button, e.g. Phone Link's unread count) if it set one, otherwise the number
/// of its toasts still waiting in the notification centre.
/// </summary>
public sealed partial class BadgeService : IDisposable
{
    private readonly string _databasePath;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    private readonly Timer _poll;
    private readonly object _gate = new();
    private Dictionary<string, Badge> _badges = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Raised on a background thread when any badge changes.</summary>
    public event Action? Changed;

    public BadgeService(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");

        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);

        // The watcher catches changes as they happen; the poll is a backstop
        // for the ones it misses (the WAL is sometimes rewritten in place).
        _poll = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var directory = Path.GetDirectoryName(_databasePath);
        if (directory is not null && Directory.Exists(directory))
        {
            try
            {
                _watcher = new FileSystemWatcher(directory, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => _debounce.Change(700, Timeout.Infinite);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Hearth] notification watcher unavailable: {ex.Message}");
            }
        }
    }

    public Badge For(string? appUserModelId)
    {
        if (string.IsNullOrEmpty(appUserModelId)) return Badge.None;
        lock (_gate)
        {
            return _badges.TryGetValue(appUserModelId, out var badge) ? badge : Badge.None;
        }
    }

    /// <summary>Re-reads the store. Safe to call from any thread.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        Dictionary<string, Badge> fresh;
        try
        {
            fresh = Read(_databasePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hearth] badge read failed: {ex.Message}");
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = fresh.Count != _badges.Count ||
                      fresh.Any(kv => !_badges.TryGetValue(kv.Key, out var old) || old != kv.Value);
            _badges = fresh;
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Reads every current badge. Public so a harness can exercise it on its own.</summary>
    public static Dictionary<string, Badge> Read(string databasePath)
    {
        var result = new Dictionary<string, Badge>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(databasePath)) return result;

        if (WinSqlite.Open(databasePath, out var db, WinSqlite.SQLITE_OPEN_READONLY, IntPtr.Zero) != WinSqlite.SQLITE_OK)
        {
            var message = db != IntPtr.Zero ? WinSqlite.Error(db) : "open failed";
            if (db != IntPtr.Zero) WinSqlite.Close(db);
            throw new IOException(message);
        }

        try
        {
            WinSqlite.BusyTimeout(db, 500);

            const string sql = """
                SELECT h.PrimaryId, n.Type, n.Payload, n.ExpiryTime
                FROM Notification n
                JOIN NotificationHandler h ON h.RecordId = n.HandlerId
                WHERE n.Type IN ('badge', 'toast')
                """;

            if (WinSqlite.Prepare(db, sql, -1, out var statement, IntPtr.Zero) != WinSqlite.SQLITE_OK)
                throw new IOException(WinSqlite.Error(db));

            var toasts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var own = new Dictionary<string, Badge>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.UtcNow.ToFileTimeUtc();

            try
            {
                while (WinSqlite.Step(statement) == WinSqlite.SQLITE_ROW)
                {
                    var app = WinSqlite.Text(statement, 0);
                    var type = WinSqlite.Text(statement, 1);
                    if (string.IsNullOrEmpty(app)) continue;

                    var expiry = WinSqlite.ColumnInt64(statement, 3);
                    if (expiry > 0 && expiry < now) continue;

                    if (type == "toast")
                    {
                        toasts[app] = toasts.GetValueOrDefault(app) + 1;
                    }
                    else if (ParseBadge(Encoding.UTF8.GetString(WinSqlite.Blob(statement, 2))) is { } badge)
                    {
                        own[app] = badge;
                    }
                }
            }
            finally
            {
                WinSqlite.Finalize(statement);
            }

            foreach (var (app, count) in toasts) result[app] = new Badge(count, false);

            // An app's own badge wins over the toast count: it knows its unread
            // total, whereas toasts are only what is still in the centre. An
            // explicit "none" (or 0) clears it.
            foreach (var (app, badge) in own)
            {
                if (badge.IsVisible) result[app] = badge;
                else result.Remove(app);
            }
        }
        finally
        {
            WinSqlite.Close(db);
        }

        return result;
    }

    /// <summary>
    /// Badge XML is <c>&lt;badge value="3"/&gt;</c>, or a glyph name such as
    /// "newMessage" or "attention", or "none".
    /// </summary>
    public static Badge? ParseBadge(string payload)
    {
        var match = BadgeValue().Match(payload);
        if (!match.Success) return null;

        var value = match.Groups[1].Value;
        if (int.TryParse(value, out var number)) return new Badge(Math.Max(0, number), false);
        return string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? Badge.None : new Badge(0, true);
    }

    [GeneratedRegex("""<badge[^>]*\bvalue\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex BadgeValue();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounce.Dispose();
        _poll.Dispose();
    }
}
