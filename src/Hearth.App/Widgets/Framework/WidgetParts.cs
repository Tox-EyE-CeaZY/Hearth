using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.App.Hosting;
using Hearth.App.Views;
using Hearth.App.Views.Start;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Shell;

namespace Hearth.App.Widgets;

/// <summary>
/// Anything clickable inside a widget: a row, a tile, a pill.
///
/// It marks the press as handled, which is what stops the desktop treating
/// the click as the start of dragging the whole widget. Brushes are taken
/// from the theme when it is built, so hover still has the right colours
/// after the theme scope has closed. Set <see cref="DragData"/> to let the
/// item be dragged out (to Explorer, a chat app, …) instead.
/// </summary>
internal class Pressable : Border
{
    private readonly Action? _onClick;
    private Brush _idle = Brushes.Transparent;
    private Brush _hover = WidgetChrome.Hover;
    private Point? _pressedAt;

    public Pressable(Action? onClick)
    {
        _onClick = onClick;
        Background = _idle;
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => Background = _hover;
        MouseLeave += (_, _) =>
        {
            Background = _idle;
            _pressedAt = null;
        };
    }

    public Brush IdleBrush
    {
        get => _idle;
        set
        {
            _idle = value;
            if (!IsMouseOver) Background = value;
        }
    }

    public Brush HoverBrush
    {
        get => _hover;
        set
        {
            _hover = value;
            if (IsMouseOver) Background = value;
        }
    }

    /// <summary>Right-click. When set, the widget's own menu doesn't open.</summary>
    public Action? ContextRequested { get; set; }

    /// <summary>What a drag out carries. Null means the item can't be dragged.</summary>
    public Func<DataObject?>? DragData { get; set; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressedAt = e.GetPosition(this);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedAt is not { } start || DragData is null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressedAt = null;
        e.Handled = true;
        if (DragData() is not { } data) return;
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (COMException ex)
        {
            Log.Write($"widget drag failed: {ex.Message}");
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var clicked = _pressedAt is not null;
        _pressedAt = null;
        e.Handled = true;
        if (clicked && IsEnabled) WidgetMenu.Run(_onClick);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (ContextRequested is not { } open) return;
        e.Handled = true;
        WidgetMenu.Run(open);
    }
}

/// <summary>A small rounded pill with a label: presets, filters, on/off choices.</summary>
internal sealed class Chip : Pressable
{
    private readonly TextBlock _label;
    private readonly TextBlock? _glyph;
    private readonly Brush _track = WidgetChrome.Track;
    private readonly Brush _hoverFill = WidgetChrome.Hover;
    private readonly Brush _accent = WidgetChrome.Accent;
    private readonly Brush _onAccent = WidgetChrome.OnAccent;
    private readonly Brush _primary = WidgetChrome.Primary;
    private bool _active;

    public Chip(WidgetContext context, string text, Action onClick, string? glyph = null, double size = 12)
        : base(onClick)
    {
        var s = context.Scale;
        CornerRadius = new CornerRadius(10 * s);
        Padding = new Thickness(10 * s, 4 * s, 10 * s, 5 * s);

        _label = WidgetChrome.Text(context, text, size, weight: FontWeights.SemiBold);
        _label.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            _glyph = WidgetChrome.Glyph(context, glyph, size - 1);
            _glyph.Margin = new Thickness(0, 1 * s, 6 * s, 0);
            content.Children.Add(_glyph);
        }
        content.Children.Add(_label);
        Child = content;
        Apply();
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Apply();
        }
    }

    private void Apply()
    {
        IdleBrush = _active ? _accent : _track;
        HoverBrush = _active ? _accent : _hoverFill;
        _label.Foreground = _active ? _onAccent : _primary;
        if (_glyph is not null) _glyph.Foreground = _label.Foreground;
    }
}

/// <summary>An on/off switch. Clicking it flips <see cref="IsOn"/> and reports the new value.</summary>
internal sealed class WidgetSwitch : FrameworkElement
{
    private readonly Action<bool> _changed;
    private readonly Brush _on = WidgetChrome.Accent;
    private readonly Brush _onKnob = WidgetChrome.OnAccent;
    private readonly Brush _off = WidgetChrome.Track;
    private readonly Brush _offKnob = WidgetChrome.Secondary;
    private bool _isOn;
    private bool _pressed;

