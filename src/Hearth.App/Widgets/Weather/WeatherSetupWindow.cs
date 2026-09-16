using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Hearth.Core.Settings;

namespace Hearth.App.Widgets.Weather;

/// <summary>
/// Asks where the weather widget should report for: the device's location,
/// or a city the user searches for. Shown when the widget is first added, and
/// again from the widget's own menu.
///
/// A separate, ordinary window on purpose: Hearth's desktop window never takes
/// keyboard focus, and a city search needs a keyboard.
/// </summary>
internal sealed class WeatherSetupWindow : Window
{
    private readonly TextBox _search;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly Button _openLocationSettings;
    private readonly RadioButton _celsius;
    private readonly RadioButton _fahrenheit;
    private readonly Button _save;

    private WeatherConfig? _choice;
    private CancellationTokenSource? _searchCancel;

    public WeatherConfig? Result { get; private set; }

    public WeatherSetupWindow(WeatherConfig? existing)
    {
        Title = "Weather — Hearth";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brush("#FF1C1C21");
        Foreground = Brush("#FFF2F2F4");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        Resources = (ResourceDictionary)XamlReader.Parse(Styles);

        var heading = new TextBlock
        {
            Text = "Weather",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };

        var intro = Muted(
            "Choose where to show weather for. Forecasts come from Open-Meteo (open-meteo.com); " +
            "only the location you pick is sent to it.");
        intro.Margin = new Thickness(0, 6, 0, 20);

        var useLocation = new Button { Content = "Use my current location", Style = (Style)Resources["Primary"] };
        useLocation.Click += async (_, _) => await UseDeviceLocationAsync().ConfigureAwait(true);

        _status = Muted(string.Empty);
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Visibility = Visibility.Collapsed;

        _openLocationSettings = new Button
        {
            Content = "Open location settings",
            Style = (Style)Resources["Link"],
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _openLocationSettings.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("ms-settings:privacy-location") { UseShellExecute = true });

        var orSearch = Muted("or search for a city");
        orSearch.Margin = new Thickness(0, 22, 0, 8);

        _search = new TextBox();
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SearchAsync().ConfigureAwait(true);
        };

        var searchButton = new Button { Content = "Search", Margin = new Thickness(8, 0, 0, 0) };
        searchButton.Click += async (_, _) => await SearchAsync().ConfigureAwait(true);

        var searchRow = new DockPanel();
        DockPanel.SetDock(searchButton, Dock.Right);
        searchRow.Children.Add(searchButton);
        searchRow.Children.Add(_search);

        _results = new ListBox { Margin = new Thickness(0, 8, 0, 0), MaxHeight = 190, Visibility = Visibility.Collapsed };
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedItem is not WeatherService.Place place) return;
            Choose(new WeatherConfig
            {
                UseDeviceLocation = false,
                PlaceName = place.Name,
                Latitude = place.Latitude,
                Longitude = place.Longitude,
            }, $"Selected {place}");
        };

        // Default units follow the region, as Windows' own apps do.
        var fahrenheit = existing?.Fahrenheit ?? !RegionInfo.CurrentRegion.IsMetric;
        _celsius = new RadioButton { Content = "°C", GroupName = "units", IsChecked = !fahrenheit, Margin = new Thickness(0, 0, 18, 0) };
        _fahrenheit = new RadioButton { Content = "°F", GroupName = "units", IsChecked = fahrenheit };
        var units = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 22, 0, 0),
            Children = { Muted("Units"), new Border { Width = 16 }, _celsius, _fahrenheit },
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        cancel.Click += (_, _) => DialogResult = false;

        _save = new Button { Content = "Save", IsDefault = false, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, Style = (Style)Resources["Primary"] };
        _save.Click += (_, _) => Save();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 26, 0, 0),
            Children = { cancel, _save },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 24),
            Children = { heading, intro, useLocation, _status, _openLocationSettings, orSearch, searchRow, _results, units, actions },
        };

        if (existing is not null)
        {
            Choose(existing, existing.UseDeviceLocation
                ? "Currently using your device location."
                : $"Currently showing {existing.PlaceName}.");
        }

        Loaded += (_, _) => _search.Focus();
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    private async Task UseDeviceLocationAsync()
    {
        ShowStatus("Finding your location…");
        _openLocationSettings.Visibility = Visibility.Collapsed;

        var location = await WeatherService.DeviceLocationAsync().ConfigureAwait(true);
        if (location is null)
        {
            ShowStatus("Windows didn't share a location. Location access may be off for desktop apps — " +
                       "turn on \"Let desktop apps access your location\", or search for a city instead.");
            _openLocationSettings.Visibility = Visibility.Visible;
            return;
        }

        Choose(new WeatherConfig
        {
            UseDeviceLocation = true,
            PlaceName = "Current location",
            Latitude = location.Value.Latitude,
            Longitude = location.Value.Longitude,
        }, "Using your current location.");
    }

    private async Task SearchAsync()
    {
        _searchCancel?.Cancel();
        _searchCancel = new CancellationTokenSource();
        var token = _searchCancel.Token;

        var query = _search.Text;
        if (string.IsNullOrWhiteSpace(query)) return;

        ShowStatus("Searching…");
        try
        {
            var places = await WeatherService.SearchAsync(query, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            _results.ItemsSource = places;
            _results.Visibility = places.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(places.Count > 0 ? "Pick one from the list." : $"No places found for \"{query}\".");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't search right now: {ex.Message}");
        }
    }

    private void Choose(WeatherConfig config, string message)
    {
        _choice = config;
        _save.IsEnabled = true;
        ShowStatus(message);
    }

    private void Save()
    {
        if (_choice is null) return;
        _choice.Fahrenheit = _fahrenheit.IsChecked == true;
        Result = _choice;
        DialogResult = true;
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("#B3F2F2F4"),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static SolidColorBrush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        // DWMWA_USE_IMMERSIVE_DARK_MODE; ignored harmlessly on builds without it.
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

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
            <Setter Property="Padding" Value="8,6" />
            <Setter Property="Template">
              <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="1" CornerRadius="8">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" />
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

          <Style TargetType="RadioButton">
            <Setter Property="Foreground" Value="{StaticResource Text}" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
          </Style>
        </ResourceDictionary>
        """;
}
