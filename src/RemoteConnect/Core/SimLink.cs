using System.Threading.Channels;

namespace RemoteConnect.Core;

/// <summary>
/// Testing aid: emulates a slow link under the encrypted stream, host → client direction only.
/// REMOTECONNECT_SIMLINK="mbps,delayMs" (for example "20,25"). Writes complete as soon as they fit in the
/// send buffer (the socket's SO_SNDBUF, like the real kernel), and drain at the link rate after a fixed
/// one-way delay, so the queueing behaviour of a bottleneck shows up on loopback.
/// </summary>
public sealed class SimLinkStream : Stream
{
    public const string Variable = "REMOTECONNECT_SIMLINK";

    private readonly Stream _inner;
    private readonly double _bytesPerMs;
    private readonly int _delayMs;
    private readonly int _capacity;
    private readonly Channel<(byte[] Data, long Due)> _queue = Channel.CreateUnbounded<(byte[], long)>();
    private readonly SemaphoreSlim _space = new(0);
    private readonly CancellationTokenSource _cts = new();
    private int _queued;

    public static (double Mbps, int DelayMs)? Config()
    {
        var v = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(v)) return null;
        var p = v.Split(',');
        return (double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p.Length > 1 ? int.Parse(p[1]) : 0);
    }

    public SimLinkStream(Stream inner, double mbps, int delayMs, int bufferBytes)
    {
        _inner = inner;
        _bytesPerMs = mbps * 1e6 / 8 / 1000;
        _delayMs = delayMs;
        _capacity = Math.Max(64 * 1024, bufferBytes);
        _ = Task.Run(DrainAsync);
        Log.Write($"SimLink: {mbps} Mbps, {delayMs} ms one-way, send buffer {_capacity / 1024} KB");
    }

    private readonly Channel<(byte[] Data, double DeliverAt)> _inFlight = Channel.CreateUnbounded<(byte[], double)>();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Stage 1: serialise onto the link at its rate; the bytes leave the send buffer as they go.</summary>
    private async Task DrainAsync()
    {
        _ = Task.Run(DeliverAsync);
        double linkFreeAt = 0;
        try
        {
            await foreach (var (data, _) in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                double start = Math.Max(linkFreeAt, _clock.Elapsed.TotalMilliseconds);
                linkFreeAt = start + data.Length / _bytesPerMs;
                double wait = linkFreeAt - _clock.Elapsed.TotalMilliseconds;
                if (wait > 2) await Task.Delay(TimeSpan.FromMilliseconds(wait), _cts.Token);
                Interlocked.Add(ref _queued, -data.Length);
                _space.Release();
                _inFlight.Writer.TryWrite((data, linkFreeAt + _delayMs));
            }
        }
        catch { }
    }

    /// <summary>Stage 2: propagation delay, pipelined (many packets are on the wire at once).</summary>
    private async Task DeliverAsync()
    {
        try
        {
            await foreach (var (data, deliverAt) in _inFlight.Reader.ReadAllAsync(_cts.Token))
            {
                double wait = deliverAt - _clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), _cts.Token);
                await _inner.WriteAsync(data, _cts.Token);
                if (_inFlight.Reader.Count == 0) await _inner.FlushAsync(_cts.Token);
            }
        }
        catch { }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (Volatile.Read(ref _queued) + buffer.Length > _capacity && Volatile.Read(ref _queued) > 0)
            await _space.WaitAsync(ct);
        Interlocked.Add(ref _queued, buffer.Length);
        _queue.Writer.TryWrite((buffer.ToArray(), 0));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts.Cancel(); _inner.Dispose(); }
        base.Dispose(disposing);
    }
}
