using System.Runtime.InteropServices;
using RemoteConnect.Core;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteConnect.Host;

public enum CaptureResult { Frame, Timeout, Lost }

public sealed class PointerShape
{
    public bool Visible;
    public byte Kind; // 1 monochrome AND/XOR mask, 2 BGRA
    public int HotX, HotY, Width, Height;
    public byte[] Data = [];
}

/// <summary>
/// GPU desktop capture via DXGI Desktop Duplication. In GPU mode the newest frame stays in a GPU texture
/// that the encoder copies from on the GPU (zero-copy); otherwise frames are copied into a CPU BGRA buffer
/// (top-down, Stride = Width * 4) which the encoder reads directly.
///
/// Rotated displays (portrait panels turned landscape, e.g. handheld PCs like the OneXPlayer): Desktop
/// Duplication hands out the image in the panel's native orientation. It is turned back to the orientation
/// the user sees (<see cref="Width"/> × <see cref="Height"/> are the rotated size) while copying to the CPU
/// buffer, so the encoder, the client and the input mapping all see the desktop the right way up.
/// </summary>
public sealed unsafe class ScreenCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _dup;
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _gpuLast;
    private byte[] _pointerBuffer = new byte[64 * 64 * 4];
    private PointerShape? _shape;
    private bool _pointerVisible = true;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Clockwise quarter turns from the captured (native) image to the desktop: 0 = none.</summary>
    public int Rotation => _rotation;
    private int _rotation;
    private int _srcW, _srcH; // native (unrotated) size of the captured image
    public int Stride { get; }
    public MonitorInfo Monitor { get; }
    public byte* Buffer { get; }
    public bool HasImage { get; private set; }
    /// <summary>GPU→CPU copy time of the last frame, in ms (diagnostics).</summary>
    public double LastCopyMs { get; private set; }
    /// <summary>Keep frames on the GPU (<see cref="CopyTo"/>) instead of copying them to <see cref="Buffer"/>.</summary>
    public bool GpuMode
    {
        get => _gpuMode;
        set { if (value != _gpuMode) { _gpuMode = value; HasImage = false; } } // the other path keeps its image elsewhere
    }
    private bool _gpuMode;
    /// <summary>The ID3D11Device frames live on, for an encoder that reads textures; 0 if the desktop isn't 8-bit BGRA.</summary>
    public nint DevicePointer => _desktopFormat == Format.B8G8R8A8_UNorm && _rotation == 0 ? _device.NativePointer : 0; // rotation needs the CPU path
    private readonly Format _desktopFormat;

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var r = d.DesktopCoordinates;
                        list.Add(new MonitorInfo
                        {
                            Index = list.Count,
                            Name = d.DeviceName,
                            Left = r.Left, Top = r.Top,
                            Width = r.Right - r.Left, Height = r.Bottom - r.Top,
                            Primary = r.Left == 0 && r.Top == 0,
                        });
                    }
                }
            }
        }
        return list;
    }

    public ScreenCapture(int monitorIndex)
    {
        int count = EnumerateMonitors().Count;
        if (count == 0) throw new InvalidOperationException("No hay monitores disponibles para capturar.");
        if (monitorIndex < 0 || monitorIndex >= count) monitorIndex = 0;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int index = 0;
        IDXGIAdapter1? foundAdapter = null;
        IDXGIOutput? foundOutput = null;
        MonitorInfo? info = null;
        for (uint a = 0; foundOutput is null && factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var d = output.Description;
                if (!d.AttachedToDesktop) { output.Dispose(); continue; }
                if (index == monitorIndex)
                {
                    var r = d.DesktopCoordinates;
                    info = new MonitorInfo
                    {
                        Index = index, Name = d.DeviceName, Left = r.Left, Top = r.Top,
                        Width = r.Right - r.Left, Height = r.Bottom - r.Top, Primary = r.Left == 0 && r.Top == 0,
                    };
                    foundAdapter = adapter;
                    foundOutput = output;
                    break;
                }
                index++;
                output.Dispose();
            }
            if (foundOutput is null) adapter.Dispose();
        }
        if (foundOutput is null) throw new InvalidOperationException("El monitor ya no está disponible.");
        Monitor = info!;

        try
        {
            D3D11.D3D11CreateDevice(foundAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out _device!, out _context!).CheckError();

            // DuplicateOutput1 lets us ask for BGRA even on HDR / FP16 desktops; it is not supported on every
            // driver / hybrid-GPU setup, so fall back to the classic DuplicateOutput.
            using var output5 = foundOutput.QueryInterfaceOrNull<IDXGIOutput5>();
            IDXGIOutputDuplication? dup = null;
            if (output5 is not null)
            {
                try { dup = output5.DuplicateOutput1(_device, [Format.B8G8R8A8_UNorm]); }
                catch (SharpGenException ex) { Log.Write($"DuplicateOutput1 failed ({ex.ResultCode.Code:X8}), using DuplicateOutput"); }
            }
            if (dup is null)
            {
                using var output1 = foundOutput.QueryInterface<IDXGIOutput1>();
                dup = output1.DuplicateOutput(_device);
            }
            _dup = dup;
        }
        finally
        {
            foundOutput.Dispose();
            foundAdapter!.Dispose();
        }

        var dupDesc = _dup.Description;
        var desc = dupDesc.ModeDescription;
        _desktopFormat = desc.Format;
        _rotation = dupDesc.Rotation switch { ModeRotation.Rotate90 => 1, ModeRotation.Rotate180 => 2, ModeRotation.Rotate270 => 3, _ => 0 };
        // Testing aid: pretend the display is rotated (REMOTECONNECT_FORCEROTATE=90 / 180 / 270).
        bool forced = int.TryParse(Environment.GetEnvironmentVariable("REMOTECONNECT_FORCEROTATE"), out int forceDeg) && forceDeg % 90 == 0;
        if (forced) _rotation = forceDeg / 90 % 4;
        // Encoders and the capture thread share this device; FFmpeg may touch it from its own threads.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
        if (_rotation == 0 || forced)
        {
            _srcW = (int)desc.Width;
            _srcH = (int)desc.Height;
        }
        else
        {
            // The desktop rectangle is in the rotated orientation; the captured image is the native one.
            _srcW = _rotation == 2 ? Monitor.Width : Monitor.Height;
            _srcH = _rotation == 2 ? Monitor.Height : Monitor.Width;
        }
        bool swap = (_rotation & 1) != 0;
        Width = swap ? _srcH : _srcW;
        Height = swap ? _srcW : _srcH;
        if (_rotation != 0) Log.Write($"Display rotated {_rotation * 90}°{(forced ? " (forced)" : "")}: captured {_srcW}x{_srcH}, streamed {Width}x{Height}");
        Stride = Width * 4;
        Buffer = (byte*)NativeMemory.AlignedAlloc((nuint)(Stride * Height), 64);
    }

    /// <summary>Waits for a desktop update. On Frame, Buffer holds the new image.</summary>
    public CaptureResult Acquire(int timeoutMs, out PointerShape? pointerChanged)
    {
        pointerChanged = null;
        Result hr = _dup.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var resource);
        if (hr.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return CaptureResult.Timeout;
        if (hr.Failure) return CaptureResult.Lost;

        bool newImage = false, gpuImage = false;
        long copyStart = Stopwatch.GetTimestamp();
        try
        {
            if (frameInfo.LastMouseUpdateTime != 0)
            {
                bool visible = frameInfo.PointerPosition.Visible;
                if (frameInfo.PointerShapeBufferSize > 0) pointerChanged = ReadPointerShape(frameInfo.PointerShapeBufferSize, visible);
                else if (visible != _pointerVisible && _shape is not null)
                {
                    _shape.Visible = visible;
                    pointerChanged = _shape;
                }
                _pointerVisible = visible;
            }

            if (frameInfo.LastPresentTime != 0 || !HasImage)
            {
                using var tex = resource.QueryInterface<ID3D11Texture2D>();
                if (GpuMode)
                {
                    if (_gpuLast is null)
                    {
                        var d = tex.Description;
                        d.Usage = ResourceUsage.Default;
                        d.BindFlags = BindFlags.ShaderResource;
                        d.CPUAccessFlags = CpuAccessFlags.None;
                        d.MiscFlags = ResourceOptionFlags.None;
                        d.MipLevels = 1;
                        d.ArraySize = 1;
                        _gpuLast = _device.CreateTexture2D(d);
                    }
                    _context.CopyResource(_gpuLast, tex);
                    gpuImage = true;
                }
                else if (_staging is null)
                {
                    var d = tex.Description;
                    if (_rotation != 0 && (d.Width != _srcW || d.Height != _srcH))
                    {
                        // Not the size we derived from the desktop rectangle: trust the texture.
                        Log.Write($"Captured image is {d.Width}x{d.Height}, expected {_srcW}x{_srcH}");
                        if (d.Width == Width && d.Height == Height) _rotation = 0; // already the right way up
                        _srcW = (int)d.Width; _srcH = (int)d.Height;
                    }
                    d.Usage = ResourceUsage.Staging;
                    d.BindFlags = BindFlags.None;
                    d.CPUAccessFlags = CpuAccessFlags.Read;
                    d.MiscFlags = ResourceOptionFlags.None;
                    d.MipLevels = 1;
                    d.ArraySize = 1;
                    _staging = _device.CreateTexture2D(d);
                }
                if (!GpuMode)
                {
                    _context.CopyResource(_staging, tex);
                    newImage = true;
                }
            }
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }

        if (gpuImage)
        {
            HasImage = true;
            LastCopyMs = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
            return CaptureResult.Frame;
        }
        if (!newImage) return CaptureResult.Timeout;

        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)map.DataPointer;
            int rowPitch = (int)map.RowPitch;
            if (_rotation != 0)
                Rotate(src, rowPitch, Math.Min(_srcW, (_rotation & 1) != 0 ? Height : Width), Math.Min(_srcH, (_rotation & 1) != 0 ? Width : Height), _rotation, (uint*)Buffer, Width, Height);
            else if (rowPitch == Stride)
                System.Buffer.MemoryCopy(src, Buffer, (long)Stride * Height, (long)Stride * Height);
            else
                for (int y = 0; y < Height; y++)
                    System.Buffer.MemoryCopy(src + (long)y * rowPitch, Buffer + (long)y * Stride, Stride, Stride);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        HasImage = true;
        LastCopyMs = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
        return CaptureResult.Frame;
    }

    /// <summary>
    /// Copies a <paramref name="w"/> × <paramref name="h"/> BGRA image turned <paramref name="quarterTurns"/> × 90°
    /// clockwise into <paramref name="dst"/> (<paramref name="dw"/> × <paramref name="dh"/>, tightly packed).
    /// Works in 32 × 32 tiles so both the reads and the scattered writes stay in cache.
    /// </summary>
    public static void Rotate(byte* src, int srcPitch, int w, int h, int quarterTurns, uint* dst, int dw, int dh)
    {
        const int T = 32;
        for (int ty = 0; ty < h; ty += T)
        {
            int yEnd = Math.Min(h, ty + T);
            for (int tx = 0; tx < w; tx += T)
            {
                int xEnd = Math.Min(w, tx + T);
                for (int y = ty; y < yEnd; y++)
                {
                    uint* row = (uint*)(src + (long)y * srcPitch);
                    switch (quarterTurns)
                    {
                        case 1: // (x, y) → (dw - 1 - y, x)
                            for (int x = tx; x < xEnd; x++) dst[(long)x * dw + (dw - 1 - y)] = row[x];
                            break;
                        case 2: // (x, y) → (dw - 1 - x, dh - 1 - y)
                        {
                            uint* d = dst + (long)(dh - 1 - y) * dw + (dw - 1);
                            for (int x = tx; x < xEnd; x++) d[-x] = row[x];
                            break;
                        }
                        default: // 3: (x, y) → (y, dh - 1 - x)
                            for (int x = tx; x < xEnd; x++) dst[(long)(dh - 1 - x) * dw + y] = row[x];
                            break;
                    }
                }
            }
        }
    }

    /// <summary>GPU mode: copies the newest frame (top-left width × height) into an encoder texture slice.</summary>
    public void CopyTo(nint texture, uint subresource, int width, int height)
    {
        if (_gpuLast is null) return;
        Marshal.AddRef(texture); // the wrapper releases it on dispose
        using var dst = new ID3D11Texture2D(texture);
        _context.CopySubresourceRegion(dst, subresource, 0, 0, 0, _gpuLast, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
    }

    private PointerShape? ReadPointerShape(uint size, bool visible)
    {
        if (_pointerBuffer.Length < size) _pointerBuffer = new byte[size];
        OutduplPointerShapeInfo si;
        fixed (byte* p = _pointerBuffer)
        {
            if (_dup.GetFramePointerShape(size, (nint)p, out _, out si).Failure) return null;
        }

        int w = (int)si.Width, h = (int)si.Height, pitch = (int)si.Pitch;
        var shape = new PointerShape { Visible = visible, HotX = si.HotSpot.X, HotY = si.HotSpot.Y, Width = w };
        // DXGI_OUTDUPL_POINTER_SHAPE_TYPE: 1 monochrome, 2 color, 4 masked color
        switch ((int)si.Type)
        {
            case 1:
            {
                // AND mask on top, XOR mask below; repack rows to the WORD alignment CreateBitmap expects.
                int rowBytes = (w + 15) / 16 * 2;
                shape.Kind = 1;
                shape.Height = h / 2;
                shape.Data = new byte[rowBytes * h];
                for (int y = 0; y < h; y++)
                    Array.Copy(_pointerBuffer, y * pitch, shape.Data, y * rowBytes, Math.Min(pitch, rowBytes));
                break;
            }
            case 2:
            case 4:
            {
                bool masked = (int)si.Type == 4;
                shape.Kind = 2;
                shape.Height = h;
                shape.Data = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Array.Copy(_pointerBuffer, y * pitch, shape.Data, y * w * 4, w * 4);
                if (masked)
                {
                    // Masked color: alpha 0 = opaque color, 0xFF = XOR with screen. Approximate XOR as transparent
                    // unless the color is non-black, which then shows as opaque.
                    for (int i = 0; i < shape.Data.Length; i += 4)
                    {
                        bool xor = shape.Data[i + 3] == 0xFF;
                        bool black = shape.Data[i] == 0 && shape.Data[i + 1] == 0 && shape.Data[i + 2] == 0;
                        shape.Data[i + 3] = (byte)(xor && black ? 0 : 0xFF);
                    }
                }
                break;
            }
            default:
                return null;
        }
        _shape = shape;
        return shape;
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _gpuLast?.Dispose();
        _dup?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        if (Buffer != null) NativeMemory.AlignedFree(Buffer);
    }
}
