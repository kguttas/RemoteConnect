namespace RemoteConnect.Host;

/// <summary>
/// Delay-based congestion control for the video stream, driven by the client's per-frame acks.
///
/// The socket's "write completed" says nothing about the link: kernel / QUIC buffers accept data long before
/// it leaves. A frame only counts as delivered when the client says it arrived, so:
///   * pacing: at most ~one base-RTT worth of frames is in flight, and never a frame older than
///     <see cref="MaxQueueMs"/> → the queue (= latency) stays bounded however slow the link is;
///   * adaptive bitrate: ack delay above the base RTT means a queue is building → lower the bitrate to what
///     was actually delivered; a clean path → raise it gently, up to the user's ceiling.
/// </summary>
public sealed class RateController
{
    private readonly record struct Sent(uint Seq, double TimeMs, int Bytes);
    private readonly record struct Rate(double TimeMs, double Kbps);
    private readonly record struct Sample(double TimeMs, double DelayMs, int Bytes);

    /// <summary>Above this much queueing delay the link is congested.</summary>
    public const double OveruseMs = 30;
    /// <summary>Below this much the path is clean and the bitrate may grow.</summary>
    public const double UnderuseMs = 8;
    /// <summary>Never keep a frame waiting for its ack longer than base RTT + this.</summary>
    public const double MaxQueueMs = 120;

    private readonly Queue<Sent> _inflight = new();
    private readonly Queue<Sample> _samples = new();      // last 10 s, for the base RTT
    private readonly Queue<Sample> _recent = new();       // last ~1 s, delay trend and delivery rate
    private readonly Queue<Rate> _deliveries = new();     // last 30 s of delivery rates: link capacity estimate
    private readonly Lock _gate = new();
    private int _inflightBytes;
    private double _lastDecrease = double.MinValue, _lastIncrease = double.MinValue, _lastUpdate, _holdUntil;
    private int _denied;          // CanSend refusals since the last Update (the caller retries every ~1 ms)
    private double _blocked;      // smoothed share of time the sender was held back (~1 s horizon)
    private double _saturatedAt = double.MinValue; // last time the link was seen full
    private long _sentBytesWindow;
    private double _sentWindowStart;

    public double BaseRttMs { get; private set; } = double.NaN;
    public double QueueMs { get; private set; }
    public double DeliveryKbps { get; private set; }
    public double SendKbps { get; private set; }
    /// <summary>Highest delivery rate seen recently (at least the current target): what the link can carry.</summary>
    public double LinkKbps { get; private set; }
    /// <summary>Share of the time frames had to wait for acks: the link, not the encoder, sets the pace.</summary>
    public double Blocked => _blocked;
    public int InflightFrames { get { lock (_gate) return _inflight.Count; } }

    public void OnSent(uint seq, double nowMs, int bytes, bool key)
    {
        lock (_gate)
        {
            // A keyframe is many times a normal frame and queues briefly by itself; that is not congestion,
            // and reacting to it is a death spiral (NVENC answers every rate change with a new IDR).
            if (key && LinkKbps > 0) _holdUntil = Math.Max(_holdUntil, nowMs + bytes * 8 / LinkKbps + (double.IsNaN(BaseRttMs) ? 0 : BaseRttMs) + 100);
            _inflight.Enqueue(new Sent(seq, nowMs, bytes));
            _inflightBytes += bytes;
            _sentBytesWindow += bytes;
        }
    }

    public void OnAck(uint seq, double nowMs)
    {
        lock (_gate)
        {
            // Acks arrive in order (one stream); anything older than seq was delivered too.
            while (_inflight.Count > 0 && (int)(seq - _inflight.Peek().Seq) >= 0)
            {
                var s = _inflight.Dequeue();
                _inflightBytes -= s.Bytes;
                if (s.Seq != seq) continue;
                var sample = new Sample(nowMs, nowMs - s.TimeMs, s.Bytes);
                _samples.Enqueue(sample);
                _recent.Enqueue(sample);
            }
            while (_samples.Count > 0 && nowMs - _samples.Peek().TimeMs > 10_000) _samples.Dequeue();
            while (_recent.Count > 0 && nowMs - _recent.Peek().TimeMs > 1000) _recent.Dequeue();
        }
    }

