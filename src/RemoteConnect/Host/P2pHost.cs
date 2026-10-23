using System.Net;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// Host side of Internet connections: waits on the sealed signalling topic and, for each valid offer,
/// punches towards the client and opens a one-shot QUIC listener on the punched port. The accepted
/// stream goes through the same password check as a LAN connection.
/// </summary>
public sealed class P2pHost : IDisposable
{
    private readonly byte[] _key;
    private readonly string _topic;
    private readonly Signaling _signal;
    private readonly X509Certificate2 _cert;
    private readonly string _fingerprint;
    private readonly Func<QuicConnection, QuicStream, IPAddress, Task> _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _oneAtATime = new(2);
    private readonly Queue<string> _seen = new();

    /// <summary>Human-readable state for the UI ("listo", or why not).</summary>
    public string State { get; private set; } = "Conectando con el servicio de señalización…";
    public event Action? StateChanged;

    public P2pHost(byte[] key, string server, X509Certificate2 cert, string fingerprint, Func<QuicConnection, QuicStream, IPAddress, Task> handle)
    {
        _key = key;
        _topic = P2p.TopicFor(key);
        _signal = new Signaling(server);
        _cert = cert;
        _fingerprint = fingerprint;
        _handle = handle;
    }

    public void Start() => _ = Task.Run(SubscribeLoop);

    private void SetState(string s)
    {
        if (s == State) return;
        State = s;
        StateChanged?.Invoke();
    }

    private async Task SubscribeLoop()
    {
        int failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.SubscribeAsync(_topic, OnMessage, () => { failures = 0; SetState("Disponible por Internet"); }, _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1 || failures % 20 == 0) Log.Write($"P2P signalling: {ex.Message}");
                SetState("Sin acceso al servicio de señalización (¿sin Internet?). Reintentando…");
            }
            try { await Task.Delay(Math.Min(30_000, 1000 * failures), _cts.Token); } catch { break; }
        }
    }

    private void OnMessage(string text)
    {
        var msg = P2p.Open(_key, _topic, text);
        if (msg is not { T: "offer" } || msg.Sid.Length is 0 or > 64) return;
        lock (_seen)
        {
            if (_seen.Contains(msg.Sid)) return;
            _seen.Enqueue(msg.Sid);
            if (_seen.Count > 64) _seen.Dequeue();
        }
        _ = Task.Run(() => AnswerAsync(msg));
    }

    private async Task AnswerAsync(P2pMessage offer)
    {
        if (!await _oneAtATime.WaitAsync(0)) return; // a client retries; don't let a burst of offers pile up listeners
        QuicListener? listener = null;
        bool released = false;
        try
        {
            var peer = offer.C.Select(P2p.ParseEndPoint).OfType<IPEndPoint>().Distinct().Take(16).ToList();
            if (peer.Count == 0) return;
            int port;
            List<string> candidates;
            using (var punch = new PunchSocket())
            {
                await punch.StunAsync(_cts.Token);
                candidates = punch.Candidates();
                // Open our NAT towards the client before QUIC owns the port.
                await punch.PunchAsync(peer, 4, _cts.Token);
                port = punch.Port;
            }
            listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                ApplicationProtocols = [P2p.Alpn],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(P2p.ServerOptions(_cert)),
            }, _cts.Token);

            await _signal.PublishAsync(_topic, P2p.Seal(_key, _topic, new P2pMessage
            {
                T = "answer",
                Sid = offer.Sid,
                C = candidates,
                Fp = _fingerprint,
            }), _cts.Token);
            Log.Write($"P2P offer {offer.Sid[..8]}: answered on port {port}, peer {string.Join(", ", peer)}");

            QuicConnection conn;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                t.CancelAfter(25_000);
                conn = await listener.AcceptConnectionAsync(t.Token);
            }
            await listener.DisposeAsync(); // one connection per offer; the accepted one keeps the port
            listener = null;

            var remote = P2p.Normalize(conn.RemoteEndPoint).Address;
            QuicStream stream;
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                t.CancelAfter(10_000);
                stream = await conn.AcceptInboundStreamAsync(t.Token);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
            Log.Write($"P2P offer {offer.Sid[..8]}: QUIC connection from {conn.RemoteEndPoint}");
            _oneAtATime.Release(); // the session itself must not block further offers (a reconnect replaces it)
            released = true;
            await _handle(conn, stream, remote);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Log.Write($"P2P offer {offer.Sid[..Math.Min(8, offer.Sid.Length)]}: client never reached us (NAT not traversable?)");
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            Log.Write($"P2P offer failed: {ex.Message}");
        }
        catch { }
        finally
        {
            if (listener is not null) await listener.DisposeAsync();
            if (!released) _oneAtATime.Release();
        }
    }

    public void Dispose() => _cts.Cancel();
}
