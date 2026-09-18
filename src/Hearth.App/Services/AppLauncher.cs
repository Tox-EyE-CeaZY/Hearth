using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Hearth.App.Tablet;
using Hearth.Core.Diagnostics;
using Hearth.Core.Shell;

namespace Hearth.App.Services;

/// <summary>
/// Launches apps and makes sure something visibly happens.
///
/// Single-instance apps (Electron apps such as Antigravity IDE and VS Code,
/// and many others) start a second process that hands over to the copy
/// already running and exits. The running copy then tries to bring its
/// window forward, and Windows refuses, because the launch came from Hearth,
/// a background window: nothing appears, and the app seems not to launch.
/// Measured: the new "Antigravity IDE" process appeared 140 ms after the
/// launch, and the existing window stayed behind, even when any process was
/// allowed to take the foreground.
///
/// So Hearth works out which program the item starts (the .exe, or the
/// target of its shortcut or Start entry) and, if no window of that program
/// comes to the front soon after the launch, brings the program's existing
/// window forward itself (Hearth may do that; it has just handled
/// the click). Apps that open a new window each time (browsers, File
/// Explorer) show that window, and nothing extra happens.
/// </summary>
internal static class AppLauncher
{
    private static readonly TimeSpan Watch = TimeSpan.FromMilliseconds(1800);

    public static bool Launch(LauncherItem item)
    {
        Hosting.StartTrigger.ClaimForegroundRight();
        var program = ShellLauncher.ProgramPathOf(item);
        var foregroundBefore = WindowTools.GetForegroundWindow();

        if (!ShellLauncher.Launch(item))
        {
            Log.Write($"launch: shell refused '{item.DisplayName}'");
            return false;
        }

        // Store apps and documents: nothing to follow.
        if (program is not null) _ = FollowUpAsync(item.DisplayName, program, foregroundBefore, Dispatcher.CurrentDispatcher);
        return true;
    }

    /// <summary>
    /// Watches for the program's window to come to the front. Only the
    /// program the item points at counts: an earlier version followed any
    /// process that started meanwhile, and once picked a script's sleep.exe.
    /// </summary>
    private static async Task FollowUpAsync(string name, string program, IntPtr foregroundBefore, Dispatcher dispatcher)
    {
        try
        {
            var programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { program };
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Watch)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (await dispatcher.InvokeAsync(() => ForegroundIsOneOf(programs, foregroundBefore)))
                    return; // it came up by itself
            }

            await dispatcher.InvokeAsync(() =>
            {
                if (ForegroundIsOneOf(programs, foregroundBefore)) return;
                var existing = WindowTools.SwitchableWindows()
                    .FirstOrDefault(w => programs.Contains(WindowTools.ProcessPath(w)));
                if (existing == IntPtr.Zero) return; // still starting, or no window at all
                if (existing == WindowTools.GetForegroundWindow()) return;
                Log.Write($"launch: '{name}' handed over to a running copy; bringing '{WindowTools.Title(existing)}' forward");
                WindowTools.Activate(existing);
            });
        }
        catch (Exception ex)
        {
            Log.Error($"launch follow-up '{name}'", ex);
        }
    }

    private static bool ForegroundIsOneOf(HashSet<string> programs, IntPtr foregroundBefore)
    {
        var foreground = WindowTools.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == foregroundBefore) return false;
        return programs.Contains(WindowTools.ProcessPath(foreground));
    }
}
