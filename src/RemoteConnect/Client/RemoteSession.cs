using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using RemoteConnect.Core;

namespace RemoteConnect.Client;

public enum SessionState { Connecting, Connected, Reconnecting, AuthFailed, SecurityError, Closed }

/// <summary>
/// Client connection to one host, identified by its stable Id. Every (re)connect races all the ways to reach
/// it at once: the last known address immediately, whatever LAN discovery and name resolution find as they
/// find it, and (if nothing on the LAN answers quickly) the Internet path (STUN + sealed signalling + QUIC
/// hole punching, see <see cref="P2p"/>). The first link that completes its TLS handshake wins.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private sealed class FatalException(SessionState state, string message) : Exception(message)
    {
        public SessionState State { get; } = state;
    }

    /// <summary>An encrypted transport that passed the certificate check, before Hello.</summary>
    private sealed class Link(Stream stream, string? fingerprint, IPEndPoint? direct, QuicConnection? quic, Func<ValueTask> cleanup) : IAsyncDisposable
    {
        public Stream Stream { get; } = stream;
        public string? Fingerprint { get; } = fingerprint;
        public IPEndPoint? Direct { get; } = direct;
        public QuicConnection? Quic { get; } = quic;
        public ValueTask DisposeAsync() => cleanup();
    }

    /// <summary>How long the LAN gets before the Internet path joins the race.</summary>
    private const int LanHeadStartMs = 1200;
    private const int PingIntervalMs = 500;
    /// <summary>Silence after which the link counts as dead (pongs come every 500 ms).</summary>
    private const int SilenceMs = 3500;

    private readonly DiscoveryBrowser _discovery;
    private readonly string _signalServer;
    private (string For, byte[] Key)? _p2pKey;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _optLock = new();
    private StreamOptions _options;
    private string _password;
    private volatile Connection? _conn;
    private Task? _run;
    private int _nextFileId;
    private FileReceiver? _downloads;
    private long _downloadedBytes;

    public KnownHost Host { get; }
    public WelcomeInfo? Info { get; private set; }
    public double RttMs { get; private set; }
    public long BytesReceived => _conn?.BytesReceived ?? 0;
    public bool IsConnected => _conn is { IsClosed: false } && Info is not null;
    /// <summary>True while the current link goes over the Internet (QUIC) rather than the LAN.</summary>
    public bool ViaInternet { get; private set; }
    /// <summary>Host's current video bitrate (adaptive), its encode time and the queueing delay it measures.</summary>
    public int HostBitrateKbps { get; private set; }
    public double HostEncodeMs { get; private set; }
    public double HostQueueMs { get; private set; }

    public event Action<SessionState, string>? StateChanged;
    public event Action<WelcomeInfo>? Welcome;
    public event Action<VideoFormatInfo>? VideoFormat;
    /// <summary>The handler owns the packet and must <see cref="Packet.Return"/> it (or hand it on).</summary>
    public event Action<Packet>? VideoFrame;
    public event Action<byte, int, int, int, int, byte[]>? Cursor;
    public event Action<ClipboardKind, byte[]>? Clipboard;
    /// <summary>(sample rate, channels, codec, data)</summary>
    public event Action<int, int, AudioCodec, byte[]>? Audio;
    public event Action<FileAckInfo>? FileAck;
    /// <summary>A folder listing of the host arrived (network thread).</summary>
    public event Action<DirListingInfo>? DirListing;
    /// <summary>A download started (totals), finished, failed or was cancelled (network thread).</summary>
    public event Action<DownloadInfo>? DownloadState;
    /// <summary>One downloaded file is complete on disk, or failed (network thread).</summary>
    public event Action<FileAckInfo>? FileDownloaded;

    /// <summary>Where downloads from the host are written.</summary>
    public string DownloadFolder { get; set; } = Paths.DefaultReceiveFolder;
    /// <summary>Bytes of the current download received so far.</summary>
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public event Action<string>? Notice;
    /// <summary>Raised when address / fingerprint / name of the host was learnt and should be persisted.</summary>
    public event Action<KnownHost>? HostUpdated;

    public RemoteSession(KnownHost host, string password, StreamOptions options, DiscoveryBrowser discovery, string? signalServer = null)
    {
        Host = host;
        _password = password;
        _options = options.Clone();
        _discovery = discovery;
        _signalServer = signalServer ?? P2p.DefaultSignalServer;
    }

    public StreamOptions Options { get { lock (_optLock) return _options.Clone(); } }

    public void Start() => _run = Task.Run(RunAsync);

    public void Retry(string password)
    {
        _password = password;
        if (_run is { IsCompleted: true }) Start();
    }

    private void SetState(SessionState s, string text) => StateChanged?.Invoke(s, text);

    private async Task RunAsync()
    {
        int attempt = 0;
        string lastError = "";
        while (!_cts.IsCancellationRequested)
        {
            SetState(attempt == 0 ? SessionState.Connecting : SessionState.Reconnecting,
                attempt == 0 ? $"Conectando con {Host.Name}…" : $"Conexión perdida. Reconectando con {Host.Name}… (intento {attempt}){(lastError.Length > 0 ? "\n" + lastError : "")}");
            bool hadSession = false;
            try
            {
                var link = await ConnectRaceAsync(attempt, e => lastError = e);
                if (link is not null)
                {
                    await using (link)
                    {
                        ViaInternet = link.Quic is not null;
                        try { hadSession = await RunSessionAsync(link); }
                        finally { ViaInternet = false; }
                    }
                }
            }
            catch (FatalException ex)
            {
                SetState(ex.State, ex.Message);
                return;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Log.Write($"Session with {Host.Name} failed: {ex.Message}");
            }
            if (hadSession) { attempt = 0; lastError = ""; }
            if (_cts.IsCancellationRequested) break;
            attempt++;
            try { await Task.Delay(hadSession ? 100 : Math.Min(3000, 300 * attempt), _cts.Token); } catch { break; }
        }
        SetState(SessionState.Closed, "Desconectado.");
    }

    /// <summary>
    /// Starts every way of reaching the host concurrently and returns the first authenticated link (the
    /// others are cancelled and closed). Null if all failed; a fatal error (identity changed) only wins when
    /// nothing else connects.
    /// </summary>
    private async Task<Link?> ConnectRaceAsync(int attempt, Action<string> reportError)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var winner = new TaskCompletionSource<Link?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tried = new HashSet<IPEndPoint>();
        var errors = new ConcurrentQueue<Exception>();
        int pending = 1; // the resolver below
        bool anyLan = false;

        void Done()
        {
            if (Interlocked.Decrement(ref pending) == 0) winner.TrySetResult(null);
        }

        async Task Offer(Func<CancellationToken, Task<Link>> open)
        {
            try
            {
                var link = await open(race.Token);
                if (!winner.TrySetResult(link)) await link.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !race.IsCancellationRequested)
            {
                errors.Enqueue(ex);
            }
            catch (OperationCanceledException) { }
            finally { Done(); }
        }

        void TryEndpoint(IPEndPoint ep)
        {
            if (ep.Address.IsIPv4MappedToIPv6) ep = new IPEndPoint(ep.Address.MapToIPv4(), ep.Port);
            lock (tried) if (!tried.Add(ep)) return;
            anyLan = true;
            Interlocked.Increment(ref pending);
            _ = Offer(ct => OpenTcpAsync(ep, ct));
        }

        // 1. The last known address right away (the usual case: nothing changed).
        if (IPAddress.TryParse(Host.LastAddress, out var lastIp)) TryEndpoint(new IPEndPoint(lastIp, Host.Port));

        bool internet = !string.IsNullOrEmpty(Host.InternetId) && P2p.Supported;
        if (internet) Interlocked.Increment(ref pending);

        // 2. LAN discovery and DNS / LLMNR / NetBIOS in parallel; each address joins the race as it appears.
        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>();
                if (!string.IsNullOrEmpty(Host.Id))
                    tasks.Add(Task.Run(async () =>
                    {
                        if (_discovery.Find(Host.Id, TimeSpan.FromSeconds(10)) is { } recent) TryEndpoint(new IPEndPoint(recent.Address, recent.Beacon.Port));
                        var found = await _discovery.ResolveAsync(Host.Id, TimeSpan.FromSeconds(1.5), race.Token);
                        if (found is not null) TryEndpoint(new IPEndPoint(found.Address, found.Beacon.Port));
                    }));
                if (!string.IsNullOrWhiteSpace(Host.LastAddress) && lastIp is null) tasks.Add(Dns(Host.LastAddress));
                if (!string.IsNullOrWhiteSpace(Host.Machine)) tasks.Add(Dns(Host.Machine));
                await Task.WhenAll(tasks);
            }
            catch { }
            finally { Done(); }

            async Task Dns(string name)
            {
                try
                {
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                    t.CancelAfter(2000);
                    foreach (var a in await System.Net.Dns.GetHostAddressesAsync(name, t.Token))
                        if (a.AddressFamily == AddressFamily.InterNetwork) TryEndpoint(new IPEndPoint(a, Host.Port));
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
            }
        });

        // 3. The Internet path, after a short head start for the LAN (or at once if the LAN has nothing).
        if (internet)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    bool lanKnown = lastIp is not null || !string.IsNullOrEmpty(Host.Id) || !string.IsNullOrWhiteSpace(Host.Machine);
                    if (lanKnown)
                    {
                        await Task.WhenAny(winner.Task, Task.Delay(LanHeadStartMs, race.Token));
                        if (winner.Task.IsCompleted) return;
                    }
                    SetState(attempt == 0 ? SessionState.Connecting : SessionState.Reconnecting,
                        $"{(attempt == 0 ? "" : "Conexión perdida. ")}Conectando con {Host.Name} por Internet…{(attempt == 0 ? "" : $" (intento {attempt})")}");
                    Interlocked.Increment(ref pending);
                    await Offer(OpenInternetAsync);
                }
                catch (OperationCanceledException) { }
                finally { Done(); }
            });
        }

        Link? link;
        try { link = await winner.Task.WaitAsync(_cts.Token); }
        finally { race.Cancel(); }
        if (link is not null) return link;

        foreach (var e in errors) if (e is FatalException) throw e;
        var last = errors.LastOrDefault();
        foreach (var e in errors) Log.Write($"Connect to {Host.Name} failed: {e.Message}");
        reportError(last?.Message ?? (anyLan || internet ? "" : "El equipo no responde en la red (¿apagado o dormido?)."));
        return null;
    }

    private async Task<Link> OpenTcpAsync(IPEndPoint ep, CancellationToken ct)
    {
        var tcp = new TcpClient(ep.AddressFamily) { NoDelay = true, ReceiveBufferSize = 4 * 1024 * 1024 };
        string? seenFingerprint = null;
        var expected = Host.Fingerprint ?? _discovery.Find(Host.Id, TimeSpan.FromMinutes(5))?.Beacon.Fingerprint;
        try
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(3000);
                await tcp.ConnectAsync(ep, t.Token);
            }
            var ssl = new SslStream(tcp.GetStream(), false);
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(8000);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "RemoteConnect",
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    seenFingerprint = Security.Fingerprint(cert);
                    // Trust on first use, then pin: the host identity is its certificate.
                    return expected is null || string.Equals(expected, seenFingerprint, StringComparison.OrdinalIgnoreCase);
                },
            }, hs.Token);
            return new Link(ssl, seenFingerprint, ep, null, () => { ssl.Dispose(); tcp.Dispose(); return ValueTask.CompletedTask; });
        }
        catch (System.Security.Authentication.AuthenticationException) when (expected is not null && seenFingerprint is not null)
        {
            tcp.Dispose();
            throw IdentityChanged(Host.Name, seenFingerprint, expected);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            if (ex is OperationCanceledException && !ct.IsCancellationRequested) throw new IOException($"{ep}: sin respuesta");
            throw new IOException($"{ep}: {ex.Message}", ex);
        }
    }

    private static FatalException IdentityChanged(string name, string seen, string expected) => new(SessionState.SecurityError,
        $"La identidad de {name} ha cambiado (huella {Security.ShortFingerprint(seen)}, esperada {Security.ShortFingerprint(expected)}).\n" +
        "Si reinstalaste RemoteConnect en ese equipo, elimínalo de la lista y vuelve a añadirlo.");

    /// <summary>
    /// One Internet attempt: offer our candidates on the sealed topic, wait for the host's answer, punch,
    /// then QUIC to the host's candidates from the punched port.
    /// </summary>
    private async Task<Link> OpenInternetAsync(CancellationToken ct)
    {
        var id = Host.InternetId!;
        if (_p2pKey is not { } cached || cached.For != id + "\n" + _password)
        {
            var pwd = _password;
            _p2pKey = (id + "\n" + pwd, await Task.Run(() => P2p.DeriveKey(pwd, id), ct));
        }
        var key = _p2pKey.Value.Key;
        var topic = P2p.TopicFor(key);
        var signal = new Signaling(_signalServer);
        var sid = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        P2pMessage answer;
        List<IPEndPoint> targets;
        int port;
        using (var punch = new PunchSocket())
        using (var sub = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var stun = punch.StunAsync(ct);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answered = new TaskCompletionSource<P2pMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var listen = signal.SubscribeAsync(topic, text =>
            {
                if (P2p.Open(key, topic, text) is { T: "answer" } m && m.Sid == sid) answered.TrySetResult(m);
            }, () => opened.TrySetResult(), sub.Token);
            try
            {
                if (await Task.WhenAny(opened.Task, listen, Task.Delay(10_000, ct)) != opened.Task)
                    throw new IOException("no se pudo contactar con el servicio de señalización (¿sin Internet o bloqueado?)", listen.Exception?.GetBaseException());
                if (await stun is null) Log.Write("STUN: no public endpoint (offering LAN / IPv6 candidates only)");
                await signal.PublishAsync(topic, P2p.Seal(key, topic, new P2pMessage { T = "offer", Sid = sid, C = punch.Candidates() }), ct);
                if (await Task.WhenAny(answered.Task, Task.Delay(12_000, ct)) != answered.Task)
                    throw new IOException("el equipo no responde por Internet (¿apagado, sin conexión, o contraseña distinta?)");
                answer = answered.Task.Result;
            }
            finally
            {
                sub.Cancel();
                try { await listen; } catch { }
            }
            targets = P2p.Order(answer.C, punch.Public, P2p.GlobalIPv6().Count > 0);
            if (targets.Count == 0) throw new IOException("el equipo no envió direcciones utilizables");
            await punch.PunchAsync(targets, 5, ct);
            port = punch.Port;
        } // releases the port for QUIC

        var expected = Host.Fingerprint ?? answer.Fp;
        Exception? last = null;
        foreach (var ep in targets)
        {
            string? seen = null;
            QuicConnection quic;
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(4000);
                quic = await QuicConnection.ConnectAsync(P2p.ClientOptions(ep, port, (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    seen = Security.Fingerprint(cert);
                    return expected is null || string.Equals(expected, seen, StringComparison.OrdinalIgnoreCase);
                }), t.Token);
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (Exception ex)
            {
                if (seen is not null && expected is not null && !string.Equals(expected, seen, StringComparison.OrdinalIgnoreCase))
                    throw IdentityChanged(Host.Name, seen, expected);
                last = ex;
                Log.Write($"QUIC {ep} failed: {ex.Message}");
                continue;
            }
            Log.Write($"QUIC connected to {ep} from local port {port}");
            QuicStream stream;
            try { stream = await quic.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct); }
            catch { await quic.DisposeAsync(); throw; }
            return new Link(stream, seen, null, quic, async () => { await stream.DisposeAsync(); await quic.DisposeAsync(); });
        }
        throw new IOException("no se pudo atravesar el NAT (ambos lados tras NAT simétrico / CGNAT)" + (last is null ? "" : $": {last.Message}"));
    }

    /// <summary>Hello / Welcome on an authenticated link, then dispatches until it drops. True if a session was established.</summary>
    private async Task<bool> RunSessionAsync(Link link)
    {
        Connection? conn = null;
        try
        {
            conn = new Connection(link.Stream);
            var caps = VideoPipeline.Capabilities();
            var hello = new HelloInfo
            {
                Password = _password,
                Options = Options,
                Decoders = caps.All,
                HardwareDecoders = caps.Hardware,
                ExtraStreams = link.Quic is not null ? ProtocolInfo.LaneCount - 1 : 0,
            };
            conn.SendJson(Msg.Hello, hello);
            if (link.Quic is { } quic)
            {
                // One QUIC stream per lane: a packet lost in a keyframe no longer stalls input, audio or files.
                for (int lane = 1; lane < ProtocolInfo.LaneCount; lane++)
                {
                    var s = await quic.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _cts.Token);
                    await s.WriteAsync(new[] { (byte)lane }, _cts.Token);
                    await s.FlushAsync(_cts.Token);
                    conn.Attach((Lane)lane, s);
                }
            }

            Packet first;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                t.CancelAfter(8000);
                first = await conn.ReceiveAsync(t.Token);
            }
            if (first.Type == Msg.AuthFailed) throw new FatalException(SessionState.AuthFailed, first.Text);
            if (first.Type != Msg.Welcome) throw new InvalidDataException("Respuesta inesperada del host");
            var info = ProtocolInfo.FromJson<WelcomeInfo>(first.Span);
            first.Return();
            if (!string.IsNullOrEmpty(Host.Id) && info.HostId != Host.Id)
                throw new InvalidDataException("En esa dirección ahora hay otro equipo");

            Host.Id = info.HostId;
            Host.Name = info.HostName;
            Host.Machine = info.Machine;
            if (link.Direct is { } direct)
            {
                Host.LastAddress = direct.Address.ToString();
                Host.Port = direct.Port;
            }
            Host.InternetId = info.InternetId ?? Host.InternetId;
            Host.Fingerprint ??= link.Fingerprint;
            Host.LastConnected = DateTime.Now;
            if (_discovery.Find(Host.Id, TimeSpan.FromMinutes(5)) is { } d) Host.Mac = d.Beacon.Mac ?? Host.Mac;
            HostUpdated?.Invoke(Host);

            Info = info;
            _conn = conn;
            SetState(SessionState.Connected, "");
            Welcome?.Invoke(info);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var pinger = PingLoop(conn, linked.Token);
            try
            {
                while (true) Dispatch(conn, await conn.ReceiveAsync(linked.Token));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
            {
                Log.Write($"Session with {Host.Name} ended: {ex.Message}");
            }
            finally
            {
                linked.Cancel();
                _conn = null;
                await pinger;
                if (Interlocked.Exchange(ref _downloads, null) is { } partial)
                {
                    partial.Abort();
                    DownloadState?.Invoke(new DownloadInfo { State = DownloadInfo.Phase.Failed, Error = "Conexión perdida durante la descarga" });
                }
            }
            return true;
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
        }
    }

    private async Task PingLoop(Connection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                conn.Send(Msg.Ping, new PacketWriter().I64(Stopwatch.GetTimestamp()));
                await Task.Delay(PingIntervalMs, ct);
                // The host answers every ping; this much silence means the link (or the host) is gone.
                if (Environment.TickCount64 - conn.LastReceiveTicks > SilenceMs)
                {
                    Log.Write($"Host silent for {SilenceMs} ms, dropping link");
                    conn.Close();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Dispatch(Connection conn, Packet p)
    {
        if (p.Type == Msg.VideoFrame)
        {
            // Ack on arrival: the host paces itself and picks its bitrate on these.
            if (p.Length >= 4) conn.Send(Msg.FrameAck, p.Span[..4]);
            if (VideoFrame is { } handler) handler(p); else p.Return();
            return;
        }
        try
        {
            var r = new PacketReader(p.Span);
            switch (p.Type)
            {
                case Msg.VideoFormat: VideoFormat?.Invoke(ProtocolInfo.FromJson<VideoFormatInfo>(p.Span)); break;
                case Msg.Pong:
                    RttMs = Stopwatch.GetElapsedTime(r.I64()).TotalMilliseconds;
                    break;
                case Msg.StreamStats:
                    HostBitrateKbps = r.I32();
                    HostEncodeMs = r.F32();
                    HostQueueMs = r.F32();
                    break;
                case Msg.CursorShape:
                {
                    byte kind = r.U8();
                    int hx = r.I16(), hy = r.I16(), w = r.I16(), h = r.I16();
                    Cursor?.Invoke(kind, hx, hy, w, h, r.Rest().ToArray());
                    break;
                }
                case Msg.Audio:
                {
                    int rate = r.I32();
                    int ch = r.U8();
                    var codec = (AudioCodec)r.U8();
                    Audio?.Invoke(rate, ch, codec, r.Rest().ToArray());
                    break;
                }
                case Msg.Clipboard:
                    if (p.Length > 1) Clipboard?.Invoke((ClipboardKind)p.Span[0], p.Span[1..].ToArray());
                    break;
                case Msg.FileAck: FileAck?.Invoke(ProtocolInfo.FromJson<FileAckInfo>(p.Span)); break;
                case Msg.DirListing: DirListing?.Invoke(ProtocolInfo.FromJson<DirListingInfo>(p.Span)); break;
                case Msg.FileBegin: _downloads?.Begin(ProtocolInfo.FromJson<FileHeader>(p.Span)); break;
                case Msg.FileChunk:
                {
                    int id = r.I32();
                    var data = r.Rest();
                    if (_downloads is { } d) { d.Chunk(id, data); Interlocked.Add(ref _downloadedBytes, data.Length); }
                    break;
                }
                case Msg.FileEnd: _downloads?.End(r.I32()); break;
                case Msg.DownloadInfo:
                {
                    var info = ProtocolInfo.FromJson<DownloadInfo>(p.Span);
                    if (info.State is DownloadInfo.Phase.Failed or DownloadInfo.Phase.Cancelled) Interlocked.Exchange(ref _downloads, null)?.Abort();
                    else if (info.State == DownloadInfo.Phase.Finished) Interlocked.Exchange(ref _downloads, null)?.Dispose();
                    DownloadState?.Invoke(info);
                    break;
                }
                case Msg.Notice: Notice?.Invoke(p.Text); break;
                case Msg.Welcome:
                    Info = ProtocolInfo.FromJson<WelcomeInfo>(p.Span);
                    Welcome?.Invoke(Info);
                    break;
            }
        }
        finally
        {
            p.Return();
        }
    }

    // ---- outgoing ----
    public void MouseMove(float x, float y) => _conn?.Send(Msg.MouseMove, new PacketWriter().F32(x).F32(y));
    public void MouseButton(MouseButtonId b, bool down) => _conn?.Send(Msg.MouseButton, new PacketWriter().U8((byte)b).U8((byte)(down ? 1 : 0)));
    public void MouseWheel(int delta, bool horizontal) => _conn?.Send(Msg.MouseWheel, new PacketWriter().I16((short)delta).U8((byte)(horizontal ? 1 : 0)));
    public void Key(ushort vk, ushort scan, KeyFlags flags) => _conn?.Send(Msg.Key, new PacketWriter().U16(vk).U16(scan).U8((byte)flags));
    public void Special(SpecialAction a) => _conn?.Send(Msg.Special, new PacketWriter().U8((byte)a));
    public void RequestKeyframe() => _conn?.Send(Msg.RequestKeyframe);

    public void SendClipboard(ClipboardKind kind, byte[] data) =>
        _conn?.Send(Msg.Clipboard, new PacketWriter().U8((byte)kind).Bytes(data), data.Length > 256 * 1024 ? Lane.Bulk : Lane.Control);

    public void SetOptions(StreamOptions o)
    {
        lock (_optLock) _options = o.Clone();
        _conn?.SendJson(Msg.Options, o);
    }

    /// <summary>Asks the host for a folder listing ("" = its drives and the user's folders); see <see cref="DirListing"/>.</summary>
    public void ListRemote(string path) => _conn?.SendJson(Msg.DirList, new DirRequest { Path = path });

    /// <summary>Downloads files / folders of the host into <see cref="DownloadFolder"/>; see <see cref="DownloadState"/>.</summary>
    public void Download(IEnumerable<string> remotePaths)
    {
        var conn = _conn ?? throw new IOException("No hay conexión");
        Directory.CreateDirectory(DownloadFolder);
        Interlocked.Exchange(ref _downloads, new FileReceiver(DownloadFolder, ack => FileDownloaded?.Invoke(ack)))?.Abort();
        Interlocked.Exchange(ref _downloadedBytes, 0);
        conn.SendJson(Msg.FileGet, new FileGetRequest { Paths = remotePaths.ToList() });
    }

    /// <summary>Stops the download; the host confirms with <see cref="DownloadInfo.Phase.Cancelled"/>.</summary>
    public void CancelDownload() => _conn?.Send(Msg.FileCancel);

    /// <summary>Uploads files to the host's receive folder. Progress reports (name, bytesDone, bytesTotal).</summary>
    public async Task UploadAsync(IReadOnlyList<(string FullPath, string Relative)> files, IProgress<(string, long, long)> progress)
    {
        long total = files.Sum(f => new FileInfo(f.FullPath).Length), done = 0;
        var buffer = new byte[256 * 1024];
        foreach (var (full, rel) in files)
        {
            var conn = _conn ?? throw new IOException("No hay conexión");
            int id = Interlocked.Increment(ref _nextFileId);
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
            conn.SendJson(Msg.FileBegin, new FileHeader { Id = id, RelativePath = rel.Replace('\\', '/'), Size = fs.Length }, Lane.Bulk);
            int n;
            while ((n = await fs.ReadAsync(buffer, _cts.Token)) > 0)
            {
                // Keep ~2 MB queued: enough to fill the link, little enough that a cancel / close is quick.
                while (conn.BulkPending > 8)
                {
                    if (conn.IsClosed) throw new IOException("Conexión perdida durante la transferencia");
                    await Task.Delay(2, _cts.Token);
                }
                SendChunk(conn, id, buffer.AsSpan(0, n));
                done += n;
                progress.Report((rel, done, total));
            }
            conn.Send(Msg.FileEnd, new PacketWriter().I32(id), Lane.Bulk);
        }

        static void SendChunk(Connection c, int id, ReadOnlySpan<byte> data)
        {
            Span<byte> head = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(head, id);
            c.Send(Msg.FileChunk, head, data, Lane.Bulk);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _conn?.Close();
        if (_run is not null) { try { await _run; } catch { } }
    }
}
