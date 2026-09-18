using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Services;
using Hearth.App.Tablet;
using Hearth.App.Widgets;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Notifications;
using Hearth.Core.Settings;

namespace Hearth.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private DesktopHost? _host;

    public static HearthSettings Settings { get; private set; } = new();
    public static IconService Icons { get; private set; } = null!;
    public static BadgeService Badges { get; private set; } = null!;
    internal static InstalledApps Apps { get; } = new();

    private StartMenuController? _start;
    private TabletMode? _tablet;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Helper runs of the same exe, which never become a desktop: the
        // watchdog (tablet mode's crash insurance) and the restore command
        // the quit script runs after a kill.
        if (RunHelperCommand(e.Args))
        {
            Shutdown();
            return;
        }

        // Two copies would fight over the same WorkerW and leave the icon layer
        // in whichever state the loser happened to set last.
        _singleInstance = new Mutex(initiallyOwned: true, "Hearth.DesktopLayer.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // "Hearth.exe --start [tab]" asks the running copy to toggle its
            // Start menu, so any hotkey tool or shortcut can open it.
            if (StartMenuController.ParseRequest(e.Args) is { } tab) StartMenuController.SendRequest(tab);
            Shutdown();
            return;
        }
        if (StartMenuController.ParseRequest(e.Args) == StartMenuController.QuitRequest)
        {
            // Nothing running to quit; don't start one either.
            _singleInstance.ReleaseMutex();
            Shutdown();
            return;
        }

        // A crash with the shell icons hidden is the one failure mode that
        // leaves the machine visibly broken, so every exit path routes through
        // the same teardown.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write($"FATAL: {args.ExceptionObject}");
            TearDown();
        };

        Log.Start("startup");

        Settings = HearthSettings.Load();
        Log.Write($"settings: shape={Settings.IconShape}, iconSize={Settings.IconSize}, " +
                  $"labels={Settings.ShowLabels}, installedApps={Settings.IncludeInstalledApps}, " +
                  $"hideShellIcons={Settings.HideShellIcons}");

        Icons = new IconService();
        Badges = new BadgeService();

        _host = new DesktopHost();
        if (!_host.Start())
        {
            MessageBox.Show(
                "Hearth could not attach to the desktop. Explorer may not be running, " +
                "or a third-party shell replacement is in use.",
                "Hearth", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _start = new StartMenuController(Dispatcher);
        _start.ListenForRequests();

        try
        {
            _tablet = new TabletMode(Dispatcher);
        }
        catch (Exception ex)
        {
            // Tablet mode is an extra; the desktop must come up without it.
            Log.Error("tablet mode start", ex);
            TaskbarControl.Restore();
        }

        WidgetServices.Start();

        // "Hearth.exe --tablet on" or "--settings" as the first start still does what it says.
        if (StartMenuController.ParseRequest(e.Args) is { } request &&
            (request.StartsWith(StartMenuController.TabletPrefix, StringComparison.Ordinal) ||
             request.StartsWith(StartMenuController.SettingsPrefix, StringComparison.Ordinal)))
            Dispatcher.BeginInvoke(() => _start.HandleRequest(request), DispatcherPriority.ApplicationIdle);
    }

    private static bool RunHelperCommand(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(Watchdog.Argument, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (index + 1 < args.Length && int.TryParse(args[index + 1], out var parent)) Watchdog.Run(parent);
            return true;
        }
        if (args.Any(a => a.Equals(Watchdog.RestoreArgument, StringComparison.OrdinalIgnoreCase)))
        {
            Watchdog.RestoreShell();
            return true;
        }
        return false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("dispatcher", e.Exception);

        // Better a visibly normal Windows desktop than a half-live Hearth: put
        // the real icons back and get out rather than limping on.
        MessageBox.Show(
            $"Hearth hit an error and will close. Your desktop icons have been restored.\n\n{e.Exception.Message}",
            "Hearth", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
        TearDown();
        Shutdown();
    }

    private void TearDown()
    {
        WidgetServices.Stop();
        // Tablet mode first: it puts the taskbar back and closes its bars.
        try { _tablet?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _tablet = null;
        try { _start?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _start = null;
        try { _host?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { Icons?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { Badges?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TearDown();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
