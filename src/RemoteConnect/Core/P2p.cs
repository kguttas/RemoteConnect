using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace RemoteConnect.Core;

/// <summary>
/// Internet connections without a server of our own:
///   1. Both peers learn their public UDP endpoint from public STUN servers.
///   2. They swap endpoints through a public ntfy topic. The topic name and every message are derived from /
///      sealed with a key only password holders have (AES-GCM), so the broker sees opaque, unlinkable blobs.
///   3. Both send UDP packets to each other's endpoints (hole punching), release the socket and run QUIC
///      (TLS 1.3) on the same local port, so the NAT mappings just opened carry the session.
/// The host's certificate fingerprint travels inside the sealed answer, so even the first connection is pinned.
/// No relay: when both sides are behind symmetric NATs (some CGNAT / mobile carriers) it cannot connect.
/// </summary>
public static class P2p
{
    public const string DefaultSignalServer = "https://ntfy.sh";
    public static readonly string[] StunServers = ["stun.l.google.com:19302", "stun.cloudflare.com:3478", "stun1.l.google.com:19302"];
    public static readonly SslApplicationProtocol Alpn = new("remoteconnect/1");
    private const int KeyIterations = 200_000;

    // ---- identity ----

    /// <summary>9-digit number derived from the stable HostId, the thing a user reads out over the phone.</summary>
    public static string InternetIdFor(string hostId)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("RemoteConnect.internet-id." + hostId));
        return (100_000_000 + BinaryPrimitives.ReadUInt64LittleEndian(h) % 900_000_000).ToString();
    }

    public static string FormatId(string id) => id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    /// <summary>Accepts "123 456 789", "123-456-789" or "123456789".</summary>
    public static bool TryParseId(string text, out string id)
    {
        id = new string(text.Where(c => c is not (' ' or '-' or '.')).ToArray());
        return id.Length == 9 && id.All(char.IsAsciiDigit) && id[0] != '0';
    }

    /// <summary>Signalling key: only someone with the password (and the ID) can find the topic or read it.</summary>
    public static byte[] DeriveKey(string password, string internetId) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes("RemoteConnect.p2p." + internetId),
            KeyIterations, HashAlgorithmName.SHA256, 32);

    public static string TopicFor(byte[] key) =>
        "rc" + Convert.ToHexStringLower(HMACSHA256.HashData(key, "RemoteConnect.topic"u8))[..30];

    // ---- sealed signalling messages ----

    public static string Seal(byte[] key, string topic, P2pMessage msg)
    {
        var plain = ProtocolInfo.ToJson(msg);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[12 + plain.Length + 16];
        nonce.CopyTo(output, 0);
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length), Encoding.UTF8.GetBytes(topic));
        return Convert.ToBase64String(output);
    }

    public static P2pMessage? Open(byte[] key, string topic, string text)
    {
        try
        {
            var data = Convert.FromBase64String(text.Trim());
            if (data.Length < 12 + 16 + 2) return null;
            var plain = new byte[data.Length - 28];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length), plain, Encoding.UTF8.GetBytes(topic));
            var msg = ProtocolInfo.FromJson<P2pMessage>(plain);
            // Replays of an old offer/answer are useless, but don't even act on them.
            return Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - msg.Ts) < 120_000 ? msg : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException) { return null; }
    }

    // ---- QUIC ----

    public static QuicServerConnectionOptions ServerOptions(X509Certificate2 cert) => new()
    {
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 0,
        IdleTimeout = TimeSpan.FromSeconds(20),
        KeepAliveInterval = TimeSpan.FromSeconds(5), // keeps both NAT mappings open
        MaxInboundBidirectionalStreams = ProtocolInfo.LaneCount, // the session stream + one per extra lane
        MaxInboundUnidirectionalStreams = 0,
        InitialReceiveWindowSizes = ReceiveWindows(),
        ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = [Alpn], ServerCertificate = cert },
    };

    public static QuicClientConnectionOptions ClientOptions(IPEndPoint remote, int localPort, RemoteCertificateValidationCallback validate) => new()
    {
        RemoteEndPoint = remote,
        LocalEndPoint = new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, localPort),
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 0,
        IdleTimeout = TimeSpan.FromSeconds(20),
        KeepAliveInterval = TimeSpan.FromSeconds(5),
        MaxInboundBidirectionalStreams = 0,
        MaxInboundUnidirectionalStreams = 0,
        InitialReceiveWindowSizes = ReceiveWindows(),
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [Alpn],
            TargetHost = "RemoteConnect",
            RemoteCertificateValidationCallback = validate,
        },
    };

    /// <summary>
    /// The default per-stream window (64 KB) caps one stream at window / RTT: about 10 Mbps at 50 ms. Size the
    /// windows for a video stream on a long path (here 16 MB per stream: ~2.6 Gbps at 50 ms).
    /// </summary>
    private static QuicReceiveWindowSizes ReceiveWindows() => new()
    {
        Connection = 64 * 1024 * 1024,
        LocallyInitiatedBidirectionalStream = 16 * 1024 * 1024,
        RemotelyInitiatedBidirectionalStream = 16 * 1024 * 1024,
    };

    public static bool Supported => QuicListener.IsSupported && QuicConnection.IsSupported;

    /// <summary>
    /// Single-file build: System.Net.Quic loads msquic.dll by full path next to the exe, where it isn't (the
    /// csproj bundles it, so it sits in the extraction folder). That load falls back to this event, which
    /// resolves it through our own probing, the way FFmpeg's DLLs are found. Runs before any QUIC type is used.
    /// </summary>
    static P2p()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "msquic.dll"))) return;
        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
            Path.GetFileName(name).StartsWith("msquic", StringComparison.OrdinalIgnoreCase)
            && NativeLibrary.TryLoad("msquic", typeof(P2p).Assembly, DllImportSearchPath.AssemblyDirectory, out var handle)
                ? handle : IntPtr.Zero;
    }

    // ---- candidates ----

    public static IPEndPoint? ParseEndPoint(string s) => IPEndPoint.TryParse(s, out var ep) && ep.Port != 0 ? Normalize(ep) : null;

    public static IPEndPoint Normalize(IPEndPoint ep) => ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    /// <summary>
    /// Order to try the peer's candidates: behind the same public IP (same NAT) the LAN addresses work and
    /// hairpinning often does not; otherwise the public endpoint first, then IPv6, then LAN (other subnet / VPN).
    /// </summary>
    public static List<IPEndPoint> Order(IReadOnlyList<string> peer, IPEndPoint? ourPublic, bool weHaveIPv6)
    {
        var eps = peer.Select(ParseEndPoint).OfType<IPEndPoint>().Distinct().ToList();
        var pub = eps.Where(e => e.AddressFamily == AddressFamily.InterNetwork && IsPublic(e.Address)).ToList();
        var lan = eps.Where(e => e.AddressFamily == AddressFamily.InterNetwork && !IsPublic(e.Address)).ToList();
        var v6 = weHaveIPv6 ? eps.Where(e => e.AddressFamily == AddressFamily.InterNetworkV6).ToList() : [];
        bool sameNat = ourPublic is not null && pub.Any(p => p.Address.Equals(ourPublic.Address));
        return sameNat ? [.. lan, .. pub, .. v6] : [.. pub, .. v6, .. lan];
    }

    public static bool IsPublic(IPAddress a)
    {
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6UniqueLocal && !IPAddress.IsLoopback(a);
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and < 128));
    }

    /// <summary>Global IPv6 addresses of this machine (no NAT there, just the stateful firewall the punch opens).</summary>
    public static List<IPAddress> GlobalIPv6()
    {
        var list = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6)) continue;
            foreach (var ua in props.UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && IsPublic(ua.Address) && !ua.Address.IsIPv6Teredo
                    && ua.SuffixOrigin != SuffixOrigin.Random) // skip short-lived privacy addresses
                    list.Add(ua.Address);
        }
        return list;
    }
}

