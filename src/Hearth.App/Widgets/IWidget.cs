using System.Windows;
using Hearth.App.Widgets.Weather;

namespace Hearth.App.Widgets;

/// <summary>
/// A home-screen widget.
///
/// Windows has no equivalent of Android's AppWidget API that third parties can
/// host — the Win11 widgets board is closed, and desktop gadgets have been dead
/// since Vista. So widgets here are Hearth's own plugin surface rather than a
/// bridge to something the OS already provides, which means the contract is
/// ours to keep small: describe yourself, hand back a view, clean up after it.
/// </summary>
public interface IWidget
{
    /// <summary>Stable identifier. Persisted in the layout, so never change it.</summary>
    string Id { get; }

    /// <summary>Shown in the "add widget" menu.</summary>
    string Title { get; }

    /// <summary>Preferred footprint in grid cells.</summary>
    (int Columns, int Rows) DefaultSpan { get; }

    /// <summary>Smallest footprint the view still renders usefully at.</summary>
    (int Columns, int Rows) MinimumSpan => (1, 1);

    /// <summary>
    /// Builds the view. Called once per placement; the returned element owns
    /// any timers or subscriptions it starts and must release them when
    /// unloaded, because the desktop layer outlives every individual widget.
    /// </summary>
    FrameworkElement CreateView(WidgetContext context);
}

/// <summary>
/// A widget with settings of its own. When it needs setting up before it can
/// show anything useful, the desktop asks for that as the widget is added.
/// </summary>
public interface IConfigurableWidget
{
    bool NeedsSetup { get; }

    /// <summary>Shows the widget's settings. Returns false if the user cancelled.</summary>
    bool Configure();
}

/// <summary>What a widget is told about the surface it is being placed on.</summary>
public sealed record WidgetContext
{
    /// <summary>Physical pixels the widget has been allocated.</summary>
    public required Size PixelSize { get; init; }

    /// <summary>DPI scale of the monitor it is on.</summary>
    public required double Scale { get; init; }

    /// <summary>Matches the home screen's theme so widgets do not fight it.</summary>
    public required bool DarkTheme { get; init; }
}

/// <summary>
/// The widgets Hearth knows how to build. A plain static registry for now;
/// this is the seam an external plugin loader would hook into later.
/// </summary>
public static class WidgetRegistry
{
    private static readonly Dictionary<string, IWidget> Widgets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Layout entries for widgets are stored as "widget:{id}".</summary>
    public const string PlacementPrefix = "widget:";

    static WidgetRegistry()
    {
        Register(new ClockWidget());
        Register(new MediaWidget());
        Register(new CalendarWidget());
        Register(new SystemWidget());
        Register(new NotesWidget());
        Register(new WeatherWidget());
    }

    public static void Register(IWidget widget) => Widgets[widget.Id] = widget;

    public static IReadOnlyCollection<IWidget> All => Widgets.Values;

    public static IWidget? Find(string id) => Widgets.GetValueOrDefault(id);

    /// <summary>Resolves a layout placement id back to its widget, or null.</summary>
    public static IWidget? FromPlacementId(string placementId) =>
        placementId.StartsWith(PlacementPrefix, StringComparison.OrdinalIgnoreCase)
            ? Find(placementId[PlacementPrefix.Length..])
            : null;

    public static string ToPlacementId(IWidget widget) => PlacementPrefix + widget.Id;
}
