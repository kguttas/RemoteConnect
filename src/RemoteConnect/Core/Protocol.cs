using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RemoteConnect.Core;

/// <summary>
/// Wire protocol. Every message is framed as [uint32 LE length][byte type][payload],
/// where length counts the type byte plus the payload. Runs inside TLS over TCP, or over QUIC streams.
/// </summary>
public enum Msg : byte
{
    // client -> host
    Hello = 1,
    MouseMove = 2,       // float x, float y   (normalized 0..1 inside the streamed monitor)
    MouseButton = 3,     // byte button, byte down
    MouseWheel = 4,      // int16 delta, byte horizontal
    Key = 5,             // uint16 vk, uint16 scan, byte flags (KeyFlags)
    Options = 10,        // json StreamOptions
    RequestKeyframe = 11,
    Ping = 12,           // int64 timestamp (echoed back as Pong)
    Special = 13,        // byte SpecialAction
    FrameAck = 14,       // uint32 seq of a VideoFrame that arrived (the host paces and adapts its bitrate on these)

    // both directions
    Clipboard = 6,       // byte kind (1 text utf8, 2 png), data
    FileBegin = 7,       // json FileHeader
    FileChunk = 8,       // int32 id, data
    FileEnd = 9,         // int32 id

    // host -> client
    Welcome = 101,       // json WelcomeInfo
    AuthFailed = 102,    // utf8 reason
    VideoFormat = 103,   // json VideoFormatInfo (sent on the Media lane, so it stays ordered with the frames)
    VideoFrame = 104,    // uint32 seq, int64 capture timestamp (Stopwatch ticks), byte flags (1 = keyframe), bitstream
    CursorShape = 105,   // byte kind (0 hidden, 1 mono mask, 2 bgra), int16 hotX, hotY, w, h, data
    Audio = 108,         // int32 sampleRate, byte channels, byte AudioCodec, data
    Pong = 109,
    FileAck = 110,       // json FileAckInfo
    Notice = 111,        // utf8 text
    StreamStats = 112,   // int32 bitrate kbps, float encode ms, float queue ms, float base RTT ms
}

[Flags]
public enum KeyFlags : byte { Down = 1, Extended = 2 }

public enum MouseButtonId : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

public enum SpecialAction : byte { ReleaseAllKeys = 1, TaskManager = 2, WinKey = 3, CtrlAltDel = 4 }

public enum ClipboardKind : byte { Text = 1, Png = 2 }

public enum AudioCodec : byte { Pcm16 = 0, Opus = 1 }

/// <summary>
/// Send priority, highest first. Over TCP the lanes share one stream and the send loop always drains the
/// higher ones first; over QUIC each lane has its own stream, so a lost packet in a keyframe never holds
/// back input or audio (no head-of-line blocking between lanes).
/// </summary>
public enum Lane { Control = 0, Audio = 1, Media = 2, Bulk = 3 }

public static class ProtocolInfo
{
    public const int Version = 2;
    public const int DefaultPort = 47800;
    public const int DiscoveryPort = 47801;   // hosts listen here for queries
    public const int AnnouncePort = 47802;    // clients listen here for beacons
    public const int MaxMessage = 64 * 1024 * 1024;
    public const int LaneCount = 4;

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static byte[] ToJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T FromJson<T>(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<T>(data, Json)!;
}

public sealed class StreamOptions
{
    public int MonitorIndex { get; set; }
    public int MaxFps { get; set; } = 60;
    /// <summary>Bitrate, or its ceiling when <see cref="AdaptiveBitrate"/> is on.</summary>
    public int BitrateMbps { get; set; } = 30;
    /// <summary>Let the host lower / raise the bitrate to what the link actually carries.</summary>
    public bool AdaptiveBitrate { get; set; } = true;
    public bool Chroma444 { get; set; }
    public bool Audio { get; set; } = true;
    /// <summary>auto, h264, hevc, av1</summary>
    public string Codec { get; set; } = "auto";
    public StreamOptions Clone() => (StreamOptions)MemberwiseClone();
}

public sealed class HelloInfo
{
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    public string Password { get; set; } = "";
    public string ClientName { get; set; } = Environment.MachineName;
    public StreamOptions Options { get; set; } = new();
    /// <summary>Codecs the client can decode at all (software or hardware).</summary>
    public List<string> Decoders { get; set; } = new();
    /// <summary>Codecs the client decodes on its GPU (the host prefers the most efficient of these).</summary>
    public List<string> HardwareDecoders { get; set; } = new();
    /// <summary>QUIC: extra streams the client opens right after Hello, one per lane (first byte = lane).</summary>
    public int ExtraStreams { get; set; }
}

public sealed class MonitorInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Primary { get; set; }
    public override string ToString() => $"{Index + 1}: {Width}×{Height}{(Primary ? " (principal)" : "")}";
}

public sealed class WelcomeInfo
{
    public string HostId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Machine { get; set; } = "";
    public List<MonitorInfo> Monitors { get; set; } = new();
    public bool Elevated { get; set; }
    public string? InternetId { get; set; }
}

