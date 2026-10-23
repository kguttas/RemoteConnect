using System.Runtime.InteropServices;
using System.Text;
using RemoteConnect.Client;
using RemoteConnect.Core;
using RemoteConnect.Host;

namespace RemoteConnect;

/// <summary>
/// Diagnostics written to %AppData%\RemoteConnect\selftest.txt:
///   --selftest                       encoder → decoder round trip for every encoder, plus desktop capture speed
///   --testclient addr password secs  headless client against a running host (fps, bitrate, RTT);
///                                    addr may be a 9-digit Internet ID to test the P2P path
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Out = new();
    private static void P(string s) { Out.AppendLine(s); Log.Write("selftest: " + s); }

    public static async Task<int> RunAsync(string[] args)
    {
        int rc;
        try
        {
            rc = args[0] == "--testclient" ? await TestClient(args) : Rotation() + Capture() + Codecs();
        }
        catch (Exception ex)
        {
            P("FAILED: " + ex);
            rc = 1;
        }
        File.WriteAllText(Path.Combine(Paths.UserDir, "selftest.txt"), Out.ToString());
        return rc;
    }

    /// <summary>
    /// Host-side rotation (rotated displays) and client-side rotation (the Rotate button) must agree: turning the
    /// image r quarter turns on the host must look exactly like showing it unturned with client rotation r.
    /// </summary>
    private static unsafe int Rotation()
    {
        const int W = 37, H = 23; // odd sizes catch off-by-one errors
        var src = new uint[W * H];
        for (int i = 0; i < src.Length; i++) src[i] = (uint)i * 2654435761u;
        int bad = 0;
        for (int r = 1; r < 4; r++)
        {
            int dw = (r & 1) != 0 ? H : W, dh = (r & 1) != 0 ? W : H;
            var dst = new uint[dw * dh];
            fixed (uint* s = src) fixed (uint* d = dst) ScreenCapture.Rotate((byte*)s, W * 4, W, H, r, d, dw, dh);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    var (u, v) = ViewLayout.ToVideo(r, (x + 0.5) / dw, (y + 0.5) / dh);
                    if (dst[y * dw + x] != src[(int)(v * H) * W + (int)(u * W)]) bad++;
                }
        }
        P($"Rotation: host and client rotation {(bad == 0 ? "agree" : $"DISAGREE on {bad} pixels")}");
        return bad == 0 ? 0 : 1;
    }

    private static unsafe int Codecs()
    {
        const int W = 1920, H = 1080;
        var img = (byte*)NativeMemory.AlignedAlloc(W * H * 4, 64);
        var outBuf = (byte*)NativeMemory.AlignedAlloc(W * H * 4, 64);
        int failures = 0;
        var (decAll, decHw) = VideoPipeline.Capabilities();
        P($"Client decoders: {string.Join(",", decAll)}; GPU decode: {string.Join(",", decHw)}");
        // Devices of our own stand in for the capture device to exercise the zero-copy GPU input path; each
        // vendor's encoder needs a device on its own adapter (hybrid laptops have two).
        var gpus = new Dictionary<uint, GpuDevice?>();
        using (var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>())
            for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
                using (a)
                {
                    var d = a.Description1;
                    bool outputs = a.EnumOutputs(0, out var o).Success;
                    o?.Dispose();
                    P($"Adapter {i}: {d.Description} vendor 0x{d.VendorId:X4}{(outputs ? " (drives a display)" : "")}");
                    if (!gpus.ContainsKey(d.VendorId) && (d.Flags & Vortice.DXGI.AdapterFlags.Software) == 0) gpus[d.VendorId] = new GpuDevice(a, W, H);
                }
        try
        {
            foreach (var name in VideoEncoder.Candidates)
            foreach (var mode in new[] { "cpu", "gpu", "444" })
            {
                if (mode == "444" && name is not ("h264_nvenc" or "libx264")) continue;
                if (mode == "gpu" && !VideoEncoder.IsHardware(name)) continue;
                VideoEncoder enc;
                uint vendor = name.EndsWith("_nvenc") ? 0x10DEu : name.EndsWith("_qsv") ? 0x8086u : 0x1002u;
                var gpu = mode == "gpu" ? gpus.GetValueOrDefault(vendor) : null;
                if (mode == "gpu" && gpu is null) { P($"{name,-11} gpu    no adapter from this vendor"); continue; }
                try { enc = new VideoEncoder([VideoEncoder.CodecOf(name)], W, H, 60, 30000, mode == "444", name, gpu?.Device.NativePointer ?? 0); }
                catch (Exception ex) { P($"{name,-11} {mode} unavailable: {ex.Message}"); continue; }
                using (enc)
                {
                    if (enc.Name != name) { P($"{name,-11} {mode} unavailable (fell back to {enc.Name})"); continue; }
                    if (mode == "gpu" && !enc.GpuInput) { P($"{name,-11} gpu unavailable (CPU input only)"); continue; }
                    string label = $"{name,-11} {(enc.Is444 ? "444" : "420")} {(enc.GpuInput ? "gpu" : "cpu")}";
                    if (!VideoDecoder.CanDecode(enc.Codec)) { P($"{label} no decoder for {enc.Codec}"); continue; }
                    using var dec = new VideoDecoder(enc.Codec);
                    double encMs = 0, decMs = 0;
                    int packets = 0, decoded = 0, keys = 0, keysAfterRate = 0;
                    long bytes = 0;
                    const int Frames = 60;
                    var produced = new List<(byte[] Data, bool Key)>();
                    PacketSink sink = (d, k) => produced.Add((d.ToArray(), k));
                    try
                    {
                    for (int f = 0; f < Frames; f++)
                    {
                        Paint(img, W, H, f);
                        // A bitrate change mid-stream must not cost a keyframe (dynamic encoders only).
                        if (f == Frames / 2 && enc.DynamicBitrate) enc.SetBitrate(15000);
                        produced.Clear();
                        var t0 = Stopwatch.GetTimestamp();
                        if (enc.GpuInput)
                        {
                            gpu!.Context.UpdateSubresource(gpu.Source, 0, null, (nint)img, W * 4, 0);
                            enc.EncodeGpu((tex, slice) =>
                            {
                                Marshal.AddRef(tex);
                                using var dst = new Vortice.Direct3D11.ID3D11Texture2D(tex);
                                gpu.Context.CopySubresourceRegion(dst, slice, 0, 0, 0, gpu.Source, 0, new Vortice.Mathematics.Box(0, 0, 0, W, H, 1));
                            }, f == 0, sink);
                        }
                        else enc.Encode(img, W * 4, f == 0, sink);
                        encMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        foreach (var (pkt, key) in produced)
                        {
                            packets++;
                            bytes += pkt.Length;
                            if (key) { keys++; if (f >= Frames / 2) keysAfterRate++; }
                            var t1 = Stopwatch.GetTimestamp();
                            if (dec.Decode(pkt)) { decoded++; dec.CopyTo(outBuf, W * 4); }
                            decMs += Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                        }
                    }
                    }
                    catch (Exception ex) { P($"{label} FAILED: {ex.Message}"); failures++; continue; }
                    if (decoded == 0) { P($"{label} FAILED: nothing decoded ({packets} packets)"); failures++; continue; }
                    Paint(img, W, H, Frames - 1);
                    // Colour accuracy on the flat patches (catches matrix/range mismatches) and overall error.
                    var (patchErr, worst) = PatchError(img, outBuf, W, H);
                    double mae = MeanAbsError(img, outBuf, W, H);
                    bool latencyOk = packets - decoded <= 1;
                    bool ok = patchErr < 6 && latencyOk;
                    if (!ok) failures++;
                    P($"{label} {(ok ? "OK  " : "BAD ")} enc {encMs / Frames,5:0.00} ms  dec {decMs / Frames,5:0.00} ms  " +
                      $"{bytes * 8.0 / Frames * 60 / 1e6,6:0.0} Mbps  patchErr {patchErr,4:0.0} (worst {worst})  MAE {mae,4:0.0}  " +
                      $"pkts {packets} decoded {decoded} keys {keys}{(enc.DynamicBitrate ? $" (after bitrate change {keysAfterRate})" : " (no dynamic bitrate)")}");
                }
            }
            failures += PipelineTest(img, W, H);
        }
        finally
        {
            foreach (var g in gpus.Values) g?.Dispose();
            NativeMemory.AlignedFree(img);
            NativeMemory.AlignedFree(outBuf);
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The real client window (native video child, swap chain, layout) against a host, resized midway.</summary>
    private static async Task<int> WindowTest(RemoteSession session, int secs, bool hookTest = false)
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        UI.RemoteWindow? w = null;
        await ui.InvokeAsync(() =>
        {
            w = new UI.RemoteWindow(session, new ClientSettings { CaptureSystemKeys = hookTest }) { Topmost = true }; // topmost: screenshots of the test
            w.Show();
        });
        if (hookTest) return await HookTest(session, w!);
        var sw = Stopwatch.StartNew();
        bool resized = false;
        int shownBefore = 0;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            double t = sw.Elapsed.TotalSeconds;
            if (session.IsConnected) session.MouseMove((float)(0.5 + 0.2 * Math.Cos(t * 3)), (float)(0.5 + 0.2 * Math.Sin(t * 3)));
            if (!resized && t > secs / 2.0)
            {
                resized = true;
                shownBefore = await ui.InvokeAsync(() => { var d = w!.Diagnostics(); w.Width = 900; w.Height = 560; return d.Shown; });
            }
            await Task.Delay(8);
        }
        var (shown, decoder, stats) = await ui.InvokeAsync(() => w!.Diagnostics());
        P($"Window: {shown} frames presented ({shown - shownBefore} after resizing), decoder {decoder}, status bar \"{stats}\"");
        await ui.InvokeAsync(w!.Close);
        await Task.Delay(1500); // the window disposes the session when it closes
        return shown > 0 && shown > shownBefore ? 0 : 1;
    }

    private sealed class GpuDevice : IDisposable
    {
        public readonly Vortice.Direct3D11.ID3D11Device Device;
        public readonly Vortice.Direct3D11.ID3D11DeviceContext Context;
        public readonly Vortice.Direct3D11.ID3D11Texture2D Source;

        public GpuDevice(Vortice.DXGI.IDXGIAdapter1 adapter, int w, int h)
        {
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(adapter, Vortice.Direct3D.DriverType.Unknown,
                Vortice.Direct3D11.DeviceCreationFlags.BgraSupport | Vortice.Direct3D11.DeviceCreationFlags.VideoSupport,
                null!, out Vortice.Direct3D11.ID3D11Device? dev, out Vortice.Direct3D11.ID3D11DeviceContext? ctx).CheckError();
            Device = dev!;
            Context = ctx!;
            using (var mt = Device.QueryInterfaceOrNull<Vortice.Direct3D11.ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
            Source = Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1));
        }

        public void Dispose() { Source.Dispose(); Context.Dispose(); Device.Dispose(); }
    }

    /// <summary>
    /// The client's real path, headless: wire packets → VideoPipeline (GPU decode when available) →
    /// D3D renderer, then the patches are read back from the rendered picture.
    /// </summary>
    private static unsafe int PipelineTest(byte* img, int w, int h)
    {
        int failures = 0;
        foreach (var codec in new[] { "h264", "hevc", "av1" })
        {
            VideoEncoder enc;
            try { enc = new VideoEncoder([codec], w, h, 60, 30000, false, "auto", 0); }
            catch (Exception ex) { P($"pipeline {codec}: no encoder ({ex.Message})"); continue; }
            if (enc.Codec != codec || !VideoDecoder.CanDecode(codec)) { enc.Dispose(); continue; }
            using (enc)
            using (var pipeline = new VideoPipeline(() => { }, windowed: false))
            {
                using var done = new ManualResetEventSlim();
                const int Frames = 30;
                var got = new (byte B, byte G, byte R)[Patches.Length];
                pipeline.Converted = () =>
                {
                    if (pipeline.FramesShown < Frames) return;
                    int pw = w / Patches.Length;
                    for (int i = 0; i < Patches.Length; i++)
                        got[i] = pipeline.Sample(i * pw + pw / 2, h / 4) ?? default;
                    done.Set();
                };
                pipeline.Reset(new VideoFormatInfo { Width = w, Height = h, Encoder = enc.Name, Codec = codec });
                uint seq = 0;
                PacketSink sink = (d, key) =>
                {
                    var buf = new byte[13 + d.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, seq++);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4), Stopwatch.GetTimestamp());
                    buf[12] = (byte)(key ? 1 : 0);
                    d.CopyTo(buf.AsSpan(13));
                    pipeline.Push(new Packet(Msg.VideoFrame, buf, buf.Length, false));
                };
                for (int f = 0; f < Frames + 10 && !done.IsSet; f++)
                {
                    Paint(img, w, h, f);
                    enc.Encode(img, w * 4, f == 0, sink);
                    Thread.Sleep(16);
                }
                bool shown = done.Wait(3000);
                double err = 0;
                for (int i = 0; i < Patches.Length; i++)
                    err += (Math.Abs(got[i].B - Patches[i].B) + Math.Abs(got[i].G - Patches[i].G) + Math.Abs(got[i].R - Patches[i].R)) / 3.0;
                err /= Patches.Length;
                bool ok = shown && err < 8;
                if (!ok) failures++;
                P($"pipeline {codec,-4} {(ok ? "OK  " : "BAD ")} {pipeline.DecoderInfo}: shown {pipeline.FramesShown}, last decode+draw {pipeline.DecodeMs:0.00} ms, patch error {err:0.0} [{string.Join(" ", got.Select(c => $"{c.R},{c.G},{c.B}"))}]");
            }
        }
        return failures;
    }

    private static readonly (byte B, byte G, byte R)[] Patches =
        [(255, 255, 255), (0, 0, 0), (0, 0, 255), (0, 255, 0), (255, 0, 0), (0, 255, 255), (255, 0, 255), (255, 255, 0), (128, 128, 128), (30, 30, 30)];

    /// <summary>Colour patches on top, 1-px text-like stripes and a moving block at the bottom.</summary>
    private static unsafe void Paint(byte* p, int w, int h, int frame)
    {
        int pw = w / Patches.Length;
        for (int y = 0; y < h; y++)
        {
            byte* row = p + (long)y * w * 4;
            for (int x = 0; x < w; x++)
            {
                byte b, g, r;
                if (y < h / 2)
                {
                    var c = Patches[Math.Min(Patches.Length - 1, x / pw)];
                    (b, g, r) = c;
                }
                else
                {
                    bool stripe = ((x / 2 + y) & 3) == 0;
                    bool block = x >= frame * 20 % (w - 200) && x < frame * 20 % (w - 200) + 200 && y > h * 3 / 4;
                    byte v = block ? (byte)200 : stripe ? (byte)20 : (byte)235;
                    (b, g, r) = (v, v, block ? (byte)40 : v);
                }
                row[x * 4] = b; row[x * 4 + 1] = g; row[x * 4 + 2] = r; row[x * 4 + 3] = 255;
            }
        }
    }

    private static unsafe (double Avg, string Worst) PatchError(byte* a, byte* b, int w, int h)
    {
        int pw = w / Patches.Length;
        double total = 0, worstErr = 0;
        string worst = "";
        for (int i = 0; i < Patches.Length; i++)
        {
            long sum = 0, n = 0;
            for (int y = h / 8; y < h * 3 / 8; y += 3)
            for (int x = i * pw + pw / 4; x < i * pw + pw * 3 / 4; x += 3)
            {
                long o = ((long)y * w + x) * 4;
                for (int c = 0; c < 3; c++) sum += Math.Abs(a[o + c] - b[o + c]);
                n += 3;
            }
            double e = (double)sum / n;
            total += e;
            if (e > worstErr) { worstErr = e; worst = $"BGR{Patches[i]}={e:0.0}"; }
        }
        return (total / Patches.Length, worst);
    }

    private static unsafe double MeanAbsError(byte* a, byte* b, int w, int h)
    {
        long sum = 0, n = 0;
        for (long i = 0; i < (long)w * h * 4; i += 4 * 7)
        {
            for (int c = 0; c < 3; c++) sum += Math.Abs(a[i + c] - b[i + c]);
            n += 3;
        }
        return (double)sum / n;
    }

    private static unsafe double MeanAbsErrorRows(byte* a, byte* b, int w, int y0, int y1)
    {
        long sum = 0, n = 0;
        for (long i = (long)y0 * w * 4; i < (long)y1 * w * 4; i += 4 * 7)
        {
            for (int c = 0; c < 3; c++) sum += Math.Abs(a[i + c] - b[i + c]);
            n += 3;
        }
        return (double)sum / n;
    }

    private static unsafe int Capture()
    {
        var mons = ScreenCapture.EnumerateMonitors();
        foreach (var m in mons) P($"Monitor {m.Index}: {m.Name} {m.Width}x{m.Height} at {m.Left},{m.Top}{(m.Primary ? " primary" : "")}");
        using var cap = new ScreenCapture(0);
        int frames = 0, timeouts = 0;
        var sw = Stopwatch.StartNew();
        double busy = 0;
        while (sw.ElapsedMilliseconds < 2000)
        {
            var t0 = Stopwatch.GetTimestamp();
            var r = cap.Acquire(50, out _);
            if (r == CaptureResult.Frame) { frames++; busy += Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
            else if (r == CaptureResult.Timeout) timeouts++;
            else { P("capture: access lost"); return 1; }
        }
        P($"Capture {cap.Width}x{cap.Height}: {frames} frames / 2 s (screen changes only), {timeouts} timeouts, " +
          $"{(frames > 0 ? busy / frames : 0):0.00} ms per copied frame, hasImage={cap.HasImage}");
        return cap.HasImage ? 0 : 1;
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);

    /// <summary>
    /// The client's keyboard hook: keys typed on this PC while the remote window is in front (simulated with
    /// SendInput, like a remapper would) must reach the session with their scan codes, and Ctrl+Alt+Enter must stay
    /// local (full screen) without its Enter going to the remote. Keys are only typed while the window is in front.
    /// </summary>
    private static async Task<int> HookTest(RemoteSession session, UI.RemoteWindow w)
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        var sent = new System.Collections.Concurrent.ConcurrentQueue<(ushort Vk, ushort Scan, KeyFlags Flags)>();
        session.KeySent += (vk, scan, f) => sent.Enqueue((vk, scan, f));
        for (int i = 0; i < 100 && !session.IsConnected; i++) await Task.Delay(100);
        await Task.Delay(1500);
        nint hwnd = await ui.InvokeAsync(() => { w.Activate(); ForceForeground(w.Handle); return w.Handle; });
        await Task.Delay(300);
        bool Front() => Native.GetForegroundWindow() == hwnd;
        if (!Front()) { P("hook: could not take the foreground, test skipped"); await ui.InvokeAsync(w.Close); return 0; }
        unsafe bool Type(ushort vk, bool down)
        {
            if (!Front()) return false;
            var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            input.ki.wVk = vk;
            input.ki.wScan = (ushort)MapVirtualKeyW(vk, 0);
            if (!down) input.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            Native.SendInput(1, &input, sizeof(Native.INPUT));
            return true;
        }
        async Task<bool> Seq(params (ushort Vk, bool Down)[] keys)
        {
            foreach (var (vk, down) in keys) { if (!Type(vk, down)) return false; await Task.Delay(20); }
            await Task.Delay(200);
            return true;
        }
        int failures = 0;
        void Expect(string name, bool ok) { P($"hook {name}: {(ok ? "OK" : "FAIL")}"); if (!ok) failures++; }

        const ushort LShift = 0xA0, LCtrl = 0xA2, LAlt = 0xA4, A = 0x41, Enter = 0x0D;
        if (!await Seq((LShift, true), (A, true), (A, false), (LShift, false))) { P("hook: lost the foreground"); return 0; }
        var got = sent.ToArray(); sent.Clear();
        P("hook Shift+A sent: " + string.Join(" ", got.Select(k => $"{k.Vk:X2}/{k.Scan:X2}{(k.Flags.HasFlag(KeyFlags.Down) ? "↓" : "↑")}")));
        Expect("Shift+A forwarded with scan codes", got.Length == 4 && got[0] is (LShift, 0x2A, KeyFlags.Down) && got[1] is (A, 0x1E, KeyFlags.Down)
            && got[2] is (A, 0x1E, 0) && got[3] is (LShift, 0x2A, 0));

        bool before = await ui.InvokeAsync(() => w.IsFullScreen);
        if (!await Seq((LCtrl, true), (LAlt, true), (Enter, true), (Enter, false), (LAlt, false), (LCtrl, false))) { P("hook: lost the foreground"); return 0; }
        await Task.Delay(300);
        bool after = await ui.InvokeAsync(() => w.IsFullScreen);
        got = sent.ToArray(); sent.Clear();
        Expect("Ctrl+Alt+Enter toggles full screen locally", after != before);
        Expect("…and its Enter is not sent to the remote", !got.Any(k => k.Vk == Enter));
        await Task.Delay(300);
        hwnd = await ui.InvokeAsync(() => w.Handle);
        await Seq((LCtrl, true), (LAlt, true), (Enter, true), (Enter, false), (LAlt, false), (LCtrl, false)); // back to a window
        await Task.Delay(300);
        Expect("second Ctrl+Alt+Enter leaves full screen", await ui.InvokeAsync(() => w.IsFullScreen) == before);
        await ui.InvokeAsync(w.Close);
        await Task.Delay(1500);
        P($"keyboard hook {(failures == 0 ? "OK" : "FAILED")}");
        return failures == 0 ? 0 : 1;
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>Foreground lock workaround: share input state with the current foreground thread for the call.</summary>
    private static void ForceForeground(nint hwnd)
    {
        uint fg = GetWindowThreadProcessId(Native.GetForegroundWindow(), out _), me = GetCurrentThreadId();
        bool attached = fg != me && AttachThreadInput(me, fg, true);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fg, false);
    }

    /// <summary>
    /// Key combinations end to end: the host (this machine) types into a text box of this process. Each key is sent
    /// only while the box's window is in the foreground, so nothing can land in another window.
    /// </summary>
    private static async Task<int> KeysTest(RemoteSession session)
    {
        var ui = System.Windows.Application.Current.Dispatcher;
        System.Windows.Controls.TextBox? box = null;
        nint hwnd = 0;
        await ui.InvokeAsync(() =>
        {
            box = new System.Windows.Controls.TextBox { FontSize = 20 };
            var w = new System.Windows.Window { Title = "RemoteConnect key test", Width = 400, Height = 120, Topmost = true, Content = box, Left = 200, Top = 200 };
            w.Show();
            hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            w.Activate();
            ForceForeground(hwnd);
            box.Focus();
        });
        await Task.Delay(500);
        bool Foreground() => Native.GetForegroundWindow() == hwnd;
        if (!Foreground()) { P("keys: could not take the foreground, test skipped"); return 0; }

        // As the client's keyboard hook sends them: virtual key + scan code.
        async Task<bool> K(ushort vk, bool down, bool scanCodes = true)
        {
            if (!Foreground()) return false;
            ushort scan = scanCodes ? (ushort)MapVirtualKeyW(vk, 0) : (ushort)0;
            session.Key(vk, scan, down ? KeyFlags.Down : 0);
            await Task.Delay(15);
            return true;
        }
        async Task<bool> Tap(ushort vk, bool scanCodes = true) => await K(vk, true, scanCodes) && await K(vk, false, scanCodes);
        async Task<bool> Combo(ushort mod, ushort vk, bool scanCodes = true) =>
            await K(mod, true, scanCodes) && await Tap(vk, scanCodes) && await K(mod, false, scanCodes);
        async Task<string> Text() { await Task.Delay(150); return await ui.InvokeAsync(() => box!.Text); }

        const ushort Shift = 0x10, Ctrl = 0x11, LCtrl = 0xA2, LShift = 0xA0;
        var results = new List<(string Name, bool Ok)>();
        bool aborted = false;
        async Task Check(string name, Func<Task<bool>> keys, string expected)
        {
            if (aborted) return;
            if (!await keys()) { aborted = true; P("keys: lost the foreground, stopping"); return; }
            var t = await Text();
            results.Add((name, t == expected));
            P($"keys {name}: \"{t}\" (expected \"{expected}\") {(t == expected ? "OK" : "FAIL")}");
        }
        await Check("letters", async () => await Tap(0x41) && await Tap(0x42), "ab");
        await Check("Shift+C", () => Combo(Shift, 0x43), "abC");
        await Check("Ctrl+A then x", async () => await Combo(Ctrl, 0x41) && await Tap(0x58), "x");
        await Check("Ctrl+Z", () => Combo(Ctrl, 0x5A), "abC");
        await Check("Ctrl+Backspace", () => Combo(Ctrl, 0x08), "");
        // The WPF fallback path (system keys not captured) sends left/right virtual keys without scan codes.
        await Check("vk-only LShift+D", () => Combo(LShift, 0x44, false), "D");
        await Check("vk-only LCtrl+A then e", async () => await Combo(LCtrl, 0x41, false) && await Tap(0x45, false), "e");
        session.Special(SpecialAction.ReleaseAllKeys);
        await ui.InvokeAsync(() => System.Windows.Window.GetWindow(box!)!.Close());
        bool ok = !aborted && results.All(r => r.Ok);
        P($"key combinations {(aborted ? "ABORTED" : ok ? "OK" : "FAILED")}");
        return aborted ? 0 : ok ? 0 : 1;
    }

    /// <summary>Queries the remote display, re-applies its current mode and scale (no visible change), restores.</summary>
    private static async Task<int> DisplayTest(RemoteSession session)
    {
        if (session.Info?.DisplayControl != true) { P("host does not offer display control"); return 1; }
        var answers = new System.Collections.Concurrent.BlockingCollection<DisplayInfo>();
        session.DisplayInfo += answers.Add;
        DisplayInfo Next() => answers.TryTake(out var i, 10000) ? i : throw new TimeoutException("no DisplayInfo");
        string Show(DisplayInfo i) => $"{i.Width}x{i.Height} at {i.Scale} % (recommended {i.RecommendedScale} %, scales {string.Join("/", i.Scales)}), " +
            $"{i.Modes.Count} modes [{string.Join(", ", i.Modes.Take(6))}…], changed={i.Changed}{(i.Error is null ? "" : " error: " + i.Error)}";
        session.QueryDisplay();
        var first = await Task.Run(Next);
        P("display: " + Show(first));
        session.SetDisplay(first.Width, first.Height, first.Scale);
        var applied = await Task.Run(Next);
        P("after set: " + Show(applied));
        session.RestoreDisplay();
        var restored = await Task.Run(Next);
        P("after restore: " + Show(restored));
        bool ok = first.Modes.Count > 0 && first.Scale > 0 && applied.Error is null && applied.Changed && !restored.Changed
                  && restored.Width == first.Width && restored.Scale == first.Scale;
        P($"display control {(ok ? "OK" : "FAILED")}");
        return ok ? 0 : 1;
    }

    /// <summary>Lists the remote top level and <paramref name="remotePath"/>, downloads it and compares the bytes.</summary>
    private static async Task<int> DownloadTest(RemoteSession session, string remotePath)
    {
        if (session.Info?.FileDownload != true) { P("host does not offer downloads"); return 1; }
        var listing = new TaskCompletionSource<DirListingInfo>();
        session.DirListing += l => listing.TrySetResult(l);
        session.ListRemote("");
        var top = await listing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        P($"top level: {string.Join(", ", top.Entries.Select(e => e.Name))}{(top.Error is null ? "" : " error " + top.Error)}");
        listing = new TaskCompletionSource<DirListingInfo>();
        session.ListRemote(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(remotePath)) ?? remotePath);
        var dir = await listing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        P($"listing {dir.Path}: {dir.Entries.Count} entries, parent '{dir.Parent}'{(dir.Error is null ? "" : " error " + dir.Error)}");

        var dest = Path.Combine(Path.GetTempPath(), "rc-download-test-" + Environment.ProcessId);
        session.DownloadFolder = dest;
        var done = new TaskCompletionSource<DownloadInfo>();
        int files = 0;
        session.FileDownloaded += a => { if (a.Ok) Interlocked.Increment(ref files); else P("file error " + a.Error); };
        session.DownloadState += d => { if (d.State == DownloadInfo.Phase.Started) P($"download started: {d.Files} files, {d.Bytes / 1e6:0.0} MB"); else done.TrySetResult(d); };
        var sw = Stopwatch.StartNew();
        session.Download([remotePath]);
        var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(5));
        double secs = sw.Elapsed.TotalSeconds;
        P($"download {result.State} in {secs:0.00} s: {files} files, {session.DownloadedBytes / 1e6:0.0} MB, {session.DownloadedBytes / 1e6 / secs:0.0} MB/s{(result.Error is null ? "" : " error " + result.Error)}");

        // Compare every file with the original (host and client share this machine in the test).
        var root = Path.GetFullPath(remotePath);
        var originals = File.Exists(root) ? [root] : Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!;
        int bad = 0;
        foreach (var o in originals)
        {
            var copy = Path.Combine(dest, Path.GetRelativePath(parent, o));
            if (!File.Exists(copy) || !File.ReadAllBytes(copy).AsSpan().SequenceEqual(File.ReadAllBytes(o))) { bad++; P("MISMATCH " + copy); }
        }
        P($"verified {originals.Length - bad}/{originals.Length} files identical");
        try { Directory.Delete(dest, true); } catch { }
        return result.State == DownloadInfo.Phase.Finished && bad == 0 && files == originals.Length ? 0 : 1;
    }

    private static async Task<int> TestClient(string[] args)
    {
        var addr = args.Length > 1 ? args[1] : "127.0.0.1";
        var pwd = args.Length > 2 ? args[2] : "";
        int secs = args.Length > 3 ? int.Parse(args[3]) : 5;
        bool want444 = args.Contains("--444");
        using var discovery = new DiscoveryBrowser();
        discovery.Start();
        await Task.Delay(1200);
        foreach (var h in discovery.Hosts) P($"Discovered {h.Beacon.Name} ({h.Beacon.Machine}) id={h.Beacon.Id[..8]} at {h.Address}:{h.Beacon.Port} busy={h.Beacon.Busy}");

        // An Internet ID (e.g. 123456789) skips the LAN entirely: exercises STUN, signalling and QUIC punching.
        var host = P2p.TryParseId(addr, out var internetId)
            ? new KnownHost { Name = "ID " + P2p.FormatId(internetId), InternetId = internetId }
            : System.Net.IPEndPoint.TryParse(addr, out var ep) && ep.Port != 0
                ? new KnownHost { Name = addr, LastAddress = ep.Address.ToString(), Port = ep.Port }
                : new KnownHost { Name = addr, LastAddress = addr };
        host.ViewRotation = args.SkipWhile(a => a != "--rotate").Skip(1).Select(int.Parse).FirstOrDefault(0) / 90; // client-side, for --window
        var session = new RemoteSession(host, pwd, new StreamOptions { MaxFps = args.SkipWhile(a => a != "--fps").Skip(1).Select(int.Parse).FirstOrDefault(60), BitrateMbps = args.SkipWhile(a => a != "--mbps").Skip(1).Select(int.Parse).FirstOrDefault(30), Chroma444 = want444, Audio = true, Codec = args.SkipWhile(a => a != "--codec").Skip(1).FirstOrDefault() ?? "auto" }, discovery, args.SkipWhile(a => a != "--signal").Skip(1).FirstOrDefault());
        int audio = 0, cursors = 0;
        string? fmt = null, failure = null;
        var audioCodec = AudioCodec.Pcm16;
        var connected = new TaskCompletionSource();
        using var pipeline = new VideoPipeline(session.RequestKeyframe, windowed: false);
        if (args.Contains("--window")) return await WindowTest(session, secs, args.Contains("--hooktest"));
        session.StateChanged += (s, t) =>
        {
            P($"state {s} {t.Replace('\n', ' ')}");
            if (s == SessionState.Connected) connected.TrySetResult();
            if (s is SessionState.AuthFailed or SessionState.SecurityError) { failure = t; connected.TrySetResult(); }
        };
        session.VideoFormat += f => { pipeline.Reset(f); fmt = $"{f.Width}x{f.Height} {f.Encoder}{(f.Chroma444 ? " 444" : "")}{(f.GpuCapture ? " gpu-capture" : "")}"; };
        session.VideoFrame += p => pipeline.Push(p);
        session.Audio += (_, _, codec, _) => { Interlocked.Increment(ref audio); audioCodec = codec; };
        session.Cursor += (_, _, _, _, _, _) => Interlocked.Increment(ref cursors);
        session.Start();
        await Task.WhenAny(connected.Task, Task.Delay(host.InternetId is null ? 10000 : 40000));
        if (failure is not null || !session.IsConnected) { P("connect failed: " + (failure ?? "timeout")); await session.DisposeAsync(); return 1; }
        P($"connected via {(session.ViaInternet ? "Internet (QUIC)" : "LAN (TLS/TCP)")}");
        if (args.Contains("--keys"))
        {
            int rc = await KeysTest(session);
            await session.DisposeAsync();
            return rc;
        }
        if (args.Contains("--display"))
        {
            int rc = await DisplayTest(session);
            await session.DisposeAsync();
            return rc;
        }
        if (args.SkipWhile(a => a != "--download").Skip(1).FirstOrDefault() is { } remotePath)
        {
            int rc = await DownloadTest(session, remotePath);
            await session.DisposeAsync();
            return rc;
        }

        // Optional: an animated patch whose colour encodes a frame counter, read back from the decoded
        // stream to measure glass-to-decode latency (host and client on this machine).
        var latencies = new List<double>();
        var stampLatencies = new List<double>();
        Func<string>? whiteReport = null;
        System.Windows.Window? anim = null;
        if (args.Contains("--animate"))
        {
            // A 3×3 grid of black / white cells encodes a 9-bit frame counter (~8 s at 60 Hz before it wraps).
            // Binary cells survive any brightness / colour shift on the way.
            const int Bits = 9, States = 1 << Bits;
            var paintTimes = new long[States];
            int level = 0;
            var cellPx = new (int X, int Y)[Bits];
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3 };
                var cells = new System.Windows.Media.SolidColorBrush[Bits];
                for (int i = 0; i < Bits; i++)
                {
                    cells[i] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black);
                    grid.Children.Add(new System.Windows.Controls.Border { Background = cells[i] });
                }
                anim = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize,
                    Left = 100, Top = 100, Width = 180, Height = 180, Topmost = true, ShowActivated = false, Content = grid,
                };
                anim.Show();
                var m = System.Windows.PresentationSource.FromVisual(anim)!.CompositionTarget!.TransformToDevice;
                for (int i = 0; i < Bits; i++)
                    cellPx[i] = ((int)((anim.Left + 30 + i % 3 * 60) * m.M11), (int)((anim.Top + 30 + i / 3 * 60) * m.M22));
                System.Windows.Media.CompositionTarget.Rendering += (_, _) =>
                {
                    level = (level + 1) % States;
                    for (int i = 0; i < Bits; i++)
                        cells[i].Color = (level >> i & 1) != 0 ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black;
                    paintTimes[level] = Stopwatch.GetTimestamp();
                };
            });
            int lastSeen = -1;
            int whiteMax = 0;
            pipeline.Converted = () =>
            {
                long captured = Volatile.Read(ref pipeline.LastStamp);
                if (captured != 0) lock (stampLatencies) stampLatencies.Add(Stopwatch.GetElapsedTime(captured).TotalMilliseconds);
                int seen = 0;
                for (int i = 0; i < Bits; i++)
                {
                    if (pipeline.Sample(cellPx[i].X, cellPx[i].Y) is not { } c) return;
                    whiteMax = Math.Max(whiteMax, c.G);
                    if (c.G > 64) seen |= 1 << i;
                }
                if (seen == lastSeen) return;
                lastSeen = seen;
                var t = paintTimes[seen];
                if (t != 0) lock (latencies) latencies.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            };
            whiteReport = () => $"brightest captured white cell: {whiteMax}";
        }

        // Wiggle the remote mouse so the screen changes and frames flow.
        var sw = Stopwatch.StartNew();
        long startBytes = session.BytesReceived;
        int startFrames = pipeline.FramesDecoded;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            double t = sw.Elapsed.TotalSeconds;
            session.MouseMove((float)(0.5 + 0.2 * Math.Cos(t * 3)), (float)(0.5 + 0.2 * Math.Sin(t * 3)));
            await Task.Delay(8);
        }
        double mbps = (session.BytesReceived - startBytes) * 8 / 1e6 / sw.Elapsed.TotalSeconds;
        P($"Stream {fmt}: {(pipeline.FramesDecoded - startFrames) / sw.Elapsed.TotalSeconds:0.0} fps decoded, total {pipeline.FramesDecoded}, " +
          $"{mbps:0.0} Mbps, RTT {session.RttMs:0.0} ms, decode+draw {pipeline.DecodeMs:0.0} ms ({pipeline.DecoderInfo}), audio packets {audio} ({audioCodec}), cursor shapes {cursors}");
        P($"Host rate control: bitrate {session.HostBitrateKbps / 1000.0:0.0} Mbps, queue {session.HostQueueMs:0.0} ms, encode {session.HostEncodeMs:0.0} ms");
        if (latencies.Count > 0)
        {
            List<double> l;
            lock (latencies) l = latencies.Skip(10).OrderBy(x => x).ToList();
            if (l.Count > 0)
                P($"Glass-to-decode latency over {l.Count} samples: median {l[l.Count / 2]:0.0} ms, p90 {l[(int)(l.Count * 0.9)]:0.0} ms, min {l[0]:0.0} ms " +
                  "(includes WPF/DWM presentation of the test patch, ~1 refresh)");
        }
        if (whiteReport is not null) P(whiteReport());
        if (stampLatencies.Count > 10)
        {
            List<double> l;
            lock (stampLatencies) l = stampLatencies.Skip(10).OrderBy(x => x).ToList();
            P($"Capture-to-screen latency (host capture timestamp → frame drawn, same clock): median {l[l.Count / 2]:0.0} ms, p90 {l[(int)(l.Count * 0.9)]:0.0} ms");
        }
        if (anim is not null) await System.Windows.Application.Current.Dispatcher.InvokeAsync(anim.Close);
        session.MouseMove(0.5f, 0.5f);
        await session.DisposeAsync();
        return pipeline.FramesDecoded > 0 ? 0 : 1;
    }
}
