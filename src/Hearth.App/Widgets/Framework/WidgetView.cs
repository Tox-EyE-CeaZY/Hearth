using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets;

/// <summary>
/// Base class for a widget's view. It takes care of the parts every widget
/// otherwise gets wrong on its own:
///
/// - Lifetime. Hosts rebuild views on every relayout and unload the old ones.
///   Work registered with <see cref="While"/> or <see cref="Every"/> runs only
///   while the view is loaded, and Loaded/Unloaded firing more than once is
///   handled.
/// - Theme. The host opens the widget's theme only while the constructor runs.
///   Callbacks that come through this class (<see cref="Post"/>,
///   <see cref="Every"/>, <see cref="OnShown"/>) run inside it again, so
///   anything they build gets the right colours. Other event handlers that
///   build UI must wrap it in <see cref="Themed"/>.
/// - Threads. Services raise events on whatever thread they like;
///   <see cref="Post"/> brings the work back to the view's.
/// - Failures. An exception in a callback is logged, not left to reach the
///   dispatcher, where it would close Hearth.
/// </summary>
internal abstract class WidgetView : ContentControl
{
    private readonly List<(Action Start, Action Stop)> _whileShown = [];
    private bool _shown;

    protected WidgetView(WidgetContext context)
    {
        Context = context;
        Loaded += (_, _) => Show();
        Unloaded += (_, _) => Hide();
    }

    protected WidgetContext Context { get; }

    /// <summary>Multiply every measurement by this: widgets are laid out in physical pixels.</summary>
    protected double S => Context.Scale;

    /// <summary>True while the view is loaded.</summary>
    protected bool IsShown => _shown;

    /// <summary>Puts <paramref name="body"/> in the standard card as the view's content.</summary>
    protected void SetBody(UIElement body) => Content = WidgetChrome.Card(Context, body);

    /// <summary>Makes this widget's theme current until disposed.</summary>
    protected IDisposable Themed() => WidgetChrome.Scope(Context);

    /// <summary>Runs <paramref name="start"/> whenever the view is shown and <paramref name="stop"/> when it goes.</summary>
    protected void While(Action start, Action stop)
    {
        _whileShown.Add((start, stop));
        if (_shown) Guard(start);
    }

    /// <summary>A timer that ticks only while the view is shown.</summary>
    protected DispatcherTimer Every(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => Guard(tick);
        While(timer.Start, timer.Stop);
        return timer;
    }

    /// <summary>Runs <paramref name="action"/> on the view's thread, in its theme. Safe from any thread.</summary>
    protected void Post(Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (_shown) Guard(action);
    });

    /// <summary>Called each time the view is shown, after any <see cref="While"/> work has started.</summary>
    protected virtual void OnShown() { }

    /// <summary>Called each time the view goes, before any <see cref="While"/> work is stopped.</summary>
    protected virtual void OnHidden() { }

    private void Show()
    {
        if (_shown) return;
        _shown = true;
        foreach (var (start, _) in _whileShown) Guard(start);
        Guard(OnShown);
    }

    private void Hide()
    {
        if (!_shown) return;
        _shown = false;
        Guard(OnHidden);
        foreach (var (_, stop) in _whileShown) Guard(stop);
    }

    private void Guard(Action action)
    {
        try
        {
            using var theme = Themed();
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"widget {GetType().Name}", ex);
        }
    }
}
