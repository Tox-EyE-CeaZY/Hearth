using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Weather;

/// <summary>
/// Current conditions and the next few days.
///
/// Needs a location before it can show anything, so adding it prompts for one
/// (see <see cref="WeatherSetupWindow"/>); the same window is reachable later
/// from the widget's own menu.
/// </summary>
public sealed class WeatherWidget : IWidget, IConfigurableWidget
{
    public string Id => "weather";
    public string Title => "Weather";
    public (int Columns, int Rows) DefaultSpan => (4, 2);
    public (int Columns, int Rows) MinimumSpan => (2, 1);

    public bool NeedsSetup => WeatherSettings.Config is null;

    public bool Configure()
    {
        var window = new WeatherSetupWindow(WeatherSettings.Config);
        if (window.ShowDialog() != true || window.Result is null) return false;

        WeatherSettings.Config = window.Result;
        return true;
    }

    public FrameworkElement CreateView(WidgetContext context) => new WeatherView(context);

    public double BoardHeight(bool wide) => wide ? 180 : 220;
    public int Order => 60;

    public bool OnBoardByDefault => true;

    private sealed class WeatherView : ContentControl
    {
        private readonly WidgetContext _context;
        private readonly TextBlock _place;
        private readonly TextBlock _symbol;
        private readonly TextBlock _temperature;
        private readonly TextBlock _condition;
        private readonly TextBlock _details;
        private readonly UniformGrid _days;
        private readonly DispatcherTimer _refresh;
        private readonly CancellationTokenSource _cancel = new();

        private static readonly FontFamily SymbolFont = new("Segoe UI Emoji, Segoe UI Symbol");

        public WeatherView(WidgetContext context)
        {
            _context = context;
            var s = context.Scale;

            _place = WidgetChrome.Text(context, "Weather", 13, WidgetChrome.Secondary, FontWeights.SemiBold);
            _symbol = new TextBlock
            {
                FontFamily = SymbolFont,
                FontSize = 40 * s,
                Foreground = WidgetChrome.Primary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10 * s, 0),
            };
            _temperature = WidgetChrome.Text(context, "--", 44, weight: FontWeights.Light, display: true);
            _temperature.VerticalAlignment = VerticalAlignment.Center;

            _condition = WidgetChrome.Text(context, "Loading…", 14, weight: FontWeights.SemiBold);
            _details = WidgetChrome.Text(context, string.Empty, 12, WidgetChrome.Secondary);

            var summary = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14 * s, 0, 0, 0),
                Children = { _condition, _details },
            };

            var now = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2 * s, 0, 0),
                Children = { _symbol, _temperature, summary },
            };

            _days = new UniformGrid { Rows = 1, Margin = new Thickness(0, 10 * s, 0, 0) };

            var layout = new StackPanel { Children = { _place, now, _days } };
            Content = WidgetChrome.Card(context, layout);

            // The day strip only fits when there is room for it.
            SizeChanged += (_, _) =>
            {
                _days.Visibility = ActualHeight >= 175 * s ? Visibility.Visible : Visibility.Collapsed;
                _days.Columns = Math.Clamp((int)(ActualWidth / (64 * s)), 2, 6);
                RefreshDayCount();
            };

            _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(20) };
            _refresh.Tick += async (_, _) => await LoadAsync(force: true).ConfigureAwait(true);

            Loaded += async (_, _) =>
            {
                _refresh.Start();
                await LoadAsync(force: false).ConfigureAwait(true);
            };
            Unloaded += (_, _) =>
            {
                _refresh.Stop();
                _cancel.Cancel();
            };
        }

        private WeatherService.Forecast? _last;

        private async Task LoadAsync(bool force)
        {
            var config = WeatherSettings.Config;
            if (config is null)
            {
                _condition.Text = "Not set up";
                _details.Text = "Right-click to choose a location";
                return;
            }

            try
            {
                if (config.UseDeviceLocation && force)
                {
                    // Follow the device if it has moved; keep the old fix if
                    // Windows can't provide a new one right now.
                    var here = await WeatherService.DeviceLocationAsync().ConfigureAwait(true);
                    if (here is { } position)
                    {
                        config.Latitude = position.Latitude;
                        config.Longitude = position.Longitude;
                    }
                }

                var forecast = await WeatherService.ForecastAsync(config, _cancel.Token, force).ConfigureAwait(true);
                Show(config, forecast);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"weather refresh failed: {ex.Message}");
                if (_last is null)
                {
                    _condition.Text = "Weather unavailable";
                    _details.Text = "Couldn't reach Open-Meteo";
                }
            }
        }

        private void Show(WeatherConfig config, WeatherService.Forecast forecast)
        {
            _last = forecast;
            var (text, symbol) = WeatherService.Describe(forecast.Code, forecast.IsDay);
            var today = forecast.Days.FirstOrDefault();

            _place.Text = config.PlaceName;
            _symbol.Text = symbol;
            _temperature.Text = Degrees(forecast.Temperature);
            _condition.Text = text;
            _details.Text = today is null
                ? $"Feels {Degrees(forecast.FeelsLike)}"
                : $"H {Degrees(today.High)}  L {Degrees(today.Low)} · Feels {Degrees(forecast.FeelsLike)}";

            RefreshDayCount();
        }

        private void RefreshDayCount()
        {
            if (_last is null) return;

            using var theme = WidgetChrome.Scope(_context);
            var s = _context.Scale;
            _days.Children.Clear();

            // Tomorrow onwards; today is already in the summary above.
            foreach (var day in _last.Days.Skip(1).Take(_days.Columns))
            {
                var (_, symbol) = WeatherService.Describe(day.Code, isDay: true);
                var name = day.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd", CultureInfo.CurrentCulture);

                var dayName = WidgetChrome.Text(_context, name, 12, WidgetChrome.Secondary, FontWeights.SemiBold);
                dayName.HorizontalAlignment = HorizontalAlignment.Center;

                var icon = new TextBlock
                {
                    Text = symbol,
                    FontFamily = SymbolFont,
                    FontSize = 20 * s,
                    Foreground = WidgetChrome.Primary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2 * s, 0, 2 * s),
                };

                var range = WidgetChrome.Text(_context, $"{Degrees(day.High)} {Degrees(day.Low)}", 12);
                range.HorizontalAlignment = HorizontalAlignment.Center;

                _days.Children.Add(new StackPanel { Children = { dayName, icon, range } });
            }
        }

        private static string Degrees(double value) => $"{Math.Round(value):0}°";
    }
}
