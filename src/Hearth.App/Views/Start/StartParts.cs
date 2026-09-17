using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearth.App.Controls;
using Hearth.Core.Diagnostics;
using Hearth.Core.Icons;
using Hearth.Core.Shell;
using Hearth.Core.Threading;

namespace Hearth.App.Views.Start;

/// <summary>
/// Colours and type for the Start menu, in the Windows light or dark theme
/// (the "Windows mode" setting, which is what the real Start follows). The
/// palette is chosen when the window is built; the controller rebuilds the
/// window when the theme changes.
/// </summary>
internal static class StartStyle
{
    private sealed record Palette(
        Color Text, Color Muted, Color Faint,
        Color Card, Color CardHover, Color CardEdge, Color Field, Color Footer,
        Color Accent, Color OnAccent, Color Selected, Color Scrim,
        Color Tint, Color MenuBackground, Color MenuBorder, Color MenuHighlight, Color MenuSeparator,
        Color Panel, Color Dim);

    private static Palette _palette = Build(light: false);
    private static readonly Dictionary<string, Brush> Cache = [];

    public static bool IsLight { get; private set; }

    /// <summary>Reads the Windows theme and accent. Call before building the window.</summary>
    public static void UseSystemTheme()
    {
        IsLight = SystemTheme.SystemUsesLight;
        _palette = Build(IsLight);
        Cache.Clear();
    }

