# Network (`network`)

Download and upload speed with a one-minute graph, the connection's name,
and a ping.

## Files

| File | What it is |
| --- | --- |
| `NetworkWidget.cs` | The widget, its view and the graph |
| `NetworkMonitor.cs` | The sampler: speeds, history, connection name, ping |

## Data

None. The last 60 samples are kept in memory, so a relayout doesn't blank
the graph.

## Background

While any Network widget is shown (ref-counted): a sample every second on a
pool thread, and a ping to 1.1.1.1 every 5 s.

## Platform

- `NetworkInterface` byte counters, summed over the interfaces that are up.
- The Wi-Fi name comes from the WinRT connection profile's name. Reading
  the SSID itself needs location permission on current Windows; the
  profile name doesn't. This runs off the UI thread because of WinRT's
  load cost.

## Notes

- The header button opens `ms-settings:network-status`.
- Default size 3x2, smallest 2x1. At narrow widths the speeds use smaller
  type, and on short placements the graph is hidden.
