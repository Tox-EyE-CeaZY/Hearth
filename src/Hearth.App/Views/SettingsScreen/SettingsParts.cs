using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.App.Controls;

namespace Hearth.App.Views.SettingsScreen;

/// <summary>Colours and building blocks for the settings screen (the dark dialog look).</summary>
internal static class SettingsStyle
{
    public static readonly Brush Text = DialogChrome.Brush("#FFF2F2F4");
    public static readonly Brush Muted = DialogChrome.Brush("#A8F2F2F4");
    public static readonly Brush Faint = DialogChrome.Brush("#66F2F2F4");
    public static readonly Brush CardBrush = DialogChrome.Brush("#FF26262D");
    public static readonly Brush CardEdge = DialogChrome.Brush("#1FFFFFFF");
    public static readonly Brush Hover = DialogChrome.Brush("#14FFFFFF");
    public static readonly Brush Track = DialogChrome.Brush("#40FFFFFF");
    public static readonly Brush Accent = DialogChrome.Brush("#FF8AB4F8");
    public static readonly Brush OnAccent = DialogChrome.Brush("#FF10182A");
    public static readonly Brush Good = DialogChrome.Brush("#FF7FD48A");
    public static readonly Brush Warn = DialogChrome.Brush("#FFF2B866");
    public static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static string Glyph(int codePoint) => ((char)codePoint).ToString();

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 26,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        Margin = new Thickness(0, 0, 0, 14),
    };

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 20, 0, 8),
    };

    public static TextBlock Note(string text, Brush? brush = null) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = brush ?? Muted,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A rounded card holding rows, with hairlines between them.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = CardEdge });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = SettingsStyle.CardBrush,
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack,
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    /// <summary>Title and description on the left, the control on the right; 52 DIPs tall at least, for fingers.</summary>
    public static FrameworkElement Row(string title, string? description, UIElement? control, bool wrapControl = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
        {
            var note = Note(description);
            note.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(note);
        }

        var grid = new Grid { MinHeight = 52, Margin = new Thickness(16, 8, 14, 8) };
        if (control is null)
        {
            grid.Children.Add(text);
            return grid;
        }

        if (wrapControl)
        {
            // Wide controls (choices with many options) go under the text.
            var stack = new StackPanel();
            stack.Children.Add(text);
            if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 10, 0, 2);
            stack.Children.Add(control);
            grid.Children.Add(stack);
            return grid;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            element.Margin = new Thickness(16, 0, 0, 0);
        }
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    public static FrameworkElement ToggleRow(string title, string? description, bool value, Action<bool> changed) =>
        Row(title, description, new SettingsToggle(value, changed));

    public static FrameworkElement ChoiceRow<T>(string title, string? description, IEnumerable<(T Value, string Label)> options, T current, Action<T> changed, bool wrap = false)
        where T : notnull =>
        Row(title, description, new Segmented<T>(options, current, changed), wrap);

    public static FrameworkElement SliderRow(string title, string? description, double min, double max, double step,
        double value, Func<double, string> format, Action<double> changed)
    {
        var label = new TextBlock { Text = format(value), Width = 64, TextAlignment = TextAlignment.Right, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Applied when the value settles: rebuilding the desktop on every tick is slow.
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            changed(slider.Value);
        };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = format(e.NewValue);
            settle.Stop();
            settle.Start();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, label } };
        return Row(title, description, panel);
    }

    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), MinHeight = 36 };
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        button.Click += (_, _) => action();
        return button;
    }
}

/// <summary>An on/off switch sized for touch. Space toggles it too.</summary>
internal sealed class SettingsToggle : FrameworkElement
{
    private readonly Action<bool> _changed;
    private bool _pressed;

    public bool IsOn { get; private set; }

    public SettingsToggle(bool isOn, Action<bool> changed)
    {
        IsOn = isOn;
        _changed = changed;
        Width = 46;
        Height = 26;
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        System.Windows.Automation.AutomationProperties.SetName(this, "Toggle");
    }

    public void Set(bool on)
    {
        IsOn = on;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        e.Handled = true;
        var p = e.GetPosition(this);
        if (p.X < -8 || p.Y < -8 || p.X > ActualWidth + 8 || p.Y > ActualHeight + 8) return;
        Flip();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        Flip();
    }

