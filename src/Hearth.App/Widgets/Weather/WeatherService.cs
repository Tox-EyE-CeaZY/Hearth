using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Hearth.Core.Diagnostics;
using Windows.Devices.Geolocation;

namespace Hearth.App.Widgets.Weather;

/// <summary>
/// Weather data from Open-Meteo (open-meteo.com): free, no account, no key.
/// The only thing sent is the latitude and longitude being asked about, or the
/// text typed into the city search.
/// </summary>
internal static class WeatherService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Hearth/0.1");
        return client;
    }

    public sealed record Place(string Name, string Detail, double Latitude, double Longitude)
    {
        public override string ToString() => Detail.Length > 0 ? $"{Name} — {Detail}" : Name;
    }

    public sealed record Day(DateOnly Date, int Code, double High, double Low);

    public sealed record Forecast(
        double Temperature,
        double FeelsLike,
        int Code,
        bool IsDay,
        double Wind,
        int Humidity,
        IReadOnlyList<Day> Days,
        DateTime FetchedAt);

    // ---- City search ------------------------------------------------------

    public static async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var url = "https://geocoding-api.open-meteo.com/v1/search"
                  + $"?name={Uri.EscapeDataString(query.Trim())}&count=6&language={language}&format=json";

        var json = await Http.GetStringAsync(url, cancel).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        var places = new List<Place>();
        foreach (var r in results.EnumerateArray())
        {
            var detail = string.Join(", ", new[] { Text(r, "admin1"), Text(r, "country") }.Where(t => t.Length > 0));
            places.Add(new Place(
                Text(r, "name"),
                detail,
                r.GetProperty("latitude").GetDouble(),
                r.GetProperty("longitude").GetDouble()));
        }
        return places;
    }

    // ---- Device location --------------------------------------------------

    /// <summary>
    /// Asks Windows for the current position. Returns null when location
    /// access is off for desktop apps, or no fix could be had in time.
    /// </summary>
    public static async Task<(double Latitude, double Longitude)?> DeviceLocationAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) return null;

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(15));
            var point = position.Coordinate.Point.Position;
            return (point.Latitude, point.Longitude);
        }
        catch (Exception ex)
        {
            // Access denied, no location hardware, or the service is off.
            Log.Write($"device location unavailable: {ex.Message}");
            return null;
        }
    }

    // ---- Forecast ---------------------------------------------------------

    private static Forecast? _cached;
    private static string? _cachedKey;

    /// <summary>
    /// Current conditions plus the next few days. Results are shared for a
    /// quarter of an hour: widget views are rebuilt on every rearrange, and a
    /// drag should not cost a network round trip.
    /// </summary>
    public static async Task<Forecast> ForecastAsync(WeatherConfig config, CancellationToken cancel, bool force = false)
    {
        var invariant = CultureInfo.InvariantCulture;
        var key = string.Join('|',
            config.Latitude.ToString("F3", invariant),
            config.Longitude.ToString("F3", invariant),
            config.Fahrenheit);

        if (!force && _cached is not null && _cachedKey == key && DateTime.Now - _cached.FetchedAt < TimeSpan.FromMinutes(15))
            return _cached;

        var url = "https://api.open-meteo.com/v1/forecast"
                  + $"?latitude={config.Latitude.ToString(invariant)}&longitude={config.Longitude.ToString(invariant)}"
                  + "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m,relative_humidity_2m"
                  + "&daily=weather_code,temperature_2m_max,temperature_2m_min"
                  + "&timezone=auto&forecast_days=6"
                  + (config.Fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : string.Empty);

        var json = await Http.GetStringAsync(url, cancel).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");

        var dates = daily.GetProperty("time").EnumerateArray().Select(e => DateOnly.Parse(e.GetString()!, invariant)).ToList();
        var codes = daily.GetProperty("weather_code").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var highs = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(e => e.GetDouble()).ToList();
        var lows = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(e => e.GetDouble()).ToList();

        var days = new List<Day>(dates.Count);
        for (var i = 0; i < dates.Count; i++) days.Add(new Day(dates[i], codes[i], highs[i], lows[i]));

        var forecast = new Forecast(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1,
            current.GetProperty("wind_speed_10m").GetDouble(),
            current.GetProperty("relative_humidity_2m").GetInt32(),
            days,
            DateTime.Now);

        _cached = forecast;
        _cachedKey = key;
        return forecast;
    }

    // ---- Conditions ---------------------------------------------------------

    /// <summary>WMO weather code to words and a symbol.</summary>
    public static (string Text, string Symbol) Describe(int code, bool isDay) => code switch
    {
        0 => (isDay ? "Clear" : "Clear night", isDay ? Symbol(0x2600) : Symbol(0x263E)),
        1 => ("Mostly clear", isDay ? Symbol(0x1F324) : Symbol(0x263E)),
        2 => ("Partly cloudy", Symbol(0x26C5)),
        3 => ("Overcast", Symbol(0x2601)),
        45 or 48 => ("Fog", Symbol(0x1F32B)),
        51 or 53 or 55 => ("Drizzle", Symbol(0x1F326)),
        56 or 57 => ("Freezing drizzle", Symbol(0x1F327)),
        61 or 63 => ("Rain", Symbol(0x1F327)),
        65 => ("Heavy rain", Symbol(0x1F327)),
        66 or 67 => ("Freezing rain", Symbol(0x1F327)),
        71 or 73 => ("Snow", Symbol(0x2744)),
        75 => ("Heavy snow", Symbol(0x2744)),
        77 => ("Snow grains", Symbol(0x2744)),
        80 or 81 => ("Showers", Symbol(0x1F326)),
        82 => ("Heavy showers", Symbol(0x1F327)),
        85 or 86 => ("Snow showers", Symbol(0x1F328)),
        95 => ("Thunderstorm", Symbol(0x26C8)),
        96 or 99 => ("Thunderstorm, hail", Symbol(0x26C8)),
        _ => ("Unknown", Symbol(0x2601)),
    };

    // Built from code points rather than written as literals, so the source
    // stays plain text no matter how it is edited.
    private static string Symbol(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
