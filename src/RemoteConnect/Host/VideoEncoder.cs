using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RemoteConnect.Core;

namespace RemoteConnect.Host;

/// <summary>
/// Low-latency H.264 / HEVC / AV1 encoder. Tries hardware encoders first (NVENC, Quick Sync, AMF), then x264
/// and Media Foundation. No B-frames, no lookahead, parameter sets in-band so a client can join at any IDR.
/// When given the capture's D3D11 device, it encodes straight from GPU textures (no GPU → CPU → GPU round
/// trip); otherwise it reads the CPU copy of the frame.
/// </summary>
public sealed unsafe class VideoEncoder : IDisposable
{
    public static readonly string[] H264 = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264", "h264_mf"];
    public static readonly string[] Hevc = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf"];
    public static readonly string[] Av1 = ["av1_nvenc", "av1_qsv", "av1_amf"];
    public static IEnumerable<string> Candidates => H264.Concat(Hevc).Concat(Av1);

    public static string[] CandidatesFor(string codec) => codec switch { "hevc" => Hevc, "av1" => Av1, _ => H264 };
    public static string CodecOf(string encoder) => encoder.StartsWith("hevc") ? "hevc" : encoder.StartsWith("av1") ? "av1" : "h264";
    public static bool IsHardware(string encoder) => encoder.EndsWith("_nvenc") || encoder.EndsWith("_qsv") || encoder.EndsWith("_amf");
    private static bool Can444(string encoder) => encoder is "h264_nvenc" or "hevc_nvenc" or "libx264";

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private AVBufferRef* _device;
    private AVBufferRef* _frames;
    private bool _direct;
    private long _pts;
    private int _fps;

    public string Name { get; private set; } = "";
    public string Codec { get; private set; } = "h264";
    public bool Is444 { get; private set; }
    /// <summary>Frames come from D3D11 textures (see <see cref="EncodeGpu"/>).</summary>
    public bool GpuInput { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int BitrateKbps { get; private set; }
    /// <summary>The bitrate can change between frames without reopening the encoder.</summary>
    public bool DynamicBitrate => Name.EndsWith("_nvenc") || Name == "h264_qsv" || Name == "libx264"; // hevc_qsv fails to reset
    /// <summary>FFmpeg applies a new NVENC / QSV rate by resetting the encoder, which starts with an IDR frame.</summary>
    public bool RateChangeCostsKeyframe => Name.EndsWith("_nvenc") || Name.EndsWith("_qsv");

    /// <param name="codecs">Codecs in order of preference; the first one with a working encoder wins.</param>
    /// <param name="d3dDevice">The capture's ID3D11Device (0 = CPU input only).</param>
    public VideoEncoder(IReadOnlyList<string> codecs, int width, int height, int fps, int bitrateKbps, bool want444, string preferred, nint d3dDevice)
    {
        FFmpegSetup.Init();
        Width = width & ~1;
        Height = height & ~1;
        _fps = fps;
        // Zero-copy needs the encoder's GPU to own the capture device; on hybrid laptops the desktop
        // usually lives on the integrated GPU while NVENC is on the discrete one.
        uint vendor = d3dDevice != 0 ? VendorOf(d3dDevice) : 0;
        foreach (var codec in codecs)
        {
            var order = CandidatesFor(codec).ToList();
            // The setting names a vendor ("h264_nvenc" = NVENC): honour it for HEVC/AV1 too.
            int us = preferred.IndexOf('_');
            var pick = preferred == "auto" ? null
                : order.Contains(preferred) ? preferred
                : us > 0 ? order.FirstOrDefault(n => n.EndsWith(preferred[us..])) : null;
            if (pick is not null && order.Remove(pick)) order.Insert(0, pick);
            foreach (var name in order)
            {
                bool is444 = want444 && Can444(name);
                // 4:4:4 needs the CPU colour conversion; every other combination tries the zero-copy path first.
                if (d3dDevice != 0 && !is444 && VendorMatches(name, vendor) && TryOpen(name, fps, bitrateKbps, false, d3dDevice)) return;
                if (TryOpen(name, fps, bitrateKbps, is444, 0)) return;
            }
        }
        throw new InvalidOperationException("Ningún codificador de vídeo disponible.");
    }

    private static bool VendorMatches(string encoder, uint vendor) => vendor == 0 || encoder switch
    {
        _ when encoder.EndsWith("_nvenc") => vendor == 0x10DE,
        _ when encoder.EndsWith("_qsv") => vendor == 0x8086,
        _ when encoder.EndsWith("_amf") => vendor == 0x1002,
        _ => true,
    };

    private static uint VendorOf(nint d3dDevice)
    {
        try
        {
            Marshal.AddRef(d3dDevice); // the wrapper releases it
            using var dev = new Vortice.Direct3D11.ID3D11Device(d3dDevice);
            using var dxgi = dev.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return adapter.Description.VendorId;
        }
        catch { return 0; }
    }

    private static bool Supports(AVCodec* codec, AVPixelFormat fmt)
    {
        if (codec->pix_fmts == null) return false;
        for (var p = codec->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == fmt) return true;
        return false;
    }

    private bool TryOpen(string name, int fps, int bitrateKbps, bool is444, nint d3dDevice)
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null) return false;
        bool gpu = d3dDevice != 0;
        if (gpu && !Supports(codec, AVPixelFormat.AV_PIX_FMT_D3D11)) { Log.Write($"Encoder {name}: no D3D11 input"); return false; }
        if (is444 && !Supports(codec, AVPixelFormat.AV_PIX_FMT_YUV444P)) is444 = false;

