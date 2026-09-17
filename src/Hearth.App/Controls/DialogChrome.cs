using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace Hearth.App.Controls;

/// <summary>
/// The look shared by Hearth's ordinary windows (weather setup, add apps):
/// dark surface, rounded fields, accent buttons, dark title bar.
/// </summary>
internal static class DialogChrome
{
    /// <summary>A fresh dictionary per window; parsed styles cannot be shared across owners safely.</summary>
    public static ResourceDictionary CreateResources() => (ResourceDictionary)XamlReader.Parse(Styles);

    public static void Apply(Window window)
    {
        window.Background = Brush("#FF1C1C21");
        window.Foreground = Brush("#FFF2F2F4");
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        window.FontSize = 14;
        window.Resources = CreateResources();
        window.SourceInitialized += (_, _) => UseDarkTitleBar(window);
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public static void UseDarkTitleBar(Window window, bool dark = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var on = dark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE; ignored harmlessly on builds without it.
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    /// <summary>
    /// Gives a window a Windows 11 system backdrop (Mica, Mica Alt or Acrylic)
    /// with rounded corners. The window must be transparent to its client edge
    /// for the material to show: WindowChrome extends the frame over the whole
    /// client area, and the HwndSource background is cleared to transparent.
    /// Returns false on builds without system backdrops.
    /// </summary>
    public static bool ApplyBackdrop(Window window, Hearth.Core.Settings.StartBackdrop kind, bool dark = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;

        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        UseDarkTitleBar(window, dark);

        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

        var backdrop = kind switch
        {
            Hearth.Core.Settings.StartBackdrop.Blur => 1, // none: the window paints its own
            Hearth.Core.Settings.StartBackdrop.Acrylic => 3,
            Hearth.Core.Settings.StartBackdrop.MicaAlt => 4,
            _ => 2,
        };
        return DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0; // DWMWA_SYSTEMBACKDROP_TYPE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const string Styles = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="Field" Color="#FF2A2A31" />
          <SolidColorBrush x:Key="FieldEdge" Color="#33FFFFFF" />
          <SolidColorBrush x:Key="Text" Color="#FFF2F2F4" />
          <SolidColorBrush x:Key="Accent" Color="#FF8AB4F8" />

          <Style TargetType="Button">
            <Setter Property="Foreground" Value="{StaticResource Text}" />
            <Setter Property="Background" Value="{StaticResource Field}" />
            <Setter Property="Padding" Value="14,7" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="8"
                          BorderBrush="{StaticResource FieldEdge}" BorderThickness="1" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Opacity" Value="0.85" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                      <Setter TargetName="Bd" Property="Opacity" Value="0.4" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Background" Value="{StaticResource Accent}" />
            <Setter Property="Foreground" Value="#FF10182A" />
            <Setter Property="FontWeight" Value="SemiBold" />
          </Style>

          <Style x:Key="Link" TargetType="Button">
            <Setter Property="Foreground" Value="{StaticResource Accent}" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Margin" Value="0,6,0,0" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Button">
                  <TextBlock Text="{TemplateBinding Content}" TextDecorations="Underline" />
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="TextBox">
            <Setter Property="Foreground" Value="{StaticResource Text}" />
            <Setter Property="Background" Value="{StaticResource Field}" />
            <Setter Property="BorderBrush" Value="{StaticResource FieldEdge}" />
            <Setter Property="CaretBrush" Value="{StaticResource Text}" />
            <Setter Property="Padding" Value="10,8" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="1" CornerRadius="8">
                    <ScrollViewer x:Name="PART_ContentHost" />
                  </Border>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ListBox">
            <Setter Property="Background" Value="{StaticResource Field}" />
            <Setter Property="Foreground" Value="{StaticResource Text}" />
            <Setter Property="BorderBrush" Value="{StaticResource FieldEdge}" />
            <Setter Property="BorderThickness" Value="1" />
          </Style>

          <Style TargetType="ListBoxItem">
            <Setter Property="Padding" Value="10,7" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                  <Border x:Name="Bd" Background="Transparent" Padding="{TemplateBinding Padding}">
                    <ContentPresenter />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#1FFFFFFF" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#408AB4F8" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style x:Key="ScrollThumb" TargetType="Thumb">
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                  <Border x:Name="Bd" Background="#40FFFFFF" CornerRadius="3" />
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Bd" Property="Background" Value="#70FFFFFF" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="ScrollBar">
            <Setter Property="Width" Value="6" />
            <Setter Property="MinWidth" Value="6" />
            <Setter Property="Margin" Value="4,0,0,0" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb>
                      <Thumb Style="{StaticResource ScrollThumb}" />
                    </Track.Thumb>
                  </Track>
                </ControlTemplate>
              </Setter.Value>
            </Setter>
          </Style>

          <Style TargetType="RadioButton">
            <Setter Property="Foreground" Value="{StaticResource Text}" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
          </Style>
        </ResourceDictionary>
        """;
}