    private static Palette Build(bool light)
    {
        var accent = SystemTheme.Accent(light);
        var onAccent = light ? Colors.White : Color.FromRgb(0x10, 0x14, 0x1C);
        return light
            ? new Palette(
                Text: Color.FromRgb(0x1B, 0x1B, 0x1F),
                Muted: Color.FromArgb(0xA8, 0x1B, 0x1B, 0x1F),
                Faint: Color.FromArgb(0x70, 0x1B, 0x1B, 0x1F),
                Card: Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
                CardHover: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                CardEdge: Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
                Field: Color.FromArgb(0x14, 0x00, 0x00, 0x00),
                Footer: Color.FromArgb(0x0D, 0x00, 0x00, 0x00),
                Accent: accent,
                OnAccent: onAccent,
                Selected: Color.FromArgb(0x33, accent.R, accent.G, accent.B),
                Scrim: Color.FromArgb(0xE6, 0xF3, 0xF3, 0xF3),
                Tint: Color.FromArgb(0xB8, 0xF3, 0xF3, 0xF3),
                MenuBackground: Color.FromRgb(0xF9, 0xF9, 0xF9),
                MenuBorder: Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                MenuHighlight: Color.FromArgb(0x10, 0x00, 0x00, 0x00),
                MenuSeparator: Color.FromArgb(0x18, 0x00, 0x00, 0x00),
                Panel: Color.FromArgb(0xF5, 0xFA, 0xFA, 0xFA),
                Dim: Color.FromArgb(0x26, 0x00, 0x00, 0x00))
            : new Palette(
                Text: Color.FromRgb(0xF3, 0xF3, 0xF5),
                Muted: Color.FromArgb(0xA8, 0xF3, 0xF3, 0xF5),
                Faint: Color.FromArgb(0x5C, 0xF3, 0xF3, 0xF5),
                Card: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                CardHover: Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
                CardEdge: Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF),
                Field: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                Footer: Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
                Accent: accent,
                OnAccent: onAccent,
                Selected: Color.FromArgb(0x3D, accent.R, accent.G, accent.B),
                Scrim: Color.FromArgb(0xD9, 0x16, 0x16, 0x1B),
                Tint: Color.FromArgb(0xB0, 0x20, 0x20, 0x24),
                MenuBackground: Color.FromRgb(0x2B, 0x2B, 0x30),
                MenuBorder: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
                MenuHighlight: Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
                MenuSeparator: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                Panel: Color.FromArgb(0xF5, 0x2B, 0x2B, 0x30),
                Dim: Color.FromArgb(0x59, 0x00, 0x00, 0x00));
    }

    private static Brush Get(Color color, [System.Runtime.CompilerServices.CallerMemberName] string key = "")
    {
        if (Cache.TryGetValue(key, out var brush)) return brush;
        var created = new SolidColorBrush(color);
        created.Freeze();
        Cache[key] = created;
        return created;
    }

    public static Brush Text => Get(_palette.Text);
    public static Brush Muted => Get(_palette.Muted);
    public static Brush Faint => Get(_palette.Faint);
    public static Brush Card => Get(_palette.Card);
    public static Brush CardHover => Get(_palette.CardHover);
    public static Brush CardEdge => Get(_palette.CardEdge);
    public static Brush Field => Get(_palette.Field);
    public static Brush Footer => Get(_palette.Footer);
    public static Brush Accent => Get(_palette.Accent);
    public static Brush OnAccent => Get(_palette.OnAccent);
    public static Brush Selected => Get(_palette.Selected);
    public static Brush Scrim => Get(_palette.Scrim);

    /// <summary>Near-opaque surface for panels over content (an open folder).</summary>
    public static Brush Panel => Get(_palette.Panel);

    /// <summary>Darkens the content behind an open panel.</summary>
    public static Brush Dim => Get(_palette.Dim);

    /// <summary>Laid over the blurred backdrop, like Acrylic's tint layer.</summary>
    public static Brush Tint => Get(_palette.Tint);

    /// <summary>
    /// Resources for the Start window: tile label and hover colours for the
    /// theme (IconTile looks these up by key), plus themed context menus.
    /// App.xaml's menu styles are dark-only and bound with StaticResource, so
    /// the menu styles are repeated here with this theme's colours.
    /// </summary>
    public static ResourceDictionary WindowResources()
    {
        var resources = DialogChrome.CreateResources();
        resources["LabelBrush"] = Text;
        resources["TileHoverBrush"] = CardHover;
        resources["TileSelectedBrush"] = Selected;
        foreach (var key in MenuResources().Keys) resources[key] = MenuResources()[key];
        return resources;
    }

    private static ResourceDictionary? _menuResources;
    private static bool _menuResourcesLight;

    /// <summary>Themed ContextMenu, MenuItem and Separator styles.</summary>
    public static ResourceDictionary MenuResources()
    {
        if (_menuResources is not null && _menuResourcesLight == IsLight) return _menuResources;

        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        var xaml = MenuXaml
            .Replace("{FG}", Hex(_palette.Text))
            .Replace("{BG}", Hex(_palette.MenuBackground))
            .Replace("{EDGE}", Hex(_palette.MenuBorder))
            .Replace("{HI}", Hex(_palette.MenuHighlight))
            .Replace("{SEP}", Hex(_palette.MenuSeparator));
        _menuResources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
        _menuResourcesLight = IsLight;
        return _menuResources;
    }

    /// <summary>A context menu in the Start theme.</summary>
    public static ContextMenu NewMenu(UIElement anchor)
    {
        var resources = MenuResources();
        return new ContextMenu
        {
            PlacementTarget = anchor,
            Resources = resources,
            Style = (Style)resources[typeof(ContextMenu)],
        };
    }

    private const string MenuXaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="StartMenuFg" Color="{FG}" />
          <SolidColorBrush x:Key="StartMenuBg" Color="{BG}" />
          <SolidColorBrush x:Key="StartMenuEdge" Color="{EDGE}" />
          <SolidColorBrush x:Key="StartMenuHi" Color="{HI}" />
          <SolidColorBrush x:Key="StartMenuSep" Color="{SEP}" />

          <Style TargetType="MenuItem">
            <Setter Property="Foreground" Value="{StaticResource StartMenuFg}" />
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Padding" Value="8,6" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                  <Grid>
                    <Border x:Name="Row" Background="{TemplateBinding Background}" CornerRadius="4" Margin="2,1">
                      <Grid>
                        <Grid.ColumnDefinitions>
                          <ColumnDefinition Width="24" />
                          <ColumnDefinition Width="*" />
                          <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <ContentPresenter x:Name="IconHost" Grid.Column="0" ContentSource="Icon" Width="16" Height="16"
                                          HorizontalAlignment="Center" VerticalAlignment="Center" />
                        <Path x:Name="Check" Grid.Column="0" Visibility="Collapsed" HorizontalAlignment="Center"
                              VerticalAlignment="Center" Width="12" Height="12" Stretch="Uniform"
                              Fill="{StaticResource StartMenuFg}" Data="M 0,5 L 4,9 L 11,1 L 9.5,0 L 4,6.5 L 1.2,4 Z" />
                        <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"
                                          Margin="{TemplateBinding Padding}" VerticalAlignment="Center"
                                          TextElement.Foreground="{StaticResource StartMenuFg}" />
                        <Path x:Name="Arrow" Grid.Column="2" Margin="12,0,8,0" VerticalAlignment="Center"
                              Width="5" Height="9" Stretch="Uniform" Fill="{StaticResource StartMenuFg}"
                              Data="M 0,0 L 5,4.5 L 0,9 Z" />
                      </Grid>
                    </Border>
                    <Popup x:Name="PART_Popup" Placement="Right" HorizontalOffset="-6"
                           IsOpen="{Binding IsSubmenuOpen, RelativeSource={RelativeSource TemplatedParent}}"
                           AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                      <Border Background="{StaticResource StartMenuBg}" BorderBrush="{StaticResource StartMenuEdge}"
                              BorderThickness="1" CornerRadius="6" Padding="4" SnapsToDevicePixels="True">
                        <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
                      </Border>
                    </Popup>
                  </Grid>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                      <Setter TargetName="Row" Property="Background" Value="{StaticResource StartMenuHi}" />
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="Check" Property="Visibility" Value="Visible" />
                      <Setter TargetName="IconHost" Property="Visibility" Value="Collapsed" />
                    </Trigger>
                    <Trigger Property="HasItems" Value="False">
                      <Setter TargetName="Arrow" Property="Visibility" Value="Collapsed" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter Property="Opacity" Value="0.4" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ContextMenu">
            <Setter Property="Foreground" Value="{StaticResource StartMenuFg}" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ContextMenu">
                  <Border Background="{StaticResource StartMenuBg}" BorderBrush="{StaticResource StartMenuEdge}"
                          BorderThickness="1" CornerRadius="6" Padding="4" SnapsToDevicePixels="True">
                    <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="Separator">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Separator">
                  <Rectangle Height="1" Margin="8,4" Fill="{StaticResource StartMenuSep}" />
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>
        </ResourceDictionary>
        """;

    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Glyphs = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Segoe Fluent Icons code points, built at run time (see Claude/notes.md:
    // the tool pipeline mangles escaped glyphs written into source).
    public static string Glyph(int codePoint) => ((char)codePoint).ToString();
    public static readonly string Search = Glyph(0xE721);
    public static readonly string ChevronLeft = Glyph(0xE76B);
    public static readonly string ChevronRight = Glyph(0xE76C);
    public static readonly string Back = Glyph(0xE72B);
    public static readonly string Power = Glyph(0xE7E8);
    public static readonly string Settings = Glyph(0xE713);
    public static readonly string Folder = Glyph(0xE8B7);
    public static readonly string Globe = Glyph(0xE774);
    public static readonly string Gear = Glyph(0xE713);
    public static readonly string Edit = Glyph(0xE70F);
    public static readonly string Open = Glyph(0xE8A7);
    public static readonly string Windows = Glyph(0xE782);
    public static readonly string More = Glyph(0xE712);

    public static TextBlock Label(string text, double size, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = Body,
        Foreground = brush ?? Text,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock GlyphText(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Glyphs,
        FontSize = size,
        Foreground = brush ?? Text,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Icon options for this window's DPI, at a given DIP size.</summary>
    public static IconRenderOptions IconOptions(Visual visual, double size)
    {
        var scale = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        return App.Settings.ToRenderOptions(scale <= 0 ? 1 : scale) with { Size = size };
    }
}

/// <summary>A rounded, hoverable surface that acts on click. Used for most Start controls.</summary>
internal class PressableBorder : Border
{
    private readonly Brush _rest;
    private readonly Brush _hover;
    private bool _pressed;

    public event Action<PressableBorder>? Clicked;
    public event Action<PressableBorder>? RightClicked;

    public PressableBorder(Brush? rest = null, Brush? hover = null, double radius = 8)
    {
        _rest = rest ?? System.Windows.Media.Brushes.Transparent;
        _hover = hover ?? StartStyle.CardHover;
        Background = _rest;
        CornerRadius = new CornerRadius(radius);
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    private bool _selected;

    /// <summary>Keyboard selection in lists.</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateBackground();
        }
    }

    protected void UpdateBackground() =>
        Background = _selected ? StartStyle.Selected : IsMouseOver ? _hover : _rest;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateBackground();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pressed = false;
        UpdateBackground();
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
        if (!_pressed) return;
        _pressed = false;
        e.Handled = true;
        Clicked?.Invoke(this);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (RightClicked is null) return;
        e.Handled = true;
        RightClicked(this);
    }

    /// <summary>For touch taps delivered by the page host.</summary>
    public void RaiseClick() => Clicked?.Invoke(this);

    public void RaiseRightClick() => RightClicked?.Invoke(this);
}

/// <summary>A glyph (and optional text) button.</summary>
internal sealed class ChromeButton : PressableBorder
{
    public ChromeButton(string glyph, string? text = null, string? tooltip = null, double glyphSize = 14)
        : base(radius: 6)
    {
        Padding = new Thickness(10, 7, 10, 7);
        ToolTip = tooltip ?? text;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(StartStyle.GlyphText(glyph, glyphSize));
        if (text is not null)
        {
            var label = StartStyle.Label(text, 13);
            label.Margin = new Thickness(8, 0, 0, 1);
            row.Children.Add(label);
        }
        Child = row;
    }
}

/// <summary>One of the view tabs along the top.</summary>
internal sealed class TabPill : PressableBorder
{
    private readonly TextBlock _label;
    private bool _active;

    public string Key { get; }

    public TabPill(string key, string text) : base(radius: 14)
    {
        Key = key;
        Padding = new Thickness(14, 5, 14, 6);
        Margin = new Thickness(0, 0, 6, 0);
        _label = StartStyle.Label(text, 13, weight: FontWeights.SemiBold);
        Child = _label;
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            _label.Foreground = value ? StartStyle.OnAccent : StartStyle.Text;
            Background = value ? StartStyle.Accent : System.Windows.Media.Brushes.Transparent;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        if (!_active) base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!_active) base.OnMouseLeave(e);
    }
}

/// <summary>
/// An app on the Pages grid or in a category: the same rendered tile the
/// desktop uses, with its badge.
/// </summary>
internal sealed class StartTile : PressableBorder
{
    public const string DragFormat = "HearthStartItem";

    public LauncherItem Item { get; }
    public IconTile Tile { get; }

    private Point? _pressAt;
    private bool _dragged;

    /// <summary>Allows dragging the tile (to reorder pages).</summary>
    public bool CanDrag { get; init; }

    public StartTile(LauncherItem item, double iconSize, bool showLabel = true, IconFit fit = IconFit.Auto)
        : base(radius: 10)
    {
        Item = item;
        Padding = new Thickness(4, 8, 4, 4);
        Tile = new IconTile
        {
            Item = item,
            IconSize = iconSize,
            ShowLabel = showLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Badge = App.Badges.For(item.Id),
            LabelBrush = StartStyle.Text,
        };
        Child = Tile;
        ToolTip = item.DisplayName;

        Loaded += async (_, _) =>
        {
            if (Tile.IconSource is not null || item.Kind == LauncherItemKind.Group) return;
            try
            {
                Tile.IconSource = await App.Icons.GetAsync(item, StartStyle.IconOptions(this, iconSize) with { Fit = fit }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error($"start icon '{item.DisplayName}'", ex);
            }
        };
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressAt = e.GetPosition(this);
        _dragged = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!CanDrag || _pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragged = true;
        _pressAt = null;
        Opacity = 0.4;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(DragFormat, Item.Id), DragDropEffects.Move);
        }
        finally
        {
            Opacity = 1;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _pressAt = null;
        if (_dragged)
        {
            _dragged = false;
            e.Handled = true;
            return;
        }
        base.OnMouseLeftButtonUp(e);
    }
}

/// <summary>A row in All apps or the search results: icon, title, optional detail.</summary>
internal sealed class ResultRow : PressableBorder
{
    private readonly Image _icon;

    public LauncherItem? Item { get; }

    public ResultRow(string title, string? detail, LauncherItem? item, string? glyph = null, double iconSize = 28)
        : base(radius: 6)
    {
        Item = item;
        Padding = new Thickness(8, 5, 8, 5);
        Margin = new Thickness(0, 0, 4, 2);

        _icon = new Image { Width = iconSize, Height = iconSize };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

        var iconHost = new Grid { Width = iconSize, Height = iconSize, Margin = new Thickness(0, 0, 12, 0) };
        if (glyph is not null) iconHost.Children.Add(StartStyle.GlyphText(glyph, iconSize * 0.6));
        else iconHost.Children.Add(_icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(StartStyle.Label(title, 13.5));
        if (!string.IsNullOrEmpty(detail))
        {
            var sub = StartStyle.Label(detail, 11.5, StartStyle.Muted);
            sub.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(sub);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(iconHost, Dock.Left);
        dock.Children.Add(iconHost);
        dock.Children.Add(text);
        Child = dock;
        ToolTip = item?.FileSystemPath ?? item?.DisplayName ?? title;

        if (item is not null && glyph is null) Loaded += async (_, _) => await LoadIconAsync(item, iconSize).ConfigureAwait(true);
    }

    private async Task LoadIconAsync(LauncherItem item, double size)
    {
        if (_icon.Source is not null) return;
        try
        {
            if (item.Kind == LauncherItemKind.App)
            {
                _icon.Source = await App.Icons.GetAsync(item, StartStyle.IconOptions(this, size)).ConfigureAwait(true);
                return;
            }

            // Files keep their own shell icon (a PDF looks like a PDF) rather
            // than an adaptive tile.
            var path = item.FileSystemPath ?? item.Target;
            if (path is null) return;
            var raw = await StaTask.Run(() => IconExtractor.Extract(path, 48)).ConfigureAwait(true);
            if (raw is not null) _icon.Source = raw.ToBitmapSource();
        }
        catch (Exception ex)
        {
            Log.Error($"start row icon '{item.DisplayName}'", ex);
        }
    }
}

/// <summary>
/// Widgets as the Start menu shows them. The desktop hands a widget its exact
/// pixel size, and a widget laid out much smaller than it was designed for
/// crowds and clips. Here the widget is laid out at desktop density instead —
/// about 105 x 125 per cell — on a canvas with the slot's proportions, and the
/// whole thing is scaled to fit. Text, controls and spacing keep their
/// proportions at any size, however far the slot is from the desktop's.
/// </summary>
internal static class StartWidgets
{
    private const double DesignCellWidth = 105;
    private const double DesignCellHeight = 125;

    public static FrameworkElement Create(Hearth.App.Widgets.IWidget widget, Size slot, int columns, int rows, bool bare = false)
    {
        var fit = Math.Min(slot.Width / (columns * DesignCellWidth), slot.Height / (rows * DesignCellHeight));
        fit = Math.Clamp(fit, 0.35, 1.5);
        var design = new Size(slot.Width / fit, slot.Height / fit);

        FrameworkElement view;
        try
        {
            var context = new Hearth.App.Widgets.WidgetContext
            {
                PixelSize = design,
                Scale = 1,
                DarkTheme = !StartStyle.IsLight,
                Bare = bare,
            };
            using var theme = Hearth.App.Widgets.WidgetChrome.Scope(context);
            view = widget.CreateView(context);
        }
        catch (Exception ex)
        {
            Log.Error($"start widget {widget.Id}", ex);
            view = new Border();
        }

        view.Width = design.Width;
        view.Height = design.Height;
        return new Viewbox
        {
            Stretch = Stretch.Fill,
            Width = slot.Width,
            Height = slot.Height,
            Child = view,
        };
    }
}

/// <summary>Lock, sleep, sign out, restart, shut down.</summary>
internal static class PowerActions
{
    public static void Lock() => LockWorkStation();

    public static void Sleep() => SetSuspendState(false, false, false);

    public static void SignOut() => ExitWindowsEx(EWX_LOGOFF, 0);

    public static void Restart() => RunShutdown("/r /t 0");

    public static void ShutDown() => RunShutdown("/s /t 0");

    private static void RunShutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"shutdown {arguments}", ex);
        }
    }

    private const uint EWX_LOGOFF = 0;

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("powrprof.dll")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
}

internal static class UserInfo
{
    /// <summary>The account's display name ("Caleb Rieger"), or the user name.</summary>
    public static string DisplayName
    {
        get
        {
            try
            {
                uint size = 256;
                var buffer = new StringBuilder((int)size);
                if (GetUserNameEx(NameDisplay, buffer, ref size) && buffer.Length > 0) return buffer.ToString();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return Environment.UserName;
        }
    }

    private const int NameDisplay = 3;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
}

/// <summary>Windows Settings pages the search box can jump to.</summary>
internal static class SettingsPages
{
    public static readonly (string Name, string Uri, string Keywords)[] All =
    [
        ("Display", "ms-settings:display", "screen resolution brightness scale monitor hdr"),
        ("Sound", "ms-settings:sound", "volume audio speakers microphone output input"),
        ("Notifications", "ms-settings:notifications", "do not disturb focus alerts toasts"),
        ("Power & battery", "ms-settings:powersleep", "sleep battery energy saver screen timeout"),
        ("Storage", "ms-settings:storagesense", "disk space cleanup"),
        ("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth pair headphones controller"),
        ("Printers & scanners", "ms-settings:printers", "print scan"),
        ("Mouse", "ms-settings:mousetouchpad", "pointer cursor scroll"),
        ("Touchpad", "ms-settings:devices-touchpad", "gestures trackpad"),
        ("Network & internet", "ms-settings:network", "ethernet connection"),
        ("Wi-Fi", "ms-settings:network-wifi", "wireless wifi"),
        ("VPN", "ms-settings:network-vpn", "vpn"),
        ("Personalization", "ms-settings:personalization", "look appearance"),
        ("Background", "ms-settings:personalization-background", "wallpaper desktop picture"),
        ("Colors", "ms-settings:colors", "accent dark mode light mode theme"),
        ("Themes", "ms-settings:themes", "theme"),
        ("Taskbar", "ms-settings:taskbar", "auto hide taskbar alignment"),
        ("Installed apps", "ms-settings:appsfeatures", "uninstall programs remove"),
        ("Default apps", "ms-settings:defaultapps", "browser file associations open with"),
        ("Startup apps", "ms-settings:startupapps", "boot login autostart"),
        ("Your info", "ms-settings:yourinfo", "account profile picture"),
        ("Sign-in options", "ms-settings:signinoptions", "password pin windows hello fingerprint"),
        ("Date & time", "ms-settings:dateandtime", "clock time zone"),
        ("Language & region", "ms-settings:regionlanguage", "keyboard input locale"),
        ("Gaming", "ms-settings:gaming-gamebar", "game bar game mode"),
        ("Accessibility", "ms-settings:easeofaccess", "narrator magnifier text size contrast"),
        ("Privacy & security", "ms-settings:privacy", "permissions location camera microphone"),
        ("Windows Update", "ms-settings:windowsupdate", "updates upgrade"),
        ("About", "ms-settings:about", "pc name specs device information"),
        ("Multitasking", "ms-settings:multitasking", "snap virtual desktops alt tab"),
        ("Night light", "ms-settings:nightlight", "blue light warm"),
        ("Clipboard", "ms-settings:clipboard", "clipboard history paste"),
        ("Mobile devices", "ms-settings:mobile-devices", "phone link android"),
        ("Remote Desktop", "ms-settings:remotedesktop", "rdp remote"),
        ("Troubleshoot", "ms-settings:troubleshoot", "fix problems"),
        ("Recovery", "ms-settings:recovery", "reset reinstall restore"),
        ("Activation", "ms-settings:activation", "product key license"),
        ("Windows Security", "windowsdefender:", "antivirus defender firewall virus"),
        ("Control Panel", "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}", "classic control panel"),
    ];

    public static IEnumerable<(string Name, string Uri)> Match(string query, int max)
    {
        var q = query.Trim();
        return All
            .Select(p => (p.Name, p.Uri, score: Math.Max(AppSearch.Score(p.Name, q), p.Keywords.Contains(q, StringComparison.CurrentCultureIgnoreCase) && q.Length > 2 ? 1 : 0)))
            .Where(p => p.score > 0)
            .OrderByDescending(p => p.score)
            .Take(max)
            .Select(p => (p.Name, p.Uri));
    }
}

/// <summary>Turns a Start item id back into something launchable.</summary>
internal static class StartItems
{
    /// <summary>An installed app by AUMID, or a file or folder by path.</summary>
    public static LauncherItem? Resolve(string id, IReadOnlyDictionary<string, LauncherItem> apps)
    {
        if (apps.TryGetValue(id, out var app)) return app;
        if (!Path.IsPathRooted(id) || !(File.Exists(id) || Directory.Exists(id))) return null;

        var isFolder = Directory.Exists(id);
        var extension = Path.GetExtension(id);
        return new LauncherItem
        {
            Id = id,
            DisplayName = isFolder ? Path.GetFileName(id.TrimEnd(Path.DirectorySeparatorChar)) : Path.GetFileNameWithoutExtension(id),
            Kind = isFolder ? LauncherItemKind.Folder
                : extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                    ? LauncherItemKind.Shortcut
                    : LauncherItemKind.File,
            Target = id,
            FileSystemPath = id,
        };
    }
}

/// <summary>The Windows theme and accent colour.</summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>"Choose your default Windows mode" — what Start and the taskbar follow.</summary>
    public static bool SystemUsesLight
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
    }

    /// <summary>
    /// The system accent, in the shade Windows uses on this theme: a lighter
    /// one on dark surfaces, a darker one on light surfaces.
    /// </summary>
    public static Color Accent(bool light)
    {
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            var c = settings.GetColorValue(light
                ? Windows.UI.ViewManagement.UIColorType.AccentDark1
                : Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException)
        {
            return light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x99, 0xEB, 0xFF);
        }
    }
}
