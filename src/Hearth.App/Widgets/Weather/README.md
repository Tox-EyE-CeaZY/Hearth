# Weather (`weather`)

Current conditions and the next few days, for your device's location or a
city you pick. It asks where as soon as it's added.

## Files

| File | What it is |
| --- | --- |
| `WeatherWidget.cs` | The widget (`IConfigurableWidget`) and its view |
| `WeatherService.cs` | Open-Meteo forecast and city search, device location, a 15-minute cache |
| `WeatherSetupWindow.cs` | The setup dialog: device location or city search, and units |
| `WeatherConfig.cs` | The settings (`WeatherConfig`) and their store (`WeatherSettings`) |

## Data

`%AppData%/Hearth/weather.json`. Older versions kept the place in
`settings.json` under `Weather`. `WeatherSettings` moves it across the first
time it runs and clears it from `settings.json` (`HearthSettings.LegacyWeather`).

## Background

While shown, a 20-minute refresh. Forecasts are cached for 15 minutes,
because views are rebuilt on every relayout.

## Platform

- Open-Meteo (open-meteo.com): free, no account and no key. The only thing
  sent is the coordinates, or the text typed into the city search.
- Device location through `Windows.Devices.Geolocation` (WinRT). If access
  is refused, the setup dialog links to `ms-settings:privacy-location`.

## Notes

- Built before `WidgetView` existed.
- Default size 4x2, smallest 2x1. On the Widgets board once set up.
