using System.IO;
using System.Text.Json;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Weather;

/// <summary>Where the weather widget reports for, and in which units.</summary>
public sealed class WeatherConfig
{
    /// <summary>Re-read the device location on each refresh instead of using a fixed place.</summary>
    public bool UseDeviceLocation { get; set; }

    public string PlaceName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool Fahrenheit { get; set; }
}

internal sealed class WeatherData
{
    /// <summary>Null until the widget has been set up.</summary>
    public WeatherConfig? Place { get; set; }
}

/// <summary>The weather widget's settings, in %AppData%\Hearth\weather.json.</summary>
internal static class WeatherSettings
{
    private static readonly WidgetStore<WeatherData> Store = new("weather.json");
    private static WeatherData? _data;

    public static WeatherConfig? Config
    {
        get => Data.Place;
        set
        {
            Data.Place = value;
            Store.Save(Data);
        }
    }

    private static WeatherData Data
    {
        get
        {
            if (_data is not null) return _data;
            _data = Store.Load();
            MoveFromAppSettings(_data);
            return _data;
        }
    }

    /// <summary>Older versions kept the place in settings.json; bring it across once.</summary>
    private static void MoveFromAppSettings(WeatherData data)
    {
        if (App.Settings.LegacyWeather is not { } legacy) return;
        try
        {
            data.Place ??= legacy.Deserialize<WeatherConfig>();
            Store.Save(data);
            App.Settings.LegacyWeather = null;
            App.Settings.Save(); // check-widgets: allow - one-time move out of settings.json
            Log.Write("weather: settings moved to weather.json");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"weather: couldn't move old settings: {ex.Message}");
        }
    }
}
