using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Windows.Threading;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// The host: listens for TLS connections, authenticates them, advertises itself on the LAN and
/// hot-reloads settings written by the UI process.
/// </summary>
public sealed class HostService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly X509Certificate2 _cert;
    private readonly string _fingerprint;
    private readonly string? _mac = NetUtil.PrimaryMac();
    private readonly Dictionary<IPAddress, (int Count, DateTime First)> _failures = new();
    private readonly Lock _gate = new();
    private HostSettings _settings;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenCts;
    private DiscoveryResponder? _discovery;
    private P2pHost? _p2p;
    private string? _p2pConfig;
    private HostSession? _session;
    private FileSystemWatcher? _watcher;
    private Timer? _statusTimer;
    private string? _error;

    /// <summary>~2 ms at 1 Gb/s, still a full window for a LAN (Windows sizes the TCP window independently).</summary>
    public const int SendBufferBytes = 256 * 1024;

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Tray balloon text, raised on arbitrary threads.</summary>
    public event Action<string>? Notify;
    public event Action? ExitRequested;

    public HostSession? Session => _session;
    public HostSettings Settings => _settings;

    public HostService(Dispatcher ui)
    {
        _ui = ui;
        _settings = HostSettings.Load();
        _cert = Security.LoadOrCreateHostCertificate();
        _fingerprint = Security.Fingerprint(_cert);
        WarmUpCapture();
    }

    /// <summary>
    /// On hybrid-GPU laptops Desktop Duplication fails with DXGI_ERROR_UNSUPPORTED if the NVIDIA driver
    /// (pulled in by NVENC) initialises before DXGI. Bringing DXGI up first keeps every later capture working.
    /// </summary>
    private static void WarmUpCapture()
    {
        try { using var _ = new ScreenCapture(0); }
        catch (Exception ex) { Log.Write("Capture warm-up failed: " + ex.Message); }
    }

    public void Start()
    {
        ApplySettings();
        _watcher = new FileSystemWatcher(Paths.HostDir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += (s, e) => OnFileEvent(s, e);
        _watcher.EnableRaisingEvents = true;
        _statusTimer = new Timer(_ => WriteStatus(), null, 0, 3000);
        ProcessCommandFile();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileName(e.FullPath);
        if (name.Equals(Path.GetFileName(Paths.HostSettingsFile), StringComparison.OrdinalIgnoreCase))
        {
            Task.Delay(150).ContinueWith(_ =>
            {
                lock (_gate)
                {
                    var fresh = HostSettings.Load();
                    if (fresh.HostId != _settings.HostId && !string.IsNullOrEmpty(_settings.HostId)) fresh.HostId = _settings.HostId;
                    _settings = fresh;
                }
                ApplySettings();
            });
        }
        else if (name.Equals(Path.GetFileName(Paths.HostCommandFile), StringComparison.OrdinalIgnoreCase))
        {
            Task.Delay(100).ContinueWith(_ => ProcessCommandFile());
        }
    }

    private void ProcessCommandFile()
    {
        string cmd;
        try
        {
            if (!File.Exists(Paths.HostCommandFile)) return;
            cmd = File.ReadAllText(Paths.HostCommandFile).Trim();
            File.Delete(Paths.HostCommandFile);
        }
        catch (IOException) { return; }
        Log.Write($"Host command: {cmd}");
        switch (cmd)
        {
            case "disconnect": _session?.Stop(); break;
            case "stop": ExitRequested?.Invoke(); break;
        }
    }

    private void ApplySettings()
    {
        lock (_gate)
        {
            bool shouldListen = _settings.Enabled;
            bool portChanged = _listener is not null && ((IPEndPoint)_listener.LocalEndpoint).Port != _settings.Port;
            if ((!shouldListen || portChanged) && _listener is not null) StopListening();
            if (shouldListen && _listener is null) StartListening();
            ApplyP2p();
        }
        WriteStatus();
    }

    private void StartListening()
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _settings.Port);
            _listener.Server.DualMode = true;
            _listener.Start();
            _listenCts = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _listenCts.Token);
            _discovery = new DiscoveryResponder(MakeBeacon);
            _discovery.Start();
            _error = null;
            Log.Write($"Host listening on {_settings.Port} (elevated={IsElevated}) fp={_fingerprint}");
        }
        catch (Exception ex)
        {
            _error = $"No se pudo escuchar en el puerto {_settings.Port}: {ex.Message}";
            Log.Error("listen", ex);
            StopListening();
        }
    }

    private void StopListening()
    {
        _listenCts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _discovery?.Dispose();
        _discovery = null;
        _p2p?.Dispose();
        _p2p = null;
        _p2pConfig = null;
        _session?.Stop();
    }

    /// <summary>(Re)starts the Internet listener when its inputs change: enabled, key (password), server.</summary>
    private void ApplyP2p()
    {
        bool want = _listener is not null && _settings.InternetEnabled && _settings.P2pKey is not null && P2p.Supported;
        var config = want ? $"{_settings.P2pKey}|{_settings.SignalServer}" : null;
        if (config == _p2pConfig) return;
        _p2p?.Dispose();
        _p2p = null;
        _p2pConfig = config;
        if (!want) return;
        _p2p = new P2pHost(Convert.FromBase64String(_settings.P2pKey!), _settings.SignalServer, _cert, _fingerprint, HandleP2p);
        _p2p.StateChanged += WriteStatus;
        _p2p.Start();
        Log.Write($"Internet listener on, ID {P2p.FormatId(_settings.InternetId)}");
    }

    private string? InternetStatus()
    {
        if (!_settings.InternetEnabled) return null;
        if (!P2p.Supported) return "QUIC no disponible en esta versión de Windows (requiere Windows 11 o Server 2022).";
        if (_settings.P2pKey is null) return _settings.HasPassword
            ? "Vuelve a guardar la contraseña para activar el acceso por Internet."
            : "Configura una contraseña para activar el acceso por Internet.";
        return _p2p?.State;
    }

    private HostBeacon MakeBeacon() => new()
    {
        Id = _settings.HostId,
        Name = _settings.Name,
        Machine = Environment.MachineName,
        Port = _settings.Port,
        Fingerprint = _fingerprint,
        Mac = _mac,
        Busy = _session is not null,
    };

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private async Task HandleClient(TcpClient tcp)
    {
        var remote = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        tcp.NoDelay = true;
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        // Small on purpose: everything sitting in the kernel buffer is latency the app can no longer reorder
        // (input behind a keyframe) nor see (pacing works on the client's frame acks, see RateController).
        tcp.SendBufferSize = SendBufferBytes;
        Connection? conn = null;
        try
        {
            if (IsBlocked(remote)) return;
            Stream raw = tcp.GetStream();
            if (SimLinkStream.Config() is { } sim) raw = new SimLinkStream(raw, sim.Mbps, sim.DelayMs, tcp.SendBufferSize);
            var ssl = new SslStream(raw, false);
            using (var hs = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, hs.Token);
            conn = new Connection(ssl, UnauthenticatedMax);
            await RunConnection(conn, remote, remote.ToString(), false, null);
        }
        catch (Exception ex)
        {
            Log.Write($"Connection from {remote} failed: {ex.Message}");
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
            tcp.Dispose();
        }
    }

    private async Task HandleP2p(QuicConnection quic, QuicStream stream, IPAddress remote)
    {
        var conn = new Connection(stream, UnauthenticatedMax);
        try
        {
            if (IsBlocked(remote)) return;
            // One QUIC stream per lane, opened by the client right after Hello; only read once authenticated.
            await RunConnection(conn, remote, $"{remote} (Internet)", true, async count =>
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    for (int i = 0; i < Math.Min(count, ProtocolInfo.LaneCount - 1); i++)
                    {
                        var s = await quic.AcceptInboundStreamAsync(t.Token);
                        var lane = new byte[1];
                        await s.ReadExactlyAsync(lane, t.Token);
                        if (lane[0] is >= 1 and < ProtocolInfo.LaneCount) conn.Attach((Lane)lane[0], s);
                        else await s.DisposeAsync();
                    }
                    Log.Write($"Internet session from {remote}: {conn.StreamCount} QUIC streams");
                }
                catch (OperationCanceledException)
                {
                    Log.Write($"Internet session from {remote}: extra streams missing, using {conn.StreamCount}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Internet connection from {remote} failed: {ex.Message}");
        }
        finally
        {
            await conn.DisposeAsync();
            await quic.DisposeAsync();
        }
    }

    private bool IsBlocked(IPAddress remote)
    {
        lock (_failures)
            return _failures.TryGetValue(remote, out var f) && f.Count >= 5 && DateTime.UtcNow - f.First < TimeSpan.FromMinutes(1);
    }

    /// <summary>Authenticates an encrypted connection (TLS over TCP, or QUIC) and runs the session on it.</summary>
    /// <summary>Unauthenticated peers may only send small messages (the Hello).</summary>
    private const int UnauthenticatedMax = 64 * 1024;

    private async Task RunConnection(Connection conn, IPAddress remote, string displayAddress, bool viaInternet, Func<int, Task>? attachStreams)
    {
        Packet hello;
        using (var hs = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            hello = await conn.ReceiveAsync(hs.Token);
        if (hello.Type != Msg.Hello) throw new InvalidDataException("Expected Hello");
        var info = ProtocolInfo.FromJson<HelloInfo>(hello.Span);
        hello.Return();

        string? reject = null;
        if (info.ProtocolVersion != ProtocolInfo.Version) reject = "Versión incompatible: actualiza RemoteConnect en ambos equipos.";
        else if (!_settings.HasPassword) reject = "El host no tiene contraseña. Configúrala en 'Este equipo'.";
        else if (!_settings.CheckPassword(info.Password))
        {
            reject = "Contraseña incorrecta.";
            lock (_failures)
            {
                _failures[remote] = _failures.TryGetValue(remote, out var f) && DateTime.UtcNow - f.First < TimeSpan.FromMinutes(1)
                    ? (f.Count + 1, f.First) : (1, DateTime.UtcNow);
            }
            await Task.Delay(1000);
        }
        if (reject is not null)
        {
            Log.Write($"Rejected {remote}: {reject}");
            conn.SendText(Msg.AuthFailed, reject);
            await Task.Delay(500);
            await conn.DisposeAsync();
            return;
        }
        lock (_failures) _failures.Remove(remote);
        conn.MaxIncoming = ProtocolInfo.MaxMessage;
        if (attachStreams is not null && info.ExtraStreams > 0) await attachStreams(info.ExtraStreams);

        var session = new HostSession(conn, info, displayAddress, viaInternet, _settings, _ui);
        var previous = Interlocked.Exchange(ref _session, session);
        previous?.Stop(); // newest viewer wins: a reconnect must never be blocked by a dead half-open session
        session.StatusChanged += WriteStatus;
        Log.Write($"Session started: {session.ClientName} from {displayAddress}");
        Notify?.Invoke($"{session.ClientName} ({displayAddress}) se ha conectado.");
        WriteStatus();
        await session.RunAsync(new WelcomeInfo
        {
            HostId = _settings.HostId,
            HostName = _settings.Name,
            Machine = Environment.MachineName,
            Elevated = IsElevated,
            InternetId = _settings.InternetEnabled ? _settings.InternetId : null,
            FileDownload = _settings.AllowFileDownload,
        });
        Interlocked.CompareExchange(ref _session, null, session);
        Log.Write($"Session ended: {session.ClientName}");
        if (_session is null) Notify?.Invoke($"{session.ClientName} se ha desconectado.");
        WriteStatus();
    }

    public void DisconnectSession() => _session?.Stop();

    private void WriteStatus()
    {
        try
        {
            var s = _session;
            JsonFile.Save(Paths.HostStatusFile, new HostStatus
            {
                Pid = Environment.ProcessId,
                Listening = _listener is not null,
                Elevated = IsElevated,
                Port = _settings.Port,
                Error = _error ?? (!_settings.HasPassword ? "Falta configurar la contraseña." : null),
                SessionClient = s?.ClientName,
                SessionAddress = s?.RemoteAddress,
                SessionSince = s?.Since,
                Encoder = s?.EncoderName,
                SystemService = SessionInterop.IsSystem,
                Internet = InternetStatus(),
                Updated = DateTime.Now,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();
        _watcher?.Dispose();
        lock (_gate) StopListening();
        try { File.Delete(Paths.HostStatusFile); } catch { }
    }
}