        AVPixelFormat fmt;
        _direct = false;
        if (gpu) fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        else if (is444) fmt = AVPixelFormat.AV_PIX_FMT_YUV444P;
        // QSV's own BGRA path produces broken HEVC frames ("Invalid FrameType"): feed it NV12.
        else if (Supports(codec, AVPixelFormat.AV_PIX_FMT_BGRA) && !name.EndsWith("_qsv")) { fmt = AVPixelFormat.AV_PIX_FMT_BGRA; _direct = true; }
        else if (Supports(codec, AVPixelFormat.AV_PIX_FMT_NV12)) fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        else fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

        AVBufferRef* device = null, frames = null;
        if (gpu && !CreateGpuFrames(d3dDevice, out device, out frames)) return false;

        var ctx = ffmpeg.avcodec_alloc_context3(codec);
        long bitrate = (long)bitrateKbps * 1000;
        ctx->width = Width;
        ctx->height = Height;
        ctx->time_base = new AVRational { num = 1, den = fps };
        ctx->framerate = new AVRational { num = fps, den = 1 };
        ctx->pix_fmt = fmt;
        if (gpu)
        {
            ctx->sw_pix_fmt = AVPixelFormat.AV_PIX_FMT_BGRA;
            ctx->hw_frames_ctx = ffmpeg.av_buffer_ref(frames);
        }
        ctx->bit_rate = bitrate;
        ctx->rc_max_rate = bitrate;
        ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate / fps * 2);
        ctx->gop_size = fps * 30;
        ctx->max_b_frames = 0;
        // Several slices per frame let the client decode one frame on several cores (slice threading adds
        // no latency, frame threading would). Crucial for 4:4:4, whose FFmpeg decode path is much slower.
        ctx->slices = 4;
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;

        AVDictionary* opts = null;
        AVDictionary** po = &opts;
        void Opt(string k, string v) => ffmpeg.av_dict_set(po, k, v, 0);
        var codecName = CodecOf(name);
        switch (name)
        {
            case "h264_nvenc" or "hevc_nvenc" or "av1_nvenc":
                Opt("preset", "p3"); Opt("tune", "ull"); Opt("rc", "cbr"); Opt("zerolatency", "1");
                Opt("delay", "0"); Opt("forced-idr", "1"); Opt("rc-lookahead", "0");
                if (codecName != "av1") Opt("profile", codecName == "hevc" ? (is444 ? "rext" : "main") : is444 ? "high444p" : "high");
                break;
            case "h264_qsv" or "hevc_qsv" or "av1_qsv":
                Opt("preset", "veryfast"); Opt("async_depth", "1"); Opt("look_ahead", "0"); Opt("forced_idr", "1");
                Opt("low_delay_brc", "1");
                break;
            case "h264_amf" or "hevc_amf":
                Opt("usage", "ultralowlatency"); Opt("quality", "speed"); Opt("rc", "cbr"); Opt("forced_idr", "1");
                break;
            case "av1_amf":
                Opt("usage", "lowlatency"); Opt("quality", "speed"); Opt("rc", "cbr");
                break;
            case "libx264":
                Opt("preset", "superfast"); Opt("tune", "zerolatency"); Opt("forced-idr", "1");
                if (is444) Opt("profile", "high444");
                ctx->thread_count = Math.Min(8, Environment.ProcessorCount);
                break;
            case "h264_mf" or "hevc_mf":
                Opt("rate_control", "cbr"); Opt("scenario", "display_remoting"); Opt("hw_encoding", "1");
                break;
        }

        int r = ffmpeg.avcodec_open2(ctx, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (r < 0)
        {
            Log.Write($"Encoder {name}{(gpu ? " (GPU input)" : "")} unavailable: {FFmpegSetup.ErrorText(r)}");
            ffmpeg.avcodec_free_context(&ctx);
            if (frames != null) ffmpeg.av_buffer_unref(&frames);
            if (device != null) ffmpeg.av_buffer_unref(&device);
            return false;
        }

        _ctx = ctx;
        _device = device;
        _frames = frames;
        _pkt = ffmpeg.av_packet_alloc();
        if (!gpu)
        {
            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)fmt;
            _frame->width = Width;
            _frame->height = Height;
            _frame->color_range = ctx->color_range;
            _frame->colorspace = ctx->colorspace;
            if (!_direct)
            {
                ffmpeg.av_frame_get_buffer(_frame, 64);
                _sws = ffmpeg.sws_getContext(Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA, Width, Height, fmt,
                    ffmpeg.SWS_BILINEAR, null, null, null); // FAST_BILINEAR visibly smears 1-px text
                ffmpeg.sws_setColorspaceDetails(_sws, Coefficients(ffmpeg.SWS_CS_DEFAULT), 1, Coefficients(ffmpeg.SWS_CS_ITU709), 0, 0, 1 << 16, 1 << 16);
            }
        }
        Name = name;
        Codec = codecName;
        Is444 = is444;
        GpuInput = gpu;
        BitrateKbps = bitrateKbps;
        Log.Write($"Encoder {name} {Width}x{Height}@{fps} {bitrateKbps / 1000.0:0.#}Mbps fmt={ffmpeg.av_get_pix_fmt_name(fmt)}{(gpu ? " (GPU input, zero-copy)" : "")}");
        return true;
    }

    /// <summary>An FFmpeg D3D11 device on the capture's own ID3D11Device, plus a small BGRA texture pool.</summary>
    private bool CreateGpuFrames(nint d3dDevice, out AVBufferRef* device, out AVBufferRef* frames)
    {
        frames = null;
        device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (device == null) return false;
        var dc = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)device->data)->hwctx;
        Marshal.AddRef(d3dDevice); // FFmpeg releases it when the device context goes away
        dc->device = (ID3D11Device*)d3dDevice;
        int r = ffmpeg.av_hwdevice_ctx_init(device);
        if (r < 0) { Log.Write($"GPU input: hwdevice_ctx_init {FFmpegSetup.ErrorText(r)}"); var d = device; ffmpeg.av_buffer_unref(&d); device = null; return false; }

        frames = ffmpeg.av_hwframe_ctx_alloc(device);
        var fc = (AVHWFramesContext*)frames->data;
        fc->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        fc->sw_format = AVPixelFormat.AV_PIX_FMT_BGRA;
        fc->width = Width;
        fc->height = Height;
        fc->initial_pool_size = 8;
        var d3dfc = (AVD3D11VAFramesContext*)fc->hwctx;
        d3dfc->BindFlags = 0x20 | 0x8; // D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE
        r = ffmpeg.av_hwframe_ctx_init(frames);
        if (r < 0)
        {
            Log.Write($"GPU input: hwframe_ctx_init {FFmpegSetup.ErrorText(r)}");
            var f = frames; ffmpeg.av_buffer_unref(&f); frames = null;
            var d = device; ffmpeg.av_buffer_unref(&d); device = null;
            return false;
        }
        return true;
    }

    internal static int_array4 Coefficients(int colorspace)
    {
        int* p = ffmpeg.sws_getCoefficients(colorspace);
        var a = new int_array4();
        for (uint i = 0; i < 4; i++) a[i] = p[i];
        return a;
    }

    /// <summary>Takes effect on the next frame. Only call when <see cref="DynamicBitrate"/>.</summary>
    public void SetBitrate(int kbps)
    {
        if (kbps == BitrateKbps) return;
        long b = (long)kbps * 1000;
        _ctx->bit_rate = b;
        _ctx->rc_max_rate = b;
        // QSV rejects a reset that changes the buffer size ("incompatible video parameters").
        if (!Name.EndsWith("_qsv")) _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, b / _fps * 2);
        BitrateKbps = kbps;
    }

    /// <summary>Encodes one BGRA image from CPU memory. Calls <paramref name="sink"/> for every produced packet.</summary>
    public void Encode(byte* bgra, int stride, bool forceKeyframe, PacketSink sink)
    {
        if (_direct)
        {
            _frame->data[0] = bgra;
            _frame->linesize[0] = stride;
        }
        else
        {
            ffmpeg.av_frame_make_writable(_frame);
            var src = new byte_ptrArray4();
            src[0] = bgra;
            var srcStride = new int_array4();
            srcStride[0] = stride;
            ffmpeg.sws_scale(_sws, src.ToArray(), srcStride.ToArray(), 0, Height, _frame->data.ToArray(), _frame->linesize.ToArray());
        }
        Submit(_frame, forceKeyframe, sink);
    }

    /// <summary>
    /// Encodes one GPU frame: <paramref name="fill"/> copies the picture into the pool texture it is given
    /// (ID3D11Texture2D*, array slice), then the encoder reads it in place.
    /// </summary>
    public void EncodeGpu(Action<nint, uint> fill, bool forceKeyframe, PacketSink sink)
    {
        var f = ffmpeg.av_frame_alloc();
        try
        {
            int r = ffmpeg.av_hwframe_get_buffer(_frames, f, 0);
            if (r < 0) throw new InvalidOperationException($"hwframe_get_buffer: {FFmpegSetup.ErrorText(r)}");
            fill((nint)f->data[0], (uint)(nint)f->data[1]);
            f->color_range = _ctx->color_range;
            f->colorspace = _ctx->colorspace;
            Submit(f, forceKeyframe, sink);
        }
        finally
        {
            ffmpeg.av_frame_free(&f);
        }
    }

    private void Submit(AVFrame* frame, bool forceKeyframe, PacketSink sink)
    {
        frame->pts = _pts++;
        frame->pict_type = forceKeyframe ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        if (forceKeyframe) frame->flags |= ffmpeg.AV_FRAME_FLAG_KEY; else frame->flags &= ~ffmpeg.AV_FRAME_FLAG_KEY;

        int r = ffmpeg.avcodec_send_frame(_ctx, frame);
        if (r < 0) throw new InvalidOperationException($"send_frame: {FFmpegSetup.ErrorText(r)}");
        while (ffmpeg.avcodec_receive_packet(_ctx, _pkt) == 0)
        {
            try
            {
                sink(new ReadOnlySpan<byte>(_pkt->data, _pkt->size), (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0);
            }
            finally
            {
                ffmpeg.av_packet_unref(_pkt);
            }
        }
    }

    public void Dispose()
    {
        if (_ctx != null)
        {
            if (_frame != null)
            {
                // Direct frames point at the capture buffer; never let FFmpeg free it.
                if (_direct) { _frame->data[0] = null; }
                var f = _frame; ffmpeg.av_frame_free(&f); _frame = null;
            }
            var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null;
            var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null;
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_frames != null) { var fr = _frames; ffmpeg.av_buffer_unref(&fr); _frames = null; }
            if (_device != null) { var d = _device; ffmpeg.av_buffer_unref(&d); _device = null; }
        }
    }
}
