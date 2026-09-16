using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Settings;

namespace Hearth.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private DesktopHost? _host;

    public static HearthSettings Settings { get; private set; } = new();
    public static IconService Icons { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two copies would fight over the same WorkerW and leave the icon layer
        // in whichever state the loser happened to set last.
        _singleInstance = new Mutex(initiallyOwned: true, "Hearth.DesktopLayer.SingleInstance", out var isFirst);
        if (!isFirst)
        {
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

        _host = new DesktopHost();
        if (!_host.Start())
        {
            MessageBox.Show(
                "Hearth could not attach to the desktop. Explorer may not be running, " +
                "or a third-party shell replacement is in use.",
                "Hearth", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
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
        try { _host?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { Icons?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TearDown();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
