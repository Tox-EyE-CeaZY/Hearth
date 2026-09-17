using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Windows;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets;

/// <summary>
/// A home-screen widget.
///
/// Windows has no equivalent of Android's AppWidget API that third parties can
/// host — the Win11 widgets board is closed, and desktop gadgets have been dead
/// since Vista. So widgets here are Hearth's own plugin surface rather than a
/// bridge to something the OS already provides, which means the contract is
/// ours to keep small: describe yourself, hand back a view, clean up after it.
///
/// How to write one: docs/widgets.md.
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

    /// <summary>
    /// Height of the widget's content on the Start menu's Widgets board, in
    /// board pixels, when it has a column to itself or (wide) the full width.
    /// </summary>
    double BoardHeight(bool wide) => Math.Max(1, DefaultSpan.Rows) * 90;

    /// <summary>
    /// Whether the Widgets board shows it before the user has picked any.
    /// Leave this off for new widgets, so the board doesn't grow on update.
    /// </summary>
    bool OnBoardByDefault => false;

    /// <summary>
    /// Where the widget sits in the add-widget menus and on the default
    /// board: lower first, then by title.
    /// </summary>
    int Order => 1000;

    /// <summary>
    /// Called once as Hearth starts, for work that must run whether or not
    /// the widget is on screen (an alarm, a running timer). Keep it fast:
    /// the desktop is waiting.
    /// </summary>
    void StartServices() { }

    /// <summary>Called once as Hearth exits. Undo whatever <see cref="StartServices"/> started.</summary>
    void StopServices() { }
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

    /// <summary>Leave out the card; the host draws its own around the widget.</summary>
    public bool Bare { get; init; }
}

/// <summary>
/// The widgets Hearth knows how to build.
///
/// Found, not listed: every top-level, non-abstract class in the app that
/// implements <see cref="IWidget"/> directly and has a parameterless
/// constructor is registered. Adding a widget is
/// dropping its folder into Widgets/; removing one is deleting the folder.
/// This is also the seam an external plugin loader would hook into later.
/// </summary>
public static class WidgetRegistry
{
    private static readonly Dictionary<string, IWidget> Widgets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Layout entries for widgets are stored as "widget:{id}".</summary>
    public const string PlacementPrefix = "widget:";

    static WidgetRegistry()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<IWidget>();
        foreach (var type in CandidateTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(IWidget).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            try
            {
                found.Add((IWidget)Activator.CreateInstance(type)!);
            }
            catch (Exception ex)
            {
                Log.Error($"widget {type.Name}", ex);
            }
        }

        foreach (var widget in found.OrderBy(w => w.Order).ThenBy(w => w.Title, StringComparer.CurrentCulture))
        {
            if (Widgets.ContainsKey(widget.Id))
            {
                Log.Write($"widget id '{widget.Id}' is used twice; {widget.GetType().Name} was left out");
                continue;
            }
            Register(widget);
        }
        Log.Write($"widgets: {Widgets.Count} found in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Classes that implement <see cref="IWidget"/> directly, read from the
    /// assembly's metadata. Asking reflection for every type instead
    /// (<c>GetTypes</c>) loads them all, and some pull in the WinRT
    /// projection: that took 2.6 s at start-up.
    /// </summary>
    private static IEnumerable<Type> CandidateTypes()
    {
        var assembly = typeof(IWidget).Assembly;
        if (string.IsNullOrEmpty(assembly.Location)) return assembly.GetTypes(); // single-file publish

        var names = new List<string>();
        using (var stream = File.OpenRead(assembly.Location))
        using (var pe = new PEReader(stream))
        {
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0) continue;
                if (!type.GetDeclaringType().IsNil) continue;

                foreach (var implementation in type.GetInterfaceImplementations())
                {
                    var face = metadata.GetInterfaceImplementation(implementation).Interface;
                    if (face.Kind != HandleKind.TypeDefinition) continue;
                    var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)face);
                    if (metadata.GetString(definition.Name) != nameof(IWidget) ||
                        metadata.GetString(definition.Namespace) != typeof(IWidget).Namespace) continue;

                    var ns = metadata.GetString(type.Namespace);
                    var name = metadata.GetString(type.Name);
                    names.Add(ns.Length > 0 ? $"{ns}.{name}" : name);
                }
            }
        }
        return names.Select(n => assembly.GetType(n)).OfType<Type>();
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
