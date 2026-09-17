using System.Diagnostics;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets;

/// <summary>
/// Runs every widget's <see cref="IWidget.StartServices"/> once Hearth is
/// attached to the desktop, and <see cref="IWidget.StopServices"/> as it
/// exits. Work that only matters while a widget is showing belongs in its
/// view (WidgetView.While) instead.
/// </summary>
internal static class WidgetServices
{
    public static void Start()
    {
        Run("notifications", SystemNotifications.Warm);
        foreach (var widget in WidgetRegistry.All) Run(widget.Id, widget.StartServices);
    }

    public static void Stop()
    {
        foreach (var widget in WidgetRegistry.All.Reverse()) Run(widget.Id, widget.StopServices);
    }

    private static void Run(string name, Action action)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            action();
            if (clock.ElapsedMilliseconds > 50) Log.Write($"widget service {name}: {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Error($"widget service {name}", ex);
        }
    }
}
