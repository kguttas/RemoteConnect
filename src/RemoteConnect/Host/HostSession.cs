using System.Buffers.Binary;
using System.Text;
using System.Windows.Threading;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>One authenticated viewer: streams the screen, cursor, audio and clipboard; applies its input.</summary>
public sealed class HostSession
{
    private const int MinKbps = 1500;
    /// <summary>Over the Internet, start low and let the rate controller climb (a burst at 30 Mb/s would queue for seconds).</summary>
    private const int InternetStartKbps = 8000;

    private readonly Connection _conn;
    private readonly HelloInfo _hello;
    private readonly HostSettings _settings;
    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly InputInjector _input = new();
    private readonly FileReceiver _files;
    private readonly FileServer _fileServer;
    private readonly RateController _rate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _optLock = new();
    private StreamOptions _opts;
    private volatile bool _reconfigure;
    private volatile bool _bitrateChanged;
    private volatile bool _forceKey = true;
    private volatile MonitorInfo? _monitor;
    private uint _seq;
    private int _targetKbps;
    private AudioCapture? _audio;
    private OpusEncoder? _opus;
    private ClipboardBridge? _clipboard;
    private Thread? _captureThread;

    public string ClientName { get; }
    public string RemoteAddress { get; }
    public bool ViaInternet { get; }
    public DateTime Since { get; } = DateTime.Now;
    public string? EncoderName { get; private set; }
    public event Action? StatusChanged;

    public HostSession(Connection conn, HelloInfo hello, string remoteAddress, bool viaInternet, HostSettings settings, Dispatcher ui)
    {
        _conn = conn;
        _hello = hello;
        _settings = settings;
        _ui = ui;
        _opts = Sanitize(hello.Options);
        ClientName = string.IsNullOrWhiteSpace(hello.ClientName) ? "?" : hello.ClientName;
        RemoteAddress = remoteAddress;
        ViaInternet = viaInternet;
        _files = new FileReceiver(settings.ReceiveFolder, ack => _conn.SendJson(Msg.FileAck, ack));
        _fileServer = new FileServer(_conn);
    }

    private static StreamOptions Sanitize(StreamOptions o) => new()
    {
        MonitorIndex = Math.Max(0, o.MonitorIndex),
        MaxFps = Math.Clamp(o.MaxFps, 5, 240),
        BitrateMbps = Math.Clamp(o.BitrateMbps, 1, 500),
        AdaptiveBitrate = o.AdaptiveBitrate,
        Chroma444 = o.Chroma444,
        Audio = o.Audio,
        Codec = o.Codec is "h264" or "hevc" or "av1" ? o.Codec : "auto",
    };

    public void Stop() => _conn.Close();

