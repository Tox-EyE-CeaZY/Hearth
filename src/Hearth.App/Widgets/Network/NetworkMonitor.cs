using System.Net.NetworkInformation;
using System.Net.Sockets;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Widgets.Network;

public enum ConnectionKind
{
    Offline,
    Ethernet,
    Wifi,
    Cellular,
    Other,
}

/// <summary>One second's worth of network activity.</summary>
public sealed record NetworkSnapshot(
    ConnectionKind Kind,
    string Name,
    double DownBytesPerSecond,
    double UpBytesPerSecond,
    int? PingMs,
    IReadOnlyList<(double Down, double Up)> History);

/// <summary>
/// Live up/down speeds, the connection's name and a ping, sampled once a
/// second while anyone is subscribed. History lives here rather than in a
/// view, so a relayout doesn't blank the graph. <see cref="Updated"/> fires
/// on a pool thread.
/// </summary>
public sealed class NetworkMonitor
{
    public const int HistoryLength = 60;
    private const string PingHost = "1.1.1.1";
    private const int PingEverySamples = 5;

    private static readonly Lazy<NetworkMonitor> Instance = new(() => new NetworkMonitor());
    public static NetworkMonitor Current => Instance.Value;

    private readonly object _gate = new();
    private readonly Queue<(double Down, double Up)> _history = new();
    private readonly Dictionary<string, (long Received, long Sent)> _last = [];
    private Timer? _timer;
    private int _subscribers;
    private DateTime _lastAt;
    private NetworkInterface[]? _interfaces;
    private (ConnectionKind Kind, string Name)? _connection;
    private int _sampleCount;
    private int? _ping;
    private bool _pinging;

    private NetworkMonitor()
    {
        for (var i = 0; i < HistoryLength; i++) _history.Enqueue((0, 0));
        NetworkChange.NetworkAddressChanged += (_, _) => Forget();
        NetworkChange.NetworkAvailabilityChanged += (_, _) => Forget();
    }

    public event Action<NetworkSnapshot>? Updated;

    public NetworkSnapshot Latest { get; private set; } = new(ConnectionKind.Offline, "Offline", 0, 0, null, []);

    public void Subscribe()
    {
        lock (_gate)
        {
            if (_subscribers++ > 0) return;
            _last.Clear();
            _timer = new Timer(_ => Sample(), null, 0, 1000);
        }
    }

    public void Unsubscribe()
    {
        lock (_gate)
        {
            if (--_subscribers > 0) return;
            _subscribers = 0;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Adapters or addresses changed: look them up again next sample.</summary>
    private void Forget()
    {
        lock (_gate)
        {
            _interfaces = null;
            _connection = null;
        }
    }

    private void Sample()
    {
        NetworkSnapshot snapshot;
        lock (_gate)
        {
            if (_timer is null) return;
            try
            {
                _interfaces ??= NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .ToArray();
                _connection ??= Describe(_interfaces);

                var now = DateTime.UtcNow;
                var seconds = _lastAt == default ? 1 : Math.Max(0.2, (now - _lastAt).TotalSeconds);
                _lastAt = now;

                double down = 0, up = 0;
                var seen = new HashSet<string>();
                foreach (var nic in _interfaces)
                {
                    IPInterfaceStatistics stats;
                    try
                    {
                        stats = nic.GetIPStatistics();
                    }
                    catch (NetworkInformationException)
                    {
                        continue;
                    }

                    seen.Add(nic.Id);
                    if (_last.TryGetValue(nic.Id, out var before))
                    {
                        down += Math.Max(0, stats.BytesReceived - before.Received) / seconds;
                        up += Math.Max(0, stats.BytesSent - before.Sent) / seconds;
                    }
                    _last[nic.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);

                _history.Enqueue((down, up));
                while (_history.Count > HistoryLength) _history.Dequeue();

                if (_sampleCount++ % PingEverySamples == 0) StartPing(_connection.Value.Kind);

                var (kind, name) = _connection.Value;
                snapshot = new NetworkSnapshot(kind, name, down, up, kind == ConnectionKind.Offline ? null : _ping, _history.ToArray());
                Latest = snapshot;
            }
            catch (Exception ex)
            {
                Log.Write($"network sample failed: {ex.Message}");
                return;
            }
        }
        Updated?.Invoke(snapshot);
    }

    /// <summary>The connection that has the default route, named the way Windows names it.</summary>
    private static (ConnectionKind, string) Describe(NetworkInterface[] interfaces)
    {
        var primary = interfaces
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
            .OrderBy(n => n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            })
            .FirstOrDefault();
        if (primary is null) return (ConnectionKind.Offline, "Offline");

        var kind = primary.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => ConnectionKind.Ethernet,
            NetworkInterfaceType.Wireless80211 => ConnectionKind.Wifi,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => ConnectionKind.Cellular,
            _ => ConnectionKind.Other,
        };

        // For Wi-Fi the profile name is the network's name. Reading the SSID
        // itself needs location permission on current Windows; this doesn't.
        string? profile = null;
        try
        {
            profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.ProfileName;
        }
        catch (Exception ex)
        {
            Log.Write($"network profile unreadable: {ex.Message}");
        }

        var name = kind == ConnectionKind.Wifi && !string.IsNullOrWhiteSpace(profile) ? profile : primary.Name;
        return (kind, name);
    }

    private void StartPing(ConnectionKind kind)
    {
        if (_pinging || kind == ConnectionKind.Offline) return;
        _pinging = true;
        _ = Task.Run(async () =>
        {
            int? result = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(PingHost, 1500).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) result = (int)reply.RoundtripTime;
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
            {
            }
            lock (_gate)
            {
                _ping = result;
                _pinging = false;
            }
        });
    }
}
