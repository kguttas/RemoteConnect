using System.Buffers.Binary;
using System.Collections.Concurrent;
using RemoteConnect.Core;

namespace RemoteConnect.Client;

/// <summary>
/// Network → decode thread → GPU → screen. Every packet is decoded (P-frames depend on each other) but only
/// the newest picture is drawn. Decoding (D3D11VA when the GPU supports the codec), YUV → RGB, scaling and
/// presenting all happen on this thread with Direct3D, so the WPF UI thread never touches a frame.
/// </summary>
public sealed unsafe class VideoPipeline : IDisposable
{
    private readonly record struct Item(byte[]? Buffer, int Length, bool Pooled);

    private static (List<string> All, List<string> Hardware)? _caps;
    private static readonly Lock CapsGate = new();

    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>(), 64);
    private readonly Thread _thread;
    private readonly Action _requestKeyframe;
    private readonly bool _windowed;
    private volatile VideoFormatInfo? _resetTo;
    private volatile ViewLayout? _layout;
    private nint _hwnd;
    private volatile bool _hwndChanged;
    private int _framesQueued;
    private int _redrawPending;
    private bool _waitKeyframe = true;
    private D3DRenderer? _renderer;

    public int FramesDecoded;
    public int FramesShown;
    public double DecodeMs;
    /// <summary>Capture timestamp (host Stopwatch ticks) of the picture on screen; same-machine diagnostics only.</summary>
    public long LastStamp;
    public string DecoderInfo { get; private set; } = "";

    /// <summary>Raised on the decode thread after each drawn picture (diagnostics).</summary>
    public Action? Converted;

    /// <summary>
    /// Codecs this machine can decode (software or GPU) and the ones its GPU decodes, for the Hello: the
    /// host picks the most efficient codec both sides handle in hardware.
    /// </summary>
    public static (List<string> All, List<string> Hardware) Capabilities()
    {
        lock (CapsGate)
        {
            if (_caps is { } c) return c;
            var all = new List<string>();
            foreach (var codec in new[] { "h264", "hevc", "av1" })
                if (VideoDecoder.CanDecode(codec)) all.Add(codec);
            List<string> hw;
            try
            {
                using var r = new D3DRenderer();
                hw = r.HardwareCodecs();
            }
            catch (Exception ex) { Log.Write("GPU decode probe: " + ex.Message); hw = new(); }
            // AV1 has no usable software fallback in FFmpeg's own decoder, but dav1d is fine as long as it exists.
            hw.RemoveAll(x => !all.Contains(x));
            _caps = (all, hw);
            Log.Write($"Decoders: {string.Join(",", all)}; GPU: {string.Join(",", hw)}");
            return _caps.Value;
        }
    }

    /// <param name="windowed">Present into a window (see <see cref="SetWindow"/>); false = headless (tests).</param>
    public VideoPipeline(Action requestKeyframe, bool windowed)
    {
        _requestKeyframe = requestKeyframe;
        _windowed = windowed;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Decode", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void SetWindow(nint hwnd)
    {
        _hwnd = hwnd;
        _hwndChanged = true;
        RequestRedraw();
    }

    /// <summary>New stream (resolution / codec change): start over at the next keyframe.</summary>
    public void Reset(VideoFormatInfo format) => _resetTo = format;

    /// <summary>Window resized, fit mode or pan changed: redraw the current picture.</summary>
    public void SetLayout(ViewLayout layout)
    {
        _layout = layout;
        RequestRedraw();
    }

    public void RequestRedraw()
    {
        if (Interlocked.Exchange(ref _redrawPending, 1) == 0 && !_queue.IsAddingCompleted)
        {
            try { _queue.TryAdd(new Item(null, 0, false)); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Takes ownership of the packet's buffer.</summary>
    public void Push(in Packet p)
    {
        Interlocked.Increment(ref _framesQueued);
        bool added;
        try { added = _queue.TryAdd(new Item(p.Buffer, p.Length, p.Pooled)); }
        catch (InvalidOperationException) { added = false; }
        if (added) return;
        Interlocked.Decrement(ref _framesQueued);
        p.Return();
        // Decoder can't keep up: drop everything and resync on a keyframe.
        while (_queue.TryTake(out var old))
        {
            if (old.Buffer is null) continue;
            Interlocked.Decrement(ref _framesQueued);
            if (old.Pooled) BufferPool.Return(old.Buffer);
        }
        _waitKeyframe = true;
        _requestKeyframe();
    }

    private void Loop()
    {
        VideoDecoder? decoder = null;
        VideoFormatInfo? format = null;
        int hwErrors = 0;
        try
        {
            try { _renderer = new D3DRenderer(); }
            catch (Exception ex) { Log.Error("renderer", ex); }
            var hw = Capabilities().Hardware;

            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_hwndChanged && _renderer is not null) { _hwndChanged = false; _renderer.SetWindow(_hwnd); }
                if (item.Buffer is null)
                {
                    Interlocked.Exchange(ref _redrawPending, 0);
                    if (_renderer is { HasPicture: true } && _layout is { } l && (_windowed ? _hwnd != 0 : true)) _renderer.Draw(l);
                    else if (_renderer is not null && _windowed && _hwnd != 0 && _layout is { } l2) _renderer.Draw(l2); // background only
                    continue;
                }
                Interlocked.Decrement(ref _framesQueued);
                var span = item.Buffer.AsSpan(0, item.Length);
                try
                {
                    if (_resetTo is { } f)
                    {
                        _resetTo = null;
                        format = f;
                        decoder?.Dispose();
                        decoder = null;
                        hwErrors = 0;
                        _waitKeyframe = true;
                    }
                    if (format is null || span.Length < 13) continue; // frames before any VideoFormat
                    if (decoder is null)
                    {
                        bool gpu = _renderer is { Warp: false } && hw.Contains(format.Codec) && !format.Chroma444 && hwErrors < 3;
                        try { decoder = new VideoDecoder(format.Codec, gpu ? _renderer!.DevicePointer : 0); }
                        catch (Exception ex) when (gpu)
                        {
                            Log.Write($"GPU decoder for {format.Codec} unavailable ({ex.Message}), using software");
                            decoder = new VideoDecoder(format.Codec);
                        }
                        DecoderInfo = decoder.Description;
                        Log.Write($"Decoder: {DecoderInfo} for {format.Width}x{format.Height} from {format.Encoder}");
                    }

                    bool key = (span[12] & 1) != 0;
                    if (_waitKeyframe && !key) continue;
                    _waitKeyframe = false;
                    long stamp = BinaryPrimitives.ReadInt64LittleEndian(span[4..]);

                    var t0 = Stopwatch.GetTimestamp();
                    bool got;
                    try { got = decoder.Decode(span[13..]); }
                    catch (Exception ex)
                    {
                        Log.Write("Decode error, requesting keyframe: " + ex.Message);
                        if (decoder.Description.StartsWith("D3D11VA") && ++hwErrors >= 3)
                        {
                            Log.Write("Repeated GPU decode errors: switching to software decoding");
                            decoder.Dispose();
                            decoder = null;
                        }
                        _waitKeyframe = true;
                        _requestKeyframe();
                        continue;
                    }
                    if (!got) continue;
                    FramesDecoded++;
                    // More frames already queued? Skip drawing this one.
                    if (Volatile.Read(ref _framesQueued) > 0) continue;
                    if (_renderer is null) continue;

                    _renderer.Upload(decoder.Frame);
                    var layout = _layout;
                    if (_windowed && (_hwnd == 0 || layout is null)) continue;
                    if (layout is not null && (layout.VideoW != decoder.Width || layout.VideoH != decoder.Height))
                        layout = ViewLayout.Compute(layout.ClientW, layout.ClientH, decoder.Width, decoder.Height, true, 0, 0);
                    if (!_renderer.Draw(layout ?? ViewLayout.Native(decoder.Width, decoder.Height))) continue;
                    DecodeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    FramesShown++;
                    LastStamp = stamp;
                    Converted?.Invoke();
                }
                finally
                {
                    if (item.Pooled) BufferPool.Return(item.Buffer);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        catch (Exception ex) { Log.Error("decode loop", ex); }
        finally
        {
            decoder?.Dispose();
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    /// <summary>
    /// Reads one pixel of the newest picture (headless diagnostics). Only valid on the decode thread,
    /// i.e. from <see cref="Converted"/>.
    /// </summary>
    public (byte B, byte G, byte R)? Sample(int x, int y) => _renderer?.Sample(x, y);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}