    private void Flip()
    {
        IsOn = !IsOn;
        InvalidateVisual();
        _changed(IsOn);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        var w = ActualWidth;
        var track = new Rect(0.5, 0.5, w - 1, h - 1);
        if (IsOn)
        {
            dc.DrawRoundedRectangle(SettingsStyle.Accent, null, track, h / 2, h / 2);
            dc.DrawEllipse(SettingsStyle.OnAccent, null, new Point(w - h / 2, h / 2), h / 2 - 5, h / 2 - 5);
        }
        else
        {
            dc.DrawRoundedRectangle(Brushes.Transparent, new Pen(SettingsStyle.Muted, 1), track, h / 2, h / 2);
            dc.DrawEllipse(SettingsStyle.Muted, null, new Point(h / 2, h / 2), h / 2 - 6, h / 2 - 6);
        }
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(SettingsStyle.Text, 1.5), new Rect(-3, -3, w + 6, h + 6), h / 2 + 3, h / 2 + 3);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }
}

/// <summary>A row of pills, one of which is chosen.</summary>
internal sealed class Segmented<T> : WrapPanel where T : notnull
{
    private readonly List<(T Value, Border Pill, TextBlock Label)> _pills = [];
    private readonly Action<T> _changed;
    private T _current;

    public Segmented(IEnumerable<(T Value, string Label)> options, T current, Action<T> changed)
    {
        _current = current;
        _changed = changed;
        foreach (var (value, text) in options)
        {
            var label = new TextBlock { Text = text, FontSize = 13 };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(14, 6, 14, 7),
                Margin = new Thickness(0, 2, 6, 2),
                MinHeight = 32,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = label,
            };
            var captured = value;
            pill.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Select(captured, raise: true);
            };
            pill.MouseEnter += (_, _) => Paint();
            pill.MouseLeave += (_, _) => Paint();
            _pills.Add((value, pill, label));
            Children.Add(pill);
        }
        Paint();
    }

    public void Select(T value, bool raise)
    {
        var changed = !EqualityComparer<T>.Default.Equals(_current, value);
        _current = value;
        Paint();
        if (raise && changed) _changed(value);
    }

    private void Paint()
    {
        foreach (var (value, pill, label) in _pills)
        {
            var on = EqualityComparer<T>.Default.Equals(value, _current);
            pill.Background = on ? SettingsStyle.Accent : pill.IsMouseOver ? SettingsStyle.Hover : Brushes.Transparent;
            pill.BorderBrush = on ? SettingsStyle.Accent : SettingsStyle.CardEdge;
            label.Foreground = on ? SettingsStyle.OnAccent : SettingsStyle.Text;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}

/// <summary>An entry in the settings screen's left-hand list.</summary>
internal sealed class NavEntry : Border
{
    private readonly TextBlock _label;
    private bool _active;

    public string Key { get; }

    public event Action<NavEntry>? Clicked;

    public NavEntry(string key, int glyph, string text)
    {
        Key = key;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(12, 9, 12, 10);
        Margin = new Thickness(0, 1, 0, 1);
        MinHeight = 42;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        _label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = SettingsStyle.Glyph(glyph),
                    FontFamily = SettingsStyle.Glyphs,
                    FontSize = 16,
                    Width = 30,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _label,
            },
        };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Clicked?.Invoke(this);
        };
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) => Paint();
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Paint();
        }
    }

    private void Paint()
    {
        Background = _active ? DialogChrome.Brush("#2E8AB4F8") : IsMouseOver ? SettingsStyle.Hover : Brushes.Transparent;
        _label.FontWeight = _active ? FontWeights.SemiBold : FontWeights.Normal;
    }
}

/// <summary>The dark slider look (DialogChrome has none).</summary>
internal static class SliderStyle
{
    public static Style Create()
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="Slider">
                    <Grid Height="28" Background="Transparent">
                      <Border Height="4" CornerRadius="2" Background="#40FFFFFF" VerticalAlignment="Center" />
                      <Track x:Name="PART_Track">
                        <Track.DecreaseRepeatButton>
                          <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                            <RepeatButton.Template>
                              <ControlTemplate TargetType="RepeatButton">
                                <Border Background="Transparent"><Border Height="4" CornerRadius="2" Background="#FF8AB4F8" VerticalAlignment="Center" /></Border>
                              </ControlTemplate>
                            </RepeatButton.Template>
                          </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.IncreaseRepeatButton>
                          <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                            <RepeatButton.Template>
                              <ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate>
                            </RepeatButton.Template>
                          </RepeatButton>
                        </Track.IncreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb Width="20" Height="20">
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Grid>
                                  <Ellipse Fill="#FF2A2A31" Stroke="#40FFFFFF" />
                                  <Ellipse Fill="#FF8AB4F8" Margin="5" />
                                </Grid>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }
}