    public async Task RunAsync(WelcomeInfo welcome)
    {
        welcome.Monitors = ScreenCapture.EnumerateMonitors();
        _conn.SendJson(Msg.Welcome, welcome);

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Capture", Priority = ThreadPriority.AboveNormal };
        _captureThread.Start();
        UpdateAudio();
        if (_settings.AllowClipboard)
        {
            await _ui.InvokeAsync(() =>
            {
                _clipboard = new ClipboardBridge();
                _clipboard.LocalChanged += (kind, data) =>
                    _conn.Send(Msg.Clipboard, new PacketWriter().U8((byte)kind).Bytes(data), data.Length > 256 * 1024 ? Lane.Bulk : Lane.Control);
            });
        }
        _ = Task.Run(Watchdog);

        try
        {
            while (true)
            {
                var p = await _conn.ReceiveAsync(_cts.Token);
                try { Handle(p); }
                finally { p.Return(); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("session receive", ex);
        }
        finally
        {
            _cts.Cancel();
            _conn.Close();
            _captureThread.Join(3000);
            StopAudio();
            _input.Dispose();
            _files.Dispose();
            _fileServer.Dispose();
            if (_clipboard is not null) await _ui.InvokeAsync(() => _clipboard.Dispose());
        }
    }

    private async Task Watchdog()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(2000, _cts.Token); } catch { return; }
            if (Environment.TickCount64 - _conn.LastReceiveTicks > 15000)
            {
                Log.Write($"Session {ClientName}: client timed out");
                _conn.Close();
                return;
            }
        }
    }

    private void Handle(in Packet p)
    {
        var r = new PacketReader(p.Span);
        switch (p.Type)
        {
            case Msg.MouseMove:
                if (_monitor is { } m) _input.MoveTo(m, r.F32(), r.F32());
                break;
            case Msg.MouseButton:
                _input.Button((MouseButtonId)r.U8(), r.U8() != 0);
                break;
            case Msg.MouseWheel:
                _input.Wheel(r.I16(), r.U8() != 0);
                break;
            case Msg.Key:
                _input.Key(r.U16(), r.U16(), (KeyFlags)r.U8());
                break;
            case Msg.FrameAck:
                _rate.OnAck(r.U32(), _clock.Elapsed.TotalMilliseconds);
                break;
            case Msg.Special:
                switch ((SpecialAction)r.U8())
                {
                    case SpecialAction.ReleaseAllKeys: _input.ReleaseAll(); break;
                    case SpecialAction.TaskManager: _input.Chord(0x11, 0x10, 0x1B); break; // Ctrl+Shift+Esc
                    case SpecialAction.WinKey: _input.Chord(0x5B); break;
                    case SpecialAction.CtrlAltDel: SendCtrlAltDel(); break;
                }
                break;
            case Msg.Ping:
                _conn.Send(Msg.Pong, p.Span);
                break;
            case Msg.RequestKeyframe:
                _forceKey = true;
                break;
            case Msg.Options:
                SetOptions(ProtocolInfo.FromJson<StreamOptions>(p.Span));
                break;
            case Msg.Clipboard:
                if (_settings.AllowClipboard && _clipboard is not null && p.Length > 1)
                {
                    var kind = (ClipboardKind)p.Span[0];
                    var data = p.Span[1..].ToArray();
                    _ui.InvokeAsync(() => _clipboard.ApplyRemote(kind, data));
                }
                break;
            case Msg.FileBegin:
                _files.Begin(ProtocolInfo.FromJson<FileHeader>(p.Span));
                break;
            case Msg.FileChunk:
                _files.Chunk(r.I32(), r.Rest());
                break;
            case Msg.FileEnd:
                _files.End(r.I32());
                break;
            case Msg.DirList:
                if (_settings.AllowFileDownload) _fileServer.List(ProtocolInfo.FromJson<DirRequest>(p.Span).Path);
                else _conn.SendJson(Msg.DirListing, new DirListingInfo { Error = "El equipo remoto no permite descargar archivos." });
                break;
            case Msg.FileGet:
                if (_settings.AllowFileDownload) _fileServer.Get(ProtocolInfo.FromJson<FileGetRequest>(p.Span));
                break;
            case Msg.FileCancel:
                _fileServer.Cancel();
                break;
        }
    }

    private void SetOptions(StreamOptions o)
    {
        o = Sanitize(o);
        lock (_optLock)
        {
            var old = _opts;
            _opts = o;
            if (old.MonitorIndex != o.MonitorIndex || old.MaxFps != o.MaxFps || old.Chroma444 != o.Chroma444 || old.Codec != o.Codec)
                _reconfigure = true;
            else if (old.BitrateMbps != o.BitrateMbps || old.AdaptiveBitrate != o.AdaptiveBitrate)
                _bitrateChanged = true;
        }
        UpdateAudio();
    }

    private void UpdateAudio()
    {
        bool want;
        lock (_optLock) want = _opts.Audio && _settings.AllowAudio;
        if (want && _audio is null && !_cts.IsCancellationRequested)
        {
            try
            {
                _opus ??= OpusEncoder.TryCreate();
                var opus = _opus;
                _audio = new AudioCapture((rate, ch, pcm) =>
                {
                    Span<byte> head = stackalloc byte[6];
                    if (opus is not null)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(head, OpusEncoder.Rate);
                        head[4] = OpusEncoder.Channels;
                        head[5] = (byte)AudioCodec.Opus;
                        var h = head.ToArray();
                        opus.Encode(rate, ch, pcm, (data, _) => _conn.Send(Msg.Audio, h, data, Lane.Audio));
                    }
                    else
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(head, rate);
                        head[4] = (byte)ch;
                        head[5] = (byte)AudioCodec.Pcm16;
                        _conn.Send(Msg.Audio, head, pcm, Lane.Audio);
                    }
                });
            }
            catch (Exception ex) { Log.Error("audio capture", ex); }
        }
        else if (!want && _audio is not null)
        {
            StopAudio();
        }
    }

    private void StopAudio()
    {
        _audio?.Dispose(); // stops the capture thread before the encoder goes away
        _audio = null;
        _opus?.Dispose();
        _opus = null;
    }

    private void SendCursor(PointerShape s)
    {
        var w = new PacketWriter()
            .U8(s.Visible ? s.Kind : (byte)0)
            .I16((short)s.HotX).I16((short)s.HotY).I16((short)s.Width).I16((short)s.Height);
        if (s.Visible) w.Bytes(s.Data);
        _conn.Send(Msg.CursorShape, w);
    }

    /// <summary>Ctrl+Alt+Del cannot be injected; only a service can generate it (SendSAS), so ask ours.</summary>
    private void SendCtrlAltDel()
    {
        try
        {
            using var sas = EventWaitHandle.OpenExisting(AgentService.SasEventName);
            sas.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            _conn.SendText(Msg.Notice, "Ctrl+Alt+Supr necesita RemoteConnect instalado como servicio en el host.");
        }
        catch (Exception ex) { Log.Error("ctrl+alt+del", ex); }
    }

    /// <summary>
    /// Codecs to try, best first. Explicit choice if the client can decode it; otherwise AV1 / HEVC only when
    /// the client decodes them on its GPU (they need a hardware encoder here too, or the encoder falls back).
    /// 4:4:4 stays on H.264, the path with a fast software decoder.
    /// </summary>
    private List<string> CodecOrder(StreamOptions o)
    {
        bool canDecode(string c) => _hello.Decoders.Count == 0 ? c == "h264" : _hello.Decoders.Contains(c);
        var order = new List<string>();
        if (o.Codec != "auto")
        {
            if (canDecode(o.Codec)) order.Add(o.Codec);
        }
        else if (!o.Chroma444 && ViaInternet)
        {
            // Over the Internet bandwidth is what limits, and AV1 / HEVC need fewer bits for the same picture
            // (measured on a 10 Mbps / 40 ms link: AV1 58 fps, 98 ms vs H.264 53 fps, 117 ms). On the LAN H.264
            // wins: its GPU decode is the fastest (0.3 ms vs 3.5 ms for AV1 on an Intel iGPU).
            if (_hello.HardwareDecoders.Contains("av1")) order.Add("av1");
            if (_hello.HardwareDecoders.Contains("hevc")) order.Add("hevc");
        }
        if (!order.Contains("h264")) order.Add("h264");
        return order;
    }

    private int StartKbps(StreamOptions o)
    {
        int max = o.BitrateMbps * 1000;
        if (!o.AdaptiveBitrate) return max;
        if (_targetKbps > 0) return Math.Min(max, _targetKbps); // reopened encoder: keep what the link carries
        return ViaInternet ? Math.Min(max, InternetStartKbps) : max;
    }

    private unsafe void CaptureLoop()
    {
        // Desktop Duplication only sees the desktop this thread is on: follow lock screen / sign-in / UAC.
        using var desktop = new SessionInterop.InputDesktopFollower();
        double lastDesktopCheck = 0;
        Native.timeBeginPeriod(1);
        if (_settings.KeepAwake) Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
        ScreenCapture? cap = null;
        VideoEncoder? enc = null;
        var clock = _clock;
        double nextDue = 0, lastEncode = double.MinValue, lastReopen = double.MinValue, lastRateChange = double.MinValue, lastStats = 0;
        int refinesLeft = 0, failures = 0;
        int statFrames = 0, secFrames = 0;
        double statCopy = 0, statEncode = 0, statBackpressure = 0, statStart = 0, statAcquireWait = 0, secEncode = 0;
        int statTimeouts = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                StreamOptions o;
                lock (_optLock) o = _opts;
                if (_reconfigure)
                {
                    _reconfigure = false;
                    enc?.Dispose(); enc = null;
                    cap?.Dispose(); cap = null;
                }

                if (SessionInterop.IsSystem && clock.Elapsed.TotalMilliseconds - lastDesktopCheck > 250)
                {
                    lastDesktopCheck = clock.Elapsed.TotalMilliseconds;
                    if (cap is not null && desktop.Changed())
                    {
                        enc?.Dispose(); enc = null;
                        cap.Dispose(); cap = null;
                    }
                }

                if (cap is null)
                {
                    try
                    {
                        desktop.Sync();
                        cap = new ScreenCapture(o.MonitorIndex);
                    }
                    catch (Exception ex)
                    {
                        // Typical while the lock screen / UAC secure desktop is showing.
                        if (failures++ % 40 == 0)
                        {
                            Log.Write($"Capture unavailable: {ex.Message}");
                            _conn.SendText(Msg.Notice, SessionInterop.IsSystem
                                ? "La pantalla del host no está disponible. Reintentando…"
                                : "La pantalla del host no está disponible (¿bloqueada o aviso UAC?). Instala RemoteConnect como servicio para controlar la pantalla de inicio.");
                        }
                        Thread.Sleep(250);
                        continue;
                    }
                    failures = 0;
                }

                if (enc is null)
                {
                    try
                    {
                        enc = new VideoEncoder(CodecOrder(o), cap.Width, cap.Height, o.MaxFps, StartKbps(o), o.Chroma444, _settings.Encoder, cap.DevicePointer);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("encoder", ex);
                        _conn.SendText(Msg.Notice, "No se pudo iniciar el codificador de vídeo: " + ex.Message);
                        cap.Dispose(); cap = null;
                        Thread.Sleep(2000);
                        continue;
                    }
                    _bitrateChanged = false;
                    _targetKbps = enc.BitrateKbps;
                    lastReopen = clock.Elapsed.TotalMilliseconds;
                    cap.GpuMode = enc.GpuInput; // same device the encoder now references
                    _monitor = cap.Monitor;
                    EncoderName = enc.Name + (enc.Is444 ? " 4:4:4" : "") + (enc.GpuInput ? " (GPU)" : "");
                    StatusChanged?.Invoke();
                    _rate.Reset();
                    // On the Media lane, ahead of the keyframe it announces (lanes may use different QUIC streams).
                    _conn.SendJson(Msg.VideoFormat, new VideoFormatInfo
                    {
                        Width = enc.Width, Height = enc.Height, MonitorIndex = cap.Monitor.Index,
                        Encoder = enc.Name, Codec = enc.Codec, Chroma444 = enc.Is444, GpuCapture = enc.GpuInput,
                    }, Lane.Media);
                    _forceKey = true;
                    nextDue = 0;
                }

                double interval = 1000.0 / o.MaxFps;
                double now = clock.Elapsed.TotalMilliseconds;

                // Adaptive bitrate (and user changes of the ceiling).
                int maxKbps = o.BitrateMbps * 1000;
                if (_bitrateChanged)
                {
                    _bitrateChanged = false;
                    _targetKbps = o.AdaptiveBitrate ? Math.Min(maxKbps, Math.Max(MinKbps, _targetKbps)) : maxKbps;
                }
                else if (o.AdaptiveBitrate && _rate.Update(now, _targetKbps, maxKbps, MinKbps) is int proposed)
                {
                    _targetKbps = proposed;
                }
                // The target moves freely; the encoder follows it, sparingly where a change costs a keyframe
                // (NVENC resets to an IDR on every rate change; other encoders must be reopened).
                if (_targetKbps != enc.BitrateKbps)
                {
                    double change = Math.Abs(_targetKbps - enc.BitrateKbps) / (double)enc.BitrateKbps;
                    bool down = _targetKbps < enc.BitrateKbps;
                    bool userSet = !o.AdaptiveBitrate;
                    if (enc.DynamicBitrate && !enc.RateChangeCostsKeyframe) enc.SetBitrate(_targetKbps);
                    else if (enc.DynamicBitrate)
                    {
                        if (userSet || (down && change >= 0.10) || (change >= 0.20 && now - lastRateChange > 2000))
                        {
                            Log.Write($"Bitrate {enc.BitrateKbps} → {_targetKbps} kbps (queue {_rate.QueueMs:0} ms, delivered {_rate.DeliveryKbps:0}, link {_rate.LinkKbps:0}, sent {_rate.SendKbps:0}, held back {_rate.Blocked:P0}, base RTT {_rate.BaseRttMs:0} ms)");
                            enc.SetBitrate(_targetKbps);
                            lastRateChange = now;
                        }
                    }
                    else if (userSet || (change > 0.25 && now - lastReopen > 4000) || (down && change > 0.40 && now - lastReopen > 1000))
                    {
                        enc.Dispose(); enc = null;
                        continue;
                    }
                }

                if (now - lastStats >= 1000)
                {
                    _conn.Send(Msg.StreamStats, new PacketWriter().I32(enc.BitrateKbps)
                        .F32((float)(secFrames > 0 ? secEncode / secFrames : 0)).F32((float)_rate.QueueMs)
                        .F32((float)(double.IsNaN(_rate.BaseRttMs) ? -1 : _rate.BaseRttMs)));
                    secFrames = 0; secEncode = 0; lastStats = now;
                }

                if (now < nextDue)
                {
                    double wait = nextDue - now;
                    if (wait >= 2) Thread.Sleep((int)wait - 1); else Thread.Yield();
                    continue;
                }
                // Backpressure on what the client confirmed, not on what the socket swallowed: the kernel / QUIC
                // buffers would otherwise hide seconds of queued video (latency) on a slow link.
                if (!_rate.CanSend(now, interval))
                {
                    Thread.Sleep(1);
                    statBackpressure += 1;
                    continue;
                }

                double acquireStart = clock.Elapsed.TotalMilliseconds;
                var result = cap.Acquire(50, out var pointer);
                statAcquireWait += clock.Elapsed.TotalMilliseconds - acquireStart;
                if (result == CaptureResult.Timeout) statTimeouts++;
                if (pointer is not null) SendCursor(pointer);
                if (result == CaptureResult.Lost)
                {
                    enc.Dispose(); enc = null;
                    cap.Dispose(); cap = null;
                    Thread.Sleep(30);
                    continue;
                }

                now = clock.Elapsed.TotalMilliseconds;
                bool encode;
                if (result == CaptureResult.Frame) { encode = true; refinesLeft = 4; }
                else encode = cap.HasImage && (_forceKey || (refinesLeft > 0 && now - lastEncode > 120));
                if (!encode) continue;
                if (result != CaptureResult.Frame && !_forceKey) refinesLeft--;

                bool key = _forceKey;
                _forceKey = false;
                long stamp = Stopwatch.GetTimestamp();
                PacketSink sink = (data, isKey) =>
                {
                    uint seq = ++_seq;
                    Span<byte> head = stackalloc byte[13];
                    BinaryPrimitives.WriteUInt32LittleEndian(head, seq);
                    BinaryPrimitives.WriteInt64LittleEndian(head[4..], stamp);
                    head[12] = (byte)(isKey ? 1 : 0);
                    _rate.OnSent(seq, clock.Elapsed.TotalMilliseconds, data.Length + 18, isKey);
                    _conn.Send(Msg.VideoFrame, head, data, Lane.Media);
                };
                var e = enc;
                var c = cap;
                if (enc.GpuInput) enc.EncodeGpu((tex, slice) => c.CopyTo(tex, slice, e.Width, e.Height), key, sink);
                else enc.Encode(cap.Buffer, cap.Stride, key, sink);
                double encodeEnd = clock.Elapsed.TotalMilliseconds;
                if (result == CaptureResult.Frame) statCopy += cap.LastCopyMs;
                statEncode += encodeEnd - now;
                secEncode += encodeEnd - now;
                statFrames++;
                secFrames++;
                if (encodeEnd - statStart > 30000)
                {
                    Log.Write($"Stream {enc.Name}{(enc.GpuInput ? " (GPU)" : "")} {enc.Width}x{enc.Height}: {statFrames * 1000 / (encodeEnd - statStart):0.0} fps, " +
                              $"copy {statCopy / Math.Max(1, statFrames):0.00} ms, encode {statEncode / Math.Max(1, statFrames):0.00} ms, backpressure {statBackpressure:0} ms, acquire {statAcquireWait:0} ms, timeouts {statTimeouts}, " +
                              $"bitrate {enc.BitrateKbps / 1000.0:0.0} Mbps, base RTT {_rate.BaseRttMs:0.0} ms, queue {_rate.QueueMs:0.0} ms, delivered {_rate.DeliveryKbps / 1000:0.0} Mbps");
                    statFrames = 0; statTimeouts = 0; statCopy = statEncode = statBackpressure = statAcquireWait = 0; statStart = encodeEnd;
                }
                lastEncode = now;
                // Fixed grid instead of "now + interval": on a 144/165 Hz desktop the latter drifts onto every
                // third vsync and caps 60 fps at ~55. After an idle gap, restart the grid.
                nextDue += interval;
                if (nextDue < now - interval) nextDue = now + interval;
            }
        }
        catch (Exception ex)
        {
            Log.Error("capture loop", ex);
            _conn.SendText(Msg.Notice, "Error en la captura: " + ex.Message);
            _conn.Close(ex);
        }
        finally
        {
            enc?.Dispose();
            cap?.Dispose();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            Native.timeEndPeriod(1);
        }
    }
}

