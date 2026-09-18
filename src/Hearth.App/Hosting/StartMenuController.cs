using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Settings;

namespace Hearth.App.Hosting;

/// <summary>
/// Owns the Start menu: the window, its layout, and — when the setting is on —
/// the hooks that open it from the Windows key and the Start button.
/// </summary>
internal sealed class StartMenuController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly StartLayout _layout = StartLayout.Load();
    private StartTrigger? _trigger;
    private StartMenuWindow? _window;
    private DateTime _lastToggle;

    public static StartMenuController? Current { get; private set; }

    public StartMenuController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Current = this;
        ApplyTriggerSetting();

        // Build the menu shortly after start-up, while nothing is waiting on it.
        var prewarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        prewarm.Tick += async (_, _) =>
        {
            prewarm.Stop();
            try
            {
                var apps = await App.Apps.GetAsync().ConfigureAwait(true);
                if (_window is not null) return;
                EnsureWindow();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _window!.Prewarm(apps);
                Log.Write($"start menu: prewarmed in {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error("start menu prewarm", ex);
            }
        };
        prewarm.Start();
    }

    public bool IsOpen => _window is { IsVisible: true };

    private bool _windowLight;

    private static bool SystemThemeIsLight() => Views.Start.SystemTheme.SystemUsesLight;

    public void SetReplaceStart(bool replace)
    {
        App.Settings.ReplaceStartMenu = replace;
        App.Settings.Save();
        ApplyTriggerSetting();
    }

    /// <summary>
    /// The hooks stay installed either way — they also report clicks outside
    /// the open menu — and the setting only decides whether the Windows key
    /// and Start button are taken over.
    /// </summary>
    private void ApplyTriggerSetting()
    {
        if (_trigger is null)
        {
            _trigger = new StartTrigger();
            _trigger.Triggered += OnTriggered;
            _trigger.Pressed += OnPressed;
        }
        _trigger.Intercept = App.Settings.ReplaceStartMenu;
        Log.Write($"start trigger: Windows key and Start button {(App.Settings.ReplaceStartMenu ? "taken over" : "left to Windows")}");
    }

    private void OnPressed(int x, int y) =>
        _dispatcher.BeginInvoke(() => _window?.OnMousePressedAt(x, y));

    /// <summary>Hook thread: hand straight to the UI thread.</summary>
    private void OnTriggered(StartTrigger.StartSource source) =>
        _dispatcher.BeginInvoke(Toggle);

    public void Toggle()
    {
        // Key repeat or a double click can arrive as two triggers in a row.
        if (DateTime.UtcNow - _lastToggle < TimeSpan.FromMilliseconds(150)) return;
        _lastToggle = DateTime.UtcNow;

        if (IsOpen)
        {
            _window!.HideMenu();
            return;
        }

        Open();
    }

    public void Open(string? tab = null)
    {
        try
        {
            EnsureWindow();
            if (tab is not null) _window!.SelectTab(tab);

            // Focus needs the right to take the foreground; the injected no-op
            // key makes this process the source of the last input.
            StartTrigger.ClaimForegroundRight();
            GetCursorPos(out var cursor);
            _window!.ShowMenu(new Point(cursor.X, cursor.Y));
        }
        catch (Exception ex)
        {
            Log.Error("start menu open", ex);
        }
    }

    // ---- Requests from a second Hearth.exe --------------------------------

    private const string RequestEventName = "Hearth.StartMenu.Request";
    private static readonly string RequestFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth", "start-request");

    private EventWaitHandle? _requests;
    private RegisteredWaitHandle? _requestWait;

    public const string QuitRequest = "quit";
    public const string TabletPrefix = "tablet:";
    public const string SettingsPrefix = "settings:";
    public const string LaunchPrefix = "launch:";

    /// <summary>
    /// "--start [all|pages|categories|widgets|edit|selftest]", "--quit",
    /// "--tablet on|off|auto|toggle|recents|evaluate" or "--settings [page]";
    /// null when absent.
    /// </summary>
    public static string? ParseRequest(string[] args)
    {
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase))) return QuitRequest;

        var tablet = Array.FindIndex(args, a => a.Equals("--tablet", StringComparison.OrdinalIgnoreCase));
        if (tablet >= 0) return TabletPrefix + (tablet + 1 < args.Length ? args[tablet + 1].ToLowerInvariant() : "toggle");

        var launch = Array.FindIndex(args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase));
        if (launch >= 0 && launch + 1 < args.Length) return LaunchPrefix + args[launch + 1];

        var settings = Array.FindIndex(args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (settings >= 0) return SettingsPrefix + (settings + 1 < args.Length ? args[settings + 1].ToLowerInvariant() : string.Empty);

        var index = Array.FindIndex(args, a => a.Equals("--start", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : string.Empty;
    }

    public static void SendRequest(string tab)
    {
        try
        {
            System.IO.File.WriteAllText(RequestFile, tab);
            if (EventWaitHandle.TryOpenExisting(RequestEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            Log.Write($"start request not delivered: {ex.Message}");
        }
    }

    public void ListenForRequests()
    {
        _requests = new EventWaitHandle(false, EventResetMode.AutoReset, RequestEventName);
        _requestWait = ThreadPool.RegisterWaitForSingleObject(_requests, (_, _) =>
        {
            string tab;
            try { tab = System.IO.File.ReadAllText(RequestFile).Trim(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { tab = string.Empty; }
            _dispatcher.BeginInvoke(() => HandleRequest(tab));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Carries out a request from another Hearth.exe (or this one's own command line).</summary>
    public void HandleRequest(string tab)
    {
        try
        {
            if (tab.StartsWith(TabletPrefix, StringComparison.Ordinal))
            {
                HandleTabletRequest(tab[TabletPrefix.Length..]);
                return;
            }
            if (tab.StartsWith(LaunchPrefix, StringComparison.Ordinal))
            {
                // "Hearth.exe --launch <app id or path>": the same launch as a tap on a tile.
                var target = tab[LaunchPrefix.Length..];
                var item = App.Apps.Current.FirstOrDefault(a => a.Id.Equals(target, StringComparison.OrdinalIgnoreCase))
                           ?? new Hearth.Core.Shell.LauncherItem
                           {
                               Id = target,
                               DisplayName = System.IO.Path.GetFileName(target),
                               Kind = Hearth.Core.Shell.LauncherItemKind.File,
                               Target = target,
                           };
                Services.AppLauncher.Launch(item);
                return;
            }
            if (tab.StartsWith(SettingsPrefix, StringComparison.Ordinal))
            {
                Views.SettingsScreen.SettingsWindow.Open(tab[SettingsPrefix.Length..]);
                return;
            }
            if (IsOpen && tab.Length == 0)
            {
                _window!.HideMenu();
                return;
            }
            // "Hearth.exe --quit": the same clean exit as the menu item,
            // so scripts can restart Hearth without leaving icons hidden.
            if (tab == QuitRequest)
            {
                Application.Current.Shutdown();
                return;
            }
            if (tab == "selftest")
            {
                Open();
                _ = _window?.RunSelfTestAsync();
                return;
            }
            Open(tab.Length > 0 ? tab : null);
        }
        catch (Exception ex)
        {
            Log.Error($"request '{tab}'", ex);
        }
    }

    private static void HandleTabletRequest(string what)
    {
        if (Tablet.TabletMode.Current is not { } tablet)
        {
            Log.Write($"tablet request '{what}' ignored: tablet mode is not running");
            return;
        }
        Log.Write($"tablet request: {what}");
        if (what.StartsWith("trayclick:", StringComparison.Ordinal) || what.StartsWith("traymenu:", StringComparison.Ordinal))
        {
            // Test hook: the same click or menu as a tile in the tray shade.
            var menu = what.StartsWith("traymenu:", StringComparison.Ordinal);
            var name = what[(what.IndexOf(':') + 1)..];
            _ = Task.Run(async () =>
            {
                var icon = (await Tablet.TrayIcons.ReadAsync(includeHidden: true).ConfigureAwait(false))
                    .FirstOrDefault(i => i.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (icon is null) Log.Write($"tray: no icon matching '{name}'");
                else await Tablet.TrayIcons.ActivateAsync(icon, menu).ConfigureAwait(false);
            });
            return;
        }
        switch (what)
        {
            case "on": tablet.SetMode(TabletModeSetting.On); break;
            case "off": tablet.SetMode(TabletModeSetting.Off); break;
            case "auto": tablet.SetMode(TabletModeSetting.Auto); break;
            case "toggle": tablet.Toggle(); break;
            case "desktop": tablet.ForceDesktop(); break;
            case "recents": tablet.ShowTaskSwitcher(); break;
            case "recents-tray": tablet.ShowTaskSwitcher(withTray: true); break;
            case "home": tablet.GoHome(); break;
            case "evaluate": tablet.Evaluate(settled: true, prompt: false); break;
            case "tray":
                _ = Task.Run(async () =>
                {
                    foreach (var icon in await Tablet.TrayIcons.ReadAsync(includeHidden: true).ConfigureAwait(false))
                        Log.Write($"tray icon: {(icon.Hidden ? "hidden " : "shown  ")} '{icon.Title}' image={(icon.Image is null ? "none" : "yes")}");
                });
                break;
            default: Log.Write($"tablet request '{what}' not understood"); break;
        }
    }

    /// <summary>Drops the hidden window so the next open builds it with current settings (a new backdrop).</summary>
    public void InvalidateWindow()
    {
        if (_window is null || _window.IsVisible) return;
        _window.CloseForExit();
        _window = null;
    }

    /// <summary>The window, rebuilt if the Windows theme changed since it was made.</summary>
    private void EnsureWindow()
    {
        var light = SystemThemeIsLight();
        if (_window is not null && _windowLight != light && !_window.IsVisible)
        {
            _window.CloseForExit();
            _window = null;
        }
        if (_window is not null) return;

        Views.Start.StartStyle.UseSystemTheme();
        _windowLight = light;
        _window = new StartMenuWindow(_layout);
        _window.IsVisibleChanged += (_, _) =>
        {
            if (_trigger is not null) _trigger.ReportPresses = _window?.IsVisible == true;
        };
    }

    /// <summary>Windows' own Start. Ctrl+Esc is never intercepted, so it gets through.</summary>
    public void OpenWindowsStart() => StartTrigger.OpenWindowsStart();

    public void Dispose()
    {
        if (_trigger is not null)
        {
            _trigger.Triggered -= OnTriggered;
            _trigger.Pressed -= OnPressed;
            _trigger.Dispose();
            _trigger = null;
        }
        _requestWait?.Unregister(null);
        _requests?.Dispose();
        _window?.CloseForExit();
        _window = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
