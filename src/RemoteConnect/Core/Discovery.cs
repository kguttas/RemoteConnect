using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteConnect.Core;

/// <summary>What a host advertises on the LAN. The Id is stable across IP changes; the IP is learnt from the packet.</summary>
public sealed class HostBeacon
{
    public string T { get; set; } = "host";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public int Port { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? Mac { get; set; }
    public bool Busy { get; set; }
    public int Version { get; set; } = ProtocolInfo.Version;
}

public sealed class DiscoveredHost
{
    public required HostBeacon Beacon { get; init; }
    public required IPAddress Address { get; init; }
    public DateTime LastSeen { get; init; }
}

public static class NetUtil
{
    /// <summary>Directed broadcast address of every active IPv4 interface, plus the limited broadcast.</summary>
    public static List<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var (addr, mask) in LocalIPv4())
        {
            var a = addr.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            var bc = new IPAddress(b);
            if (!list.Contains(bc)) list.Add(bc);
        }
        return list;
    }

    public static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.IPv4Mask is not null && !IPAddress.IsLoopback(ua.Address))
                    yield return (ua.Address, ua.IPv4Mask);
        }
    }

    /// <summary>MAC of the interface that has a default gateway (used for Wake-on-LAN).</summary>
    public static string? PrimaryMac()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                if (ni.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    return ni.GetPhysicalAddress().ToString();
            }
            catch { }
        }
        return null;
    }

    /// <summary>LAN address(es) first (interfaces with a gateway); virtual adapters (WSL, VMs…) after, in brackets.</summary>
    public static string LocalAddressesText()
    {
        var main = new List<string>();
        var other = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    (hasGateway ? main : other).Add(ua.Address.ToString());
        }
        return string.Join(", ", main) + (other.Count > 0 ? $"  (virtuales: {string.Join(", ", other)})" : "");
    }

    public static void SendWakeOnLan(string mac)
    {
        var bytes = Convert.FromHexString(mac.Replace("-", "").Replace(":", ""));
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) bytes.CopyTo(packet, 6 + i * 6);
        using var udp = new UdpClient { EnableBroadcast = true };
        foreach (var bc in BroadcastAddresses())
        {
            try { udp.Send(packet, packet.Length, new IPEndPoint(bc, 9)); } catch { }
        }
    }

    public static UdpClient CreateUdp(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        // Ignore ICMP port-unreachable resets so ReceiveAsync keeps working.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, [0], null); } catch { }
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }
}

/// <summary>Host side: answers discovery queries and periodically announces itself.</summary>
public sealed class DiscoveryResponder : IDisposable
{
    private readonly Func<HostBeacon> _beacon;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryResponder(Func<HostBeacon> beacon) => _beacon = beacon;

    public void Start()
    {
        _udp = NetUtil.CreateUdp(ProtocolInfo.DiscoveryPort);
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(AnnounceLoop);
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_cts.Token);
                using var doc = JsonDocument.Parse(r.Buffer);
                if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "discover")
                {
                    var data = ProtocolInfo.ToJson(_beacon());
                    await _udp.SendAsync(data, r.RemoteEndPoint, _cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) when (ex is JsonException or SocketException) { }
        }
    }

    private async Task AnnounceLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var data = ProtocolInfo.ToJson(_beacon());
                foreach (var bc in NetUtil.BroadcastAddresses())
                {
                    try { await _udp!.SendAsync(data, new IPEndPoint(bc, ProtocolInfo.AnnouncePort)); } catch (SocketException) { }
                }
                await Task.Delay(3000, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log.Error("announce", ex); await Task.Delay(3000); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}

/// <summary>Client side: collects beacons and resolves a host Id to its current address.</summary>
public sealed class DiscoveryBrowser : IDisposable
{
    private static readonly byte[] Query = """{"t":"discover"}"""u8.ToArray();
    private readonly ConcurrentDictionary<string, DiscoveredHost> _hosts = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public event Action? Changed;

    public IReadOnlyCollection<DiscoveredHost> Hosts => _hosts.Values.ToList();

    public void Start()
    {
        try
        {
            _udp = NetUtil.CreateUdp(ProtocolInfo.AnnouncePort);
        }
        catch (SocketException)
        {
            // Port busy (another client instance): fall back to an ephemeral port, we still get query replies.
            _udp = NetUtil.CreateUdp(0);
        }
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Probe();
                try { await Task.Delay(5000, _cts.Token); } catch { break; }
            }
        });
    }

    public void Probe()
    {
        if (_udp is null) return;
        foreach (var bc in NetUtil.BroadcastAddresses())
        {
            try { _udp.Send(Query, Query.Length, new IPEndPoint(bc, ProtocolInfo.DiscoveryPort)); } catch { }
        }
        // Loopback too, so a host on this same machine is found.
        try { _udp.Send(Query, Query.Length, new IPEndPoint(IPAddress.Loopback, ProtocolInfo.DiscoveryPort)); } catch { }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_cts.Token);
                var beacon = JsonSerializer.Deserialize<HostBeacon>(r.Buffer, ProtocolInfo.Json);
                if (beacon is null || beacon.T != "host" || string.IsNullOrEmpty(beacon.Id)) continue;
                var addr = r.RemoteEndPoint.Address;
                // Prefer a LAN address over loopback when the host is local and seen on both.
                if (IPAddress.IsLoopback(addr) && _hosts.TryGetValue(beacon.Id, out var prev) && !IPAddress.IsLoopback(prev.Address)
                    && (DateTime.UtcNow - prev.LastSeen).TotalSeconds < 10)
                    addr = prev.Address;
                bool isNew = !_hosts.TryGetValue(beacon.Id, out var old) || !old.Address.Equals(addr) || old.Beacon.Busy != beacon.Busy || old.Beacon.Name != beacon.Name;
                _hosts[beacon.Id] = new DiscoveredHost { Beacon = beacon, Address = addr, LastSeen = DateTime.UtcNow };
                if (isNew) Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) when (ex is JsonException or SocketException) { }
        }
    }

    public DiscoveredHost? Find(string id, TimeSpan maxAge) =>
        _hosts.TryGetValue(id, out var h) && DateTime.UtcNow - h.LastSeen < maxAge ? h : null;

    public bool IsOnline(string id) => Find(id, TimeSpan.FromSeconds(12)) is not null;

    /// <summary>Actively probes and waits for a fresh beacon of the given host.</summary>
    public async Task<DiscoveredHost?> ResolveAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Probe();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            if (_hosts.TryGetValue(id, out var h) && h.LastSeen >= started) return h;
            if (sw.ElapsedMilliseconds % 500 < 50) Probe();
            await Task.Delay(50, ct);
        }
        return Find(id, TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}
