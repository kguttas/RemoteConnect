using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RemoteConnect.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

namespace RemoteConnect.Client;

/// <summary>
/// Where the remote picture goes in the window, in physical pixels: which part of the video (Src*) is drawn
/// into which rectangle of the client area (Dst*). Fit = letterboxed and scaled; 1:1 = pixel exact, panning
/// with the mouse when the remote screen is larger than the window.
/// Rotation (clockwise quarter turns) turns the picture on the client; Src* are then in the rotated picture
/// (<see cref="ViewW"/> × <see cref="ViewH"/>), while VideoW × VideoH stay the decoded size.
/// </summary>
public sealed record ViewLayout(int ClientW, int ClientH, int VideoW, int VideoH,
    double SrcX, double SrcY, double SrcW, double SrcH, int DstX, int DstY, int DstW, int DstH, int Rotation = 0)
{
    public bool Exact => Math.Abs(SrcW - DstW) < 0.01 && Math.Abs(SrcH - DstH) < 0.01;
    public int ViewW => (Rotation & 1) != 0 ? VideoH : VideoW;
    public int ViewH => (Rotation & 1) != 0 ? VideoW : VideoH;

    public static ViewLayout Native(int w, int h) => new(w, h, w, h, 0, 0, w, h, 0, 0, w, h);

    public static ViewLayout Compute(int cw, int ch, int vw, int vh, bool fit, double panX, double panY, int rotation = 0)
    {
        rotation &= 3;
        int pw = (rotation & 1) != 0 ? vh : vw, ph = (rotation & 1) != 0 ? vw : vh; // picture as shown
        if (cw <= 0 || ch <= 0 || vw <= 0 || vh <= 0) return new(Math.Max(cw, 0), Math.Max(ch, 0), vw, vh, 0, 0, pw, ph, 0, 0, 0, 0, rotation);
        if (fit)
        {
            double s = Math.Min((double)cw / pw, (double)ch / ph);
            int dw = Math.Max(1, (int)Math.Round(pw * s)), dh = Math.Max(1, (int)Math.Round(ph * s));
            return new(cw, ch, vw, vh, 0, 0, pw, ph, (cw - dw) / 2, (ch - dh) / 2, dw, dh, rotation);
        }
        (double sx, int dx, int w) Axis(int client, int video, double pan) => video <= client
            ? (0, (client - video) / 2, video)
            : (Math.Round(Math.Clamp(pan, 0, 1) * (video - client)), 0, client);
        var (sx, dx, w) = Axis(cw, pw, panX);
        var (sy, dy, h) = Axis(ch, ph, panY);
        return new(cw, ch, vw, vh, sx, sy, w, h, dx, dy, w, h, rotation);
    }

    /// <summary>Window pixel → normalized position on the remote monitor (0..1).</summary>
    public (float X, float Y) Normalize(double px, double py)
    {
        if (DstW <= 0 || DstH <= 0 || VideoW <= 0 || VideoH <= 0) return (0, 0);
        double vx = SrcX + (px - DstX) * SrcW / DstW, vy = SrcY + (py - DstY) * SrcH / DstH;
        var (u, v) = ToVideo(Rotation, Math.Clamp(vx / ViewW, 0, 1), Math.Clamp(vy / ViewH, 0, 1));
        return ((float)u, (float)v);
    }

    /// <summary>Normalized point of the rotated picture → the same point of the decoded video.</summary>
    public static (double U, double V) ToVideo(int rotation, double x, double y) => (rotation & 3) switch
    {
        1 => (y, 1 - x),
        2 => (1 - x, 1 - y),
        3 => (1 - y, x),
        _ => (x, y),
    };
}