    /// <summary>Clears everything in flight (new encoder / stream restart).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _inflight.Clear();
            _inflightBytes = 0;
        }
    }

    /// <summary>May another frame go out now? Keeps the link's queue (and so the latency) bounded.</summary>
    public bool CanSend(double nowMs, double frameIntervalMs)
    {
        lock (_gate)
        {
            if (_inflight.Count == 0) return true;
            double baseRtt = double.IsNaN(BaseRttMs) ? 0 : BaseRttMs;
            // A frame that has waited this long is sitting in a queue: sending more only adds latency.
            bool ok = nowMs - _inflight.Peek().TimeMs <= baseRtt + MaxQueueMs;
            // About one RTT worth of frames, plus one being produced (2 on a LAN, as before).
            int window = Math.Clamp((int)Math.Ceiling((baseRtt + 4) / frameIntervalMs) + 1, 2, 12);
            ok &= _inflight.Count < window;
            if (!ok) _denied++;
            return ok;
        }
    }

    /// <summary>
    /// Called a few times a second. Returns the new target bitrate in kbps, or null to keep the current one.
    /// </summary>
    public int? Update(double nowMs, int currentKbps, int maxKbps, int minKbps)
    {
        lock (_gate)
        {
            double elapsed = nowMs - _sentWindowStart;
            if (elapsed >= 1000)
            {
                SendKbps = _sentBytesWindow * 8 / elapsed;
                _sentBytesWindow = 0;
                _sentWindowStart = nowMs;
            }
            if (nowMs - _lastUpdate < 200) return null;
            double sinceUpdate = nowMs - _lastUpdate;
            _lastUpdate = nowMs;
            _blocked = 0.7 * _blocked + 0.3 * Math.Min(1, _denied / Math.Max(1, sinceUpdate));
            _denied = 0;

            if (_samples.Count == 0) return null;
            double baseRtt = double.MaxValue;
            foreach (var s in _samples) baseRtt = Math.Min(baseRtt, s.DelayMs);
            BaseRttMs = baseRtt;

            // Delivery rate: what actually arrived over the last second; its recent peak estimates the link.
            if (_recent.Count >= 2)
            {
                double span = Math.Max(250, nowMs - _recent.Peek().TimeMs);
                long bytes = 0;
                foreach (var s in _recent) bytes += s.Bytes;
                DeliveryKbps = bytes * 8 / span;
                _deliveries.Enqueue(new Rate(nowMs, DeliveryKbps));
            }
            while (_deliveries.Count > 0 && nowMs - _deliveries.Peek().TimeMs > 30_000) _deliveries.Dequeue();
            double link = 0;
            foreach (var d in _deliveries) link = Math.Max(link, d.Kbps);
            if (link <= 0) link = currentKbps;
            LinkKbps = link;

            // Queueing delay: what is left of each recent ack delay after the base RTT and the frame's own
            // serialisation time. The minimum over the last 300 ms: a burst (big frame) delays some frames, only
            // a standing queue delays all of them.
            double queue = double.MaxValue;
            int n = 0;
            foreach (var s in _recent)
            {
                if (nowMs - s.TimeMs >= 300) continue;
                queue = Math.Min(queue, s.DelayMs - baseRtt - s.Bytes * 8 / link);
                n++;
            }
            if (n < 2) queue = 0;
            // A frame stuck in flight far longer than its size explains is a growing queue too (no acks at all).
            double stuck = 0;
            if (_inflight.Count > 0)
            {
                var head = _inflight.Peek();
                stuck = nowMs - head.TimeMs - baseRtt - head.Bytes * 8 / link;
            }
            QueueMs = Math.Max(0, Math.Max(queue, stuck));

            if (QueueMs > OveruseMs && nowMs >= _holdUntil)
            {
                if (nowMs - _lastDecrease < 250) return null;
                _lastDecrease = nowMs;
                _saturatedAt = nowMs;
                // Drop to just under what the link delivered (the queue must drain): at least 15 %, at most half.
                double target = currentKbps * 0.85;
                if (DeliveryKbps > 0) target = Math.Min(target, DeliveryKbps * 0.9);
                return Math.Max(minKbps, (int)Math.Max(target, currentKbps * 0.5));
            }

            // No queue on the link because pacing holds frames back on the host instead: the link is full when
            // the sender keeps waiting while less than the target arrives. Follow what is delivered.
            if (_blocked > 0.15 && DeliveryKbps > 0 && DeliveryKbps < currentKbps * 0.9) _saturatedAt = nowMs;
            if (_blocked > 0.15 && DeliveryKbps > 0 && DeliveryKbps < currentKbps * 0.9 && nowMs - _lastDecrease > 1000)
            {
                _lastDecrease = nowMs;
                return Math.Max(minKbps, (int)Math.Max(DeliveryKbps * 0.95, currentKbps * 0.7));
            }

            if (QueueMs < UnderuseMs && _blocked < 0.05 && currentKbps < maxKbps && nowMs - _lastDecrease > 1500 && nowMs - _lastIncrease > 500)
            {
                // Probe upwards only when the link carries what is asked now; a static screen proves nothing.
                if (DeliveryKbps < currentKbps * 0.8) return null;
                // Right after the link was seen full, stay a little under it: running exactly at capacity makes
                // every frame wait for acks on the host (one RTT of latency). Probe past it again after 30 s.
                double ceiling = nowMs - _saturatedAt < 30_000 ? Math.Min(maxKbps, LinkKbps * 0.92) : maxKbps;
                if (currentKbps >= ceiling) return null;
                _lastIncrease = nowMs;
                return (int)Math.Min(ceiling, currentKbps * 1.08 + 250);
            }
            return null;
        }
    }
}
