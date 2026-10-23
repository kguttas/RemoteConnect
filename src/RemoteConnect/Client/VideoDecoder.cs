using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RemoteConnect.Core;

namespace RemoteConnect.Client;

/// <summary>
/// Low-delay H.264 / HEVC / AV1 decoder. Given a D3D11 device it decodes on the GPU (D3D11VA) and the
/// pictures stay there as NV12 textures; otherwise (or if the GPU refuses the stream) it decodes in software.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVFrame* _tmp;
    private AVFrame* _sw;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private AVBufferRef* _hwDevice;
    private int _swsW, _swsH, _swsFmt = -1, _swsSpace = -1, _swsRange = -1;
    // get_format runs on FFmpeg's threads; keep the delegate alive as long as the context.
    private readonly AVCodecContext_get_format? _getFormat;

    public int Width => _frame->width;
    public int Height => _frame->height;
    public string Codec { get; }
    /// <summary>"D3D11VA h264", "h264 (software)", "libdav1d (software)"…</summary>
    public string Description { get; }
    /// <summary>The newest decoded picture (valid until the next <see cref="Decode"/>).</summary>
    public AVFrame* Frame => _frame;
    public bool LastFrameOnGpu => _frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;

    public static AVCodecID IdOf(string codec) => codec switch
    {
        "hevc" => AVCodecID.AV_CODEC_ID_HEVC,
        "av1" => AVCodecID.AV_CODEC_ID_AV1,
        _ => AVCodecID.AV_CODEC_ID_H264,
    };

    /// <summary>Software decoder for a codec (FFmpeg's native AV1 decoder is hardware-only: use dav1d).</summary>
    private static AVCodec* SoftwareDecoder(string codec) =>
        codec == "av1" ? ffmpeg.avcodec_find_decoder_by_name("libdav1d") : ffmpeg.avcodec_find_decoder(IdOf(codec));

    public static bool CanDecode(string codec)
    {
        FFmpegSetup.Init();
        return SoftwareDecoder(codec) != null;
    }

    /// <param name="d3dDevice">ID3D11Device created with VIDEO_SUPPORT for hardware decoding, or 0.</param>
    public VideoDecoder(string codec = "h264", nint d3dDevice = 0)
    {
        FFmpegSetup.Init();
        Codec = codec;
        bool hw = d3dDevice != 0;
        var dec = hw ? ffmpeg.avcodec_find_decoder(IdOf(codec)) : SoftwareDecoder(codec);
        if (dec == null) throw new InvalidOperationException($"Sin decodificador {codec}");
        _ctx = ffmpeg.avcodec_alloc_context3(dec);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        AVDictionary* opts = null;
        if (hw)
        {
            _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            var dc = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)_hwDevice->data)->hwctx;
            Marshal.AddRef(d3dDevice); // released by FFmpeg with the device context
            dc->device = (ID3D11Device*)d3dDevice;
            int hr = ffmpeg.av_hwdevice_ctx_init(_hwDevice);
            if (hr < 0) { Dispose(); throw new InvalidOperationException("D3D11VA: " + FFmpegSetup.ErrorText(hr)); }
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            _getFormat = (_, fmts) =>
            {
                for (var p = fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
                    if (*p == AVPixelFormat.AV_PIX_FMT_D3D11) return *p;
                return *fmts; // hardware refused this stream: FFmpeg decodes it in software
            };
            _ctx->get_format = _getFormat;
            _ctx->thread_count = 1;
            // The decoder's surfaces are only held until we copy them into our own texture.
            _ctx->extra_hw_frames = 2;
            Description = $"D3D11VA {codec}";
        }
        else if (dec->name is not null && Marshal.PtrToStringAnsi((nint)dec->name) == "libdav1d")
        {
            // dav1d defaults to frame threading with a multi-frame delay; one frame in, one frame out.
            ffmpeg.av_dict_set(&opts, "max_frame_delay", "1", 0);
            _ctx->thread_count = Math.Min(8, Environment.ProcessorCount);
            Description = "libdav1d (software)";
        }
        else
        {
            // Slice threading adds no latency (frame threading would add one frame per thread).
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
            _ctx->thread_count = Math.Min(8, Environment.ProcessorCount);
            Description = $"{codec} (software)";
        }
        int r = ffmpeg.avcodec_open2(_ctx, dec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (r < 0) { Dispose(); throw new InvalidOperationException($"No se pudo abrir el decodificador {codec}: " + FFmpegSetup.ErrorText(r)); }
        _frame = ffmpeg.av_frame_alloc();
        _tmp = ffmpeg.av_frame_alloc();
        _sw = ffmpeg.av_frame_alloc();
        _pkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>Feeds one access unit. Returns true when a picture is ready; throws on corrupt data.</summary>
    public bool Decode(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = data.Length;
            int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            _pkt->data = null;
            _pkt->size = 0;
            if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) throw new InvalidDataException("decode: " + FFmpegSetup.ErrorText(r));
        }
        bool got = false;
        // receive_frame unrefs its target even when it fails, so receive into a scratch frame.
        while (ffmpeg.avcodec_receive_frame(_ctx, _tmp) == 0)
        {
            ffmpeg.av_frame_unref(_frame);
            ffmpeg.av_frame_move_ref(_frame, _tmp);
            got = true; // keep the newest
        }
        return got;
    }

    /// <summary>Converts the last decoded picture to BGRA into <paramref name="dst"/> (CPU path: diagnostics).</summary>
    public void CopyTo(byte* dst, int dstStride)
    {
        AVFrame* src = _frame;
        if (LastFrameOnGpu)
        {
            ffmpeg.av_frame_unref(_sw);
            if (ffmpeg.av_hwframe_transfer_data(_sw, _frame, 0) < 0) return;
            _sw->colorspace = _frame->colorspace;
            _sw->color_range = _frame->color_range;
            src = _sw;
        }
        int w = src->width, h = src->height, fmt = src->format;
        if (fmt == (int)AVPixelFormat.AV_PIX_FMT_YUV444P && src->color_range != AVColorRange.AVCOL_RANGE_JPEG)
        {
            // swscale has no SIMD path for 4:4:4 → RGB (≈15 ms per 1080p frame); this parallel one takes ~1 ms.
            Yuv444ToBgra(src, dst, dstStride, w, h, src->colorspace == AVColorSpace.AVCOL_SPC_BT709);
            return;
        }
        int space = (int)src->colorspace, range = (int)src->color_range;
        if (_sws == null || w != _swsW || h != _swsH || fmt != _swsFmt || space != _swsSpace || range != _swsRange)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            _sws = ffmpeg.sws_getContext(w, h, (AVPixelFormat)fmt, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_BILINEAR, null, null, null);
            int cs = src->colorspace switch
            {
                AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
                AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
                _ => ffmpeg.SWS_CS_DEFAULT,
            };
            int srcFull = src->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            ffmpeg.sws_setColorspaceDetails(_sws, Host.VideoEncoder.Coefficients(cs), srcFull,
                Host.VideoEncoder.Coefficients(ffmpeg.SWS_CS_DEFAULT), 1, 0, 1 << 16, 1 << 16);
            (_swsW, _swsH, _swsFmt, _swsSpace, _swsRange) = (w, h, fmt, space, range);
        }
        var dstData = new byte_ptrArray4();
        dstData[0] = dst;
        var dstLines = new int_array4();
        dstLines[0] = dstStride;
        ffmpeg.sws_scale(_sws, src->data.ToArray(), src->linesize.ToArray(), 0, h, dstData.ToArray(), dstLines.ToArray());
    }

    private static void Yuv444ToBgra(AVFrame* f, byte* dst, int dstStride, int w, int h, bool bt709)
    {
        // Limited-range YCbCr → RGB in 16.16 fixed point.
        int cy = 76309, crv = bt709 ? 117489 : 104597, cgu = bt709 ? 13975 : 25675, cgv = bt709 ? 34925 : 53279, cbu = bt709 ? 138438 : 132201;
        byte* py = f->data[0], pu = f->data[1], pv = f->data[2];
        int sy = f->linesize[0], su = f->linesize[1], sv = f->linesize[2];
        const int Band = 32;
        Parallel.For(0, (h + Band - 1) / Band, band =>
        {
            int end = Math.Min(h, (band + 1) * Band);
            for (int y = band * Band; y < end; y++)
            {
                byte* Y = py + (long)y * sy, U = pu + (long)y * su, V = pv + (long)y * sv;
                uint* o = (uint*)(dst + (long)y * dstStride);
                for (int x = 0; x < w; x++)
                {
                    int c = (Y[x] - 16) * cy + 32768, d = U[x] - 128, e = V[x] - 128;
                    int r = (c + crv * e) >> 16, g = (c - cgu * d - cgv * e) >> 16, b = (c + cbu * d) >> 16;
                    r = r < 0 ? 0 : r > 255 ? 255 : r;
                    g = g < 0 ? 0 : g > 255 ? 255 : g;
                    b = b < 0 ? 0 : b > 255 ? 255 : b;
                    o[x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
                }
            }
        });
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_tmp != null) { var t = _tmp; ffmpeg.av_frame_free(&t); _tmp = null; }
        if (_sw != null) { var s = _sw; ffmpeg.av_frame_free(&s); _sw = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_hwDevice != null) { var d = _hwDevice; ffmpeg.av_buffer_unref(&d); _hwDevice = null; }
    }
}