    public WidgetSwitch(WidgetContext context, bool isOn, Action<bool> changed)
    {
        _isOn = isOn;
        _changed = changed;
        Width = 36 * context.Scale;
        Height = 20 * context.Scale;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        if (!_pressed) return;
        _pressed = false;
        IsOn = !IsOn;
        WidgetMenu.Run(() => _changed(IsOn));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pressed = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (w, h) = (RenderSize.Width, RenderSize.Height);
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.DrawRoundedRectangle(_isOn ? _on : _off, null, new Rect(0, 0, w, h), h / 2, h / 2);

        var knob = h / 2 - h * 0.18;
        var x = _isOn ? w - h / 2 : h / 2;
        dc.DrawEllipse(_isOn ? _onKnob : _offKnob, null, new Point(x, h / 2), knob, knob);
    }
}

/// <summary>
/// The line at the top of a widget: accent glyph, title, an optional detail
/// ("3 left", "24 ms") and small action buttons on the right.
/// </summary>
internal sealed class WidgetHeader : DockPanel
{
    private readonly WidgetContext _context;
    private readonly TextBlock _glyph;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly StackPanel _actions;

    public WidgetHeader(WidgetContext context, string glyph, string title)
    {
        _context = context;
        var s = context.Scale;
        LastChildFill = true;
        Margin = new Thickness(0, 0, 0, 8 * s);

        _actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6 * s, -4 * s, -6 * s, -4 * s) };
        SetDock(_actions, Dock.Right);
        Children.Add(_actions);

        _glyph = WidgetChrome.Glyph(context, glyph, 14, WidgetChrome.Accent);
        _glyph.Margin = new Thickness(0, 0, 8 * s, 0);
        _title = WidgetChrome.Text(context, title, 14, weight: FontWeights.SemiBold, display: true);
        _detail = WidgetChrome.Text(context, string.Empty, 12, WidgetChrome.Secondary);
        _detail.Margin = new Thickness(8 * s, 1 * s, 0, 0);
        _detail.VerticalAlignment = VerticalAlignment.Center;

        Children.Add(new TitleRow { Children = { _glyph, _title, _detail } });
    }

    public string Glyph
    {
        set => _glyph.Text = value;
    }

    public string Title
    {
        set => _title.Text = value;
    }

    public string Detail
    {
        set => _detail.Text = value;
    }

    public GlyphButton AddAction(string glyph, string tip, Action onClick)
    {
        var button = new GlyphButton(_context, glyph, 26, onClick) { ToolTip = tip };
        _actions.Children.Add(button);
        return button;
    }

    /// <summary>Glyph, title, detail in a row; when space runs out the title is shortened, not the detail.</summary>
    private sealed class TitleRow : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            var infinite = new Size(double.PositiveInfinity, available.Height);
            var (glyph, title, detail) = (InternalChildren[0], InternalChildren[1], InternalChildren[2]);
            glyph.Measure(infinite);
            detail.Measure(infinite);
            var fixedWidth = glyph.DesiredSize.Width + detail.DesiredSize.Width;
            title.Measure(new Size(Math.Max(0, available.Width - fixedWidth), available.Height));
            return new Size(
                fixedWidth + title.DesiredSize.Width,
                Math.Max(glyph.DesiredSize.Height, Math.Max(title.DesiredSize.Height, detail.DesiredSize.Height)));
        }

        protected override Size ArrangeOverride(Size final)
        {
            var x = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, (final.Height - size.Height) / 2, size.Width, size.Height));
                x += size.Width;
            }
            return final;
        }
    }
}

/// <summary>
/// A one-line text field inside a widget. Enter hands the text to
/// <see cref="Committed"/> and clears the field; Escape clears it and gives
/// the keyboard back. Takes care of the desktop's keyboard opt-in.
/// </summary>
internal sealed class InlineInput : Grid
{
    private readonly TextBox _box;
    private readonly TextBlock _hint;
    private bool _editing;
    private bool _tookKeyboard;