public sealed class VideoFormatInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int MonitorIndex { get; set; }
    public string Encoder { get; set; } = "";
    /// <summary>h264, hevc or av1</summary>
    public string Codec { get; set; } = "h264";
    public bool Chroma444 { get; set; }
    /// <summary>The host encodes straight from the captured GPU texture.</summary>
    public bool GpuCapture { get; set; }
}

public sealed class FileHeader
{
    public int Id { get; set; }
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
}

public sealed class FileAckInfo
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
}

/// <summary>Little-endian payload builder.</summary>
public sealed class PacketWriter
{
    private byte[] _buf = new byte[64];
    private int _len;

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);

    private Span<byte> Take(int n)
    {
        if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public PacketWriter U8(byte v) { Take(1)[0] = v; return this; }
    public PacketWriter I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(Take(2), v); return this; }
    public PacketWriter U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v); return this; }
    public PacketWriter I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Take(4), v); return this; }
    public PacketWriter U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v); return this; }
    public PacketWriter I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(Take(8), v); return this; }
    public PacketWriter F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(Take(4), v); return this; }
    public PacketWriter Bytes(ReadOnlySpan<byte> v) { v.CopyTo(Take(v.Length)); return this; }
}

/// <summary>Little-endian payload reader.</summary>
public ref struct PacketReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _d = data;
    private int _p;

    public byte U8() => _d[_p++];
    public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_d[_p..]); _p += 2; return v; }
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d[_p..]); _p += 2; return v; }
    public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d[_p..]); _p += 4; return v; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]); _p += 4; return v; }
    public long I64() { var v = BinaryPrimitives.ReadInt64LittleEndian(_d[_p..]); _p += 8; return v; }
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_d[_p..]); _p += 4; return v; }
    public ReadOnlySpan<byte> Rest() { var r = _d[_p..]; _p = _d.Length; return r; }
}

/// <summary>
/// One received message. Large payloads live in a pooled buffer: whoever consumes the packet calls
/// <see cref="Return"/> when done (or hands that duty on, as the video path does).
/// </summary>
public readonly struct Packet(Msg type, byte[] buffer, int length, bool pooled)
{
    public Msg Type { get; } = type;
    public byte[] Buffer { get; } = buffer;
    public int Length { get; } = length;
    public bool Pooled { get; } = pooled;
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
    public string Text => Encoding.UTF8.GetString(Span);
    public void Return() { if (Pooled) BufferPool.Return(Buffer); }
}

/// <summary>Buffers for video frames and other large messages: no LOH allocation (and Gen2 GC) per keyframe.</summary>
public static class BufferPool
{
    public const int Threshold = 2048;
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(32 * 1024 * 1024, 64);
    public static byte[] Rent(int n) => Pool.Rent(n);
    public static void Return(byte[] b) => Pool.Return(b);
}