/// <summary>
/// Draws decoded pictures with Direct3D 11: GPU-decoded NV12 surfaces are copied on the GPU (never read
/// back), software pictures are uploaded as Y/U/V planes, and a pixel shader does YUV → RGB and scaling.
/// Windowed: a flip-model swap chain on a child HWND, presented immediately (tearing allowed where possible),
/// so neither WPF nor its render thread sit between decode and the screen. Headless (tests): renders 1:1
/// into an offscreen target that <see cref="Sample"/> reads back.
/// All methods run on the decode thread.
/// </summary>
public sealed unsafe class D3DRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer View : register(b0) { float4 src; float4 offs; float4 m0; float4 m1; float4 m2; float4 r0; float4 r1; };
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        VSOut vs(uint id : SV_VertexID)
        {
            float2 t = float2((id << 1) & 2, id & 2);
            VSOut o;
            o.pos = float4(t.x * 2 - 1, 1 - t.y * 2, 0, 1);
            float3 p = float3(src.xy + t * (src.zw - src.xy), 1); // in the picture as shown
            o.uv = float2(dot(r0.xyz, p), dot(r1.xyz, p));       // → the (unrotated) video
            return o;
        }
        SamplerState sY : register(s0);
        SamplerState sC : register(s1);
        Texture2D<float> tY : register(t0);
        #ifdef NV12
        Texture2D<float2> tUV : register(t1);
        float2 chroma(float2 uv) { return tUV.Sample(sC, uv); }
        #else
        Texture2D<float> tU : register(t1);
        Texture2D<float> tV : register(t2);
        float2 chroma(float2 uv) { return float2(tU.Sample(sC, uv), tV.Sample(sC, uv)); }
        #endif
        float3 rgb(float2 uv)
        {
            float4 v = float4(tY.Sample(sY, uv), chroma(uv), 1);
            return saturate(float3(dot(m0, v), dot(m1, v), dot(m2, v)));
        }
        float4 ps(VSOut i) : SV_Target
        {
            if (offs.x == 0 && offs.y == 0) return float4(rgb(i.uv), 1);
            // Downscaling: 4 bilinear taps per output pixel, so 1-px text doesn't alias away.
            float3 c = rgb(i.uv + float2(-offs.x, -offs.y)) + rgb(i.uv + float2(offs.x, -offs.y))
                     + rgb(i.uv + float2(-offs.x, offs.y)) + rgb(i.uv + offs.xy);
            return float4(c * 0.25, 1);
        }
        """;

    private enum Kind { None, Nv12, Planar }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psNv12, _psPlanar;
    private readonly ID3D11SamplerState _point, _linear;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11Buffer _cb;
    private nint _hwnd;
    private IDXGISwapChain1? _swap;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _offscreen, _readback;
    private int _bbW, _bbH;
    private bool _tearing;

    private Kind _kind;
    private int _vw, _vh;
    private bool _chromaFull; // 4:4:4
    private ID3D11Texture2D? _nv12;
    private ID3D11ShaderResourceView? _srvY, _srvUV;
    private readonly ID3D11Texture2D?[] _planes = new ID3D11Texture2D?[3];
    private readonly ID3D11ShaderResourceView?[] _planeSrv = new ID3D11ShaderResourceView?[3];
    private readonly int[] _planeW = new int[3], _planeH = new int[3];
    private readonly float[] _matrix = new float[12];
    private AVFrame* _swFrame;
    private SwsContext* _sws;
    private AVFrame* _conv;

    public nint DevicePointer => _device.NativePointer;
    /// <summary>Running on WARP (software rasteriser): no hardware decoding then.</summary>
    public bool Warp { get; }
    public bool HasPicture => _kind != Kind.None;
    public string Info { get; }

    public D3DRenderer()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        ID3D11Device? dev = null;
        ID3D11DeviceContext? ctx = null;
        foreach (var (type, flags) in new[]
        {
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport),
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport),
            (DriverType.Warp, DeviceCreationFlags.BgraSupport),
        })
        {
            if (D3D11.D3D11CreateDevice(IntPtr.Zero, type, flags, levels, out dev, out ctx).Success && dev is not null)
            {
                Warp = type == DriverType.Warp;
                Info = $"{(Warp ? "WARP" : "GPU")}{((flags & DeviceCreationFlags.VideoSupport) != 0 ? "" : ", sin vídeo")}";
                break;
            }
        }
        _device = dev ?? throw new InvalidOperationException("No se pudo crear un dispositivo Direct3D 11");
        _ctx = ctx!;
        Info ??= "GPU";
        // FFmpeg's D3D11VA decoder and this renderer share the device.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);

        _vs = _device.CreateVertexShader(Compile(ShaderSource, "vs", "vs_4_0"));
        _psNv12 = _device.CreatePixelShader(Compile("#define NV12 1\n" + ShaderSource, "ps", "ps_4_0"));
        _psPlanar = _device.CreatePixelShader(Compile(ShaderSource, "ps", "ps_4_0"));
        _point = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _linear = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _raster = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cb = _device.CreateBuffer(new BufferDescription(112, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    /// <summary>Codecs this machine's GPU decodes (D3D11 video decoder profiles).</summary>
    public List<string> HardwareCodecs()
    {
        var list = new List<string>();
        if (Warp) return list;
        using var video = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        if (video is null) return list;
        var h264 = new Guid("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
        var hevc = new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
        var av1 = new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");
        for (uint i = 0; i < video.VideoDecoderProfileCount; i++)
        {
            var g = video.GetVideoDecoderProfile(i);
            string? name = g == h264 ? "h264" : g == hevc ? "hevc" : g == av1 ? "av1" : null;
            if (name is not null && !list.Contains(name) && video.CheckVideoDecoderFormat(g, Format.NV12)) list.Add(name);
        }
        return list;
    }

    /// <summary>Windowed mode: the child window to present into (0 = headless).</summary>
    public void SetWindow(nint hwnd)
    {
        if (hwnd == _hwnd) return;
        ReleaseTarget();
        _swap?.Dispose();
        _swap = null;
        _hwnd = hwnd;
    }

    // ---------------- upload ----------------

    /// <summary>Takes the decoder's newest picture (GPU or CPU) into this renderer's textures.</summary>
    public void Upload(AVFrame* f)
    {
        int w = f->width, h = f->height;
        var fmt = (AVPixelFormat)f->format;
        SetMatrix(f->colorspace, f->color_range == AVColorRange.AVCOL_RANGE_JPEG || fmt is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ444P, h);
        if (fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            var frames = (AVHWFramesContext*)f->hw_frames_ctx->data;
            if (frames->sw_format == AVPixelFormat.AV_PIX_FMT_NV12)
            {
                EnsureNv12(w, h);
                nint tex = (nint)f->data[0];
                Marshal.AddRef(tex);
                using var src = new ID3D11Texture2D(tex);
                _ctx.CopySubresourceRegion(_nv12!, 0, 0, 0, 0, src, (uint)(nint)f->data[1], new Box(0, 0, 0, w, h, 1));
                _kind = Kind.Nv12;
                (_vw, _vh, _chromaFull) = (w, h, false);
                return;
            }
            // 10-bit or other surfaces: read back and take the software path.
            if (_swFrame == null) _swFrame = ffmpeg.av_frame_alloc();
            ffmpeg.av_frame_unref(_swFrame);
            if (ffmpeg.av_hwframe_transfer_data(_swFrame, f, 0) < 0) return;
            f = _swFrame;
            fmt = (AVPixelFormat)f->format;
        }
        switch (fmt)
        {
            case AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P:
                UploadPlanar(f, w, h, (w + 1) / 2, (h + 1) / 2);
                break;
            case AVPixelFormat.AV_PIX_FMT_YUV444P or AVPixelFormat.AV_PIX_FMT_YUVJ444P:
                UploadPlanar(f, w, h, w, h);
                break;
            default:
                // Anything else (NV12 in memory, high bit depth…): convert to 8-bit 4:2:0 planes first.
                if (_conv == null) _conv = ffmpeg.av_frame_alloc();
                if (_conv->width != w || _conv->height != h)
                {
                    ffmpeg.av_frame_unref(_conv);
                    _conv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                    _conv->width = w;
                    _conv->height = h;
                    ffmpeg.av_frame_get_buffer(_conv, 64);
                }
                _sws = ffmpeg.sws_getCachedContext(_sws, w, h, fmt, w, h, AVPixelFormat.AV_PIX_FMT_YUV420P, ffmpeg.SWS_BILINEAR, null, null, null);
                ffmpeg.sws_scale(_sws, f->data.ToArray(), f->linesize.ToArray(), 0, h, _conv->data.ToArray(), _conv->linesize.ToArray());
                UploadPlanar(_conv, w, h, (w + 1) / 2, (h + 1) / 2);
                break;
        }
    }

    private void EnsureNv12(int w, int h)
    {
        if (_nv12 is not null && _vw == w && _vh == h && _kind == Kind.Nv12) return;
        _srvY?.Dispose(); _srvUV?.Dispose(); _nv12?.Dispose();
        _nv12 = _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
        _srvY = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
        _srvUV = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
    }

    private void UploadPlanar(AVFrame* f, int w, int h, int cw, int ch)
    {
        for (int i = 0; i < 3; i++)
        {
            int pw = i == 0 ? w : cw, ph = i == 0 ? h : ch;
            if (_planes[i] is null || _planeW[i] != pw || _planeH[i] != ph)
            {
                _planeSrv[i]?.Dispose();
                _planes[i]?.Dispose();
                _planes[i] = _device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)pw, (uint)ph, 1, 1, BindFlags.ShaderResource));
                _planeSrv[i] = _device.CreateShaderResourceView(_planes[i]!);
                _planeW[i] = pw;
                _planeH[i] = ph;
            }
            _ctx.UpdateSubresource(_planes[i]!, 0, null, (nint)f->data[(uint)i], (uint)f->linesize[(uint)i], 0);
        }
        _kind = Kind.Planar;
        (_vw, _vh, _chromaFull) = (w, h, cw == w);
    }

    /// <summary>YCbCr → RGB rows (with the range offsets folded into w) for BT.601 / BT.709, limited or full range.</summary>
    private void SetMatrix(AVColorSpace space, bool full, int height)
    {
        bool bt709 = space == AVColorSpace.AVCOL_SPC_BT709 || (space == AVColorSpace.AVCOL_SPC_UNSPECIFIED && height >= 720);
        double kr = bt709 ? 0.2126 : 0.299, kb = bt709 ? 0.0722 : 0.114, kg = 1 - kr - kb;
        double ys = full ? 1 : 255.0 / 219, cs = full ? 1 : 255.0 / 224, yo = full ? 0 : 16.0 / 255, co = 128.0 / 255;
        double rv = cs * 2 * (1 - kr), bu = cs * 2 * (1 - kb), gu = cs * 2 * (1 - kb) * kb / kg, gv = cs * 2 * (1 - kr) * kr / kg;
        double[] m =
        [
            ys, 0, rv, -ys * yo - rv * co,
            ys, -gu, -gv, -ys * yo + (gu + gv) * co,
            ys, bu, 0, -ys * yo - bu * co,
        ];
        for (int i = 0; i < 12; i++) _matrix[i] = (float)m[i];
    }

    // ---------------- draw ----------------

    /// <summary>Draws the current picture with <paramref name="layout"/> and presents it. False if there is nowhere to draw yet.</summary>
    public bool Draw(ViewLayout layout)
    {
        bool headless = _hwnd == 0;
        if (headless)
        {
            if (_kind == Kind.None) return false;
            layout = ViewLayout.Native(_vw, _vh);
        }
        if (layout.ClientW <= 0 || layout.ClientH <= 0) return false;
        if (!EnsureTarget(layout.ClientW, layout.ClientH, headless)) return false;

        _ctx.OMSetRenderTargets(_rtv!);
        _ctx.ClearRenderTargetView(_rtv!, new Color4(11 / 255f, 13 / 255f, 16 / 255f, 1));
        if (_kind != Kind.None && layout.DstW > 0 && layout.DstH > 0 && layout.VideoW == _vw && layout.VideoH == _vh)
        {
            _ctx.RSSetViewport(layout.DstX, layout.DstY, layout.DstW, layout.DstH);
            _ctx.RSSetState(_raster);
            var map = _ctx.Map(_cb, MapMode.WriteDiscard);
            var p = (float*)map.DataPointer;
            int r = layout.Rotation & 3;
            double pw = layout.ViewW, ph = layout.ViewH;
            p[0] = (float)(layout.SrcX / pw); p[1] = (float)(layout.SrcY / ph);
            p[2] = (float)((layout.SrcX + layout.SrcW) / pw); p[3] = (float)((layout.SrcY + layout.SrcH) / ph);
            double sx = layout.SrcW / layout.DstW, sy = layout.SrcH / layout.DstH;
            if ((r & 1) != 0) (sx, sy) = (sy, sx); // the video's x axis runs along the picture's y
            p[4] = sx > 1.01 ? (float)(0.25 * sx / _vw) : 0;
            p[5] = sy > 1.01 ? (float)(0.25 * sy / _vh) : 0;
            p[6] = p[7] = 0;
            for (int i = 0; i < 12; i++) p[8 + i] = _matrix[i];
            // Affine map (x, y, 1) → video u and v; see ViewLayout.ToVideo.
            ReadOnlySpan<float> rot = r switch
            {
                1 => [0, 1, 0, 0, -1, 0, 1, 0],
                2 => [-1, 0, 1, 0, 0, -1, 1, 0],
                3 => [0, -1, 1, 0, 1, 0, 0, 0],
                _ => [1, 0, 0, 0, 0, 1, 0, 0],
            };
            for (int i = 0; i < 8; i++) p[20 + i] = rot[i];
            _ctx.Unmap(_cb, 0);

            bool exact = layout.Exact;
            _ctx.VSSetShader(_vs);
            _ctx.VSSetConstantBuffer(0, _cb); // the VS maps the source rectangle
            _ctx.PSSetConstantBuffer(0, _cb);
            _ctx.PSSetSampler(0, exact ? _point : _linear);
            _ctx.PSSetSampler(1, exact && _chromaFull ? _point : _linear);
            if (_kind == Kind.Nv12)
            {
                _ctx.PSSetShader(_psNv12);
                _ctx.PSSetShaderResources(0, [_srvY!, _srvUV!]);
            }
            else
            {
                _ctx.PSSetShader(_psPlanar);
                _ctx.PSSetShaderResources(0, [_planeSrv[0]!, _planeSrv[1]!, _planeSrv[2]!]);
            }
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _ctx.Draw(3, 0);
        }
        if (headless)
        {
            _ctx.Flush();
            return true;
        }
        var hr = _swap!.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
        if (hr.Failure)
        {
            Log.Write($"Present failed {hr.Code:X8}, recreating swap chain");
            ReleaseTarget();
            _swap.Dispose();
            _swap = null;
            return false;
        }
        return true;
    }

    private bool EnsureTarget(int w, int h, bool headless)
    {
        if (headless)
        {
            if (_offscreen is not null && _bbW == w && _bbH == h) return true;
            ReleaseTarget();
            _offscreen = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget));
            _readback = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            _rtv = _device.CreateRenderTargetView(_offscreen);
            (_bbW, _bbH) = (w, h);
            return true;
        }
        if (_swap is null)
        {
            try
            {
                using var dxgi = _device.QueryInterface<IDXGIDevice1>();
                dxgi.MaximumFrameLatency = 1;
                using var adapter = dxgi.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) _tearing = f5 is not null && f5.PresentAllowTearing;
                var desc = new SwapChainDescription1
                {
                    Width = (uint)w, Height = (uint)h, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                    Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
                    Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
                };
                _swap = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
                factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
                (_bbW, _bbH) = (w, h);
            }
            catch (SharpGenException ex)
            {
                Log.Write($"Swap chain: {ex.Message}");
                return false;
            }
        }
        else if (_bbW != w || _bbH != h)
        {
            ReleaseTarget();
            if (_swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).Failure) return false;
            (_bbW, _bbH) = (w, h);
        }
        if (_rtv is null)
        {
            using var bb = _swap.GetBuffer<ID3D11Texture2D>(0);
            _rtv = _device.CreateRenderTargetView(bb);
        }
        return true;
    }

    private void ReleaseTarget()
    {
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _rtv?.Dispose(); _rtv = null;
        _offscreen?.Dispose(); _offscreen = null;
        _readback?.Dispose(); _readback = null;
        _bbW = _bbH = 0;
    }

    /// <summary>Headless: one pixel of the last drawn picture (diagnostics; stalls the GPU).</summary>
    public (byte B, byte G, byte R)? Sample(int x, int y)
    {
        if (_offscreen is null || _readback is null || x < 0 || y < 0 || x >= _bbW || y >= _bbH) return null;
        _ctx.CopySubresourceRegion(_readback, 0, 0, 0, 0, _offscreen, 0, new Box(x, y, 0, x + 1, y + 1, 1));
        var map = _ctx.Map(_readback, 0, MapMode.Read);
        try
        {
            var p = (byte*)map.DataPointer;
            return (p[0], p[1], p[2]);
        }
        finally { _ctx.Unmap(_readback, 0); }
    }

    // ---------------- shaders ----------------

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte* srcData, nuint srcDataSize, string? sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out nint code, out nint errorMsgs);

    private static byte[] Compile(string source, string entry, string target)
    {
        var src = System.Text.Encoding.ASCII.GetBytes(source);
        int hr;
        nint code, errors;
        fixed (byte* p = src) hr = D3DCompile(p, (nuint)src.Length, "remoteconnect.hlsl", 0, 0, entry, target, 1u << 15 /* OPTIMIZATION_LEVEL3 */, 0, out code, out errors);
        string? message = null;
        if (errors != 0)
        {
            using var e = new Blob(errors);
            message = Marshal.PtrToStringAnsi(e.BufferPointer);
        }
        if (hr < 0 || code == 0) throw new InvalidOperationException($"HLSL {entry}: {message ?? hr.ToString("X8")}");
        using var blob = new Blob(code);
        return blob.AsBytes();
    }

    public void Dispose()
    {
        ReleaseTarget();
        _swap?.Dispose();
        _srvY?.Dispose(); _srvUV?.Dispose(); _nv12?.Dispose();
        for (int i = 0; i < 3; i++) { _planeSrv[i]?.Dispose(); _planes[i]?.Dispose(); }
        _cb.Dispose(); _raster.Dispose(); _linear.Dispose(); _point.Dispose();
        _psPlanar.Dispose(); _psNv12.Dispose(); _vs.Dispose();
        _ctx.ClearState();
        _ctx.Dispose();
        _device.Dispose();
        if (_swFrame != null) { var f = _swFrame; ffmpeg.av_frame_free(&f); _swFrame = null; }
        if (_conv != null) { var c = _conv; ffmpeg.av_frame_free(&c); _conv = null; }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
    }
}