    public InlineInput(WidgetContext context, string placeholder, double size = 13)
    {
        var s = context.Scale;
        _box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = WidgetChrome.Primary,
            CaretBrush = WidgetChrome.Primary,
            SelectionBrush = WidgetChrome.Accent,
            FontFamily = WidgetChrome.Body,
            FontSize = size * s,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
        };
        _hint = WidgetChrome.Text(context, placeholder, size, WidgetChrome.Faint);
        _hint.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(2 * s, 0, 0, 0);

        Children.Add(_box);
        Children.Add(_hint);

        _box.PreviewMouseLeftButtonDown += (_, _) => BeginEditing();
        _box.TextChanged += (_, _) => _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _box.LostKeyboardFocus += (_, _) => EndEditing();
        _box.PreviewKeyDown += OnKey;
        Unloaded += (_, _) => EndEditing();
    }

    public event Action<string>? Committed;

    public string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    /// <summary>Puts the caret in the field, as if it had been clicked.</summary>
    public void BeginEditing()
    {
        if (!_editing)
        {
            _editing = true;
            _tookKeyboard = WidgetKeyboard.Begin(this);
        }
        Dispatcher.BeginInvoke(() => Keyboard.Focus(_box), DispatcherPriority.Input);
    }

    private void EndEditing()
    {
        if (!_editing) return;
        _editing = false;
        WidgetKeyboard.End(_tookKeyboard);
        _tookKeyboard = false;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = _box.Text.Trim();
            if (text.Length == 0) return;
            _box.Clear();
            WidgetMenu.Run(() => Committed?.Invoke(text));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _box.Clear();
            Keyboard.ClearFocus();
            EndEditing();
        }
    }
}

/// <summary>
/// Hearth's desktop window never takes keyboard focus unless asked. Text
/// entry asks through here, which only does so on the desktop: the Start
/// menu is an ordinary window that already has the keyboard.
/// </summary>
internal static class WidgetKeyboard
{
    /// <summary>Returns whether it took the keyboard; pass that to <see cref="End"/>.</summary>
    public static bool Begin(Visual element)
    {
        if (DesktopHost.Current is not { } host || !host.Owns(element)) return false;
        host.BeginKeyboardInput();
        return true;
    }

    public static void End(bool began)
    {
        if (began) DesktopHost.Current?.EndKeyboardInput();
    }
}

/// <summary>Menus opened from inside a widget, styled for wherever the widget is.</summary>
internal static class WidgetMenu
{
    public static void Show(FrameworkElement anchor, Action<ContextMenu> fill)
    {
        var onDesktop = DesktopHost.Current?.Owns(anchor) == true;
        var menu = onDesktop ? new ContextMenu { PlacementTarget = anchor } : StartStyle.NewMenu(anchor);
        fill(menu);
        if (menu.Items.Count == 0) return;

        if (onDesktop) DesktopSurface.OpenMenu(menu);
        else menu.IsOpen = true;
    }

    public static MenuItem Item(string header, Action action, bool? isChecked = null)
    {
        var item = new MenuItem { Header = header };
        if (isChecked is { } on)
        {
            item.IsCheckable = true;
            item.IsChecked = on;
        }
        item.Click += (_, e) =>
        {
            e.Handled = true;
            Run(action);
        };
        return item;
    }

    /// <summary>Runs a widget's click handler, logging a failure rather than letting it close Hearth.</summary>
    public static void Run(Action? action)
    {
        if (action is null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("widget action", ex);
        }
    }
}

/// <summary>Icons and shell actions for widgets that show files and apps.</summary>
internal static class WidgetFiles
{
    public static LauncherItem ItemFor(string path) => new()
    {
        Id = path,
        DisplayName = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path,
        Kind = Directory.Exists(path) ? LauncherItemKind.Folder : LauncherItemKind.File,
        Target = path,
        FileSystemPath = path,
    };