/// <summary>
/// A framed, prioritised, full-duplex message connection over one stream (TLS) or several (QUIC, one per
/// lane, attached after authentication). Each stream has a send loop that drains its lanes in priority
/// order and a receive loop that feeds a single incoming queue.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    private readonly record struct Item(byte[] Buf, int Len, bool Pooled, Action? OnSent);

    private sealed class Pipe(Stream stream)
    {
        public readonly Stream Stream = stream;
        public readonly SemaphoreSlim Signal = new(0);
        public Task? SendLoop;
    }

    private readonly ConcurrentQueue<Item>[] _lanes = new ConcurrentQueue<Item>[ProtocolInfo.LaneCount];
    private readonly Pipe[] _laneTo = new Pipe[ProtocolInfo.LaneCount];
    private readonly List<Pipe> _pipes = new();
    private readonly Channel<Packet> _incoming = Channel.CreateBounded<Packet>(new BoundedChannelOptions(512)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _cts = new();
    private int _bulkPending;
    private int _closed;
    private volatile int _maxIncoming;

    public long BytesReceived;
    public long BytesSent;
    public long LastReceiveTicks = Environment.TickCount64;

    /// <summary>Raised once when the connection fails or is closed.</summary>
    public event Action<Exception?>? Closed;

    /// <param name="maxIncoming">Largest accepted message; keep it small until the peer is authenticated.</param>
    public Connection(Stream stream, int maxIncoming = ProtocolInfo.MaxMessage)
    {
        _maxIncoming = maxIncoming;
        for (int i = 0; i < _lanes.Length; i++) _lanes[i] = new();
        var pipe = new Pipe(stream);
        for (int i = 0; i < _laneTo.Length; i++) _laneTo[i] = pipe;
        Start(pipe);
    }

    public bool IsClosed => _closed != 0;
    public int BulkPending => Volatile.Read(ref _bulkPending);
    public int MaxIncoming { get => _maxIncoming; set => _maxIncoming = value; }
    /// <summary>Number of underlying streams (1 over TCP; up to 4 over QUIC).</summary>
    public int StreamCount { get { lock (_pipes) return _pipes.Count; } }

    private void Start(Pipe pipe)
    {
        lock (_pipes) _pipes.Add(pipe);
        pipe.SendLoop = Task.Run(() => SendLoopAsync(pipe));
        _ = Task.Run(() => ReceiveLoopAsync(pipe));
    }

    /// <summary>Moves <paramref name="lane"/> onto its own stream (QUIC). Both directions use it from now on.</summary>
    public void Attach(Lane lane, Stream stream)
    {
        if (IsClosed) { stream.Dispose(); return; }
        var pipe = new Pipe(stream);
        _laneTo[(int)lane] = pipe;
        Start(pipe);
        int queued = _lanes[(int)lane].Count;
        if (queued > 0) pipe.Signal.Release(queued);
    }

    public void Send(Msg type, ReadOnlySpan<byte> payload, Lane lane = Lane.Control, Action? onSent = null) =>
        Send(type, payload, ReadOnlySpan<byte>.Empty, lane, onSent);

    /// <summary>Sends head + body as one message, copied once into a (pooled) wire buffer.</summary>
    public void Send(Msg type, ReadOnlySpan<byte> head, ReadOnlySpan<byte> body, Lane lane = Lane.Control, Action? onSent = null)
    {
        if (IsClosed) return;
        int n = 5 + head.Length + body.Length;
        bool pooled = n >= BufferPool.Threshold;
        var buf = pooled ? BufferPool.Rent(n) : new byte[n];
        BinaryPrimitives.WriteInt32LittleEndian(buf, n - 4);
        buf[4] = (byte)type;
        head.CopyTo(buf.AsSpan(5));
        body.CopyTo(buf.AsSpan(5 + head.Length));
        if (lane == Lane.Bulk) Interlocked.Increment(ref _bulkPending);
        _lanes[(int)lane].Enqueue(new Item(buf, n, pooled, onSent));
        _laneTo[(int)lane].Signal.Release();
    }

    public void Send(Msg type, PacketWriter w, Lane lane = Lane.Control, Action? onSent = null) => Send(type, w.Span, lane, onSent);
    public void Send(Msg type, Lane lane = Lane.Control) => Send(type, ReadOnlySpan<byte>.Empty, lane);
    public void SendJson<T>(Msg type, T value, Lane lane = Lane.Control) => Send(type, ProtocolInfo.ToJson(value), lane);
    public void SendText(Msg type, string text) => Send(type, Encoding.UTF8.GetBytes(text));

    private async Task SendLoopAsync(Pipe pipe)
    {
        try
        {
            while (true)
            {
                await pipe.Signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                Item item = default;
                int lane = -1;
                for (int i = 0; i < _lanes.Length; i++)
                    if (_laneTo[i] == pipe && _lanes[i].TryDequeue(out item)) { lane = i; break; }
                if (lane < 0) continue; // the lane moved to another stream; that stream got the signal
                try
                {
                    await pipe.Stream.WriteAsync(item.Buf.AsMemory(0, item.Len), _cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (item.Pooled) BufferPool.Return(item.Buf);
                }
                Interlocked.Add(ref BytesSent, item.Len);
                if (lane == (int)Lane.Bulk) Interlocked.Decrement(ref _bulkPending);
                item.OnSent?.Invoke();
                // Coalesce: flush only when nothing else is waiting.
                if (pipe.Signal.CurrentCount == 0) await pipe.Stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Close(ex is OperationCanceledException ? null : ex);
        }
    }

    private async Task ReceiveLoopAsync(Pipe pipe)
    {
        var header = new byte[5];
        try
        {
            while (true)
            {
                await pipe.Stream.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                int len = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (len < 1 || len > _maxIncoming) throw new InvalidDataException($"Bad message length {len}");
                int n = len - 1;
                bool pooled = n >= BufferPool.Threshold;
                var data = pooled ? BufferPool.Rent(n) : n == 0 ? [] : new byte[n];
                if (n > 0) await pipe.Stream.ReadExactlyAsync(data.AsMemory(0, n), _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref BytesReceived, len + 4);
                LastReceiveTicks = Environment.TickCount64;
                await _incoming.Writer.WriteAsync(new Packet((Msg)header[4], data, n, pooled), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Close(ex is OperationCanceledException ? null : ex);
        }
    }

    public async Task<Packet> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new IOException("Conexión cerrada", ex.InnerException);
        }
    }

    public void Close(Exception? reason = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _incoming.Writer.TryComplete(reason is null ? null : new IOException(reason.Message, reason));
        List<Pipe> pipes;
        lock (_pipes) pipes = _pipes.ToList();
        foreach (var p in pipes)
        {
            try { p.Stream.Dispose(); } catch { }
        }
        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        List<Pipe> pipes;
        lock (_pipes) pipes = _pipes.ToList();
        foreach (var p in pipes)
        {
            try { if (p.SendLoop is not null) await p.SendLoop.ConfigureAwait(false); } catch { }
        }
    }
}