/// <summary>Plaintext of a sealed signalling message.</summary>
public sealed class P2pMessage
{
    public string T { get; set; } = "";          // "offer" (client) / "answer" (host) / "refuse" (host)
    public string Sid { get; set; } = "";        // attempt id, chosen by the client
    public long Ts { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public List<string> C { get; set; } = new(); // candidate endpoints
    public string? Fp { get; set; }              // host certificate fingerprint (answer)
    public string? Error { get; set; }           // refuse reason
}

/// <summary>
/// A UDP socket used only before QUIC takes over its port: STUN queries and hole-punch packets.
/// Dual-stack, so the same port number serves IPv4 and IPv6 candidates.
/// </summary>
public sealed class PunchSocket : IDisposable
{
    private static readonly byte[] PunchPayload = "RCPUNCH1"u8.ToArray();
    private readonly Socket _s = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };

    public int Port { get; }
    public IPEndPoint? Public { get; private set; }

    public PunchSocket()
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        try { _s.IOControl(SIO_UDP_CONNRESET, [0], null); } catch { }
        _s.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        Port = ((IPEndPoint)_s.LocalEndPoint!).Port;
    }

    /// <summary>Asks the STUN servers (in parallel, first answer wins) for our public IPv4 endpoint.</summary>
    public async Task<IPEndPoint?> StunAsync(CancellationToken ct)
    {
        var tx = new Dictionary<string, byte[]>();
        var targets = new List<IPEndPoint>();
        foreach (var server in P2p.StunServers)
        {
            var parts = server.Split(':');
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(1500);
                var ip = (await Dns.GetHostAddressesAsync(parts[0], AddressFamily.InterNetwork, t.Token)).FirstOrDefault();
                if (ip is not null) targets.Add(new IPEndPoint(ip.MapToIPv6(), int.Parse(parts[1])));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        if (targets.Count == 0) return null;

        var buf = new byte[1500];
        for (int round = 0; round < 3 && Public is null; round++)
        {
            foreach (var target in targets)
            {
                var req = new byte[20];
                BinaryPrimitives.WriteUInt16BigEndian(req, 0x0001); // Binding Request
                BinaryPrimitives.WriteUInt32BigEndian(req.AsSpan(4), 0x2112A442);
                RandomNumberGenerator.Fill(req.AsSpan(8, 12));
                tx[Convert.ToHexString(req, 8, 12)] = req;
                try { await _s.SendToAsync(req, target, ct); } catch (SocketException) { }
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(700 + 400 * round);
            try
            {
                while (Public is null)
                {
                    var r = await _s.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.IPv6Any, 0), wait.Token);
                    Public = ParseStun(buf.AsSpan(0, r.ReceivedBytes), tx);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return Public;
    }

    private static IPEndPoint? ParseStun(ReadOnlySpan<byte> b, Dictionary<string, byte[]> tx)
    {
        if (b.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(b) != 0x0101) return null; // Binding Success
        if (!tx.ContainsKey(Convert.ToHexString(b.Slice(8, 12)))) return null;
        IPEndPoint? mapped = null;
        for (int i = 20; i + 4 <= b.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(b[i..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
            var v = b.Slice(i + 4, Math.Min(len, b.Length - i - 4));
            if (v.Length >= 8 && v[1] == 0x01) // IPv4
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(v[2..]);
                var addr = v.Slice(4, 4).ToArray();
                if (type == 0x0020) // XOR-MAPPED-ADDRESS
                {
                    port ^= 0x2112;
                    addr[0] ^= 0x21; addr[1] ^= 0x12; addr[2] ^= 0xA4; addr[3] ^= 0x42;
                    return new IPEndPoint(new IPAddress(addr), port);
                }
                if (type == 0x0001) mapped = new IPEndPoint(new IPAddress(addr), port); // MAPPED-ADDRESS (old servers)
            }
            i += 4 + ((len + 3) & ~3);
        }
        return mapped;
    }

    /// <summary>Everything the peer could reach us at: public (STUN), global IPv6, LAN — all on this port.</summary>
    public List<string> Candidates()
    {
        var list = new List<string>();
        if (Public is not null) list.Add(Public.ToString());
        foreach (var a in P2p.GlobalIPv6()) list.Add(new IPEndPoint(a, Port).ToString());
        foreach (var (a, _) in NetUtil.LocalIPv4()) list.Add(new IPEndPoint(a, Port).ToString());
        return list.Distinct().ToList();
    }

    /// <summary>Opens our NAT / firewall towards every peer candidate. Packets the peer's NAT drops still do the job.</summary>
    public async Task PunchAsync(IEnumerable<IPEndPoint> peer, int rounds, CancellationToken ct)
    {
        var targets = peer.Select(e => e.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(e.Address.MapToIPv6(), e.Port) : e).ToList();
        for (int i = 0; i < rounds; i++)
        {
            foreach (var t in targets)
            {
                try { await _s.SendToAsync(PunchPayload, t, ct); } catch (SocketException) { }
            }
            await Task.Delay(40, ct);
        }
    }

    public void Dispose() => _s.Dispose();
}

/// <summary>ntfy pub/sub, used as a dumb mailbox for sealed messages. Self-hostable: set SignalServer.</summary>
public sealed class Signaling(string server)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(8),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _server = server.TrimEnd('/');

    public async Task PublishAsync(string topic, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_server}/{topic}") { Content = new StringContent(body) };
        req.Headers.Add("X-Cache", "no");     // deliver to live subscribers only, keep nothing on the server
        req.Headers.Add("X-Firebase", "no");
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(10_000);
        using var resp = await Http.SendAsync(req, t.Token);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Streams message bodies of a topic until cancelled or the connection drops. <paramref name="opened"/>
    /// fires once the subscription is live (only then is it safe to publish something expecting an answer).
    /// </summary>
    public async Task SubscribeAsync(string topic, Action<string> onMessage, Action? opened, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_server}/{topic}/json");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var ev = doc.RootElement.GetProperty("event").GetString();
                if (ev == "open") opened?.Invoke();
                else if (ev == "message" && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { } text)
                    onMessage(text);
            }
            catch (JsonException) { }
        }
        throw new IOException("Signalling stream closed");
    }
}