    /// <summary>
    /// The item's home-screen icon at <paramref name="pixels"/> physical
    /// pixels, loaded once the image is shown.
    /// </summary>
    public static Image Icon(WidgetContext context, LauncherItem item, double pixels)
    {
        var image = new Image
        {
            Width = pixels,
            Height = pixels,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var options = App.Settings.ToRenderOptions(context.Scale) with
        {
            Size = pixels / context.Scale,
            BakeShadow = false,
            DarkTheme = context.DarkTheme,
        };

        var requested = false;
        image.Loaded += async (_, _) =>
        {
            if (requested) return;
            requested = true;
            try
            {
                image.Source = await App.Icons.GetAsync(item, options).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Write($"widget icon '{item.DisplayName}': {ex.Message}");
            }
        };
        return image;
    }

    /// <summary>The window handle shell dialogs (progress, delete confirmation) should belong to.</summary>
    public static IntPtr OwnerOf(Visual visual) =>
        (PresentationSource.FromVisual(visual) as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;

    public static DataObject? DragData(string path) =>
        File.Exists(path) || Directory.Exists(path)
            ? new DataObject(DataFormats.FileDrop, new[] { path })
            : null;

    /// <summary>Open, Show in folder, Copy path.</summary>
    public static void AddMenuItems(ContextMenu menu, string path)
    {
        menu.Items.Add(WidgetMenu.Item("Open", () => ShellLauncher.Open(path)));
        menu.Items.Add(WidgetMenu.Item("Show in folder", () => ShellLauncher.OpenFileLocation(ItemFor(path))));
        menu.Items.Add(WidgetMenu.Item("Copy path", () => SetClipboardText(path)));
    }

    /// <summary>Clipboard writes fail while another app holds it open, so this retries briefly.</summary>
    public static bool SetClipboardText(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException ex) when (attempt < 5)
            {
                Log.Write($"clipboard busy, retrying: {ex.Message}");
                Thread.Sleep(60);
            }
            catch (ExternalException ex)
            {
                Log.Write($"clipboard write failed: {ex.Message}");
                return false;
            }
        }
    }
}

/// <summary>Small shared layout pieces.</summary>
internal static class WidgetLayout
{
    /// <summary>A vertical scroller without a visible bar; the wheel still scrolls it.</summary>
    public static ScrollViewer Scroller(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.VerticalOnly,
        Content = content,
    };

    /// <summary>The faint glyph and line shown when a widget has nothing to list.</summary>
    public static FrameworkElement Empty(WidgetContext context, string glyph, string text)
    {
        var line = WidgetChrome.Text(context, text, 12.5, WidgetChrome.Secondary);
        line.TextWrapping = TextWrapping.Wrap;
        line.TextAlignment = TextAlignment.Center;
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.Margin = new Thickness(0, 6 * context.Scale, 0, 0);
        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { WidgetChrome.Glyph(context, glyph, 22, WidgetChrome.Faint), line },
        };
    }

    /// <summary>Shows <paramref name="actions"/> only while the mouse is over <paramref name="row"/>.</summary>
    public static void RevealOnHover(UIElement row, UIElement actions)
    {
        actions.Visibility = Visibility.Hidden;
        row.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => actions.Visibility = Visibility.Hidden;
    }
}

/// <summary>How widgets word times, sizes and rates.</summary>
internal static class WidgetFormat
{
    public static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";

        var local = utc.ToLocalTime().Date;
        if (local == DateTime.Today.AddDays(-1)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return local.ToString("dddd");
        return local.ToString("d MMM");
    }

    /// <summary>"25:00", or "1:02:03" past an hour.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var seconds = (long)Math.Ceiling(span.TotalSeconds);
        var (h, m, sec) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    /// <summary>"in 7h 20m", "in 5m", "in under a minute".</summary>
    public static string Until(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "in under a minute";
        var total = (long)span.TotalMinutes;
        var (d, h, m) = (total / 1440, total / 60 % 24, total % 60);
        if (d > 0) return h > 0 ? $"in {d}d {h}h" : $"in {d}d";
        if (h > 0) return m > 0 ? $"in {h}h {m}m" : $"in {h}h";
        return $"in {m}m";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1000 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 || bytes >= 100 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.0} {units[unit]}";
    }
}
